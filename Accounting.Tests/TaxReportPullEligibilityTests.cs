using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 225 — ดึงเอกสารเข้ารายงาน ภ.พ.30 หลายใบในครั้งเดียว: ตัวตัดสินกลางตัวเดียว <see cref="TaxReportPullEligibility"/>
/// ที่รายการ "ดึงเอกสาร" · ดึงทีละใบ · ดึงหลายใบ · คืนบรรทัดดึงตอน "สร้างใหม่" เรียกร่วมกัน (ล็อกจุดเรียกด้วย tools/required_call_site_check.py) ·
/// ทุกด่านมีเทสต์ทิศตรงข้าม ("ใบที่ถูกอยู่แล้วไม่ถูกแตะ")
/// </summary>
public class TaxReportPullEligibilityTests
{
    private const int Y = 2026, M = 10;   // รายงานงวด ต.ค. 2026
    private static readonly DateTime InPeriod = new(2026, 10, 5);

    private static Document Doc(DocumentType type = DocumentType.PurchaseInvoice, DateTime? date = null,
        DocumentStatus status = DocumentStatus.Approved, decimal vat = 70m, string no = "PI-001")
        => new()
        {
            Id = Guid.NewGuid(), DocumentNumber = no, DocumentType = type, Status = status,
            DocumentDate = date ?? InPeriod, SubTotal = 1000m, VatAmount = vat,
        };

