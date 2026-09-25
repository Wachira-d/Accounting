using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 เฟส 1 ทีม A — แผนลงบัญชีรอบโอน settlement (Helpers/SettlementBatchMath) · ตัวเลข golden จาก
/// erp-review/2026-09-25/settlement/report-S1.md §2 (gateway 1,070 · ค่าธรรมเนียม 39.06+2.73 · โอน 1,028.21) และ §3
/// (marketplace 963 · โค้ดแพลตฟอร์ม 100 · ค่าธรรมเนียม 67.41+4.72) · สองครึ่ง: ใบที่ต้องถูกบล็อก/ติดป้าย และใบที่ถูกอยู่แล้ว
/// ต้องไม่ถูกแตะ (ลงตัว ⇒ ไม่มีบรรทัดปรับ · ไม่มีปัญหาที่บล็อก)
/// </summary>
public class SettlementBatchMathTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bank = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DocB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementChannelKind kind = SettlementChannelKind.Gateway,
        SettlementFeeVatMode vat = SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode wht = SettlementFeeWhtMode.None)
        => new()
        {
            CompanyId = Co, Kind = kind, DisplayName = kind == SettlementChannelKind.Gateway ? "Omise" : "Shopee ร้านหลัก",
            ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht,
        };

    private static SettlementBatch Batch(decimal netPayout, decimal opening = 0m, decimal closing = 0m, bool bank = true)
        => new()
        {
            CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = netPayout,
            OpeningWalletBalance = opening, ClosingWalletBalance = closing, BankAccountId = bank ? Bank : null,
        };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null, decimal? vat = null,
        Guid? intent = null, string? reason = null, Guid? account = null, string? order = null)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, VatAmount = vat, MatchedDocumentId = doc,
            PaymentIntentId = intent, AdjustmentReason = reason, OverrideAccountId = account, TxnDate = Day, ExternalOrderId = order,
        };

    private static void AssertBalancedJournal(SettlementPostingPlan p)
        => Assert.Equal(p.PayoutJournal.Sum(l => l.Debit), p.PayoutJournal.Sum(l => l.Credit));

    private static void AssertClearingInvariant(SettlementPostingPlan p)
        => Assert.Equal(p.LinesTotal - p.AlreadyInClearing - p.NetPayout, p.ClearingMovement);

    // ═════════════════ gateway (report-S1 §2 G1–G2) ═════════════════

    [Fact]
    public void Gateway_ขาย1070_ค่าธรรมเนียม41_79_โอน1028_21_แยกVATเป็นภาษีซื้อรอเครดิต()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) }, Channel(), true);

        Assert.True(p.CanPost);
        Assert.Equal(0m, p.Difference);
        var r = Assert.Single(p.Receipts);
        Assert.Equal(DocA, r.DocumentId);
        Assert.Equal(1070m, r.Amount);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.InputVatPending, fee.VatTreatment);
        Assert.Equal(41.79m, fee.Deducted);
        Assert.Equal(39.06m, fee.Expense);
        Assert.Equal(2.73m, fee.InputVat);
        var feeLine = Assert.Single(fee.Lines);
        Assert.Equal(SettlementChartSeed.PaymentFeeAccountCode, feeLine.DefaultAccountCode);   // 53170 ไม่ใช่ 54710 (G-3)
        var bank = Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Bank);
        Assert.Equal(1028.21m, bank.Debit);
        var clr = Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Clearing);
        Assert.Equal(1028.21m, clr.Credit);
        Assert.Equal(Clearing, clr.AccountId);
        AssertBalancedJournal(p);
        AssertClearingInvariant(p);
        Assert.DoesNotContain(p.Issues, i => i.Blocking);
    }

    [Fact]
    public void Gateway_บรรทัดขายที่มาจากPaymentIntent_อยู่ในบัญชีพักแล้ว_ไม่ลงรับชำระซ้ำ()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, intent: Guid.NewGuid()), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(), true);
        Assert.True(p.CanPost);
        Assert.Empty(p.Receipts);
        Assert.Empty(p.SummarySales);
        Assert.Equal(1070m, p.AlreadyInClearing);
        AssertClearingInvariant(p);
    }

    // ═════════════════ marketplace (report-S1 §3 M1–M2) ═════════════════

    [Fact]
    public void Marketplace_963_โค้ดแพลตฟอร์ม100อยู่ในฐานVAT_ค่าธรรมเนียม67_41บวก4_72_จับคู่ไม่ได้เข้าใบขายสรุปรายวัน()
    {
        var lines = new[]
        {
            L(SettlementLineType.Sale, 863m, order: "SO-1"),
            L(SettlementLineType.PlatformVoucherSubsidy, 100m, order: "SO-1"),
            L(SettlementLineType.Commission, -51.52m),
            L(SettlementLineType.PaymentFee, -20.61m),
        };
        var p = SettlementBatchMath.Plan(Batch(890.87m), lines, Channel(SettlementChannelKind.Marketplace), true);

        Assert.True(p.CanPost);
        var s = Assert.Single(p.SummarySales);
        Assert.Equal(963m, s.Gross);
        Assert.Equal(900m, s.Net);
        Assert.Equal(63m, s.Vat);
        Assert.Equal(100m, s.PlatformVoucher);
        Assert.Equal(new[] { "SO-1" }, s.ExternalOrderIds);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(72.13m, fee.Deducted);
        Assert.Equal(67.41m, fee.Expense);
        Assert.Equal(4.72m, fee.InputVat);
        var com = Assert.Single(fee.Lines, l => l.LineType == SettlementLineType.Commission);
        Assert.Equal(48.15m, com.Expense);
        Assert.Equal("53140", com.DefaultAccountCode);
        var pay = Assert.Single(fee.Lines, l => l.LineType == SettlementLineType.PaymentFee);
        Assert.Equal(19.26m, pay.Expense);
        // ติดป้ายให้ตรวจ (DECISIONS ข้อ 3) แต่ไม่บล็อก
        var tag = Assert.Single(p.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleCreated);
        Assert.False(tag.Blocking);
        AssertBalancedJournal(p);
        AssertClearingInvariant(p);
    }

    [Fact]
    public void Marketplace_ส่วนลดร้านลดยอดขาย_ไม่ใช่ค่าใช้จ่าย()
    {
        var p = SettlementBatchMath.Plan(Batch(963m),
            new[] { L(SettlementLineType.Sale, 1070m), L(SettlementLineType.SellerVoucher, -107m) },
            Channel(SettlementChannelKind.Marketplace), true);
        Assert.True(p.CanPost);
        var s = Assert.Single(p.SummarySales);
        Assert.Equal(963m, s.Gross);
        Assert.Equal(-107m, s.SellerVoucher);
        Assert.Empty(p.FeeDocuments);
    }

    [Fact]
    public void Marketplace_ส่วนลดของออเดอร์ที่จับคู่ได้_รวมเป็นรับชำระก้อนเดียวของใบนั้น()
    {
        var p = SettlementBatchMath.Plan(Batch(963m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.SellerVoucher, -107m, DocA) },
            Channel(SettlementChannelKind.Marketplace), true);
        var r = Assert.Single(p.Receipts);
        Assert.Equal(963m, r.Amount);
        Assert.Equal(2, r.LineIds.Count);
        Assert.Empty(p.SummarySales);
    }

    // ═════════════════ reserve · ยอดติดลบ · คืนเงินหลังโอน ═════════════════

    [Fact]
    public void Reserve_กันไว้แล้วปล่อย_ลงผัง11350ทั้งสองทาง()
    {
        var hold = SettlementBatchMath.Plan(Batch(900m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.ReserveHold, -100m) }, Channel(), true);
        Assert.True(hold.CanPost);
        var dr = Assert.Single(hold.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Reserve);
        Assert.Equal(100m, dr.Debit);
        Assert.Equal("11350", dr.DefaultAccountCode);
        AssertBalancedJournal(hold);
        AssertClearingInvariant(hold);

        var release = SettlementBatchMath.Plan(Batch(100m), new[] { L(SettlementLineType.ReserveRelease, 100m) }, Channel(), true);
        Assert.True(release.CanPost);
        var cr = Assert.Single(release.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Reserve);
        Assert.Equal(100m, cr.Credit);
        AssertBalancedJournal(release);
    }

    [Fact]
    public void ยอดติดลบยกไป_รอบแรกไม่มีเงินเข้า_รอบถัดไปหักคืนแล้วสมการลงตัว()
    {
        var first = SettlementBatchMath.Plan(Batch(0m, opening: 0m, closing: -535m, bank: false),
            new[] { L(SettlementLineType.Refund, -535m, DocA) }, Channel(SettlementChannelKind.Marketplace), true);
        Assert.True(first.CanPost);
        Assert.Contains(first.Issues, i => i.Code == SettlementPlanIssueCode.NegativeBalanceCarried && !i.Blocking);
        Assert.DoesNotContain(first.Issues, i => i.Code == SettlementPlanIssueCode.BankAccountMissing);
        Assert.DoesNotContain(first.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Bank);
        var refund = Assert.Single(first.Refunds);
        Assert.Equal(535m, refund.Amount);
        AssertClearingInvariant(first);

        var next = SettlementBatchMath.Plan(Batch(1605m, opening: -535m, closing: 0m),
            new[] { L(SettlementLineType.Sale, 2140m, DocB) }, Channel(SettlementChannelKind.Marketplace), true);
        Assert.True(next.CanPost);
        Assert.Equal(0m, next.Difference);
        Assert.Equal(1605m, Assert.Single(next.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Bank).Debit);
    }

    [Fact]
    public void คืนเงินหลังโอน_ไม่รู้ใบเดิม_บล็อกพร้อมทางไปต่อ_86_10()
    {
        var p = SettlementBatchMath.Plan(Batch(1605m),
            new[] { L(SettlementLineType.Sale, 2140m, DocB), L(SettlementLineType.Refund, -535m, order: "SO-9") }, Channel(), true);
        Assert.False(p.CanPost);
        var i = Assert.Single(p.Issues, x => x.Code == SettlementPlanIssueCode.RefundUnmatched);
        Assert.True(i.Blocking);
        Assert.Contains("86/10", i.NextStep);
        Assert.Contains("SO-9", i.Message);
    }

    // ═════════════════ chargeback ═════════════════

    [Fact]
    public void Chargeback_เปิดพักที่ลูกหนี้อื่น_แพ้ลง57140_ชนะคืนบัญชีพัก()
    {
        var ch = Channel();
        var open = SettlementBatchMath.Plan(Batch(700m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Chargeback, -300m) }, ch, true);
        Assert.True(open.CanPost);
        var dispute = Assert.Single(open.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Dispute);
        Assert.Equal(300m, dispute.Debit);
        Assert.Equal("11320", dispute.DefaultAccountCode);

        var lost = SettlementBatchMath.PlanChargebackResolution(300m, won: false, ch, "CB-1");
        Assert.Equal(300m, Assert.Single(lost, l => l.AccountRole == SettlementAccountRoles.ChargebackLoss).Debit);
        Assert.Equal("57140", Assert.Single(lost, l => l.AccountRole == SettlementAccountRoles.ChargebackLoss).DefaultAccountCode);
        Assert.Equal(300m, Assert.Single(lost, l => l.AccountRole == SettlementAccountRoles.Dispute).Credit);

        var won = SettlementBatchMath.PlanChargebackResolution(300m, won: true, ch, "CB-1");
        Assert.Equal(300m, Assert.Single(won, l => l.AccountRole == SettlementAccountRoles.Clearing).Debit);
        Assert.DoesNotContain(won, l => l.AccountRole == SettlementAccountRoles.ChargebackLoss);

        var reversal = SettlementBatchMath.Plan(Batch(300m), new[] { L(SettlementLineType.ChargebackReversal, 300m) }, ch, true);
        Assert.True(reversal.CanPost);
        Assert.Equal(300m, Assert.Single(reversal.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Dispute).Credit);
    }

    // ═════════════════ VAT บนค่าธรรมเนียม ═════════════════

    [Fact]
    public void ค่าธรรมเนียมที่ไฟล์ระบุVATเอง_เชื่อไฟล์()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m, vat: -2.73m) }, Channel(), true);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(39.06m, fee.Expense);
        Assert.Equal(2.73m, fee.InputVat);
    }

    [Fact]
    public void ค่าธรรมเนียมไม่มีVAT_ลงค่าใช้จ่ายเต็มจำนวน()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(vat: SettlementFeeVatMode.None), true);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.NoVat, fee.VatTreatment);
        Assert.Equal(41.79m, fee.Expense);
        Assert.Equal(0m, fee.InputVat);
    }

    [Fact]
    public void บริษัทไม่จดVAT_ไม่มีขาภาษีซื้อ_ใบขายสรุปไม่มีVAT()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m), L(SettlementLineType.PaymentFee, -41.79m) }, Channel(), false);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.VatNotClaimable, fee.VatTreatment);
        Assert.Equal(41.79m, fee.Expense);
        Assert.Equal(0m, fee.InputVat);
        var s = Assert.Single(p.SummarySales);
        Assert.Equal(0m, s.Vat);
        Assert.Equal(1070m, s.Net);
    }

    // ═════════════════ หัก ณ ที่จ่าย 3 โหมด (report-S1 D5 · W1–W3) ═════════════════

    [Fact]
    public void WHT_ตัวแทนหัก_คิดจากฐานก่อนVAT_ไม่มีขาJE_ติดป้ายว่าตัวแทนยื่น()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(wht: SettlementFeeWhtMode.AgentWithholds), true);
        var fee = Assert.Single(p.FeeDocuments);
        Assert.Equal(SettlementFeeWhtMode.AgentWithholds, fee.WhtMode);
        Assert.Equal(1.17m, fee.WhtAmount);            // 39.06 × 3% — ไม่ใช่ 41.79 × 3% = 1.25 (G-4)
        Assert.Equal(39.06m, Assert.Single(fee.Lines).WhtBase);
        Assert.DoesNotContain(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);
        Assert.Contains(p.Issues, i => i.Code == SettlementPlanIssueCode.WhtFiledByAgent && !i.Blocking);
    }

    [Fact]
    public void WHT_หักเองแพลตฟอร์มคืน_ตั้งลูกหนี้อื่นคู่21917()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdReimbursed), true);
        Assert.Equal(1.17m, Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtReimbursable).Debit);
        var pay = Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable);
        Assert.Equal(1.17m, pay.Credit);
        Assert.Equal("21917", pay.DefaultAccountCode);
        Assert.Equal(41.79m, Assert.Single(p.FeeDocuments).Deducted);   // จ่ายเต็มจากบัญชีพัก — ห้ามหักซ้ำตอนจ่าย
        AssertBalancedJournal(p);
        AssertClearingInvariant(p);
    }

    [Fact]
    public void WHT_ออกภาษีแทน_ฐานคูณ3ส่วน97_เงินได้บน50ทวิเท่าฐานบวกภาษี()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        var line = Assert.Single(Assert.Single(p.FeeDocuments).Lines);
        Assert.Equal(1.21m, line.WhtAmount);           // round(39.06 × 3/97) = 1.2080…
        Assert.Equal(40.27m, line.WhtCertIncome);
        var borne = Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.PaymentFee);
        Assert.Equal(1.21m, borne.Debit);
        Assert.Equal(1.21m, Assert.Single(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.WhtPayable).Credit);
        AssertBalancedJournal(p);
    }

    [Fact]
    public void WHT_บริษัทไม่จดVAT_ฐานยังเป็นก่อนVAT()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.21m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdReimbursed), false);
        Assert.Equal(1.17m, Assert.Single(p.FeeDocuments).WhtAmount);
    }

    // ═════════════════ ด่าน (ต้องบล็อกพร้อมทางไปต่อ) ═════════════════

    [Fact]
    public void ไม่ลงตัว_บล็อกพร้อมผลต่าง_แล้วบรรทัดปรับที่มีเหตุผลและผังทำให้ผ่าน()
    {
        var lines = new List<SettlementLine> { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) };
        var bad = SettlementBatchMath.Plan(Batch(1023.21m), lines, Channel(), true);
        Assert.False(bad.CanPost);
        var u = Assert.Single(bad.Issues, i => i.Code == SettlementPlanIssueCode.Unbalanced);
        Assert.Equal(5m, u.Amount);
        Assert.False(string.IsNullOrWhiteSpace(u.NextStep));

        var otherExpense = Guid.NewGuid();
        lines.Add(L(SettlementLineType.Adjustment, -5m, reason: "ค่าปรับส่งช้าตามอีเมลแพลตฟอร์ม", account: otherExpense));
        var fixedPlan = SettlementBatchMath.Plan(Batch(1023.21m), lines, Channel(), true);
        Assert.True(fixedPlan.CanPost);
        var adj = Assert.Single(fixedPlan.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Adjustment);
        Assert.Equal(otherExpense, adj.AccountId);
        Assert.Equal(5m, adj.Debit);
        AssertBalancedJournal(fixedPlan);
        AssertClearingInvariant(fixedPlan);
    }

    [Fact]
    public void ทิศตรงข้าม_batchที่ลงตัว_ไม่มีบรรทัดปรับ_ไม่มีปัญหาที่บล็อก_ผลต่างเศษสตางค์ในเกณฑ์ผ่าน()
    {
        var p = SettlementBatchMath.Plan(Batch(1028.20m),
            new[] { L(SettlementLineType.Sale, 1070m, DocA), L(SettlementLineType.PaymentFee, -41.79m) }, Channel(), true);
        Assert.True(p.CanPost);
        Assert.Empty(p.Issues);
        Assert.DoesNotContain(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Adjustment);
    }

    [Fact]
    public void บรรทัดปรับไม่มีเหตุผลหรือผัง_บล็อกทั้งสองข้อ()
    {
        var p = SettlementBatchMath.Plan(Batch(995m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Adjustment, -5m) }, Channel(), true);
        Assert.False(p.CanPost);
        Assert.Contains(p.Issues, i => i.Code == SettlementPlanIssueCode.AdjustmentReasonMissing);
        Assert.Contains(p.Issues, i => i.Code == SettlementPlanIssueCode.AdjustmentAccountMissing);
    }

    [Fact]
    public void บรรทัดยังไม่จัดประเภท_ห้ามลงบัญชี_ไม่ตกค่าใช้จ่ายอื่นเงียบ()
    {
        var p = SettlementBatchMath.Plan(Batch(990m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Unclassified, -10m) }, Channel(), true);
        Assert.False(p.CanPost);
        Assert.Single(p.Issues, i => i.Code == SettlementPlanIssueCode.Unclassified);
        Assert.Empty(p.FeeDocuments);
        Assert.DoesNotContain(p.PayoutJournal, l => l.AccountRole == SettlementAccountRoles.Adjustment);
    }

    [Fact]
    public void เครื่องหมายผิดประเภท_บล็อก_แถวศูนย์ไม่ใช่หลักฐาน()
    {
        var p = SettlementBatchMath.Plan(Batch(1100m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.ReserveHold, 100m), L(SettlementLineType.Commission, 0m) },
            Channel(), true);
        Assert.Single(p.Issues, i => i.Code == SettlementPlanIssueCode.SignNotAllowed);
        Assert.Empty(p.FeeDocuments);
    }

    [Fact]
    public void ยังไม่ผูกผังพักหรือบัญชีธนาคาร_บล็อก_สกุลต่างประเทศและโมเดลสุทธิยังไม่รองรับ()
    {
        var ch = Channel();
        ch.ClearingAccountId = null;
        ch.RevenueModel = SettlementRevenueModel.NetRate;
        var b = Batch(1000m, bank: false);
        b.Currency = "USD";
        var p = SettlementBatchMath.Plan(b, new[] { L(SettlementLineType.Sale, 1000m, DocA) }, ch, true);
        Assert.False(p.CanPost);
        foreach (var code in new[] { SettlementPlanIssueCode.ClearingAccountMissing, SettlementPlanIssueCode.BankAccountMissing,
                     SettlementPlanIssueCode.CurrencyNotSupported, SettlementPlanIssueCode.RevenueModelNotSupported })
            Assert.Contains(p.Issues, i => i.Code == code && i.Blocking && i.NextStep.Length > 0);
    }

    [Fact]
    public void ค่าธรรมเนียมคืนมากกว่าที่เก็บ_บล็อกให้บันทึกใบลดหนี้ค่าธรรมเนียม()
    {
        var p = SettlementBatchMath.Plan(Batch(20m), new[] { L(SettlementLineType.Commission, 20m) }, Channel(), true);
        Assert.False(p.CanPost);
        Assert.Single(p.Issues, i => i.Code == SettlementPlanIssueCode.FeeGroupNetRefund);
    }

    [Fact]
    public void ผังจากFeeAccountMap_ชนะผังมาตรฐาน()
    {
        var mapped = Guid.NewGuid();
        var ch = Channel(SettlementChannelKind.Marketplace);
        ch.FeeAccountMapJson = "{\"commission\":\"" + mapped + "\"}";
        var p = SettlementBatchMath.Plan(Batch(948.48m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -51.52m) }, ch, true);
        Assert.Equal(mapped, Assert.Single(Assert.Single(p.FeeDocuments).Lines).AccountId);
    }
}
