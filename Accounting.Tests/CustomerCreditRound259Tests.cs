using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 259 แพ็ก P4/P5 "เงินรอจัดสรร ต่อจนจบ" (DECISIONS 259-1-x) — ตัวตัดสิน pure ทั้งสองทิศ:
/// คืนเงิน/ตัดเศษ/ย้ายเป็นมัดจำ/จองให้ใบ (<see cref="CustomerCreditMovementRules"/>) · สูตรยอดคงเหลือรวมขาออก (<see cref="UnappliedCreditBalance"/>) ·
/// ค้น subset ตัวเดียว (<see cref="SubsetSum"/>) · ข้อเสนอบรรทัดเงินเข้า (<see cref="BankReceiptSplitSuggestion"/>)
/// </summary>
public class CustomerCreditRound259Tests
{
    private static readonly Guid Co = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Received = new(2026, 10, 20);
    private static readonly DateTime Today = new(2026, 11, 11);

    private static CustomerCreditFacts Credit(decimal unapplied = 600m, bool unknownOwner = false, bool voided = false, bool needsReview = false,
        bool thb = true, bool hasAccount = true, bool settlement = false)
        => new(Co, unknownOwner ? null : Customer, "PAY-202610-0007", Received, unapplied, hasAccount, voided, settlement, thb, needsReview);

    private static CustomerCreditMovementRefusal? Check(PaymentCreditMovementKind kind, decimal amount, decimal? available = null,
        CustomerCreditFacts? credit = null, DateTime? date = null, decimal cap = 1.00m, bool bank = true, string? evidence = "TRX-889",
        string? reason = "ลูกค้าโอนเกินเศษสตางค์")
    {
        var c = credit ?? Credit();
        return CustomerCreditMovementRules.Check(kind, c, available ?? c.UnappliedAmount, amount, date ?? Today, Today, cap, bank, evidence, reason);
    }

    // ───────── คืนเงิน ─────────

    [Fact]
    public void คืนเงิน_ครบหลักฐาน_ผ่าน_และบางส่วนก็ได้()
    {
        Assert.Null(Check(PaymentCreditMovementKind.Refund, 600m));
        Assert.Null(Check(PaymentCreditMovementKind.Refund, 250.50m));
    }

    [Fact]
    public void คืนเงิน_ไม่เลือกธนาคาร_ไม่มีหลักฐาน_เกินยอดที่ใช้ได้_ถูกปฏิเสธ()
    {
        Assert.Equal(CustomerCreditMovementRules.RefundRuleCode, Check(PaymentCreditMovementKind.Refund, 600m, bank: false)!.RuleCode);
        Assert.Equal(400, Check(PaymentCreditMovementKind.Refund, 600m, evidence: " ab ")!.StatusCode);
        // ยอดที่จองให้ใบร่างไม่นับเป็นยอดที่ใช้ได้ (คงเหลือ 600 · จองไป 400)
        var r = Check(PaymentCreditMovementKind.Refund, 300m, available: 200m)!;
        Assert.Equal(409, r.StatusCode);
        Assert.Contains("ใช้ได้", r.Message, StringComparison.Ordinal);
        Assert.Null(Check(PaymentCreditMovementKind.Refund, 200m, available: 200m));
    }

    [Fact]
    public void ขาออก_การชำระถูกยกเลิก_ต้องตรวจ_สกุลต่างประเทศ_ไม่มีผังพัก_รอบโอน_ถูกปฏิเสธทุกชนิด()
    {
        foreach (var kind in Enum.GetValues<PaymentCreditMovementKind>())
        {
            Assert.NotNull(Check(kind, 0.5m, credit: Credit(voided: true)));
            Assert.NotNull(Check(kind, 0.5m, credit: Credit(needsReview: true)));
            Assert.NotNull(Check(kind, 0.5m, credit: Credit(thb: false)));
            Assert.NotNull(Check(kind, 0.5m, credit: Credit(hasAccount: false)));
            Assert.NotNull(Check(kind, 0.5m, credit: Credit(settlement: true)));
        }
    }

    [Fact]
    public void ไม่รู้ลูกค้า_คืนเงิน_มัดจำ_ออกใบ_ถูกปฏิเสธ_แต่ตัดเศษได้()
    {
        var unknown = Credit(unapplied: 0.40m, unknownOwner: true);
        Assert.NotNull(Check(PaymentCreditMovementKind.Refund, 0.40m, credit: unknown));
        Assert.NotNull(Check(PaymentCreditMovementKind.ToDeposit, 0.40m, credit: unknown));
        Assert.NotNull(Check(PaymentCreditMovementKind.ToInvoice, 0.40m, credit: unknown));
        Assert.Null(Check(PaymentCreditMovementKind.WriteOff, 0.40m, credit: unknown));
    }

