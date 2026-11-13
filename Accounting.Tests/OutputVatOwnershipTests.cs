using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 225 B1 — "ใบไหนเป็นเจ้าของภาษีขาย" ย้ายจาก inline ใน <c>TaxService.GenerateVatReport</c> มาเป็น <see cref="OutputVatOwnership"/>
/// (ตัวเดียวกับปุ่ม "ดึงเอกสาร") — <b>loop หลักต้องได้ผลเดิมทุกกรณี</b>: เทสต์นี้เทียบกับสำเนาเงื่อนไขเดิม (ลอกจาก
/// <c>git show HEAD:Accounting/Services/Implementations/TaxService.cs</c> ก่อนรอบ 225 — สาขาใบกำกับ/ใบแจ้งหนี้ + สาขาใบเสร็จ/มัดจำ)
/// บนตารางข้อเท็จจริงทุกทาง (ชนิด × VAT × FK × ใบกำกับแทน × ใบเสร็จถือ VAT × 21913 × มัดจำ × ธง)
/// </summary>
public class OutputVatOwnershipTests
{
    private static readonly DateTime Tp = new(2026, 10, 5), Due = new(2026, 11, 3), Rec = new(2026, 12, 1);

    /// <summary>ผลของโค้ดเดิม: (สาขา, "ข้ามด้วยเหตุความเป็นเจ้าของ", วันที่ที่ใช้เทียบงวด) — ไม่รวมการเทียบงวด (คงอยู่ใน loop)</summary>
    private static (OutputVatOwnership.Branch Branch, bool Skip, DateTime? DueDate) Legacy(OutputVatOwnership.Facts f)
    {
        // ── รอบ 269 H1/H2 (เปลี่ยนโดยตั้งใจ · คำตัดสิน 269-2-1/2): ยอดยกมา · ใบเสร็จจากใบวางบิลรวม · ใบสำคัญรับเดี่ยวไม่มี VAT ไม่ใช่เจ้าของภาษีขาย ──
        if (f.IsOpeningBalance || ((f.Type is DocumentType.Receipt or DocumentType.ReceiptVoucher) && f.RelatedIsRollupBillingNote)
            || (f.Type == DocumentType.ReceiptVoucher && !f.HasRelated && f.VatAmount <= 0.005m))
            return (OutputVatOwnership.Branch.None, true, null);
        // ── สาขาที่ 1 (เดิม): TaxInvoice || (Invoice && VatAmount > 0 && !superseded) ──
        if (f.Type == DocumentType.TaxInvoice
            || (f.Type == DocumentType.Invoice && f.VatAmount > 0 && !f.SupersededByTaxInvoice))
        {
            if (f.Type == DocumentType.Invoice)
            {
                if (f.OutputVatDueAt != null)
                {
                    if (f.OwnedByVatReceipt) return (OutputVatOwnership.Branch.Invoice, true, null);
                    return (OutputVatOwnership.Branch.Invoice, false, f.OutputVatDueAt);
                }
                if (f.Gl21913Net > 0.005m) return (OutputVatOwnership.Branch.Invoice, true, null);
            }
            return (OutputVatOwnership.Branch.Invoice, false, f.TaxPoint);
        }
        // ── สาขาที่ 2 (เดิม): Receipt/RV ที่ standalone / จาก QT,BN / ถือ VAT ของใบแจ้งหนี้ที่ถึงกำหนดแล้ว ──
        if ((f.Type == DocumentType.Receipt || f.Type == DocumentType.ReceiptVoucher)
            && (!f.HasRelated
                || (f.RelatedType != null && (f.RelatedType == DocumentType.Quotation || f.RelatedType == DocumentType.BillingNote))
                || (f.VatAmount > 0.005m && f.RelatedType == DocumentType.Invoice && f.RelatedOutputVatDueAt != null)))
        {
            var effectivelyDeferred = f.IsDeposit && (f.DepositOutputVatDeferred || f.Gl21913Net > 0.005m);
            if (effectivelyDeferred)
            {
                if (f.DepositApplied) return (OutputVatOwnership.Branch.Receipt, true, null);
                if (f.DepositOutputVatRecognizedAt == null) return (OutputVatOwnership.Branch.Receipt, true, null);
                return (OutputVatOwnership.Branch.Receipt, false, f.DepositOutputVatRecognizedAt);
            }
            if (f.IsDeposit && f.DepositApplied) return (OutputVatOwnership.Branch.Receipt, true, null);
            return (OutputVatOwnership.Branch.Receipt, false, f.TaxPoint);
        }
        return (OutputVatOwnership.Branch.None, true, null);
    }

