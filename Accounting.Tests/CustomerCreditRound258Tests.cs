using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" (DECISIONS 258-1-x · erp-review/2026-11-11/team-D-lump-payment.md §3.3/§3.9) — ตัวตัดสิน pure ทั้งสองทิศ:
/// ลูกค้าโอน 16,050 ปิดใบ 10,700 + ยอด 5,350 ที่ยังไม่ออกใบ ⇒ เงินเกิน 5,350 เก็บได้เมื่อมีเหตุผล · จัดสรรทีหลังได้เฉพาะใบของลูกค้ารายเดียวกัน ·
/// ทิศตรงข้าม: ยอดพอดี/เกินไม่เกิน 0.01 = ไม่มีเงินเกิน (พฤติกรรมเดิม) · ไม่มีเงินรอจัดสรร = ทวงตามเดิม · ฝั่งจ่าย/สกุลต่างประเทศ/ไม่มีเหตุผล = ปฏิเสธ
/// </summary>
public class CustomerCreditRound258Tests
{
    private static readonly Guid Co = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Received = new(2026, 11, 3);
    private static readonly DateTime Today = new(2026, 11, 20);
    private const string Uninvoiced = nameof(UnappliedReason.UninvoicedSupply);

    private static CustomerCreditFacts Credit(decimal unapplied = 5_350m, Guid? contact = null, bool unknownOwner = false,
        bool hasAccount = true, bool voided = false, bool settlement = false, bool thb = true, Guid? company = null)
        => new(company ?? Co, unknownOwner ? null : contact ?? Customer, "PAY-202611-0001", Received, unapplied,
            hasAccount, voided, settlement, thb);

    private static CustomerCreditTargetFacts Target(decimal balance = 5_350m, Guid? contact = null,
        DocumentType type = DocumentType.Invoice, DocumentStatus status = DocumentStatus.Approved, DateTime? date = null,
        string? currency = "THB", decimal rate = 1m, Guid? company = null)
        => new(company ?? Co, contact ?? Customer, "INV-0002", type, status, date ?? new DateTime(2026, 11, 10), balance, currency, rate);

    private static CustomerCreditRefusal? Check(CustomerCreditFacts c, CustomerCreditTargetFacts t, decimal amount, DateTime? apply = null)
        => CustomerCreditAllocation.Check(c, t, amount, apply ?? Today, Today);

    // ───────── PaymentExcessPlan ─────────