    [Fact]
    public void วันที่อนาคต_ก่อนวันรับเงิน_ยอดศูนย์_ทศนิยมเกินสองตำแหน่ง_ถูกปฏิเสธ()
    {
        Assert.NotNull(Check(PaymentCreditMovementKind.Refund, 100m, date: Today.AddDays(1)));
        Assert.NotNull(Check(PaymentCreditMovementKind.Refund, 100m, date: Received.AddDays(-1)));
        Assert.NotNull(Check(PaymentCreditMovementKind.Refund, 0m));
        Assert.NotNull(Check(PaymentCreditMovementKind.Refund, 100.005m));
        Assert.Null(Check(PaymentCreditMovementKind.Refund, 100m, date: Received));   // วันรับเงินเองได้
    }

    // ───────── ตัดเศษ ─────────

    [Fact]
    public void ตัดเศษ_ได้เฉพาะยอดที่เหลือทั้งหมดและไม่เกินเพดาน()
    {
        var crumb = Credit(unapplied: 0.75m);
        Assert.Null(Check(PaymentCreditMovementKind.WriteOff, 0.75m, credit: crumb));
        Assert.NotNull(Check(PaymentCreditMovementKind.WriteOff, 0.50m, credit: crumb));            // บางส่วน = ไม่ใช่เศษ
        var five = Credit(unapplied: 5m);
        Assert.Contains("เพดาน", Check(PaymentCreditMovementKind.WriteOff, 5m, credit: five)!.Message, StringComparison.Ordinal);
        Assert.Null(Check(PaymentCreditMovementKind.WriteOff, 5m, credit: five, cap: 5m));          // บริษัทเพิ่มเพดาน
        Assert.Equal(400, Check(PaymentCreditMovementKind.WriteOff, 0.75m, credit: crumb, reason: "")!.StatusCode);   // 258-1-10: ต้องมีเหตุผล
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(20.01, false)]
    [InlineData(-1, false)]
    [InlineData(1.005, false)]
    public void เพดานตัดเศษ_ตั้งได้_0ถึง20_สองตำแหน่ง(double cap, bool ok)
        => Assert.Equal(ok, CustomerCreditMovementRules.CapProblem((decimal)cap) == null);

    // ───────── สูตร + สถานะ ─────────