    public static IEnumerable<object[]> Matrix()
    {
        var types = new[] { DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.PurchaseInvoice, DocumentType.CreditNote };
        var related = new (bool Has, DocumentType? Type, DateTime? Due)[]
        {
            (false, null, null), (true, null, null), (true, DocumentType.Quotation, null), (true, DocumentType.BillingNote, null),
            (true, DocumentType.TaxInvoice, null), (true, DocumentType.Invoice, null), (true, DocumentType.Invoice, Due),
        };
        var i = 0;
        foreach (var t in types)
        foreach (var vat in new[] { 70m, 0m, -70m, 0.004m })
        foreach (var rel in related)
        foreach (var dueAt in new DateTime?[] { null, Due })
        foreach (var sup in new[] { false, true })
        foreach (var ownRc in new[] { false, true })
        foreach (var gl in new[] { 0m, 70m })
        foreach (var dep in new[] { false, true })
        foreach (var deferred in new[] { false, true })
        foreach (var applied in new[] { false, true })
        foreach (var rec in new DateTime?[] { null, Rec })
        {
            // ตัดชุดใหญ่ด้วยการสุ่มแบบกำหนดได้ (ทุกตัวที่ 7) — ยังครอบทุกค่าทุกมิติ
            if (i++ % 7 != 0) continue;
            yield return new object[] { new OutputVatOwnership.Facts(t, vat, Tp, dueAt, rel.Has, rel.Type, rel.Due,
                dep, deferred, applied, rec, sup, ownRc, gl) };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ผลเท่ากับโค้ดเดิมของ_loop_หลักทุกกรณี(OutputVatOwnership.Facts f)
    {
        var legacy = Legacy(f);
        var d = OutputVatOwnership.Decide(f);
        Assert.Equal(legacy.Branch, d.Branch);
        Assert.Equal(legacy.Skip, !d.Owns);
        Assert.Equal(legacy.DueDate, d.VatDueDate);
        if (!d.Owns) { Assert.False(string.IsNullOrWhiteSpace(d.Reason)); Assert.NotNull(d.RuleCode); }
    }

    [Fact]
    public void ใบกำกับเดี่ยวเป็นเจ้าของ_ใบแจ้งหนี้ที่ถูกแทนไม่เป็น()
    {
        var ti = new OutputVatOwnership.Facts(DocumentType.TaxInvoice, 70m, Tp, null, false, null, null, false, false, false, null, false, false, 0m);
        Assert.True(OutputVatOwnership.Decide(ti).Owns);
        var inv = ti with { Type = DocumentType.Invoice, SupersededByTaxInvoice = true };
        var d = OutputVatOwnership.Decide(inv);
        Assert.Equal(OutputVatOwnership.Branch.None, d.Branch);
        Assert.Equal(OutputVatOwnership.RuleSuperseded, d.RuleCode);
    }

    // ═══ รอบ 269 — ช่องยอดขาย 5/7/8 (SalesBoxOwner) ═══
    private static readonly OutputVatOwnership.Facts Base0 =
        new(DocumentType.TaxInvoice, 0m, Tp, null, false, null, null, false, false, false, null, false, false, 0m);

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.DeliveryNote)]
    public void R269_ใบที่ไม่ถือการขาย_ไม่เข้าช่องยอดขาย(DocumentType t)
    {
        Assert.False(OutputVatOwnership.SalesBoxOwner(Base0 with { Type = t }).Owns);
        Assert.False(OutputVatOwnership.SalesBoxOwner(Base0 with { Type = t, HasRelated = true, RelatedType = DocumentType.TaxInvoice }).Owns);
    }

    [Fact]
    public void R269_ใบเสร็จรับชำระใบกำกับหรือใบแจ้งหนี้_ไม่เข้าช่องยอดขาย()
    {
        foreach (var rel in new[] { DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.DebitNote })
        {
            var d = OutputVatOwnership.SalesBoxOwner(Base0 with { Type = DocumentType.Receipt, HasRelated = true, RelatedType = rel });
            Assert.False(d.Owns);
            Assert.Equal(OutputVatOwnership.RuleSettlementOnly, d.RuleCode);
        }
    }

    [Fact]
    public void R269_ใบแจ้งหนี้VATศูนย์_ไม่นับเข้าช่อง_แต่คงวันที่ไว้เตือน_ถูกใบกำกับแทนไม่เตือน()
    {
        var inv = Base0 with { Type = DocumentType.Invoice };
        Assert.Equal(OutputVatOwnership.Branch.None, OutputVatOwnership.Decide(inv).Branch);   // ไม่มีแถว §87 (ไม่มีภาษี)
        var box = OutputVatOwnership.SalesBoxOwner(inv);
        Assert.False(box.Owns);
        Assert.Equal(OutputVatOwnership.RuleZeroVatNotTaxInvoice, box.RuleCode);
        Assert.Equal(Tp, box.VatDueDate);
        var replaced = OutputVatOwnership.SalesBoxOwner(inv with { SupersededByTaxInvoice = true });
        Assert.False(replaced.Owns);
        Assert.Equal(OutputVatOwnership.RuleSuperseded, replaced.RuleCode);
    }