    [Fact]
    public void เงินเกิน_โอนสองยอดใบเดียว_ได้ส่วนเกินเท่ายอดที่ยังไม่ออกใบ()
    {
        Assert.Equal(5_350m, PaymentExcessPlan.Excess(16_050m, 0m, 10_700m));
        Assert.Equal(0.02m, PaymentExcessPlan.Excess(100.02m, 0m, 100m));
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดพอดี_เกินไม่เกินหนึ่งสตางค์_ค่าธรรมเนียมรวมแล้วพอดี_ไม่มีเงินเกิน()
    {
        Assert.Equal(0m, PaymentExcessPlan.Excess(10_700m, 0m, 10_700m));
        Assert.Equal(0m, PaymentExcessPlan.Excess(10_700.01m, 0m, 10_700m));   // ด่านเดิม WouldOverpay ยอมเกิน 0.01
        Assert.Equal(0m, PaymentExcessPlan.Excess(5_000m, 0m, 10_700m));        // ชำระบางส่วน
        Assert.Equal(0m, PaymentExcessPlan.Excess(10_000m, 700m, 10_700m));     // เงินสุทธิ + ค่าธรรมเนียม = ยอดค้าง
    }

    [Fact]
    public void เก็บเงินเกิน_ต้องฝั่งรับ_สกุลบาท_ไม่มีค่าธรรมเนียม_มีเหตุผล_จดVatห้ามค้างเงินล่วงหน้า()
    {
        Assert.Null(PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.UninvoicedSupply));
        Assert.Null(PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.Overpaid));
        Assert.Null(PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.Unknown));
        Assert.Null(PaymentExcessPlan.RefuseExcess(true, true, false, false, UnappliedReason.Advance));   // ไม่จด VAT เก็บเงินล่วงหน้าได้

        Assert.Contains("ฝั่งจ่ายเจ้าหนี้", PaymentExcessPlan.RefuseExcess(false, true, false, true, UnappliedReason.Unknown)!, StringComparison.Ordinal);
        Assert.Contains("สกุลเงินต่างประเทศ", PaymentExcessPlan.RefuseExcess(true, false, false, true, UnappliedReason.Unknown)!, StringComparison.Ordinal);
        Assert.Contains("ค่าธรรมเนียม", PaymentExcessPlan.RefuseExcess(true, true, true, true, UnappliedReason.Unknown)!, StringComparison.Ordinal);
        Assert.Contains("เลือกเหตุผล", PaymentExcessPlan.RefuseExcess(true, true, false, true, null)!, StringComparison.Ordinal);
        Assert.Equal(PaymentExcessPlan.AdvanceVatBlocked, PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.Advance));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ตัวเลือกเหตุผลที่หน้าเว็บแสดง_ตรงกับที่การบันทึกรับ(bool vatRegistered)
    {
        var options = PaymentExcessPlan.ReasonOptions(vatRegistered);
        Assert.Equal(4, options.Count);
        foreach (var o in options)
        {
            var reason = Enum.Parse<UnappliedReason>(o.Reason);
            var refusal = PaymentExcessPlan.RefuseExcess(true, true, false, vatRegistered, reason);
            Assert.Equal(o.Allowed, refusal == null);                 // เลือกได้บนหน้า ⇔ เซิร์ฟเวอร์รับ
            Assert.Equal(o.Allowed, o.BlockedReason == null);
            Assert.Equal(PaymentExcessPlan.Label(reason), o.Label);
        }
        Assert.Equal(!vatRegistered, options.Single(o => o.Reason == nameof(UnappliedReason.Advance)).Allowed);
    }

    [Fact]
    public void กระจายเงินเกินให้ใบค้างอื่น_เก่าสุดก่อน_ไม่เกินเงินเกิน()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var rows = PaymentExcessPlan.SuggestSplit(5_000m, new[] { (a, 3_000m), (b, 4_000m), (c, 1_000m) });
        Assert.Equal(new[] { 3_000m, 2_000m, 0m }, rows.Select(r => r.Suggested).ToArray());
        Assert.True(rows.Sum(r => r.Suggested) <= 5_000m);
        Assert.All(PaymentExcessPlan.SuggestSplit(0m, new[] { (a, 3_000m) }), r => Assert.Equal(0m, r.Suggested));
    }

    [Fact]
    public void ป้ายเหตุผล_ค่าที่ไม่รู้จักหรือว่าง_เป็นยังไม่ทราบ()
    {
        Assert.Equal("ค้างออกใบแจ้งหนี้/ใบกำกับ", PaymentExcessPlan.LabelOf("UninvoicedSupply"));
        Assert.Equal("ยังไม่ทราบ", PaymentExcessPlan.LabelOf(null));
        Assert.Equal("ยังไม่ทราบ", PaymentExcessPlan.LabelOf("อะไรก็ไม่รู้"));
        Assert.Equal("ยังไม่ทราบ", PaymentExcessPlan.LabelOf("99"));
    }

    [Fact]
    public void ออกใบเสร็จอัตโนมัติไม่ได้_ป้ายบนใบมีเครื่องหมายคิวและทางไปต่อ()
    {
        var note = PaymentExcessPlan.ReceiptPendingNote("INV-0002", "PAY-202611-0001", "ผู้บันทึกไม่มีสิทธิ์");
        Assert.StartsWith(PaymentExcessPlan.ReceiptPendingMarker, note, StringComparison.Ordinal);
        Assert.Contains("ออกใบเสร็จ", note, StringComparison.Ordinal);
        Assert.Contains("PAY-202611-0001", note, StringComparison.Ordinal);
    }

    [Fact]
    public void M2_เงินเกินพร้อมบรรทัดปรับ_ปฏิเสธ_และยอดที่หน้าจอเห็นต้องตรงกับที่เซิร์ฟเวอร์คิด()
    {
        Assert.Contains("บรรทัดปรับ", PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.Unknown, hasAdjustments: true)!, StringComparison.Ordinal);
        Assert.Null(PaymentExcessPlan.RefuseExcess(true, true, false, true, UnappliedReason.Unknown, hasAdjustments: false));
        // ยอดค้าง 1,000 · เงิน 1,500 · คูปอง −50: แผงแสดงเงินเกิน 550 แต่คำขอจริงมีเงินเกิน 500 ⇒ ปฏิเสธ
        Assert.Contains("ไม่ตรง", PaymentExcessPlan.ExpectedExcessMismatch(550m, 500m)!, StringComparison.Ordinal);
        Assert.Null(PaymentExcessPlan.ExpectedExcessMismatch(500m, 500m));
        Assert.Null(PaymentExcessPlan.ExpectedExcessMismatch(null, 500m));      // API เดิมไม่ส่ง = ไม่ตรวจ
        Assert.NotNull(PaymentExcessPlan.ExpectedExcessMismatch(0m, 500m));     // หน้าจอคิดว่าไม่เหลือ แต่จริงเหลือ
    }

    // ───────── M5 ด่านใบแจ้งหนี้ที่มี VAT ─────────

    [Fact]
    public void M5_ใบแจ้งหนี้มีVat_ปิดบางส่วนถูกปฏิเสธ_ปิดเต็มได้_ใบกำกับและใบไม่มีVatไม่ถูกแตะ()
    {
        Assert.Contains("แปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน",
            VatInvoiceSettlementGate.PartialRefusal(DocumentType.Invoice, 700m, 500m, 10_700m, "INV-1")!, StringComparison.Ordinal);
        Assert.Null(VatInvoiceSettlementGate.PartialRefusal(DocumentType.Invoice, 700m, 10_700m, 10_700m, "INV-1"));       // เต็มยอดค้าง
        Assert.Null(VatInvoiceSettlementGate.PartialRefusal(DocumentType.Invoice, 700m, 10_699.99m, 10_700m, "INV-1"));    // ขาดไม่เกิน 0.01
        Assert.Null(VatInvoiceSettlementGate.PartialRefusal(DocumentType.Invoice, 0m, 500m, 10_000m, "INV-1"));            // ไม่มี VAT
        Assert.Null(VatInvoiceSettlementGate.PartialRefusal(DocumentType.TaxInvoice, 700m, 500m, 10_700m, "IV-1"));        // ใบกำกับออกแล้ว
        // การจัดสรรเดินด่านเดียวกัน
        var partial = Check(Credit(unapplied: 500m), Target(balance: 10_700m) with { VatAmount = 700m }, 500m);
        Assert.Equal(VatInvoiceSettlementGate.RuleCode, partial!.RuleCode);
        Assert.Null(Check(Credit(unapplied: 10_700m), Target(balance: 10_700m) with { VatAmount = 700m }, 10_700m));
    }

    // ───────── M4 / M7 ─────────

    [Fact]
    public void M4_เงินก่อนรอบ258ที่ยังต้องตรวจ_จัดสรรไม่ได้จนกว่าจะยืนยัน()
    {
        var refusal = Check(Credit() with { NeedsReview = true }, Target(), 100m);
        Assert.Equal(CustomerCreditAllocation.NeedsReviewRuleCode, refusal!.RuleCode);
        Assert.Contains("ยืนยันว่ายังค้างจริง", refusal.Message, StringComparison.Ordinal);
        Assert.Null(Check(Credit() with { NeedsReview = false }, Target(), 100m));
    }

    [Fact]
    public void M7_ยอดที่หน้าจอเห็นไม่ตรงกับของจริงใต้ล็อก_ปฏิเสธ409_ตรงหรือไม่ส่งมา_ผ่าน()
    {
        // เงิน 2,000 · กดจัดสรร 500 สองครั้งติดกัน: ครั้งที่สองยังส่ง "เห็น 2,000" แต่ของจริงเหลือ 1,500
        var stale = CustomerCreditAllocation.StaleCheck(2_000m, 1_500m, "PAY-202611-0001");
        Assert.Equal((CustomerCreditAllocation.StaleRuleCode, 409), (stale!.RuleCode, stale.StatusCode));
        Assert.Null(CustomerCreditAllocation.StaleCheck(2_000m, 2_000m, "PAY-202611-0001"));
        Assert.Null(CustomerCreditAllocation.StaleCheck(null, 1_500m, "PAY-202611-0001"));
    }

    // ───────── M3 ─────────

    [Fact]
    public void M3_วันที่รับชำระที่ระบบประทับ_ไม่สืบทอดไปเอกสารลูก_ค่าที่ผู้ใช้กรอกสืบทอดตามเดิม()
    {
        var d = new DateTime(2026, 11, 28);
        Assert.Null(SystemStampedPaymentDate.InheritToChild(d, stampedBySystem: true));
        Assert.Equal(d, SystemStampedPaymentDate.InheritToChild(d, stampedBySystem: false));
        Assert.Null(SystemStampedPaymentDate.InheritToChild(null, stampedBySystem: false));
    }

    // ───────── UnappliedCreditBalance ─────────

    [Fact]
    public void ยอดคงเหลือ_ยอดรับลบการจัดสรรที่มีผล_ปัดAwayFromZero_ไม่ติดลบ()
    {
        Assert.Equal(5_350m, UnappliedCreditBalance.Compute(16_050m, 10_700m));
        Assert.Equal(0m, UnappliedCreditBalance.Compute(16_050m, 16_050m));
        Assert.Equal(0m, UnappliedCreditBalance.Compute(100m, 100.01m));      // จัดสรรเกินได้ไม่เกิน 0.01 (ด่านเดิม) ⇒ ไม่ติดลบ
        Assert.Equal(10.01m, UnappliedCreditBalance.Compute(10.005m, 0m));    // AwayFromZero (banker's ได้ 10.00)
        Assert.False(UnappliedCreditBalance.HasBalance(0.005m));
        Assert.True(UnappliedCreditBalance.HasBalance(0.01m));
    }

    // ───────── CustomerCreditAllocation ─────────

    [Fact]
    public void จัดสรรให้ใบที่ออกทีหลังของลูกค้ารายเดียวกัน_ผ่าน()
    {
        Assert.Null(Check(Credit(), Target(), 5_350m));                                   // พอดีทั้งสองฝั่ง
        Assert.Null(Check(Credit(), Target(balance: 9_000m), 5_350m));                    // ปิดบางส่วนของใบ
        Assert.Null(Check(Credit(), Target(balance: 2_000m), 2_000m));                    // ใช้เงินบางส่วน
        Assert.Null(Check(Credit(), Target(type: DocumentType.TaxInvoice, status: DocumentStatus.Overdue), 100m));
        Assert.Null(Check(Credit(), Target(type: DocumentType.DebitNote, status: DocumentStatus.PartiallyPaid), 100m));
        Assert.Null(Check(Credit(), Target(currency: null, rate: 1m), 100m));             // สกุลว่าง = บาท
        Assert.Null(Check(Credit(), Target(balance: 100m), 100.01m));                     // ด่านเดิม: เกินยอดค้างได้ไม่เกิน 0.01
    }

    [Fact]
    public void ผู้ติดต่อคนละราย_หรือไม่รู้เจ้าของเงิน_ปฏิเสธ409พร้อมรหัสกฎ()
    {
        var other = Check(Credit(), Target(contact: Guid.NewGuid()), 100m);
        Assert.Equal((CustomerCreditAllocation.ContactRuleCode, 409), (other!.RuleCode, other.StatusCode));
        Assert.Contains("ลูกค้าอีกราย", other.Message, StringComparison.Ordinal);
        var unknown = Check(Credit(unknownOwner: true), Target(), 100m);
        Assert.Equal(CustomerCreditAllocation.ContactRuleCode, unknown!.RuleCode);
    }

    [Fact]
    public void คนละบริษัท_ตอบเหมือนไม่พบเอกสาร()
        => Assert.Equal(404, Check(Credit(), Target(company: Guid.NewGuid()), 100m)!.StatusCode);

    [Fact]
    public void การชำระถูกยกเลิก_ของรอบโอน_ไม่มีผังพัก_หรือไม่เหลือเงิน_ปฏิเสธ()
    {
        Assert.Contains("ถูกยกเลิกแล้ว", Check(Credit(voided: true), Target(), 100m)!.Message, StringComparison.Ordinal);
        Assert.Contains("รอบโอน", Check(Credit(settlement: true), Target(), 100m)!.Message, StringComparison.Ordinal);
        Assert.Contains("ไม่มีเงินรอจัดสรรคงเหลือ", Check(Credit(hasAccount: false), Target(), 100m)!.Message, StringComparison.Ordinal);
        Assert.Contains("ไม่มีเงินรอจัดสรรคงเหลือ", Check(Credit(unapplied: 0m), Target(), 100m)!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.CreditNote)]
    public void ชนิดเอกสารที่ไม่ใช่ใบตั้งลูกหนี้ฝั่งขาย_ปฏิเสธ(DocumentType type)
        => Assert.Contains("รับการจัดสรรเงินรอจัดสรรไม่ได้", Check(Credit(), Target(type: type), 100m)!.Message, StringComparison.Ordinal);

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Voided)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.WaitingApproval)]
    public void สถานะที่รับชำระไม่ได้_ปฏิเสธ(DocumentStatus status)
        => Assert.Contains("รับชำระไม่ได้", Check(Credit(), Target(status: status), 100m)!.Message, StringComparison.Ordinal);

    [Fact]
    public void สกุลเงินต่างประเทศฝั่งใดฝั่งหนึ่ง_ปฏิเสธ()
    {
        Assert.Contains("สกุลบาทเท่านั้น", Check(Credit(), Target(currency: "USD", rate: 35m), 100m)!.Message, StringComparison.Ordinal);
        Assert.Contains("สกุลบาทเท่านั้น", Check(Credit(thb: false), Target(), 100m)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ยอดผิด_ศูนย์_ทศนิยมเกิน_เกินคงเหลือ_เกินยอดค้าง_ปฏิเสธ()
    {
        Assert.Equal(400, Check(Credit(), Target(), 0m)!.StatusCode);
        Assert.Equal(400, Check(Credit(), Target(), 10.005m)!.StatusCode);
        Assert.Contains("มากกว่าเงินรอจัดสรรคงเหลือ", Check(Credit(unapplied: 1_000m), Target(), 1_000.01m)!.Message, StringComparison.Ordinal);
        Assert.Contains("มากกว่ายอดค้าง", Check(Credit(), Target(balance: 100m), 100.02m)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void วันที่จัดสรร_ก่อนวันรับเงิน_ก่อนวันที่ใบ_หรืออนาคต_ปฏิเสธ()
    {
        Assert.Contains("ก่อนวันรับเงิน", Check(Credit(), Target(date: new DateTime(2026, 10, 1)), 100m, new DateTime(2026, 11, 2))!.Message, StringComparison.Ordinal);
        Assert.Contains("ก่อนวันที่ของใบ", Check(Credit(), Target(date: new DateTime(2026, 11, 10)), 100m, new DateTime(2026, 11, 9))!.Message, StringComparison.Ordinal);
        Assert.Contains("อนาคต", Check(Credit(), Target(), 100m, Today.AddDays(1))!.Message, StringComparison.Ordinal);
        Assert.Null(Check(Credit(), Target(date: new DateTime(2026, 11, 10)), 100m, new DateTime(2026, 11, 10)));   // วันเดียวกับใบ = ได้
    }

    [Fact]
    public void เลขอ้างอิงJEของการจัดสรร_ขึ้นต้นด้วยเลขการชำระตามด้วยทับ_ให้การยกเลิกการชำระหาเจอ()
    {
        var reference = CustomerCreditAllocation.JournalReference("PAY-202611-0001", 2);
        Assert.Equal("PAY-202611-0001/A2", reference);
        Assert.StartsWith("PAY-202611-0001/", reference, StringComparison.Ordinal);
    }

    // ───────── DunningSuppression ─────────

    [Fact]
    public void เงินรอจัดสรรพอปิดใบ_ไม่ทวงลูกค้า_น้อยกว่า_ทวงตามเดิมพร้อมข้อสังเกต()
    {
        var covers = DunningSuppression.Decide(5_350m, 5_350m);
        Assert.True(covers.SuppressCustomerReminder);
        Assert.Contains("ใช้เงินรอจัดสรร", covers.Reason!, StringComparison.Ordinal);
        Assert.True(DunningSuppression.Decide(5_350m, 9_000m).SuppressCustomerReminder);

        var partial = DunningSuppression.Decide(5_350m, 1_000m);
        Assert.False(partial.SuppressCustomerReminder);
        Assert.NotNull(partial.Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีเงินรอจัดสรร_ทวงตามเดิมทุกประการ()
    {
        Assert.Equal(new DunningSuppressionDecision(false, null), DunningSuppression.Decide(5_350m, 0m));
        Assert.Equal(new DunningSuppressionDecision(false, null), DunningSuppression.Decide(5_350m, 0.005m));
        Assert.Equal(new DunningSuppressionDecision(false, null), DunningSuppression.Decide(0m, 1_000m));
    }

    // ───────── LateReceiptTaxPoint ─────────

    [Fact]
    public void งวดภาษี_รับเงินและจัดสรรเดือนเดียวกัน_ไม่มีธง_ไม่มีข้อความ()
    {
        var d = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 11, 3), new DateTime(2026, 11, 20), false, new DateTime(2026, 11, 20));
        Assert.False(d.LateFlag);
        Assert.Null(d.Note);
        Assert.Equal((2026, 11), (d.TaxPointDate.Year, d.TaxPointDate.Month));
    }

    [Fact]
    public void งวดภาษี_รับเงินเดือนก่อน_ยังไม่เลยกำหนดยื่นและไม่ล็อก_เข้างวดของวันรับเงิน()
    {
        var d = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 10, 28), new DateTime(2026, 11, 5), false, new DateTime(2026, 11, 5));
        Assert.Equal(new DateTime(2026, 10, 28), d.TaxPointDate);
        Assert.False(d.LateFlag);
        Assert.Contains("10/2026", d.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void งวดภาษี_งวดของวันรับเงินยื่นหรือล็อกแล้ว_หรือเลยกำหนดยื่น_เข้างวดปัจจุบันพร้อมธง()
    {
        var locked = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 10, 28), new DateTime(2026, 11, 5), true, new DateTime(2026, 11, 5));
        Assert.Equal(new DateTime(2026, 11, 5), locked.TaxPointDate);
        Assert.True(locked.LateFlag);
        Assert.Contains(DepositPolicyResolver.LateVatMarker, locked.Note!, StringComparison.Ordinal);
        Assert.Contains("89/1", locked.Note!, StringComparison.Ordinal);

        var late = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 9, 28), new DateTime(2026, 11, 20), false, new DateTime(2026, 11, 20));
        Assert.Equal(new DateTime(2026, 11, 20), late.TaxPointDate);
        Assert.True(late.LateFlag);
    }

    [Fact]
    public void ใบที่ภาษีไม่ได้พัก_เงินเข้าก่อนเดือนของใบ_ได้คำเตือน_นอกนั้นเงียบ()
    {
        var note = LateReceiptTaxPoint.IssuedAfterReceiptNote(Uninvoiced, new DateTime(2026, 10, 28), new DateTime(2026, 11, 5), 350m);
        Assert.Contains(DepositPolicyResolver.LateVatMarker, note!, StringComparison.Ordinal);
        Assert.Contains("10/2026", note!, StringComparison.Ordinal);
        Assert.Null(LateReceiptTaxPoint.IssuedAfterReceiptNote(Uninvoiced, new DateTime(2026, 11, 3), new DateTime(2026, 11, 5), 350m));    // เดือนเดียวกัน
        Assert.Null(LateReceiptTaxPoint.IssuedAfterReceiptNote(Uninvoiced, new DateTime(2026, 10, 28), new DateTime(2026, 11, 5), 0m));     // ไม่มี VAT
        Assert.Null(LateReceiptTaxPoint.IssuedAfterReceiptNote(Uninvoiced, new DateTime(2026, 12, 2), new DateTime(2026, 11, 5), 350m));    // เงินเข้าหลังออกใบ
    }

    // ───────── H1: งวดภาษีขึ้นกับเหตุผลของเงิน (สถานการณ์ของฝ่ายค้าน) ─────────

    [Theory]
    [InlineData(nameof(UnappliedReason.Overpaid))]
    [InlineData(nameof(UnappliedReason.Advance))]
    [InlineData(nameof(UnappliedReason.Unknown))]
    [InlineData(null)]
    [InlineData("อะไรก็ไม่รู้")]
    public void H1_ก_เงินที่ไม่ใช่ค้างออกใบ_รับ28พย_จัดสรรให้ใบบริการ5ธค_ภาษีเข้างวดธค_ไม่ย้อนไปพย(string? reason)
    {
        var d = LateReceiptTaxPoint.Decide(reason, new DateTime(2026, 11, 28), new DateTime(2026, 12, 5), false, new DateTime(2026, 12, 5));
        Assert.Equal(new DateTime(2026, 12, 5), d.TaxPointDate);
        Assert.False(d.LateFlag);
        Assert.Null(d.Note);
        Assert.False(LateReceiptTaxPoint.ReceiptDrivesTaxPoint(reason));
        Assert.Equal(new DateTime(2026, 12, 5), LateReceiptTaxPoint.EffectiveReceiptDate(reason, new DateTime(2026, 11, 28), new DateTime(2026, 12, 5)));
    }

    [Fact]
    public void H1_ข_เงินโอนเกินตั้งแต่สค_ตัดใบบริการธค_ไม่มีธงภาษีล่าช้าเท็จ_แม้งวดสคยื่นแล้ว()
    {
        var d = LateReceiptTaxPoint.Decide(nameof(UnappliedReason.Overpaid), new DateTime(2026, 8, 10), new DateTime(2026, 12, 5), true, new DateTime(2026, 12, 5));
        Assert.Equal((new DateTime(2026, 12, 5), false, (string?)null), (d.TaxPointDate, d.LateFlag, d.Note));
        Assert.Null(LateReceiptTaxPoint.IssuedAfterReceiptNote(nameof(UnappliedReason.Overpaid), new DateTime(2026, 8, 10), new DateTime(2026, 12, 5), 700m));
    }

    [Fact]
    public void H1_ทิศตรงข้าม_เงินค้างออกใบ_วันรับเงินยังเป็นจุดความรับผิด_และธงล่าช้าเมื่องวดนั้นยื่นแล้ว()
    {
        Assert.True(LateReceiptTaxPoint.ReceiptDrivesTaxPoint(Uninvoiced));
        Assert.Equal(new DateTime(2026, 11, 28), LateReceiptTaxPoint.EffectiveReceiptDate(Uninvoiced, new DateTime(2026, 11, 28), new DateTime(2026, 12, 5)));
        var open = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 11, 28), new DateTime(2026, 12, 5), false, new DateTime(2026, 12, 5));
        Assert.Equal((new DateTime(2026, 11, 28), false), (open.TaxPointDate, open.LateFlag));
        var filed = LateReceiptTaxPoint.Decide(Uninvoiced, new DateTime(2026, 8, 10), new DateTime(2026, 12, 5), true, new DateTime(2026, 12, 5));
        Assert.Equal((new DateTime(2026, 12, 5), true), (filed.TaxPointDate, filed.LateFlag));
    }

    // ───────── M1: ระงับทวงแบบหักเงินสะสม ─────────

    [Fact]
    public void M1_เงิน1000_ใบ5ใบใบละ900_ระงับเฉพาะใบเก่าสุดใบเดียว_เงินแต่ละบาทนับครั้งเดียว()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var held = DunningSuppression.Plan(1_000m, ids.Select(id => new DunningSuppression.OpenDocument(id, 900m, true)));
        Assert.Equal(new[] { ids[0] }, held.Keys.ToArray());
    }

    [Fact]
    public void M1_ใบที่จัดสรรไม่ได้ไม่ถูกระงับและไม่กินเงิน_ใบที่เงินเหลือไม่พอถูกข้าม_ใบถัดไปที่พอยังถูกระงับ()
    {
        var usd = Guid.NewGuid(); var big = Guid.NewGuid(); var small = Guid.NewGuid();
        var held = DunningSuppression.Plan(600m, new[]
        {
            new DunningSuppression.OpenDocument(usd, 500m, Allocatable: false),   // สกุลต่างประเทศ/ไม่มีสมุดรายวันตั้งลูกหนี้
            new DunningSuppression.OpenDocument(big, 700m, Allocatable: true),    // เงินไม่พอปิดเต็ม ⇒ ทวงตามเดิม
            new DunningSuppression.OpenDocument(small, 600m, Allocatable: true),
        });
        Assert.Equal(new[] { small }, held.Keys.ToArray());
        Assert.Empty(DunningSuppression.Plan(0m, new[] { new DunningSuppression.OpenDocument(small, 600m, true) }));   // ทิศตรงข้าม: ไม่มีเงิน = ไม่ระงับ
    }

    // ───────── ผัง 21630 ─────────

    [Fact]
    public void ผัง21630_อยู่ในแม่แบบใต้กลุ่ม216_เป็นผังเฉพาะกิจ_และมีSQLใส่ให้บริษัทเดิม()
    {
        var row = Accounting.Services.ChartOfAccountTemplates.GetCommonAccounts().Single(a => a.Code == UnappliedReceiptAccount.Code);
        Assert.Equal(AccountType.Liability, row.Type);
        Assert.Equal("216", SettlementChartSeed.ParentGroupCode(row.Code));
        Assert.Contains(UnappliedReceiptAccount.Code, SpecialPurposeAccount.Codes);
        Assert.Contains("'21630'", UnappliedReceiptAccount.MigrationSeedSql(), StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT DO NOTHING", UnappliedReceiptAccount.MigrationSeedSql(), StringComparison.Ordinal);
    }

    [Fact]
    public void คำสั่งย้ายฐาน_idempotent_และbackfillอยู่ใต้marker()
    {
        var statements = CustomerCreditMigration.Statements();
        Assert.All(statements.Where(s => s.Contains("ADD COLUMN", StringComparison.Ordinal)),
            s => Assert.Contains("ADD COLUMN IF NOT EXISTS", s, StringComparison.Ordinal));
        var backfill = CustomerCreditMigration.BackfillSql();
        Assert.Contains("'" + CustomerCreditMigration.BackfillMarkerKey + "'", backfill, StringComparison.Ordinal);
        Assert.Contains("\"DepositAppliedToDocumentId\" IS NULL", backfill, StringComparison.Ordinal);   // P-2: แถวที่ถูกใช้ไปแล้วไม่แตะ
        Assert.Contains(CustomerCreditMigration.ExcessJournalDescriptionPrefix + "%", backfill, StringComparison.Ordinal);
    }
}