    [Fact]
    public void ตัดยอดคงเหลือ_เฉพาะคืนเงิน_มัดจำ_ตัดเศษ_การจองไม่ตัด()
    {
        Assert.True(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.Refund));
        Assert.True(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.ToDeposit));
        Assert.True(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.WriteOff));
        Assert.False(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.ToInvoice));
    }

    [Fact]
    public void ยอดคงเหลือ_หักการจัดสรรและขาออก_ไม่ติดลบ_และสูตรเดิมไม่เปลี่ยนเมื่อไม่มีขาออก()
    {
        Assert.Equal(5_350m, UnappliedCreditBalance.Compute(16_050m, 10_700m));
        Assert.Equal(5_350m, UnappliedCreditBalance.Compute(16_050m, 10_700m, 0m));
        Assert.Equal(350m, UnappliedCreditBalance.Compute(16_050m, 10_700m, 5_000m));
        Assert.Equal(0m, UnappliedCreditBalance.Compute(16_050m, 10_700m, 5_350m));
        Assert.Equal(0m, UnappliedCreditBalance.Compute(100m, 60m, 50m));
    }

    [Fact]
    public void การจอง_กันยอดเฉพาะใบร่างหรือรออนุมัติ_อนุมัติแล้ว_ลบ_ปล่อยแล้ว_ไม่กัน()
    {
        var k = PaymentCreditMovementKind.ToInvoice;
        Assert.True(CustomerCreditMovementRules.HoldsUsable(k, false, null, DocumentStatus.Draft));
        Assert.True(CustomerCreditMovementRules.HoldsUsable(k, false, null, DocumentStatus.WaitingApproval));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(k, false, null, DocumentStatus.Approved));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(k, false, null, DocumentStatus.Rejected));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(k, false, null, null));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(k, false, DateTime.UtcNow, DocumentStatus.Draft));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(k, true, null, DocumentStatus.Draft));
        Assert.False(CustomerCreditMovementRules.HoldsUsable(PaymentCreditMovementKind.Refund, false, null, DocumentStatus.Draft));
        Assert.Equal(200m, CustomerCreditMovementRules.Available(600m, 400m));
        Assert.Equal(0m, CustomerCreditMovementRules.Available(600m, 700m));
    }

    [Fact]
    public void เลขอ้างอิงJEขาออก_ไม่ขึ้นต้นด้วยเลขการชำระทับ_กันการยกเลิกการชำระกลับเงินที่คืนไปแล้ว()
    {
        var pn = "PAY-202610-0007";
        var r = CustomerCreditMovementRules.JournalReference(pn, PaymentCreditMovementKind.Refund, 2);
        Assert.Equal("PAY-202610-0007#R2", r);
        Assert.False(r.StartsWith(pn + "/", StringComparison.Ordinal));
        Assert.NotEqual(pn, r);
        Assert.Equal("PAY-202610-0007#W1", CustomerCreditMovementRules.JournalReference(pn, PaymentCreditMovementKind.WriteOff, 1));
    }

    [Fact]
    public void ข้อความบล็อกยกเลิกการชำระ_ระบุชนิดที่ยังมีผล()
    {
        var m = CustomerCreditMovementRules.VoidBlockMessage("PAY-1", new[] { PaymentCreditMovementKind.WriteOff, PaymentCreditMovementKind.Refund, PaymentCreditMovementKind.Refund });
        Assert.Contains("คืนเงินลูกค้า", m, StringComparison.Ordinal);
        Assert.Contains("ตัดเศษ", m, StringComparison.Ordinal);
        Assert.Contains("PAY-1", m, StringComparison.Ordinal);
    }

    // ───────── ม.78/1 ของใบมัดจำที่ย้ายมา ─────────

    [Fact]
    public void มัดจำย้ายมา_เงินล่วงหน้าคนละเดือน_มีธงล่าช้า_เดือนเดียวกัน_โอนเกิน_VATพัก_ไม่มีVAT_ไม่มีธง()
    {
        var oct = new DateTime(2026, 10, 20);
        var nov = new DateTime(2026, 11, 11);
        var note = CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Advance), oct, nov, 350m, vatDeferred: false);
        Assert.NotNull(note);
        Assert.StartsWith(DepositPolicyResolver.LateVatMarker, note!, StringComparison.Ordinal);
        Assert.Contains("10/2026", note!, StringComparison.Ordinal);
        Assert.NotNull(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.UninvoicedSupply), oct, nov, 350m, false));
        Assert.Null(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Advance), new DateTime(2026, 11, 2), nov, 350m, false));
        Assert.Null(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Overpaid), oct, nov, 350m, false));
        Assert.Null(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Unknown), oct, nov, 350m, false));
        Assert.Null(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Advance), oct, nov, 350m, vatDeferred: true));
        Assert.Null(CustomerCreditMovementRules.DepositLateVatNote(nameof(UnappliedReason.Advance), oct, nov, 0m, false));
    }

    // ───────── SubsetSum ─────────

    [Fact]
    public void SubsetSum_ชุดเดียว_สองชุด_ขนาดจำกัด_และงบหมด()
    {
        var one = SubsetSum.Find(new[] { 1_000m, 2_500m, 700m }, 3_200m, 0.005m, 2, 6);
        Assert.Single(one.Solutions);
        Assert.Equal(new[] { 1, 2 }, one.Solutions[0].ToArray());
        Assert.False(one.Exhausted);

        var two = SubsetSum.Find(new[] { 500m, 500m, 1_000m }, 1_000m, 0.005m, 2, 6);
        Assert.Single(two.Solutions);                                             // 1,000 เดี่ยวไม่นับ (ขั้นต่ำ 2 ใบ)
        var ambiguous = SubsetSum.Find(new[] { 500m, 500m, 300m, 700m }, 1_000m, 0.005m, 2, 6);
        Assert.Equal(2, ambiguous.Solutions.Count);

        Assert.Empty(SubsetSum.Find(new[] { 100m, 100m, 100m }, 300m, 0.005m, 2, 2).Solutions);   // ต้อง 3 ใบ แต่จำกัด 2
        var big = Enumerable.Repeat(1m, 30).ToArray();
        Assert.True(SubsetSum.Find(big, 1_000m, 0.005m, 2, 30, nodeBudget: 500).Exhausted);
    }

    // ───────── ข้อเสนอบรรทัดเงินเข้า ─────────

    private static BankReceiptSplitSuggestion.Doc D(string no, decimal expected, int day, bool cited = false)
        => new(Guid.NewGuid(), no, new DateTime(2026, 11, day), expected, cited);

    [Fact]
    public void เงินโอนก้อนเดียวเท่าสองใบของลูกค้าที่ระบุตัวได้_เสนอแบ่ง()
    {
        var c = new BankReceiptSplitSuggestion.ContactDocs(Customer, "บริษัท ลูกค้า ก", NameConfident: true,
            new[] { D("INV-1", 10_700m, 1), D("INV-2", 5_350m, 3), D("INV-3", 999m, 4) });
        var r = BankReceiptSplitSuggestion.Suggest(16_050m, new[] { c });
        var s = Assert.Single(r.Suggestions);
        Assert.Equal(BankReceiptSplitSuggestion.Kind.Split, s.Kind);
        Assert.Equal(new[] { "INV-1", "INV-2" }, s.Allocations.Select(a => a.DocumentNumber).ToArray());
        Assert.Equal(16_050m, s.Allocations.Sum(a => a.Amount));
        Assert.Null(r.Note);
    }

    [Fact]
    public void ไม่มีตัวระบุตัว_ไม่เสนอ_แม้ยอดรวมตรง()
    {
        var c = new BankReceiptSplitSuggestion.ContactDocs(Customer, "บริษัท ลูกค้า ก", NameConfident: false,
            new[] { D("INV-1", 10_700m, 1), D("INV-2", 5_350m, 3) });
        var r = BankReceiptSplitSuggestion.Suggest(16_050m, new[] { c });
        Assert.Empty(r.Suggestions);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void หลายชุดรวมได้ยอดเดียวกัน_กำกวม_ไม่เลือกชุด()
    {
        var c = new BankReceiptSplitSuggestion.ContactDocs(Customer, "ก", true,
            new[] { D("INV-1", 500m, 1), D("INV-2", 500m, 2), D("INV-3", 300m, 3), D("INV-4", 700m, 4) });
        var r = BankReceiptSplitSuggestion.Suggest(1_000m, new[] { c });
        Assert.DoesNotContain(r.Suggestions, s => s.Kind == BankReceiptSplitSuggestion.Kind.Split);
        Assert.Contains("หลายชุด", r.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void สองลูกค้าต่างมีชุดที่ตรง_กำกวม()
    {
        var a = new BankReceiptSplitSuggestion.ContactDocs(Guid.NewGuid(), "ก", true, new[] { D("A-1", 600m, 1), D("A-2", 400m, 2) });
        var b = new BankReceiptSplitSuggestion.ContactDocs(Guid.NewGuid(), "ข", true, new[] { D("B-1", 300m, 1), D("B-2", 700m, 2) });
        Assert.DoesNotContain(BankReceiptSplitSuggestion.Suggest(1_000m, new[] { a, b }).Suggestions, s => s.Kind == BankReceiptSplitSuggestion.Kind.Split);
    }

    [Fact]
    public void อ้างเลขใบเดียวที่ยอดน้อยกว่าเงินเข้า_เสนอใบนั้นพร้อมส่วนเกิน_ยอดพอดีหรือมากกว่า_ไม่เสนอ()
    {
        var c = new BankReceiptSplitSuggestion.ContactDocs(Customer, "ก", false, new[] { D("INV-9", 10_700m, 1, cited: true) });
        var r = BankReceiptSplitSuggestion.Suggest(16_050m, new[] { c });
        var s = Assert.Single(r.Suggestions);
        Assert.Equal(BankReceiptSplitSuggestion.Kind.InvoicePlusExcess, s.Kind);
        Assert.Equal(5_350m, s.Excess);

        Assert.Empty(BankReceiptSplitSuggestion.Suggest(10_700m, new[] { c }).Suggestions);
        Assert.Empty(BankReceiptSplitSuggestion.Suggest(9_000m, new[] { c }).Suggestions);
    }

    [Fact]
    public void ชุดที่ต้องใช้เกินหกใบ_ไม่เสนอ()
    {
        var docs = Enumerable.Range(1, 7).Select(i => D("INV-" + i, 100m, i)).ToArray();
        var c = new BankReceiptSplitSuggestion.ContactDocs(Customer, "ก", true, docs);
        Assert.Empty(BankReceiptSplitSuggestion.Suggest(700m, new[] { c }).Suggestions);
        // 600 จาก 100 × 7 ใบ = หลายชุด ⇒ กำกวม ไม่เลือกชุดให้
        var r = BankReceiptSplitSuggestion.Suggest(600m, new[] { c });
        Assert.Empty(r.Suggestions);
        Assert.Contains("หลายชุด", r.Note!, StringComparison.Ordinal);
    }
}
