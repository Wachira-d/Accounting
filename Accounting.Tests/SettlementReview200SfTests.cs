using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม SF — แก้ผลฝ่ายค้านของทีม P2 (X-1..X-10) · T (T-1..T-8) · V2 (V2-C1..C4 · V2-P1/P2) ตามคำตัดสิน DECISIONS ข้อ 25–27
/// ทุกเรื่องมีสองทิศ (F2 ข้อ 8): เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementReview200SfTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bank = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Fee = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid DeadBatch = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementFeeVatMode vat = SettlementFeeVatMode.ThaiVat7,
        SettlementFeeWhtMode wht = SettlementFeeWhtMode.None)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Gateway, DisplayName = "Omise", ClearingAccountId = Clearing,
            FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Guid.NewGuid(),
        };

    private static SettlementBatch Batch(decimal net, DateTime? payout = null)
        => new() { CompanyId = Co, PayoutRef = "PO-SF", PayoutDate = payout ?? Day, NetPayout = net, BankAccountId = Bank };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null, decimal? vat = null, DateTime? txn = null)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, VatAmount = vat, MatchedDocumentId = doc, TxnDate = txn ?? Day,
            MatchStatus = doc is not null ? SettlementMatchStatus.Matched : SettlementMatchStatus.AutoSummary,
        };

    private static SettlementPostingFacts Facts(SettlementPostingPlan plan, Func<SettlementReceiptPlan, SettlementReceiptTarget>? target = null)
        => new(
            SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
            new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
            true, true, Clearing,
            plan.Receipts.Select(r => target?.Invoke(r)
                ?? new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice, DocumentStatus.Approved, r.Amount, false))
                .ToList(),
            Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    // ═════════════════ X-3 / X-10 (DECISIONS ข้อ 26): "ตรงกัน" = สองเส้นให้ผลภาษีเท่ากัน ═════════════════

    [Theory]
    // คู่ที่เคยผ่านแต่สองเส้นให้ภาษีต่าง ⇒ ไม่ตรง (ทั้งบริษัทจด/ไม่จด VAT)
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.None, true, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.None, false, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.SelfWithholdReimbursed, true, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, true, true)]
    // ทิศตรงข้าม: คู่ที่ให้ผลเท่ากันจริงต้องผ่าน — ไม่จด VAT ⇒ VAT ไทยเป็นค่าใช้จ่ายทั้งก้อนทั้งสองเส้น (X-10) · W1 ไม่มีขา JE/50 ทวิ ฝั่งเรา
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, false, false)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.None, false, false)]
    [InlineData(GatewayFeeVatMode.AddedOnTop, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, false, false)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.AgentWithholds, true, false)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.None, true, false)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.Withhold3Percent, SettlementFeeVatMode.ThaiVat7,
        SettlementFeeWhtMode.SelfWithholdPayerBorne, true, false)]
    public void X3_โหมดต้องให้ผลภาษีเท่ากันทั้งสองเส้น(GatewayFeeVatMode gVat, GatewayFeeWhtMode gWht, SettlementFeeVatMode cVat,
        SettlementFeeWhtMode cWht, bool vatRegistered, bool blocked)
    {
        var why = GatewayBatchIntentRules.ModeMismatch(gVat, gWht, cVat, cWht, vatRegistered, null);
        Assert.Equal(blocked, why != null);
        if (blocked) Assert.Contains("ทางไปต่อ", why);
    }

    [Fact]
    public void X3_ForeignPp36_เส้นรอบโอนตั้งหนี้ภพ36แต่เส้นเดิมไม่มี_จึงไม่ตรง_และบอกทางไปต่อแบบไม่ผูกconfig()
    {
        // ล็อก "ทำไม": ช่องทาง ภ.พ.36 ตั้งหนี้ 7% ของค่าธรรมเนียม — เส้นรอบโอน gateway เดิมไม่มีแนวคิดนี้ ⇒ ภาษีขึ้นกับหน้าที่กด
        var plan = SettlementBatchMath.Plan(Batch(958.21m), new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(SettlementFeeVatMode.ForeignPp36), true);
        Assert.True(Assert.Single(plan.FeeDocuments).Pp36Payable > 0m);
        var why = GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36,
            SettlementFeeWhtMode.None, true, null);
        Assert.Contains("ไม่ผูกการตั้งค่า gateway", why);
    }

    [Fact]
    public void X1_ด่านลงบัญชี_โหมดขัด_บล็อกด้วยรหัสของตัวเอง_ตรงกัน_ไม่มีปัญหา()
    {
        // รอบโอนเก่า (VatAmount null) ของคู่ค่าเริ่มต้น config None ↔ ช่องทาง VAT 7% ⇒ แผนแต่งภาษีซื้อ 2.73 — ด่านลงบัญชีต้องบล็อก
        var plan = SettlementBatchMath.Plan(Batch(1028.21m), new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(), true);
        Assert.Equal(2.73m, Assert.Single(plan.FeeDocuments).InputVat);
        var issue = GatewayBatchIntentRules.PostingIssue(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.None,
            SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, true, null), SettlementFeeVatMode.ThaiVat7);
        Assert.NotNull(issue);
        Assert.True(issue!.Blocking);
        Assert.Equal(SettlementPlanIssueCode.GatewayModeMismatch, issue.Code);
        Assert.Contains("ประกอบใหม่", issue.NextStep);
        // ทิศตรงข้าม: คู่ที่ตรงกัน ⇒ ไม่มีปัญหา (ลงได้ตามเดิม)
        Assert.Null(GatewayBatchIntentRules.PostingIssue(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.IncludedInFee,
            GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, true, null), SettlementFeeVatMode.ThaiVat7));
    }

    [Fact]
    public void X1_บันทึกค่าตั้งgateway_เปลี่ยนโหมดแล้วขัดกับช่องทางที่ผูก_ปฏิเสธพร้อมชื่อช่องทาง_ไม่ขัดหรือไม่มีช่องทาง_บันทึกได้()
    {
        var bound = new[] { ("Omise หลัก", SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, (string?)null) };
        var bad = GatewayBatchIntentRules.ConfigChangeRefusal(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, true, bound);
        Assert.NotNull(bad);
        Assert.Contains("Omise หลัก", bad);
        Assert.Null(GatewayBatchIntentRules.ConfigChangeRefusal(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.None, true, bound));
        Assert.Null(GatewayBatchIntentRules.ConfigChangeRefusal(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, true,
            Array.Empty<(string, SettlementFeeVatMode, SettlementFeeWhtMode, string?)>()));
    }

    [Fact]
    public void X10_บันทึกช่องทาง_ตรวจโหมดเฉพาะช่องทางใหม่หรือเมื่อการผูกหรือโหมดเปลี่ยน()
    {
        var cfg = Guid.NewGuid();
        Assert.True(GatewayBatchIntentRules.ChannelModeTouched(null, null, null, cfg, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None));
        Assert.True(GatewayBatchIntentRules.ChannelModeTouched(cfg, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None,
            cfg, SettlementFeeVatMode.None, SettlementFeeWhtMode.None));
        Assert.True(GatewayBatchIntentRules.ChannelModeTouched(null, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None,
            cfg, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None));
        // ทิศตรงข้าม: แก้ชื่อ/ปิดใช้งานช่องทางเดิม (โหมด+การผูกเท่าเดิม) ⇒ ไม่ตรวจ ไม่บล็อก
        Assert.False(GatewayBatchIntentRules.ChannelModeTouched(cfg, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None,
            cfg, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None));
    }

    // ═════════════════ X-4: WHT ออกภาษีแทนคิดจากฐานรวมของบรรทัดใบ ═════════════════

    [Fact]
    public void X4_ค่าธรรมเนียม3_65คูณ100รายการระบุVATรายการ_ออกภาษีแทน10_55ไม่ใช่11_00()
    {
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 10000m, DocA) };
        for (var i = 0; i < 100; i++) lines.Add(L(SettlementLineType.PaymentFee, -3.65m, vat: -0.24m));
        var plan = SettlementBatchMath.Plan(Batch(9635m), lines, Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        var fee = Assert.Single(plan.FeeDocuments);
        var line = Assert.Single(fee.Lines);
        Assert.Equal(24.00m, fee.InputVat);                  // VAT ยังต่อรายการ (เชื่อค่าที่ระบุ)
        Assert.Equal(341.00m, line.WhtBase);
        Assert.Equal(10.55m, line.WhtAmount);                // round(341 × 3/97) — เดิม 0.11 × 100 = 11.00
        Assert.Equal(351.55m, line.WhtCertIncome);
        Assert.Equal(10.55m, Assert.Single(plan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable).Credit);
        Assert.Equal(plan.PayoutJournal.Sum(l => l.Debit), plan.PayoutJournal.Sum(l => l.Credit));
    }

    [Fact]
    public void X4_ทิศตรงข้าม_ก้อนเดียวไม่ระบุVAT_ตัวเลขเท่าเดิม_และสูตรตัวเดียวเท่ารายก้อน()
    {
        var plan = SettlementBatchMath.Plan(Batch(1028.21m), new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        var line = Assert.Single(Assert.Single(plan.FeeDocuments).Lines);
        Assert.Equal(1.21m, line.WhtAmount);
        Assert.Equal(40.27m, line.WhtCertIncome);
        Assert.Equal((1.21m, 40.27m, 1.21m), SettlementFeeTax.WhtOnBase(39.06m, 3m, SettlementFeeWhtMode.SelfWithholdPayerBorne));
        Assert.Equal((1.17m, 39.06m, 0m), SettlementFeeTax.WhtOnBase(39.06m, 3m, SettlementFeeWhtMode.SelfWithholdReimbursed));
        Assert.Equal((0m, 0m, 0m), SettlementFeeTax.WhtOnBase(39.06m, 3m, SettlementFeeWhtMode.None));
    }

    // ═════════════════ T-1 (DECISIONS ข้อ 27): WHT ลูกค้าของใบที่รอบโอนรับชำระ ═════════════════

    [Fact]
    public void T1_ตัวตัดสิน_ไม่มีWHT_ศูนย์_รับยอดสุทธิครบ_งวดสุดท้าย_รับบางส่วน_ตัดสินไม่ได้()
    {
        Assert.Equal(SettlementReceiptWhtKind.None, SettlementReceiptWht.Decide(0m, 1070m, 500m));
        Assert.Equal(SettlementReceiptWhtKind.FinalInstallment, SettlementReceiptWht.Decide(30m, 1040m, 1040m));
        Assert.Equal(SettlementReceiptWhtKind.FinalInstallment, SettlementReceiptWht.Decide(30m, 1040m, 1039.995m));   // เกณฑ์เดียวกับ CreatePaymentAsync
        Assert.Equal(SettlementReceiptWhtKind.Undecidable, SettlementReceiptWht.Decide(30m, 1040m, 500m));
    }

    [Fact]
    public void T1_รอบโอนจ่ายสุทธิครบ_ส่งnullให้เส้นรับชำระหยิบWHTที่เหลือของใบ_ใบไม่มีWHT_ส่ง0เหมือนเดิม_บางส่วน_ล้มดัง()
    {
        var r = new SettlementReceiptPlan(DocA, 1040m, Array.Empty<Guid>());
        var final = SettlementDocumentBuilder.ReceiptPayment(r, Guid.NewGuid(), Clearing, Day, "PO-SF", "Omise", SettlementReceiptWhtKind.FinalInstallment);
        Assert.Null(final.WithholdingTaxAmount);   // CreatePaymentAsync งวดสุดท้าย ⇒ remainingCap ⇒ Dr 11910 · ลูกหนี้ใน GL ปิดเต็มก้อน
        var none = SettlementDocumentBuilder.ReceiptPayment(r, Guid.NewGuid(), Clearing, Day, "PO-SF", "Omise", SettlementReceiptWhtKind.None);
        Assert.Equal(0m, none.WithholdingTaxAmount);
        Assert.Throws<BusinessRuleException>(() =>
            SettlementDocumentBuilder.ReceiptPayment(r, Guid.NewGuid(), Clearing, Day, "PO-SF", "Omise", SettlementReceiptWhtKind.Undecidable));
    }

    [Fact]
    public void T1_ด่าน_ใบมีWHTรับบางส่วนบล็อกพร้อมทางไปต่อ_รับครบผ่าน_ใบไม่มีWHTรับบางส่วนไม่ถูกแตะ()
    {
        var plan = SettlementBatchMath.Plan(Batch(500m), new[] { L(SettlementLineType.Sale, 500m, DocA) }, Channel(), true);
        Assert.True(plan.CanPost);
        SettlementReceiptTarget Target(SettlementReceiptPlan r, decimal balance, decimal wht)
            => new(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice, DocumentStatus.Approved, balance, false, DocumentWht: wht);

        var partial = SettlementPostingGate.Evaluate(plan, Facts(plan, r => Target(r, 1040m, 30m)));
        Assert.False(partial.CanPost);
        var issue = Assert.Single(partial.Issues, i => i.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable);
        Assert.Contains("หัก ณ ที่จ่าย", issue.Message);
        Assert.Contains("หน้าเอกสาร", issue.NextStep);

        Assert.True(SettlementPostingGate.Evaluate(plan, Facts(plan, r => Target(r, 500m, 30m))).CanPost);   // ยอดสุทธิที่เหลือครบ
        Assert.True(SettlementPostingGate.Evaluate(plan, Facts(plan, r => Target(r, 1040m, 0m))).CanPost);   // ใบไม่มี WHT — ผ่อนชำระได้ตามเดิม
    }

    [Fact]
    public void T1_เงินเข้าเต็มยอดก่อนหักของใบที่ตั้งWHT_ทางไปต่อบอกให้ถอดWHT()
    {
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m, DocA) }, Channel(), true);
        var gated = SettlementPostingGate.Evaluate(plan, Facts(plan,
            r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice, DocumentStatus.Approved, 1040m, false,
                DocumentWht: 30m)));
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable);
        Assert.Contains("ถอดภาษีหัก ณ ที่จ่าย", issue.NextStep);
    }

    // ═════════════════ T-2: ใบสรุปเพิ่มเติมที่เนื้อหาซ้ำรอบที่ออกใบแรก ═════════════════

    [Fact]
    public void T2_ทุกบรรทัดตรงรอบที่ออกใบแรก_บล็อกเป็นรายได้ซ้ำ_ตรงบางบรรทัดหรือตรงรอบอื่น_ยังเป็นใบเพิ่มเติม()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sup = new SettlementSupplementarySummary(Day, new[] { a, b }, "TIV-0100", "PO-1", true, 1);
        var allSame = new[] { new SettlementContentHit<Guid>(a, new[] { "PO-1" }), new SettlementContentHit<Guid>(b, new[] { "PO-1", "PO-2" }) };
        var (dups, kept) = SettlementSummarySupplement.SplitDuplicates(new[] { sup }, allSame, new HashSet<Guid>());
        var dup = Assert.Single(dups);
        Assert.Contains("PO-1", dup.Evidence);
        Assert.Empty(kept);

        var (d2, k2) = SettlementSummarySupplement.SplitDuplicates(new[] { sup }, new[] { new SettlementContentHit<Guid>(a, new[] { "PO-1" }) },
            new HashSet<Guid>());
        Assert.Empty(d2);
        Assert.Single(k2);
        var (d3, k3) = SettlementSummarySupplement.SplitDuplicates(new[] { sup },
            new[] { new SettlementContentHit<Guid>(a, new[] { "PO-9" }), new SettlementContentHit<Guid>(b, new[] { "PO-9" }) },
            new HashSet<Guid>());
        Assert.Empty(d3);
        Assert.Single(k3);
    }

    // ═════════════════ T-3 / T-4: ความต่อเนื่องของ wallet ═════════════════

    [Fact]
    public void T3_payoutวันเดียวกันนำเข้าสลับลำดับ_ไม่บล็อกกันเอง()
    {
        var yesterday = new SettlementWalletCandidate("PO-0", Day.AddDays(-1), Day.AddDays(-1), 0m, 0m);
        var p1 = new SettlementWalletCandidate("PO-1", Day, Day.AddHours(2), 0m, 100m);   // นำเข้าทีหลัง
        var p2 = new SettlementWalletCandidate("PO-2", Day, Day.AddHours(1), 100m, 50m);  // นำเข้าก่อน
        // P1 (ต้น 0 → ปลาย 100): P2 มาหลัง P1 ตามยอด (ต้น P2 = ปลาย P1) ⇒ ไม่ใช่รอบก่อน ⇒ รอบก่อน = เมื่อวาน (ปลาย 0) ⇒ ต่อเนื่อง
        var prev1 = SettlementWalletContinuity.PickPrevious(p1.CreatedAt, 0m, 100m, new[] { p2 }, yesterday);
        Assert.Equal("PO-0", prev1!.PayoutRef);
        Assert.Equal(SettlementWalletContinuityKind.Continuous, SettlementWalletContinuity.Judge(0m, prev1).Kind);
        // P2 (ต้น 100): รอบวันเดียวกันที่ปลายรอบ = 100 คือ P1 (แม้นำเข้าทีหลัง) ⇒ ต่อเนื่อง
        var prev2 = SettlementWalletContinuity.PickPrevious(p2.CreatedAt, 100m, 50m, new[] { p1 }, yesterday);
        Assert.Equal("PO-1", prev2!.PayoutRef);
        Assert.Equal(SettlementWalletContinuityKind.Continuous, SettlementWalletContinuity.Judge(100m, prev2).Kind);
    }

    [Fact]
    public void T3_ทิศตรงข้าม_ยอดขาดจริงยังเป็นGap_และไม่มีรอบก่อนเลยยังไม่รู้()
    {
        var yesterday = new SettlementWalletCandidate("PO-0", Day.AddDays(-1), Day.AddDays(-1), 0m, 20m);
        var prev = SettlementWalletContinuity.PickPrevious(Day, 0m, 100m, Array.Empty<SettlementWalletCandidate>(), yesterday);
        Assert.Equal(SettlementWalletContinuityKind.Gap, SettlementWalletContinuity.Judge(0m, prev).Kind);
        Assert.Null(SettlementWalletContinuity.PickPrevious(Day, 0m, 100m, Array.Empty<SettlementWalletCandidate>(), null));
    }

    [Fact]
    public void T4_รอบก่อนเป็น0ต่อ0_ไม่รู้_เตือนไม่บล็อก_รอบก่อนมียอดจริงแต่ขาด_ยังบล็อก()
    {
        var blank = new SettlementWalletPrevious("PO-OLD", Day.AddDays(-7), 0m, 0m);
        var r = SettlementWalletContinuity.Judge(250m, blank);
        Assert.Equal(SettlementWalletContinuityKind.PreviousHadNoBalances, r.Kind);
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m, DocA) }, Channel(), true);
        var gated = SettlementPostingGate.Evaluate(plan, Facts(plan) with { Wallet = r });
        Assert.True(gated.CanPost);
        Assert.Contains(gated.Issues, i => i.Code == SettlementPlanIssueCode.WalletContinuityUnknown && !i.Blocking && i.Message.Contains("0/0"));
        // ทิศตรงข้าม: รอบก่อนมียอด (ต้น 50 ปลาย 0) ⇒ ยังเป็น Gap บล็อก
        var real = SettlementWalletContinuity.Judge(250m, new SettlementWalletPrevious("PO-OLD", Day.AddDays(-7), 0m, 50m));
        Assert.Equal(SettlementWalletContinuityKind.Gap, real.Kind);
        Assert.False(SettlementPostingGate.Evaluate(plan, Facts(plan) with { Wallet = real }).CanPost);
    }

    // ═════════════════ T-6 / T-8 ═════════════════

    [Fact]
    public void T6_ใบแรกยังเป็นร่าง_ทางไปต่อบอกทางลบร่างหรือยกเลิกรอบนั้นด้วย()
    {
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m) }, Channel(), true);
        var sale = Assert.Single(plan.SummarySales);
        var sup = new SettlementSupplementarySummary(sale.Date, sale.LineIds, "DRAFT-abc", "PO-1", false, 1);
        var gated = SettlementPostingGate.Evaluate(plan, Facts(plan) with { Supplementary = new[] { sup } });
        var issue = Assert.Single(gated.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleFirstNotIssued);
        Assert.Contains("ลบร่าง DRAFT-abc", issue.NextStep);
        Assert.Contains("ยกเลิกรอบโอน PO-1", issue.NextStep);
    }

    [Fact]
    public void T8_เดือนค่าธรรมเนียมข้ามปีเรียงตามปฏิทิน_ไม่ใช่ข้อความ()
    {
        var payout = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc);
        var plan = SettlementBatchMath.Plan(Batch(1000m, payout), new[]
        {
            L(SettlementLineType.Sale, 1100m, DocA, txn: payout),
            L(SettlementLineType.PaymentFee, -50m, txn: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)),
            L(SettlementLineType.PaymentFee, -50m, txn: new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc)),
        }, Channel(), true);
        var cut = Assert.Single(plan.Issues, i => i.Code == SettlementPlanIssueCode.FeeCutoffCrossesMonth);
        Assert.Contains("12/2568, 01/2569", cut.Message);
    }

    // ═════════════════ T-7: JE ปรับปรุงแล้ว (มีหลักฐาน) ไม่ฟ้องซ้ำ ═════════════════

    [Fact]
    public void T7_มีJEอื่นอ้างเลขนี้ในช่องอ้างอิงตรงตัว_ถือว่าปรับปรุงแล้ว_อ้างใกล้เคียงหรืออ้างตัวเอง_ยังเตือน()
    {
        var self = Guid.NewGuid();
        var adj = new LegacyMoneyLegAudit.AdjustingEntry(Guid.NewGuid(), "JV-2026-0099", " JV-2025-0001 ");
        Assert.Equal("JV-2026-0099", LegacyMoneyLegAudit.AdjustedBy(self, "JV-2025-0001", new[] { adj }));
        Assert.Null(LegacyMoneyLegAudit.AdjustedBy(self, "JV-2025-0001",
            new[] { new LegacyMoneyLegAudit.AdjustingEntry(Guid.NewGuid(), "JV-2026-0100", "JV-2025-00011") }));   // ไม่ fuzzy
        Assert.Null(LegacyMoneyLegAudit.AdjustedBy(self, "JV-2025-0001",
            new[] { new LegacyMoneyLegAudit.AdjustingEntry(self, "JV-2025-0001", "JV-2025-0001") }));              // อ้างตัวเองไม่นับ
        Assert.Null(LegacyMoneyLegAudit.AdjustedBy(self, "JV-2025-0001", Array.Empty<LegacyMoneyLegAudit.AdjustingEntry>()));
    }

    // ═════════════════ V2-C1 (DECISIONS ข้อ 25): "ส่งลูกค้าแล้ว" จากหลักฐานการส่งจริง ═════════════════

    [Theory]
    [InlineData(EmailLogStatus.Sent, true)]
    [InlineData(EmailLogStatus.Pending, false)]
    [InlineData(EmailLogStatus.Failed, false)]
    [InlineData(EmailLogStatus.Bounced, false)]
    public void V2C1_หลักฐานการส่ง_นับเฉพาะส่งสำเร็จ(EmailLogStatus status, bool delivered)
        => Assert.Equal(delivered, DocumentDeliveryEvidence.IsDeliveryEvidence(status));

    // ═════════════════ V2-C2 / V2-P1: ป้ายการรับรู้ · การรับรู้ครอบรอบไหน ═════════════════

    // รอบ 201 ทีม ST (A-ST5): การรับรู้ประทับลายนิ้วมือเหตุที่รายการแสดงตอนกด (ตัวเดียวกับ service) — ไม่มีลายนิ้วมือ = ไม่ครอบ
    private static readonly SettlementOrphanAck Ack = new(Guid.Parse("88888888-8888-8888-8888-888888888888"), "สมหญิง", Day,
        "ตรวจแล้วรอบใหม่ไม่ซ้ำ", Assert.Single(TriageAccepted(null, null).Items!).ReasonHash);

    private static SettlementOrphanTriageResult TriageAccepted(SettlementOrphanAck? ack, SettlementOrphanCurrentBatch? current)
    {
        var doc = new SettlementUnpostDocument(Fee, "PV-0009", DocumentType.PaymentVoucher,
            SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending), Day, 7m, false, true, false, false, null, null);
        var refusals = SettlementUnpostGate.Evaluate(new[] { doc }, Array.Empty<SettlementUnpostCertificate>(), new HashSet<(TaxType, int, int)>());
        return SettlementOrphanTriage.Split(new[] { new SettlementOrphanArtifact(Fee, DeadBatch, "PO-OLD", false, "PV-0009", ack) },
            refusals, new[] { doc }, null, current);
    }

    [Fact]
    public void V2C2_ป้ายรับรู้แล้วเฉพาะกองที่การรับรู้มีผล()
    {
        var eff = new SettlementOrphanItem(Fee, false, "PV-0009", "PO-OLD", SettlementOrphanPile.Unvoidable, "", "", Ack, false);
        Assert.True(eff.AckEffective);
        Assert.Equal("✅ รับรู้แล้ว", eff.AckStatusLabel);
        var stale = eff with { Pile = SettlementOrphanPile.Voidable };
        Assert.False(stale.AckEffective);
        Assert.DoesNotContain("✅", stale.AckStatusLabel);
        Assert.Null((eff with { Ack = null }).AckStatusLabel);
    }

    [Fact]
    public void V2P1_รอบใหม่ใช้เลขรอบโอนเดียวกับรอบที่ยกเลิกและนำเข้าหลังการรับรู้_ต้องรับรู้ใหม่()
    {
        var reimport = new SettlementOrphanCurrentBatch(Guid.NewGuid(), "PO-OLD", Day.AddDays(1));
        var t = TriageAccepted(Ack, reimport);
        Assert.Single(t.Unvoidable);          // บล็อกอีกครั้ง
        var item = Assert.Single(t.Items!);
        Assert.True(item.CanAcknowledge);     // ปุ่มรับรู้ขึ้น (รับรู้ใหม่ได้ — ไม่ใช่ตอบซ้ำ)
        Assert.Contains("ไม่ครอบรอบนี้", item.Why);
    }

    [Fact]
    public void V2P1_ทิศตรงข้าม_รอบเลขอื่น_หรือรับรู้หลังนำเข้า_หรือไม่ระบุรอบ_การรับรู้เดิมยังมีผล()
    {
        Assert.Single(TriageAccepted(Ack, new SettlementOrphanCurrentBatch(Guid.NewGuid(), "PO-NEW", Day.AddDays(1))).Acknowledged!);
        Assert.Single(TriageAccepted(Ack, new SettlementOrphanCurrentBatch(Guid.NewGuid(), "PO-OLD", Day.AddDays(-1))).Acknowledged!);
        Assert.Single(TriageAccepted(Ack, null).Acknowledged!);
        Assert.Empty(TriageAccepted(Ack, null).Unvoidable);
    }
}