    [Fact]
    public void R269_เจ้าของแถว87ที่ถือภาษีขาย_คือเจ้าของช่องยอดขาย_วันที่เดียวกัน()
    {
        // ใบกำกับ 0% · ใบเสร็จเดี่ยวที่มี VAT · ใบเสร็จถือ VAT ของใบแจ้งหนี้บริการ · มัดจำรับรู้แล้ว
        var cases = new[]
        {
            Base0,
            Base0 with { Type = DocumentType.Receipt, VatAmount = 70m },
            Base0 with { Type = DocumentType.Receipt, VatAmount = 70m, HasRelated = true, RelatedType = DocumentType.Invoice, RelatedOutputVatDueAt = Due },
            Base0 with { Type = DocumentType.Receipt, VatAmount = 70m, IsDeposit = true, DepositOutputVatDeferred = true, DepositOutputVatRecognizedAt = Rec },
        };
        foreach (var f in cases)
        {
            var row = OutputVatOwnership.Decide(f);
            Assert.True(row.Owns);
            Assert.Equal(row, OutputVatOwnership.SalesBoxOwner(f));
        }
        // H2: ใบเสร็จเดี่ยวไม่มี VAT มีแถว §87 (เดิม) แต่ไม่เข้าช่อง 0%/ยกเว้น
        var cashNoVat = Base0 with { Type = DocumentType.Receipt };
        Assert.True(OutputVatOwnership.Decide(cashNoVat).Owns);
        Assert.Equal(OutputVatOwnership.RuleZeroVatNotTaxInvoice, OutputVatOwnership.SalesBoxOwner(cashNoVat).RuleCode);
        // มัดจำที่ถูกหักแล้ว · ใบแจ้งหนี้ที่ใบเสร็จถือ VAT — ไม่เป็นเจ้าของทั้งสองที่
        var applied = Base0 with { Type = DocumentType.Receipt, VatAmount = 70m, IsDeposit = true, DepositApplied = true };
        var ownedByRec = Base0 with { Type = DocumentType.Invoice, VatAmount = 70m, OutputVatDueAt = Due, OwnedByVatReceipt = true };
        Assert.False(OutputVatOwnership.SalesBoxOwner(applied).Owns);
        Assert.False(OutputVatOwnership.SalesBoxOwner(ownedByRec).Owns);
    }