    /// <summary>ข้อเท็จจริงตั้งต้น: ฝั่งขาย = ใบเป็นเจ้าของภาษีขาย (ไม่มีใบกำกับแทน/ใบเสร็จถือ VAT/21913) · ฝั่งซื้อ = เคลมได้เต็ม VAT</summary>
    private static TaxReportPullEligibility.PullFacts Facts(Document d,
        TaxReportPullEligibility.ExistingLine? line = null, (int, int)? other = null, string? replacement = null,
        OutputVatOwnership.Decision? output = null, decimal? claimable = null)
        => new(line, other, replacement,
            output ?? OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(d, false, false, 0m, null, null)),
            claimable ?? d.VatAmount);

    private static TaxReportPullEligibility.Verdict Eval(Document d,
        TaxReportPullEligibility.ExistingLine? line = null, (int, int)? other = null, string? replacement = null,
        OutputVatOwnership.Decision? output = null, decimal? claimable = null)
        => TaxReportPullEligibility.Evaluate(d, Y, M, Facts(d, line, other, replacement, output, claimable));

    // ── ใบปกติผ่าน (ทิศตรงข้ามของทุกด่าน) ──

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, true)]
    [InlineData(DocumentType.Expense, true)]
    [InlineData(DocumentType.TaxInvoice, false)]
    [InlineData(DocumentType.Receipt, false)]
    public void ใบที่ออกแล้ว_มีVAT_ในกรอบ_ดึงได้_พร้อมฝั่งที่ถูก(DocumentType type, bool isInput)
    {
        var v = Eval(Doc(type));
        Assert.True(v.Ok, v.Reason);
        Assert.Equal(isInput, v.IsInput);
        Assert.Null(v.RuleCode);
    }

    // ── CIL: เดิมอยู่ในตาราง "ดึงได้ฝั่งขาย" ทั้งที่ห้ามดึง (§82/5(1)) ──

    [Fact]
    public void ใบรับรองแทนใบเสร็จ_ดึงไม่ได้_และไม่อยู่ในตารางประเภท()
    {
        Assert.Equal(TaxReportPullEligibility.RuleCertificateInLieu, Eval(Doc(DocumentType.CertificateInLieu)).RuleCode);
        Assert.DoesNotContain(DocumentType.CertificateInLieu, TaxReportPullEligibility.PullableTypes);
        Assert.DoesNotContain(DocumentType.CertificateInLieu, TaxReportPullEligibility.InputTypes);
    }

    [Theory]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.Quotation)]
    public void ประเภทที่ไม่อยู่ในตาราง_ดึงไม่ได้(DocumentType type)
        => Assert.Equal(TaxReportPullEligibility.RuleTypeNotPullable, Eval(Doc(type)).RuleCode);

    // ── สถานะเอกสาร: เดิมเรียก API ตรงดึงใบร่าง/ยกเลิกได้ ──

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void ใบที่ยังไม่ออก_ดึงไม่ได้(DocumentStatus st)
        => Assert.Equal(TaxReportPullEligibility.RuleNotIssued, Eval(Doc(status: st)).RuleCode);

    [Fact]
    public void ใบยกเลิก_ดึงไม่ได้()
        => Assert.Equal(TaxReportPullEligibility.RuleVoided, Eval(Doc(status: DocumentStatus.Voided)).RuleCode);

    [Theory]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    public void ใบที่ชำระแล้ว_ยังดึงได้(DocumentStatus st) => Assert.True(Eval(Doc(status: st)).Ok);

    [Fact]
    public void ไม่มีVAT_ดึงไม่ได้() => Assert.Equal(TaxReportPullEligibility.RuleNoVat, Eval(Doc(vat: 0m)).RuleCode);

    [Fact]
    public void ออกใบกำกับเต็มรูปแทนแล้ว_ดึงไม่ได้_และบอกเลขใบแทน()
    {
        var d = Doc(DocumentType.Receipt); d.ReplacedByDocumentId = Guid.NewGuid();
        var v = Eval(d, replacement: "TI-777");
        Assert.Equal(TaxReportPullEligibility.RuleReplaced, v.RuleCode);
        Assert.Contains("TI-777", v.Reason);
    }

    [Fact]
    public void ใบสำคัญจ่าย_ต้องติ๊กใช้งานใบกำกับ_และไม่อ้างใบต้นทาง()
    {
        var pv = Doc(DocumentType.PaymentVoucher);
        Assert.Equal(TaxReportPullEligibility.RulePvNoTaxInvoice, Eval(pv).RuleCode);
        pv.HasTaxInvoiceReference = true;
        Assert.True(Eval(pv).Ok);                                   // ทิศตรงข้าม
        pv.RelatedDocumentId = Guid.NewGuid();
        Assert.Equal(TaxReportPullEligibility.RulePvNoTaxInvoice, Eval(pv).RuleCode);
    }

    [Fact]
    public void ภาษีซื้อลงต้นทุน_override_นอก116_ดึงไม่ได้_ส่วน116ดึงได้()
    {
        var d = Doc(); d.InputVatAccountCodeOverride = "51000";
        Assert.Equal(TaxReportPullEligibility.RuleVatExpensed, Eval(d).RuleCode);
        d.InputVatAccountCodeOverride = "11610";
        Assert.True(Eval(d).Ok);
    }

    [Fact]
    public void ภาษีซื้อพัก11640_ดึงไม่ได้จนกว่าถึงกำหนด()
    {
        var d = Doc(); d.InputVatPostedAsUndue = true;
        Assert.Equal(TaxReportPullEligibility.RuleUndueVat, Eval(d).RuleCode);
        d.InputVatBecameClaimableAt = new DateTime(2026, 10, 3);
        Assert.True(Eval(d).Ok);
    }

    // ── S2: ภาษีซื้อที่เคลมได้ (ตัวคำนวณ §82/5 ตัวเดียวกับ loop หลัก) ──

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ภาษีซื้อเคลมได้เป็นศูนย์หรือติดลบ_ดึงไม่ได้(int claimable)
        => Assert.Equal(TaxReportPullEligibility.RuleNoClaimableVat, Eval(Doc(), claimable: claimable).RuleCode);

    [Fact]
    public void ภาษีซื้อเคลมได้บางส่วน_ดึงได้() => Assert.True(Eval(Doc(), claimable: 0.01m).Ok);

    [Fact]
    public void ไม่มีข้อเท็จจริงที่ต้องใช้_ไม่ผ่าน_failclosed()
    {
        var purchase = Doc();
        Assert.Equal(TaxReportPullEligibility.RuleFactsMissing,
            TaxReportPullEligibility.Evaluate(purchase, Y, M, new TaxReportPullEligibility.PullFacts()).RuleCode);
        var sale = Doc(DocumentType.TaxInvoice);
        Assert.Equal(TaxReportPullEligibility.RuleFactsMissing,
            TaxReportPullEligibility.Evaluate(sale, Y, M, new TaxReportPullEligibility.PullFacts(ClaimableInputVat: 70m)).RuleCode);
    }

    // ── B1: ฝั่งขายต้องเป็น "เจ้าของภาษีขาย" ตามกติกาเดียวกับ loop หลัก ──

    [Fact]
    public void ใบแจ้งหนี้ที่ใบกำกับเป็นเจ้าของ_ดึงไม่ได้()
    {
        var inv = Doc(DocumentType.Invoice);
        var own = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(inv, supersededByTaxInvoice: true, false, 0m, null, null));
        Assert.Equal(OutputVatOwnership.RuleSuperseded, Eval(inv, output: own).RuleCode);
    }

    [Fact]
    public void ใบแจ้งหนี้บริการ_VAT_พัก21913_ดึงไม่ได้_รับเงินแล้วดึงได้ในเดือนรับเงิน()
    {
        var inv = Doc(DocumentType.Invoice, new DateTime(2026, 9, 10));
        var pending = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(inv, false, false, 70m, null, null));
        Assert.Equal(OutputVatOwnership.RuleNotDue21913, Eval(inv, output: pending).RuleCode);
        inv.OutputVatDueAt = new DateTime(2026, 10, 2);   // รับเงิน ต.ค. ⇒ VAT ถึงกำหนด ต.ค.
        var due = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(inv, false, false, 0m, null, null));
        Assert.True(Eval(inv, output: due).Ok);                 // ทิศตรงข้าม: เดือนภาษี = เดือนรับเงิน ไม่ใช่เดือนใบ
    }

    [Fact]
    public void ใบแจ้งหนี้ที่ใบเสร็จถือVATเป็นเจ้าของ_ดึงไม่ได้()
    {
        var inv = Doc(DocumentType.Invoice); inv.OutputVatDueAt = InPeriod;
        var own = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(inv, false, ownedByVatReceipt: true, 0m, null, null));
        Assert.Equal(OutputVatOwnership.RuleOwnedByReceipt, Eval(inv, output: own).RuleCode);
    }

    [Fact]
    public void ใบเสร็จที่แค่รับชำระใบกำกับ_ดึงไม่ได้_ใบเสร็จขายสดดึงได้()
    {
        var rc = Doc(DocumentType.Receipt); rc.RelatedDocumentId = Guid.NewGuid();
        var settle = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(rc, false, false, 0m, DocumentType.TaxInvoice, null));
        Assert.Equal(OutputVatOwnership.RuleSettlementOnly, Eval(rc, output: settle).RuleCode);
        var cash = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(rc, false, false, 0m, DocumentType.Quotation, null));
        Assert.True(Eval(rc, output: cash).Ok);
    }

    [Fact]
    public void มัดจำVATพักยังไม่รับรู้_หรือถูกนำไปหักแล้ว_ดึงไม่ได้()
    {
        var dep = Doc(DocumentType.Receipt); dep.IsDeposit = true; dep.DepositOutputVatDeferred = true;
        Assert.Equal(OutputVatOwnership.RuleDepositNotRecognized,
            Eval(dep, output: OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(dep, false, false, 0m, null, null))).RuleCode);
        dep.DepositAppliedToDocumentId = Guid.NewGuid();
        Assert.Equal(OutputVatOwnership.RuleDepositApplied,
            Eval(dep, output: OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(dep, false, false, 0m, null, null))).RuleCode);
        dep.DepositAppliedToDocumentId = null; dep.DepositOutputVatRecognizedAt = InPeriod;
        Assert.True(Eval(dep, output: OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(dep, false, false, 0m, null, null))).Ok);
    }

    [Fact]
    public void ใบกำกับภาษีเดี่ยว_ยังดึงได้()
        => Assert.True(Eval(Doc(DocumentType.TaxInvoice)).Ok);

    // ── คำตัดสินผู้ทำบัญชี: ภาษีขายย้อนเดือนดึงไม่ได้ (ยื่นเพิ่มเติม §83/§89/1) · ภาษีซื้อย้อนได้ตาม §82/3 ──

    [Fact]
    public void ภาษีขายเดือนก่อน_ดึงเข้างวดหลังไม่ได้_ต้องยื่นเพิ่มเติม()
    {
        var v = Eval(Doc(DocumentType.TaxInvoice, new DateTime(2026, 9, 28)));
        Assert.Equal(TaxReportPullEligibility.RuleOutputLate, v.RuleCode);
        Assert.Contains("ยื่น ภ.พ.30 เพิ่มเติม", v.Reason);
        Assert.Contains("09/2026", v.Reason);
    }

    [Fact]
    public void ภาษีซื้อเกิน6เดือน_RD82_3_ส่วนในกรอบดึงได้()
    {
        Assert.Equal(TaxReportPullEligibility.RuleClaimWindow, Eval(Doc(date: new DateTime(2026, 3, 31))).RuleCode);
        Assert.True(Eval(Doc(date: new DateTime(2026, 4, 1))).Ok);   // เม.ย. + 6 = ต.ค. ⇒ ยังทัน
    }

    [Fact]
    public void เอกสารเดือนหลังงวด_ดึงย้อนเข้างวดเก่าไม่ได้()
    {
        Assert.Equal(TaxReportPullEligibility.RuleBeforeDocMonth, Eval(Doc(date: new DateTime(2026, 11, 2))).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleBeforeDocMonth, Eval(Doc(DocumentType.TaxInvoice, new DateTime(2026, 11, 2))).RuleCode);
    }

    [Fact]
    public void ใบซื้อบันทึกเดือนถัดไป_แต่ใบกำกับผู้ขายอยู่ในงวด_ดึงได้()
    {
        var d = Doc(date: new DateTime(2026, 11, 3)); d.SupplierTaxInvoiceDate = new DateTime(2026, 10, 28);
        Assert.True(Eval(d).Ok);
    }

    // ── ซ้ำ + บรรทัดเตือน (S4) ──

    [Fact]
    public void มีบรรทัดในงวดนี้แล้ว_แยกบรรทัดรอติ๊ก_บรรทัดที่ใช้อยู่_และบรรทัดเตือน()
    {
        Assert.Equal(TaxReportPullEligibility.RulePendingLineThisReport,
            Eval(Doc(), new TaxReportPullEligibility.ExistingLine(true)).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleAlreadyInThisReport,
            Eval(Doc(), new TaxReportPullEligibility.ExistingLine(false)).RuleCode);
        var warn = Eval(Doc(), new TaxReportPullEligibility.ExistingLine(true, IsWarningLine: true));
        Assert.Equal(TaxReportPullEligibility.RuleWarningLineThisReport, warn.RuleCode);
        Assert.DoesNotContain("ติ๊กช่อง", warn.Reason);
    }

    [Theory]
    [InlineData("🚫 [ภาษีซื้อเกิน 6 เดือน §82/3] PI-1", true)]
    [InlineData("⚠️ [ใบลดหนี้ — ใบเดิมยังไม่ถึง tax point] CN-1", true)]
    [InlineData("[รอใบกำกับ §82/3] PI-2", true)]
    [InlineData("[ยกมา §82/3 — ติ๊ก 'ใช้' เพื่อเคลมเดือนนี้] PI-3", false)]
    [InlineData("[ดึงเข้างวด-ภาษีซื้อ] PI-4", false)]
    public void บรรทัดเตือน_จำแนกตามคำนำหน้า(string desc, bool warning)
        => Assert.Equal(warning, TaxReportPullEligibility.IsWarningLineDescription(desc));

    [Fact]
    public void ใช้ในงวดอื่นอยู่_ดึงไม่ได้_บอกงวด()
    {
        var v = Eval(Doc(), other: (2026, 9));
        Assert.Equal(TaxReportPullEligibility.RuleClaimedInOtherReport, v.RuleCode);
        Assert.Contains("09/2026", v.Reason);
    }

    // ── ระดับรายงาน ──

    [Fact]
    public void รายงานที่ยื่นแล้ว_หรือไม่ใช่ภพ30_ทั้งคำขอล้ม()
    {
        Assert.Equal(TaxReportPullEligibility.RuleReportLocked,
            TaxReportPullEligibility.EvaluateReport(TaxType.VAT, TaxReportStatus.Filed).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleReportLocked,
            TaxReportPullEligibility.EvaluateReport(TaxType.VAT, TaxReportStatus.Submitted).RuleCode);
        Assert.Equal(TaxReportPullEligibility.RuleNotVatReport,
            TaxReportPullEligibility.EvaluateReport(TaxType.WithholdingTax3, TaxReportStatus.Draft).RuleCode);
        Assert.True(TaxReportPullEligibility.EvaluateReport(TaxType.VAT, TaxReportStatus.Draft).Ok);
    }

    // ── หลายใบ: partial + ไม่พบ/ต่างบริษัท + id ซ้ำในคำขอ ──

    [Fact]
    public void ดึงหลายใบ_ผ่านบางใบ_ข้ามบางใบพร้อมเหตุผล_idซ้ำนับครั้งเดียว()
    {
        var ok1 = Doc(no: "PI-A"); var ok2 = Doc(DocumentType.TaxInvoice, no: "TI-B");
        var cil = Doc(DocumentType.CertificateInLieu, no: "CIL-C");
        var dupOther = Doc(no: "PI-D");
        var foreign = Guid.NewGuid();   // ไม่อยู่ในชุดของบริษัทนี้ (ต่างบริษัท/ไม่มีจริง)
        var all = new[] { ok1, ok2, cil, dupOther };
        var facts = all.ToDictionary(d => d.Id, d => Facts(d, other: d == dupOther ? (2026, 8) : null));

        var (items, accepted) = TaxReportPullEligibility.EvaluateBatch(
            new[] { ok1.Id, cil.Id, ok1.Id, foreign, ok2.Id, dupOther.Id },
            all.ToDictionary(d => d.Id), Y, M, facts);

        Assert.Equal(5, items.Count);                                   // ok1 ซ้ำในคำขอนับครั้งเดียว
        Assert.Equal(new[] { ok1.Id, ok2.Id }, accepted.Select(a => a.Doc.Id));
        Assert.Equal(new[] { true, false }, accepted.Select(a => a.IsInput));
        Assert.Equal(TaxReportPullEligibility.RuleCertificateInLieu, items.Single(i => i.DocumentId == cil.Id).RuleCode);
        var nf = items.Single(i => i.DocumentId == foreign);
        Assert.False(nf.Added);
        Assert.Equal(TaxReportPullEligibility.RuleNotFound, nf.RuleCode);
        Assert.Null(nf.DocumentNumber);                                 // ไม่เปิดเผยข้อมูลบริษัทอื่น
        Assert.Equal(TaxReportPullEligibility.RuleClaimedInOtherReport, items.Single(i => i.DocumentId == dupOther.Id).RuleCode);
        Assert.All(items.Where(i => !i.Added), i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }

    [Fact]
    public void ส่งซ้ำหลังดึงแล้ว_ทุกใบถูกข้ามด้วยเหตุผลซ้ำในงวดนี้()
    {
        var a = Doc(no: "PI-A"); var b = Doc(no: "PI-B");
        var (items, accepted) = TaxReportPullEligibility.EvaluateBatch(
            new[] { a.Id, b.Id }, new[] { a, b }.ToDictionary(d => d.Id), Y, M,
            new[] { a, b }.ToDictionary(d => d.Id, d => Facts(d, line: new TaxReportPullEligibility.ExistingLine(false))));
        Assert.Empty(accepted);
        Assert.All(items, i => Assert.Equal(TaxReportPullEligibility.RuleAlreadyInThisReport, i.RuleCode));
    }

    [Fact]
    public void ขนาดคำขอ_ว่างหรือเกินเพดาน_ล้ม_ส่วนซ้ำในเพดานผ่าน()
    {
        Assert.NotNull(TaxReportPullEligibility.ValidateRequestSize(Array.Empty<Guid>()));
        Assert.NotNull(TaxReportPullEligibility.ValidateRequestSize(null));
        var max = Enumerable.Range(0, TaxReportPullEligibility.MaxPerCall).Select(_ => Guid.NewGuid()).ToList();
        Assert.Null(TaxReportPullEligibility.ValidateRequestSize(max));
        Assert.Null(TaxReportPullEligibility.ValidateRequestSize(max.Concat(max.Take(50)).ToList()));   // ซ้ำไม่นับ
        Assert.NotNull(TaxReportPullEligibility.ValidateRequestSize(max.Append(Guid.NewGuid()).ToList()));
    }

    [Fact]
    public void ย้อนเดือน_นับจากเดือนภาษีของใบ_ฝั่งขายใช้วันที่VATถึงกำหนด()
    {
        var p = Doc(date: new DateTime(2026, 7, 31));
        Assert.Equal(3, TaxReportPullEligibility.MonthsLate(TaxReportPullEligibility.TaxMonthOf(p, true, null), Y, M));
        var inv = Doc(DocumentType.Invoice, new DateTime(2026, 8, 20)); inv.OutputVatDueAt = InPeriod;
        var own = OutputVatOwnership.Decide(OutputVatOwnership.FactsOf(inv, false, false, 0m, null, null));
        Assert.Equal(0, TaxReportPullEligibility.MonthsLate(TaxReportPullEligibility.TaxMonthOf(inv, false, own), Y, M));
        Assert.Equal(new DateTime(2026, 4, 1), TaxReportPullEligibility.LateWindowStart(Y, M));
    }
}
