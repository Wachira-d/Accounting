using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 265 (คำตัดสิน 265-1-1…6) — ออกใบเสร็จ/ใบกำกับภาษีจากเงินรอจัดสรรโดยไม่มีใบแจ้งหนี้: ตัวตัดสินเดียว <see cref="CreditReceiptPlan"/> +
/// ขาออกชนิดใหม่ <see cref="PaymentCreditMovementKind.ToReceipt"/> — ทั้งทิศ "ทำได้" และ "ถูกปฏิเสธ"
/// </summary>
public class CustomerCreditRound265Tests
{
    private static CreditReceiptLineFact Line(string? d = "ค่าบริการเดือน ต.ค.", decimal q = 1m, decimal p = 5_000m, decimal vat = 0m, decimal wht = 0m)
        => new(d, q, p, vat, wht);

    [Fact]
    public void ToReceipt_ตัดยอดคงเหลือ_และยกเลิกที่หน้าเอกสาร_ไม่ใช่ปุ่มยกเลิกรายการ()
    {
        Assert.True(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.ToReceipt));
        Assert.True(CustomerCreditMovementRules.IssuesApprovedDocument(PaymentCreditMovementKind.ToReceipt));
        Assert.True(CustomerCreditMovementRules.IssuesApprovedDocument(PaymentCreditMovementKind.ToDeposit));
        // ทิศตรงข้าม: การจองให้ใบร่างยังไม่ตัดยอด · คืนเงิน/ตัดเศษยกเลิกที่หน้านี้
        Assert.False(CustomerCreditMovementRules.ReducesBalance(PaymentCreditMovementKind.ToInvoice));
        Assert.False(CustomerCreditMovementRules.IssuesApprovedDocument(PaymentCreditMovementKind.Refund));
        Assert.False(CustomerCreditMovementRules.IssuesApprovedDocument(PaymentCreditMovementKind.ToInvoice));
        Assert.Equal(PaymentCreditMovementKind.ToReceipt, CustomerCreditMovementRules.Parse("ToReceipt"));
        Assert.Contains("ใบเสร็จ", CustomerCreditMovementRules.LabelOf(PaymentCreditMovementKind.ToReceipt), StringComparison.Ordinal);
    }

    [Fact]
    public void ตัวตัดสินขาออก_ใช้รหัสกฎของใบเสร็จ_เกินยอดที่ใช้ได้ปฏิเสธ_พอดีผ่าน()
    {
        var today = new DateTime(2026, 11, 12);
        var credit = new CustomerCreditFacts(Guid.NewGuid(), Guid.NewGuid(), "PAY-1", today.AddDays(-3), 5_000m, true, false, false, true);
        Assert.Null(CustomerCreditMovementRules.Check(PaymentCreditMovementKind.ToReceipt, credit, 5_000m, 5_000m, today.AddDays(-3), today));
        var over = CustomerCreditMovementRules.Check(PaymentCreditMovementKind.ToReceipt, credit, 5_000m, 5_000.01m, today.AddDays(-3), today);
        Assert.NotNull(over);
        Assert.Equal(CustomerCreditMovementRules.ToReceiptRuleCode, over!.RuleCode);
        // ไม่ทราบลูกค้า ⇒ ออกใบเสร็จไม่ได้ (ม.86/4 ต้องรู้ผู้ซื้อ)
        Assert.NotNull(CustomerCreditMovementRules.Check(PaymentCreditMovementKind.ToReceipt, credit with { ContactId = null }, 5_000m, 5_000m, today, today));
    }

    [Fact]
    public void บรรทัด_ครบ86ทับ4_ผ่าน_ขาดชื่อจำนวนหรือว่างปฏิเสธ()
    {
        Assert.Null(CreditReceiptPlan.LinesProblem(new[] { Line() }, vatRegistered: false, companyVatRate: 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(Array.Empty<CreditReceiptLineFact>(), false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(null, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(d: "  ") }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(q: 0m) }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(p: -1m) }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(p: 10.005m) }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(p: 0m) }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(wht: 20m) }, false, 0m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(Enumerable.Range(0, CreditReceiptPlan.MaxLines + 1).Select(_ => Line()).ToList(), false, 0m));
    }

    [Fact]
    public void VAT_กิจการไม่จดเรียกเก็บไม่ได้_กิจการจดได้เฉพาะ0หรืออัตราบริษัท_ปนได้()
    {
        Assert.Contains("ม.86/13", CreditReceiptPlan.LinesProblem(new[] { Line(vat: 7m) }, vatRegistered: false, companyVatRate: 0m)!, StringComparison.Ordinal);
        Assert.Null(CreditReceiptPlan.LinesProblem(new[] { Line(vat: 7m), Line(vat: 0m) }, vatRegistered: true, companyVatRate: 7m));
        Assert.NotNull(CreditReceiptPlan.LinesProblem(new[] { Line(vat: 10m) }, vatRegistered: true, companyVatRate: 7m));
    }

    [Fact]
    public void วันที่บนใบ_ค้างออกใบ_เป็นวันรับเงิน_เหตุผลอื่นเป็นวันนี้()
    {
        var paid = new DateTime(2026, 10, 3);
        var today = new DateTime(2026, 11, 12);
        Assert.Equal(paid, CreditReceiptPlan.PaperDate(nameof(UnappliedReason.UninvoicedSupply), paid, today));
        Assert.Equal(today, CreditReceiptPlan.PaperDate(nameof(UnappliedReason.Overpaid), paid, today));
        Assert.Equal(today, CreditReceiptPlan.PaperDate(nameof(UnappliedReason.Unknown), paid, today));
        Assert.Equal(today, CreditReceiptPlan.PaperDate(null, paid, today));   // ไม่รู้เหตุผล = ไม่ถือวันรับเงิน (DOCTRINE §1)
    }

    [Fact]
    public void ยอดสุทธิ_ตรงผ่าน_ไม่ตรงปฏิเสธพร้อมตัวเลขทั้งสอง()
    {
        Assert.Null(CreditReceiptPlan.TotalMismatch(5_000m, 5_000m));
        Assert.Null(CreditReceiptPlan.TotalMismatch(5_000.004m, 5_000m));
        var m = CreditReceiptPlan.TotalMismatch(4_850m, 5_000m);
        Assert.NotNull(m);
        Assert.Contains("4,850.00", m!, StringComparison.Ordinal);
        Assert.Contains("5,000.00", m, StringComparison.Ordinal);
    }

    [Fact]
    public void งวดภาษี_งวดรับเงินเปิดอยู่ไม่ประทับ_งวดรับเงินยื่นแล้วประทับงวดปัจจุบัน_ไม่มีVATไม่ประทับ()
    {
        var paid = new DateTime(2026, 11, 2);
        var today = new DateTime(2026, 11, 12);
        var reason = nameof(UnappliedReason.UninvoicedSupply);
        var open = LateReceiptTaxPoint.Decide(reason, paid, today, receiptPeriodLocked: false, today);
        Assert.Null(CreditReceiptPlan.ReportingTaxPoint(open, paid, 350m));   // งวดรับเงิน = งวดปัจจุบันที่ยังเปิด ⇒ ไม่แตะ (ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
        paid = new DateTime(2026, 9, 20);
        var locked = LateReceiptTaxPoint.Decide(reason, paid, today, receiptPeriodLocked: true, today);
        var stamp = CreditReceiptPlan.ReportingTaxPoint(locked, paid, 350m);
        Assert.NotNull(stamp);
        Assert.False(DepositPolicyResolver.SameVatPeriod(stamp!.Value, paid));
        Assert.Equal(locked.TaxPointDate.Date, stamp.Value);
        Assert.Null(CreditReceiptPlan.ReportingTaxPoint(locked, paid, 0m));
        Assert.Contains(DepositPolicyResolver.LateVatMarker, CreditReceiptPlan.LateVatNote(paid, stamp.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void ออกช้าหลังวันรับเงิน_มีVATได้หมายเหตุ_วันเดียวกันหรือไม่มีVATไม่มี()
    {
        var paid = new DateTime(2026, 11, 1);
        var today = new DateTime(2026, 11, 12);
        Assert.Contains(LateReceiptTaxPoint.IssuedAfterMoneyMarker, CreditReceiptPlan.IssuedLateNote(paid, today, 350m)!, StringComparison.Ordinal);
        Assert.Null(CreditReceiptPlan.IssuedLateNote(today, today, 350m));
        Assert.Null(CreditReceiptPlan.IssuedLateNote(paid, today, 0m));
    }

    [Fact]
    public void หมายเหตุบนใบ_อ้างใบที่เงินก้อนเดียวกันชำระแล้ว_ไม่มีใบไม่อ้าง()
    {
        var withInv = CreditReceiptPlan.ReceiptNotes("PAY-9", new DateTime(2026, 11, 1), new[] { "INV-001" });
        Assert.Contains("INV-001", withInv, StringComparison.Ordinal);
        Assert.Contains("PAY-9", withInv, StringComparison.Ordinal);
        Assert.DoesNotContain("ออกแยก", CreditReceiptPlan.ReceiptNotes("PAY-9", new DateTime(2026, 11, 1), Array.Empty<string>()), StringComparison.Ordinal);
    }

    [Fact]
    public void รายการประจำ_งวดว่างของลูกค้าเดียวกันผ่าน_งวดออกแล้วหรือคนละลูกค้าหรือไม่ใช่เอกสารขายปฏิเสธ()
    {
        var contact = Guid.NewGuid();
        var ok = new CreditReceiptRecurringFacts(true, contact, RecurringStatus.Active, "Document", DocumentType.Invoice, new DateTime(2026, 12, 1), null);
        Assert.Null(CreditReceiptPlan.RecurringProblem(ok, contact));
        Assert.Contains("INV-777", CreditReceiptPlan.RecurringProblem(ok with { ExistingDocumentNumber = "INV-777" }, contact)!, StringComparison.Ordinal);
        Assert.NotNull(CreditReceiptPlan.RecurringProblem(ok, Guid.NewGuid()));
        Assert.NotNull(CreditReceiptPlan.RecurringProblem(ok with { TemplateType = "Journal" }, contact));
        Assert.NotNull(CreditReceiptPlan.RecurringProblem(ok with { DocumentType = DocumentType.PurchaseInvoice }, contact));
        Assert.NotNull(CreditReceiptPlan.RecurringProblem(ok with { Status = RecurringStatus.Paused }, contact));
        Assert.NotNull(CreditReceiptPlan.RecurringProblem(ok with { Found = false }, contact));
    }

    [Fact]
    public void หน้ารับชำระ_เสนอออกใบเสร็จเฉพาะเหตุผลค้างออกใบ()
    {
        foreach (var vat in new[] { true, false })
        {
            var opts = PaymentExcessPlan.ReasonOptions(vat);
            Assert.True(opts.Single(o => o.Reason == nameof(UnappliedReason.UninvoicedSupply)).OffersReceipt);
            Assert.All(opts.Where(o => o.Reason != nameof(UnappliedReason.UninvoicedSupply)), o => Assert.False(o.OffersReceipt));
        }
    }

    [Fact]
    public void RecurringTemplate_Summary_ยอดและคำอธิบายบรรทัดแรก_JSONเสียได้ศูนย์()
    {
        var s = RecurringTemplate.Summary("""{"lines":[{"description":"ค่าบริการรายเดือน","quantity":1,"unitPrice":"5,000"},{"quantity":2,"unitPrice":100}]}""");
        Assert.Equal(5_200m, s.Total);
        Assert.Equal("ค่าบริการรายเดือน", s.FirstDescription);
        Assert.Equal(0m, RecurringTemplate.Summary("{not json").Total);
        Assert.Equal(0m, RecurringTemplate.Summary(null).Total);
    }

    [Fact]
    public void L3_ด่านอนุมัติ_วงเงินหรือแบ่งแยกหน้าที่ปฏิเสธพร้อมทางไปต่อ_ไม่ติดด่านผ่าน()
    {
        Assert.Null(CreditReceiptPlan.ApprovalControlProblem(false, false));
        Assert.Contains("ออกใบแจ้งหนี้ส่วนที่ขาด", CreditReceiptPlan.ApprovalControlProblem(true, false)!, StringComparison.Ordinal);
        Assert.Contains("ผู้มีสิทธิ์อนุมัติอีกคน", CreditReceiptPlan.ApprovalControlProblem(false, true)!, StringComparison.Ordinal);
    }

    [Fact]
    public void M3_ป้ายงวดบอกงวดที่กำลังจะถึง_ยกเลิกหลังรอบผ่านมีข้อความ_ยังไม่ผ่านไม่มี_กุญแจอ่านกลับได้()
    {
        var period = new DateTime(2026, 10, 1);
        Assert.Contains("งวดที่กำลังจะถึง", CreditReceiptPlan.PeriodLabel("ค่าดูแลระบบ", period), StringComparison.Ordinal);
        Assert.NotNull(CreditReceiptPlan.VoidedRecurringPeriodNotice("RC-1", "ค่าดูแลระบบ", period, new DateTime(2026, 11, 1)));
        Assert.Null(CreditReceiptPlan.VoidedRecurringPeriodNotice("RC-1", "ค่าดูแลระบบ", period, period));
        var id = Guid.NewGuid();
        Assert.True(RecurringRunKey.TryParsePeriod(RecurringRunKey.ForPeriod(id, period), out var back, out var date));
        Assert.Equal(id, back);
        Assert.Equal(ThaiDate.CalendarDateUtc(period).Date, date.Date);
        Assert.False(RecurringRunKey.TryParsePeriod(RecurringRunKey.For(id, period, consumesPeriod: false, confirmedExtra: false), out _, out _));
        Assert.False(RecurringRunKey.TryParsePeriod(null, out _, out _));
    }
}