    [Fact]
    public void R269_H1_ใบยอดยกมาไม่เป็นเจ้าของทั้งแถว87และช่องยอดขาย_ทิศตรงข้ามใบที่ไม่ใช่ยอดยกมาเป็นเจ้าของ()
    {
        foreach (var t in new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt })
        {
            var ob = Base0 with { Type = t, VatAmount = t == DocumentType.Invoice ? 0m : 70m, IsOpeningBalance = true };
            Assert.False(OutputVatOwnership.Decide(ob).Owns);
            Assert.Equal(OutputVatOwnership.RuleOpeningBalance, OutputVatOwnership.Decide(ob).RuleCode);
            Assert.Equal(OutputVatOwnership.RuleOpeningBalance, OutputVatOwnership.SalesBoxOwner(ob).RuleCode);
        }
        Assert.True(OutputVatOwnership.SalesBoxOwner(Base0 with { VatAmount = 70m }).Owns);
        Assert.True(OutputVatOwnership.IsOutsidePp30(new Accounting.Models.Entities.Document { IsOpeningBalance = true }));
        Assert.False(OutputVatOwnership.IsOutsidePp30(new Accounting.Models.Entities.Document()));
    }

    [Fact]
    public void R269_H2_ใบเสร็จจากใบวางบิลรวมและใบสำคัญรับเดี่ยวไม่มีVAT_ไม่เป็นเจ้าของ_ทิศตรงข้ามยังเป็น()
    {
        var rollup = Base0 with { Type = DocumentType.Receipt, HasRelated = true, RelatedType = DocumentType.BillingNote, RelatedIsRollupBillingNote = true };
        Assert.Equal(OutputVatOwnership.RuleRollupBillingNoteReceipt, OutputVatOwnership.Decide(rollup).RuleCode);
        Assert.False(OutputVatOwnership.SalesBoxOwner(rollup).Owns);
        // ทิศตรงข้าม: ใบเสร็จจากใบวางบิลของใบเสนอราคา (ขายสด) ที่มี VAT = เจ้าของ
        Assert.True(OutputVatOwnership.SalesBoxOwner(rollup with { RelatedIsRollupBillingNote = false, VatAmount = 70m }).Owns);
        var rv = Base0 with { Type = DocumentType.ReceiptVoucher };
        Assert.Equal(OutputVatOwnership.RuleZeroVatReceiptVoucher, OutputVatOwnership.Decide(rv).RuleCode);
        Assert.False(OutputVatOwnership.SalesBoxOwner(rv).Owns);
        // ทิศตรงข้าม: ใบสำคัญรับเดี่ยวที่มี VAT = ขายสด เป็นเจ้าของ
        Assert.True(OutputVatOwnership.SalesBoxOwner(rv with { VatAmount = 70m }).Owns);
    }

    [Fact]
    public void R269_L3_ใบลดหนี้นับเมื่อใบเดิมเป็นใบที่ช่องยอดขายนับได้และถึงจุดความรับผิดแล้ว()
    {
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(false, null, 0m, false, false));                    // ไม่มีใบเดิม — ตามเดิม
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.TaxInvoice, 0m, false, false));   // ใบกำกับ 0%
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Invoice, 70m, false, false));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Invoice, 0m, false, false));     // ใบแจ้งหนี้ไม่มี VAT ไม่เคยอยู่ในช่อง
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Receipt, 0m, false, false));     // ใบเสร็จรับชำระ
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.BillingNote, 0m, false, false));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.TaxInvoice, 70m, true, false));   // ยอดยกมา
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.TaxInvoice, 70m, false, true));   // ใบเดิม VAT ยังพัก
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, null, 0m, false, false));                      // หาใบเดิมไม่เจอ — ไม่เดา
    }

    [Fact]
    public void R269_M3_เลขช่องตามแบบภพ30ทางการ_และยอดขายที่ต้องเสียภาษีไม่ถูกหักด้วยยอดที่ไม่มีแถว()
    {
        Assert.Equal("ช่อง 1", Pp30FormBoxes.Ref(Pp30FormBox.TotalSales));
        Assert.Equal("ช่อง 2", Pp30FormBoxes.Ref(Pp30FormBox.ZeroRatedSales));
        Assert.Equal("ช่อง 3", Pp30FormBoxes.Ref(Pp30FormBox.ExemptSales));
        Assert.Equal("ช่อง 4", Pp30FormBoxes.Ref(Pp30FormBox.TaxableSales));
        Assert.Equal("ช่อง 5", Pp30FormBoxes.Ref(Pp30FormBox.OutputVat));
        Assert.Equal("ช่อง 6", Pp30FormBoxes.Ref(Pp30FormBox.Purchases));
        Assert.Equal("ช่อง 7", Pp30FormBoxes.Ref(Pp30FormBox.InputVat));
        Assert.Equal("ช่อง 10", Pp30FormBoxes.Ref(Pp30FormBox.OverpaidBroughtForward));
        Assert.Equal("ช่อง 11", Pp30FormBoxes.Ref(Pp30FormBox.NetTaxPayable));
        Assert.Equal("ช่อง 12", Pp30FormBoxes.Ref(Pp30FormBox.NetTaxOverpaid));
        var lines = new (string? Code, decimal Amount, decimal Tax, bool Excluded)[]
        {
            (null, 10_000m, 700m, false),                             // ใบกำกับ 7%
            (null, 2_000m, 0m, false),                                // ใบกำกับส่งออก 0%
            (VatReportLineKind.ZeroRatedSales, 2_000m, 0m, false),
            (null, 5_000m, 0m, true),                                 // บรรทัดเตือนใบไม่มี VAT (ติ๊กออก)
            ("INPUT", 3_000m, 210m, false),
        };
        var f = Pp30FormBoxes.Figures(lines, 700m, 210m).ToDictionary(x => x.Box, x => x.Amount);
        Assert.Equal(10_000m, f[Pp30FormBox.TaxableSales]);
        Assert.Equal(2_000m, f[Pp30FormBox.ZeroRatedSales]);
        Assert.Equal(12_000m, f[Pp30FormBox.TotalSales]);
        Assert.Equal(3_000m, f[Pp30FormBox.Purchases]);
        Assert.Equal(490m, f[Pp30FormBox.TaxPayable]);
        Assert.Equal(0m, f[Pp30FormBox.TaxOverpaid]);
        Assert.Equal(490m, f[Pp30FormBox.NetTaxPayable]);
    }

    [Fact]
    public void R269_2_M3_ช่อง8ก่อนหักเครดิตยกมา_ช่อง10เครดิตยกมา_ช่อง11สุทธิ_ทิศตรงข้ามเครดิตเกินได้ช่อง12()
    {
        // ฝ่ายค้านชุดสอง M3: ภาษีขาย 10,000 · ภาษีซื้อ 3,000 · ชำระเกินยกมา 5,000 ⇒ 8 = 7,000 · 10 = 5,000 · 11 = 2,000 (เดิมพิมพ์ "ช่อง 8 = 2,000")
        var lines = new (string? Code, decimal Amount, decimal Tax, bool Excluded)[]
        {
            (Pp30FormBoxes.CreditBroughtForwardCode, 0m, -5_000m, false),
        };
        var f = Pp30FormBoxes.Figures(lines, 10_000m, 3_000m).ToDictionary(x => x.Box, x => x.Amount);
        Assert.Equal(7_000m, f[Pp30FormBox.TaxPayable]);
        Assert.Equal(0m, f[Pp30FormBox.TaxOverpaid]);
        Assert.Equal(5_000m, f[Pp30FormBox.OverpaidBroughtForward]);
        Assert.Equal(2_000m, f[Pp30FormBox.NetTaxPayable]);
        Assert.Equal(0m, f[Pp30FormBox.NetTaxOverpaid]);
        // ทิศตรงข้าม: เครดิตยกมามากกว่าภาษีเดือนนี้ ⇒ ช่อง 12
        var g = Pp30FormBoxes.Figures(new (string?, decimal, decimal, bool)[] { (Pp30FormBoxes.CreditBroughtForwardCode, 0m, -9_000m, false) },
            10_000m, 3_000m).ToDictionary(x => x.Box, x => x.Amount);
        Assert.Equal(0m, g[Pp30FormBox.NetTaxPayable]);
        Assert.Equal(2_000m, g[Pp30FormBox.NetTaxOverpaid]);
        // ภาษีซื้อมากกว่าภาษีขาย ⇒ ช่อง 9 + 10 = ช่อง 12
        var h = Pp30FormBoxes.Figures(new (string?, decimal, decimal, bool)[] { (Pp30FormBoxes.CreditBroughtForwardCode, 0m, -1_000m, false) },
            3_000m, 5_000m).ToDictionary(x => x.Box, x => x.Amount);
        Assert.Equal(2_000m, h[Pp30FormBox.TaxOverpaid]);
        Assert.Equal(3_000m, h[Pp30FormBox.NetTaxOverpaid]);
    }

    [Fact]
    public void R269_2_M1_ช่อง2และ3รวมจากส่วนของใบกับแถวที่ยังใช้_ติ๊กออกลด_ใบไม่มีแถวยังนับ_แถวเตือนไม่ใช่แถวรายการ()
    {
        Guid tiv0 = Guid.NewGuid(), tivMix = Guid.NewGuid(), invExempt = Guid.NewGuid();
        var parts = new Dictionary<Guid, Pp30SalesBoxSummary.Part>
        {
            [tiv0] = new(50_000m, 0m),          // ใบกำกับส่งออก 0% (มีแถว)
            [tivMix] = new(5_000m, 2_000m),     // ใบกำกับผสม (มีแถว)
            [invExempt] = new(0m, 300m),        // ใบแจ้งหนี้ยกเว้น (ไม่มีแถว · มีแต่แถวเตือน)
        };
        Pp30SalesBoxSummary.Row R(Guid id, bool excluded, string desc = "TIV") => new(id, desc, null, excluded);
        var all = Pp30SalesBoxSummary.Totals(new[] { R(tiv0, false), R(tivMix, false), R(invExempt, true, "⚠️ [ยอด 0% ไม่นับเข้าแบบ") }, parts);
        Assert.Equal(new Pp30SalesBoxSummary.Part(55_000m, 2_300m), all);
        // ติ๊กแถวใบส่งออกออก ⇒ ช่อง 2 ลด 50,000 (ช่อง 4 = แถว − ช่อง 2 จึงไม่ติดลบ)
        var excluded = Pp30SalesBoxSummary.Totals(new[] { R(tiv0, true), R(tivMix, false), R(invExempt, true, "⚠️ x") }, parts);
        Assert.Equal(new Pp30SalesBoxSummary.Part(5_000m, 2_300m), excluded);
    }

    [Fact]
    public void R269_ทบทวนรายงานที่บันทึกไว้_สาเหตุที่ระบุคือสาเหตุที่ตรวจ_ร่างสร้างใหม่_ยื่นแล้วพิจารณายื่นเพิ่มเติม_ยอดตรงไม่เตือน()
    {
        var stored = new Pp30SalesBoxReview.Boxes(1_235_494.24m, 0m);
        var fresh = new Pp30SalesBoxReview.Boxes(0m, 0m);
        // ยอดที่บันทึก = กติกาเดิมบนข้อมูลวันนี้ ⇒ สาเหตุ = เปลี่ยนกติกา
        (string, Pp30SalesBoxReview.Boxes)[] Priors(Pp30SalesBoxReview.Boxes pre, Pp30SalesBoxReview.Boxes? v1 = null, Pp30SalesBoxReview.Boxes? v2 = null)
            => new[] { (Pp30SalesBoxReview.RulePre269, pre), (Pp30SalesBoxReview.Rule2691, v1 ?? new(-1m, -1m)), (Pp30SalesBoxReview.Rule2692, v2 ?? new(-1m, -1m)) };
        var draft = Pp30SalesBoxReview.Evaluate(stored, fresh, Priors(stored), alreadyFiled: false, "09/2569");
        Assert.True(draft.NeedsAttention);
        Assert.Equal(TaxReportRecompute.ActionRegenerate, draft.SuggestedAction);
        Assert.Equal(Pp30SalesBoxReview.CauseRuleChange, draft.Cause);
        var filed = Pp30SalesBoxReview.Evaluate(stored, fresh, Priors(stored), alreadyFiled: true, "09/2569");
        Assert.Equal(TaxReportRecompute.ActionConsiderAmendedFiling, filed.SuggestedAction);
        Assert.Contains("ภ.พ.30 เพิ่มเติม", filed.Message);
        Assert.Contains("1,235,494.24", filed.Message);
        Assert.Contains(Pp30FormBoxes.Ref(Pp30FormBox.ZeroRatedSales), filed.Message);
        // ไม่ตรงทั้งสองกติกา ⇒ สาเหตุ = ข้อมูลเปลี่ยน (ไม่อ้างว่าเป็นกติกาเดิม)
        var data = Pp30SalesBoxReview.Evaluate(new(500m, 0m), fresh, Priors(new(900m, 0m)), alreadyFiled: true, "09/2569");
        Assert.Equal(Pp30SalesBoxReview.CauseDataChanged, data.Cause);
        Assert.Null(data.MatchedRule);
        Assert.DoesNotContain("ตรงกับกติการุ่น", data.Message);
        // ชุดสอง M6: รายงานที่สร้างใต้กติการุ่น 269-1 (ไม่ตรงกติกาก่อน 269) ⇒ สาเหตุ = รุ่นกติกา 269-1 ไม่ใช่ "ข้อมูลเปลี่ยน"
        var v1 = Pp30SalesBoxReview.Evaluate(new(700m, 0m), fresh, Priors(new(900m, 0m), v1: new(700m, 0m)), alreadyFiled: true, "09/2569");
        Assert.Equal(Pp30SalesBoxReview.CauseRuleChange, v1.Cause);
        Assert.Equal(Pp30SalesBoxReview.Rule2691, v1.MatchedRule);
        var v2 = Pp30SalesBoxReview.Evaluate(new(0m, 0m), new(0m, 300m), Priors(new(9m, 9m), v2: new(0m, 0m)), alreadyFiled: false, "09/2569");
        Assert.Equal(Pp30SalesBoxReview.Rule2692, v2.MatchedRule);
        // ยอดย้ายเข้า (สดมากกว่าที่บันทึก · งวดที่เดิมไม่มีแถว) ก็ถูกตรวจ
        var moved = Pp30SalesBoxReview.Evaluate(new(0m, 0m), new(5_000m, 0m), Priors(new(0m, 0m)), alreadyFiled: true, "09/2569");
        Assert.True(moved.NeedsAttention);
        Assert.Equal(Pp30SalesBoxReview.CauseRuleChange, moved.Cause);
        // ทิศตรงข้าม: ยอดตรง (ต่างไม่ถึงครึ่งสตางค์) ⇒ ไม่เตือน
        var same = Pp30SalesBoxReview.Evaluate(new(2_000m, 300m), new(2_000.004m, 300m), Priors(new(9m, 9m)), alreadyFiled: true, "09/2569");
        Assert.False(same.NeedsAttention);
        Assert.Equal(TaxReportRecompute.ActionNone, same.SuggestedAction);
    }

    [Fact]
    public void R269_2_9_ช่อง3ยกเว้น_นับที่ใบต้นทางของการขาย_ใบรับชำระ_ยอดยกมา_ใบถูกแทน_มัดจำที่หัก_ไม่นับ()
    {
        // นับ: ใบแจ้งหนี้ตรงไม่มี VAT · ใบเสร็จขายสดไม่มี VAT · ใบสำคัญรับเดี่ยวไม่มี VAT · ใบกำกับผสม (เจ้าของ)
        var inv = Base0 with { Type = DocumentType.Invoice };
        var cash = Base0 with { Type = DocumentType.Receipt };
        var rv = Base0 with { Type = DocumentType.ReceiptVoucher };
        foreach (var f in new[] { inv, cash, rv, Base0 with { VatAmount = 70m } })
        {
            var d = OutputVatOwnership.ExemptBoxOwner(f);
            Assert.True(d.Owns);
            Assert.Equal(Tp, d.VatDueDate);
        }
        // ช่อง 2 ของใบเดียวกันที่ไม่มี VAT และไม่ใช่ใบกำกับ ⇒ ไม่นับ (เตือน)
        foreach (var f in new[] { inv, cash, rv })
            Assert.Equal(OutputVatOwnership.RuleZeroVatNotTaxInvoice, OutputVatOwnership.SalesBoxOwner(f).RuleCode);
        // ไม่นับ: ใบเสร็จรับชำระใบแจ้งหนี้ · ยอดยกมา · ใบแจ้งหนี้ที่ถูกใบกำกับแทน/ใบเสร็จถือ VAT · มัดจำที่ถูกหัก · ใบวางบิล/ใบส่งของ/ใบเสนอราคา
        var notCounted = new[]
        {
            cash with { HasRelated = true, RelatedType = DocumentType.Invoice },
            inv with { IsOpeningBalance = true },
            inv with { SupersededByTaxInvoice = true },
            inv with { OwnedByVatReceipt = true },
            rv with { IsDeposit = true, DepositApplied = true },
            Base0 with { Type = DocumentType.BillingNote },
            Base0 with { Type = DocumentType.DeliveryNote },
            Base0 with { Type = DocumentType.Quotation },
            cash with { HasRelated = true, RelatedType = DocumentType.BillingNote, RelatedIsRollupBillingNote = true },
        };
        foreach (var f in notCounted)
            Assert.False(OutputVatOwnership.ExemptBoxOwner(f).Owns);
        // ใบลดหนี้ของใบแจ้งหนี้ยกเว้น (ไม่มี VAT) ลดช่อง 3 ได้ แต่ไม่ลดช่อง 2
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Invoice, 0m, false, false, exemptBox: true));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Invoice, 0m, false, false));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Invoice, 0m, true, false, exemptBox: true));
    }

    [Fact]
    public void R269_3_M2_ใบลดหนี้ของใบเสร็จขายสดยกเว้นลดช่อง3_ใบรับชำระไม่ลด_ช่อง2ไม่ลด()
    {
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Receipt, 0m, false, false, exemptBox: true, relatedHasRelated: false));
        Assert.True(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.ReceiptVoucher, 0m, false, false, exemptBox: true, relatedHasRelated: false));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Receipt, 0m, false, false, exemptBox: true, relatedHasRelated: true));
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Receipt, 0m, false, false, exemptBox: true));            // ไม่รู้ ⇒ ไม่นับ (ค่าตั้งต้นปลอดภัย)
        Assert.False(OutputVatOwnership.AdjustmentNoteCounts(true, DocumentType.Receipt, 0m, false, false, relatedHasRelated: false));    // ช่อง 2 ไม่ลด
    }

    [Fact]
    public void R269_3_M5_ใบแจ้งหนี้แปลงบางส่วน_ถือยอดยกเว้นที่เหลือ_แปลงทั้งใบเหลือศูนย์_ไม่รู้ยอดใบกำกับไม่นับ()
    {
        var inv = Base0 with { Type = DocumentType.Invoice, VatAmount = 700m, SupersededByTaxInvoice = true };
        var partial = OutputVatOwnership.ExemptBoxOwner(inv with { SupersedingExemptBase = 0m });
        Assert.True(partial.Owns);
        Assert.Equal(OutputVatOwnership.RuleSupersededExemptRemainder, partial.RuleCode);
        Assert.Equal(3_000m, OutputVatOwnership.ExemptRemainder(3_000m, 0m));
        Assert.Equal(0m, OutputVatOwnership.ExemptRemainder(700m, 700m));
        Assert.Equal(0m, OutputVatOwnership.ExemptRemainder(700m, 900m));     // ไม่ติดลบ
        Assert.False(OutputVatOwnership.ExemptBoxOwner(inv).Owns);            // ไม่ได้ส่งยอดใบกำกับ ⇒ ไม่เดา
        Assert.False(OutputVatOwnership.ExemptBoxOwner(inv with { SupersedingExemptBase = 0m, IsOpeningBalance = true }).Owns);
        Assert.False(OutputVatOwnership.SalesBoxOwner(inv with { SupersedingExemptBase = 0m }).Owns);   // ช่อง 2 ไม่ได้ส่วนที่เหลือ
    }

    [Fact]
    public void R269_3_M6_กติการุ่น2691_ยอดยกมาและใบสำคัญรับเดี่ยวยังนับ_ใบรับชำระไม่นับ()
    {
        Assert.True(OutputVatOwnership.SalesBoxOwnerRule2691(Base0 with { Type = DocumentType.Invoice, IsOpeningBalance = true }).Owns);
        Assert.True(OutputVatOwnership.SalesBoxOwnerRule2691(Base0 with { Type = DocumentType.ReceiptVoucher }).Owns);
        Assert.True(OutputVatOwnership.SalesBoxOwnerRule2691(Base0 with { Type = DocumentType.Receipt, HasRelated = true, RelatedType = DocumentType.BillingNote, RelatedIsRollupBillingNote = true }).Owns);
        Assert.False(OutputVatOwnership.SalesBoxOwnerRule2691(Base0 with { Type = DocumentType.Receipt, HasRelated = true, RelatedType = DocumentType.TaxInvoice }).Owns);
        Assert.False(OutputVatOwnership.SalesBoxOwnerRule2691(Base0 with { Type = DocumentType.Invoice, SupersededByTaxInvoice = true }).Owns);
    }

    [Fact]
    public void R269_3_L2_บรรทัดปัดเศษPOSไม่ใช่ยอดขาย0_ทิศตรงข้ามบรรทัด0ทั่วไปยังเป็น0()
    {
        Assert.True(PosTaxInvoiceLines.IsRoundingLine(PosTaxInvoiceLines.RoundingLabel, null, 0m));
        Assert.False(PosTaxInvoiceLines.IsRoundingLine(PosTaxInvoiceLines.RoundingLabel, "SKU-1", 0m));
        Assert.False(PosTaxInvoiceLines.IsRoundingLine("Export goods", null, 0m));
        Assert.False(PosTaxInvoiceLines.IsRoundingLine(PosTaxInvoiceLines.RoundingLabel, null, 7m));
        var split = Pp30SalesClassifier.Split(new[] { new Pp30Line(7m, 93.22m, 6.53m), new Pp30Line(0m, 0.25m, 0m, PosRounding: true), new Pp30Line(0m, 400m, 0m) });
        Assert.Equal(400m, split.ZeroRatedBase);
        Assert.Equal(93.47m, split.TaxableBase);
    }

    [Fact]
    public void R269_3_H1_ร่างล้าสมัย_เทียบแถวที่ระบบสร้าง_ไม่สนติ๊กและบรรทัดดึง_เอกสารเพิ่มหายเปลี่ยนนับถูก()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), pulled = Guid.NewGuid();
        const string prefix = "[ดึงเข้างวด-";
        var stored = TaxReportDraftFreshness.Signature(new (Guid?, string?, string?, decimal, decimal)[]
        {
            (a, "TIV-A", null, 1_000m, 70m),
            (b, "TIV-B", null, 2_000m, 140m),
            (pulled, prefix + "ภาษีซื้อ] PI", "INPUT", 100_000m, 7_000m),   // บรรทัดดึง — ไม่เทียบ
            (null, "[เลื่อนเคลม]", "INPUT", 0m, 50m),                        // ไม่มีเอกสาร — ไม่เทียบ
            (null, "ช่อง 2", VatReportLineKind.ZeroRatedSales, 9m, 0m),
        }, prefix);
        var same = TaxReportDraftFreshness.Signature(new (Guid?, string?, string?, decimal, decimal)[]
        {
            (a, "TIV-A", null, 1_000m, 70m), (b, "TIV-B", null, 2_000m, 140m),
        }, prefix);
        Assert.False(TaxReportDraftFreshness.Compare(stored, same).Stale);
        var changed = TaxReportDraftFreshness.Signature(new (Guid?, string?, string?, decimal, decimal)[]
        {
            (a, "TIV-A", null, 1_000m, 70m), (b, "TIV-B", null, 2_500m, 175m), (c, "TIV-C", null, 10m, 0.7m),
        }, prefix);
        var v = TaxReportDraftFreshness.Compare(stored, changed);
        Assert.Equal((true, 1, 0, 1), (v.Stale, v.Added, v.Removed, v.Changed));
        Assert.Contains("สร้างใหม่", TaxReportDraftFreshness.Message(v, "09/2569"));
        Assert.Null(TaxReportDraftFreshness.Message(TaxReportDraftFreshness.Compare(stored, same), "09/2569"));
    }
}
