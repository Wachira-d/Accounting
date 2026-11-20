using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 275 แพ็ก C — ตัวตัดสินเดียว "ใบเสร็จของการรับครั้งนี้ถือบทบาทใบกำกับภาษี ณ วันรับเงินไหม + VAT เท่าใด" (<see cref="SettlementTaxInvoiceRole"/>) ·
/// ตัวเลขจากกรณีจำลองของ T3 (ฐาน 700,000 + VAT 49,000 = 749,000) · ทุกด่านมีเทสต์ทิศตรงข้าม (ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
/// </summary>
public class SettlementTaxInvoiceRoleTests
{
    private static readonly DateTime D0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementReceiptTotals.ThisSettlement Me(decimal cash, decimal fee = 0m, decimal wht = 0m, int day = 10, decimal adj = 0m)
        => new(D0.AddDays(day), D0.AddDays(day).AddHours(9), cash, fee, adj, wht);

    private static SettlementReceiptTotals.LedgerEvent Pay(decimal amount, int day)
        => new(SettlementReceiptTotals.EventKind.Payment, D0.AddDays(day), D0.AddDays(day).AddHours(9), amount, "PAY-" + day);

    private static SettlementReceiptTotals.LedgerEvent Cn(decimal amount, int day)
        => new(SettlementReceiptTotals.EventKind.CreditNote, D0.AddDays(day), D0.AddDays(day).AddHours(9), amount, "CN-" + day);

    /// <param name="total">ยอดสุทธิที่ต้องจ่าย (หลังภาษีหัก)</param>
    /// <param name="undue">ภาษีที่ยังพัก 21913 ของใบ (ก่อนย้ายของการรับครั้งนี้)</param>
    private static SettlementTaxInvoiceRole.Facts F(SettlementReceiptTotals.ThisSettlement me, decimal outstandingAfter,
        DocumentType type = DocumentType.Invoice, bool parked = true, decimal otherVat = 0m, decimal total = 749_000m, decimal undue = 0m,
        params SettlementReceiptTotals.LedgerEvent[] events)
        => new(type, 700_000m, 49_000m, total, false, parked, otherVat, me, events, outstandingAfter,
            UndueVatRemaining: undue, RateGroups: new[] { new SettlementTaxInvoiceRole.RateLine(7m, 700_000m, 49_000m) },
            SourceWithholdingTaxAmount: 749_000m - total);

    // ── HIGH-1: ค่าธรรมเนียมธนาคาร ──────────────────────────────────────────────

    [Fact]
    public void HIGH1_ใบแจ้งหนี้จ่ายเต็ม_ธนาคารหักค่าธรรมเนียม30_ยังเป็นใบกำกับเต็มใบ()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(748_970m, fee: 30m), 0m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.FullCopy, 49_000m), (d.Mode, d.VatAmount));
        Assert.True(SettlementTaxInvoiceRole.IsSingleShotFull(F(Me(748_970m, fee: 30m), 0m)));
    }

    [Fact]
    public void HIGH1_ทิศตรงข้าม_จ่ายเต็มไม่มีค่าธรรมเนียม_ผลเดิม_และใบกำกับภาษีต้นทางไม่ถือซ้ำ()
    {
        Assert.Equal(SettlementTaxInvoiceRole.Mode.FullCopy, SettlementTaxInvoiceRole.Decide(F(Me(749_000m), 0m)).Mode);
        var tiv = SettlementTaxInvoiceRole.Decide(F(Me(749_000m), 0m, type: DocumentType.TaxInvoice));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.NotInvoiceWithVat), (tiv.Mode, tiv.Reason));
        // ใบกำกับรวม (3-in-1) ใช้ "งวดเดียว" ตัวเดียวกัน — ไม่ดูชนิดใบ
        Assert.True(SettlementTaxInvoiceRole.IsSingleShotFull(F(Me(749_000m), 0m, type: DocumentType.TaxInvoice)));
    }

    [Fact]
    public void ภาษีหักณที่จ่าย3เปอร์เซ็นต์_จ่ายเต็มงวดเดียว_ใบกำกับเต็มใบ()
    {
        // ยอดสุทธิ 728,000 (หัก 21,000) · รับ 728,000 + ภาษีหัก 21,000
        var d = SettlementTaxInvoiceRole.Decide(F(Me(728_000m, wht: 21_000m), 0m, total: 728_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.FullCopy, 49_000m), (d.Mode, d.VatAmount));
    }

    // ── MED-1: ภาษีหักบังงวดก่อนหน้า ────────────────────────────────────────────

    [Fact]
    public void MED1_งวดแรก20000ผ่านทางอัตโนมัติ_งวดปิด_ไม่ใช่งวดเดียว_ถือVATที่ยังพัก()
    {
        // งวดแรก 20,000 (ใบกำกับรายงวด 1,308.41 · ย้ายภาษีเท่านั้น) · งวดปิด 708,000 + หัก 21,000 ⇒ ไม่ใช่ FullCopy (เดิมคัดลอกทั้งใบ 728,000)
        var f = F(Me(708_000m, wht: 21_000m, day: 20), 0m, total: 728_000m, otherVat: 1_308.41m, undue: 47_691.59m, events: Pay(20_000m, 5));
        var d = SettlementTaxInvoiceRole.Decide(f);
        Assert.False(SettlementTaxInvoiceRole.IsSingleShotFull(f));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 47_691.59m, 708_000m, true), (d.Mode, d.VatAmount, d.TotalAmount, d.ClosesSource));
    }

    [Fact]
    public void MED1_ขายสินค้า_ภาษีไม่พัก_งวดปิดไม่ใช่ใบกำกับ_คงเดิม()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(708_000m, wht: 21_000m, day: 20), 0m, parked: false, total: 728_000m, events: Pay(20_000m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.VatNotParked), (d.Mode, d.Reason));
    }

    // ── ฝ่ายค้าน HIGH-1: ใบลดหนี้/หักมัดจำ ⇒ งวดปิดถือภาษีที่ยังพักทั้งหมด (ปิดยอดแล้ว 21913 = 0) ─────────

    [Fact]
    public void ใบลดหนี้ก่อนรับ_รับครบ642000_ใบกำกับVAT42000มูลค่า600000()
    {
        // ใบลดหนี้ 107,000 (VAT 7,000 ลด 21913) ก่อนรับ ⇒ ภาษีที่ยังพัก 42,000 ⇒ ใบกำกับถือ 42,000 (เดิม MED-2 คัดลอก 49,000)
        var d = SettlementTaxInvoiceRole.Decide(F(Me(642_000m, day: 20), 0m, undue: 42_000m, events: Cn(107_000m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 600_000m, 42_000m, 642_000m, true), (d.Mode, d.SubTotal, d.VatAmount, d.TotalAmount, d.ClosesSource));
        Assert.All(d.Lines!, l => Assert.Equal(l.Vat, Math.Round(l.Base * l.VatRate / 100m, 2, MidpointRounding.AwayFromZero)));
    }

    [Fact]
    public void ใบลดหนี้หลังงวด1_งวดปิดถือVATที่เหลือ17500()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(267_500m, day: 35), 0m, otherVat: 24_500m, undue: 17_500m,
            events: new[] { Pay(374_500m, 5), Cn(107_000m, 20) }));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 250_000m, 17_500m), (d.Mode, d.SubTotal, d.VatAmount));
    }

    [Fact]
    public void หักมัดจำแล้วรับส่วนที่เหลือ_งวดปิดถือภาษีที่ยังพัก()
    {
        // หักมัดจำ 149,000 (ไม่อยู่ในรายการที่ไล่ได้) · รับ 600,000 ปิดยอด ⇒ VAT = ที่ยังพัก 39,252.34 · มูลค่า × 7% = VAT
        var d = SettlementTaxInvoiceRole.Decide(F(Me(600_000m), 0m, undue: 39_252.34m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 39_252.34m, 560_747.66m), (d.Mode, d.VatAmount, d.SubTotal));
    }

    // ── MED-3 / คำตัดสิน 4.1: งานบริการรับเป็นงวด ───────────────────────────────

    [Fact]
    public void MED3_บริการผ่อน2งวด_ใบกำกับทุกงวด_VATตามส่วน_รวมเท่าVATทั้งใบ()
    {
        var first = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 374_500m, undue: 49_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 350_000m, 24_500m, 374_500m, false),
            (first.Mode, first.SubTotal, first.VatAmount, first.TotalAmount, first.ClosesSource));
        var second = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 35), 0m, otherVat: first.VatAmount, undue: 24_500m, events: Pay(374_500m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m, true), (second.Mode, second.VatAmount, second.ClosesSource));
        Assert.Equal(49_000m, first.VatAmount + second.VatAmount);
    }

    [Fact]
    public void MED3_สามงวดเศษสตางค์_งวดปิดถือส่วนที่เหลือ_ผลรวมตรงทุกสตางค์()
    {
        var a = SettlementTaxInvoiceRole.Decide(F(Me(249_666.67m, day: 1), 499_333.33m, undue: 49_000m));
        var b = SettlementTaxInvoiceRole.Decide(F(Me(249_666.67m, day: 2), 249_666.66m, otherVat: a.VatAmount, undue: 49_000m - a.VatAmount,
            events: Pay(249_666.67m, 1)));
        var c = SettlementTaxInvoiceRole.Decide(F(Me(249_666.66m, day: 3), 0m, otherVat: a.VatAmount + b.VatAmount,
            undue: 49_000m - a.VatAmount - b.VatAmount, events: new[] { Pay(249_666.67m, 1), Pay(249_666.67m, 2) }));
        Assert.Equal(49_000m, a.VatAmount + b.VatAmount + c.VatAmount);
        Assert.Equal(16_333.33m, a.VatAmount);
        Assert.Equal(249_666.67m, a.SubTotal + a.VatAmount);
    }

    [Fact]
    public void งวดมีภาษีหัก_ถอดVATจากยอดตัดหนี้รวมภาษีหัก()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(364_000m, wht: 10_500m), 364_000m, total: 728_000m, undue: 49_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 350_000m, 24_500m, 10_500m, 364_000m),
            (d.Mode, d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount));
    }

    [Fact]
    public void LOW_ไม่ได้บันทึกภาษีหักที่การรับ_ใบมีภาษีหัก3เปอร์เซ็นต์_ใช้สัดส่วนจากใบ()
    {
        // ยอดสุทธิ 728,000 · รับ 364,000 โดยไม่ระบุภาษีหักของงวด ⇒ ยอดรวมภาษีหัก = 364,000 × 749,000/728,000 = 374,500 ⇒ VAT 24,500
        var d = SettlementTaxInvoiceRole.Decide(F(Me(364_000m), 364_000m, total: 728_000m, undue: 49_000m));
        Assert.Equal((24_500m, 350_000m, 10_500m, 364_000m), (d.VatAmount, d.SubTotal, d.WithholdingTaxAmount, d.TotalAmount));
    }

    [Fact]
    public void MED2_ใบผสม7และ0เปอร์เซ็นต์_แยกบรรทัดตามอัตรา_มูลค่าคูณอัตราเท่าVAT()
    {
        var f = new SettlementTaxInvoiceRole.Facts(DocumentType.Invoice, 707_000m, 42_000m, 749_000m, false, true, 0m, Me(374_500m), Array.Empty<SettlementReceiptTotals.LedgerEvent>(),
            374_500m, UndueVatRemaining: 42_000m,
            RateGroups: new[] { new SettlementTaxInvoiceRole.RateLine(7m, 600_000m, 42_000m), new SettlementTaxInvoiceRole.RateLine(0m, 107_000m, 0m) });
        var d = SettlementTaxInvoiceRole.Decide(f);
        Assert.Equal(21_000m, d.VatAmount);
        Assert.Equal(new[] { new SettlementTaxInvoiceRole.RateLine(7m, 300_000m, 21_000m), new SettlementTaxInvoiceRole.RateLine(0m, 53_500m, 0m) }, d.Lines);
        Assert.Equal(353_500m, d.SubTotal);
    }

    [Fact]
    public void ใบกำกับของการขายนี้ถือVATครบแล้ว_ไม่ออกใบที่สอง()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(749_000m), 0m, otherVat: 49_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.FullVatAlreadyIssued), (d.Mode, d.Reason));
    }

    [Fact]
    public void ใบเก่าภาษีรายงานที่ใบแจ้งหนี้แล้ว_ใบกำกับเป็นหลักฐานกระดาษ_VATตามส่วน()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 374_500m) with { VatReportedWholesale = true });
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m, true), (d.Mode, d.VatAmount, d.VatReportedOnSource));
        // ทิศตรงข้าม: เส้นใหม่ไม่ติดธง
        Assert.False(SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 374_500m, undue: 49_000m)).VatReportedOnSource);
    }

    [Fact]
    public void ออกใบกำกับทีหลัง_ถือเท่าที่ย้ายจริงของการรับนั้น()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 0m, undue: 0m, events: Pay(374_500m, 35)) with { TaggedVatThisPayment = 24_500m });
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m), (d.Mode, d.VatAmount));
    }

    [Fact]
    public void ตัวประมาณของงานหลังรับชำระ_เฉพาะใบแจ้งหนี้ที่มีVAT()
    {
        Assert.True(SettlementTaxInvoiceRole.MayCarryVat(DocumentType.Invoice, 49_000m));
        Assert.False(SettlementTaxInvoiceRole.MayCarryVat(DocumentType.TaxInvoice, 49_000m));
        Assert.False(SettlementTaxInvoiceRole.MayCarryVat(DocumentType.Invoice, 0m));
    }

    [Fact]
    public void ใบแทน_ถือบทบาทเท่าใบเดิม()
    {
        Assert.Equal(SettlementTaxInvoiceRole.Mode.FullCopy, SettlementTaxInvoiceRole.CarryIssued(true, 700_000m, 49_000m, 0m, 749_000m, 49_000m).Mode);
        var inst = SettlementTaxInvoiceRole.CarryIssued(true, 350_000m, 24_500m, 0m, 374_500m, 49_000m);
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m), (inst.Mode, inst.VatAmount));
        Assert.Equal(SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.CarryIssued(false, 749_000m, 0m, 0m, 749_000m, 49_000m).Mode);
    }

    [Fact]
    public void ฝ่ายค้านMED5_วันที่รับชำระ_แปลงเฉพาะเวลาที่ระบุว่าUTC()
    {
        // gateway: 18:00 UTC 30 พ.ย. = 01:00 น. 1 ธ.ค. เวลาไทย ⇒ 1 ธ.ค.
        Assert.Equal(new DateTime(2026, 12, 1), ThaiDate.PaymentCalendarDate(new DateTime(2026, 11, 30, 18, 0, 0, DateTimeKind.Utc)).Date);
        // นำเข้า/bank feed/ฟอร์ม: เวลาไทยไม่ระบุ Kind ⇒ คงเดิมทุกตัวอักษร (เดิมตีความเป็น UTC ⇒ 23:30 น. กลายเป็นวันถัดไป)
        var local = new DateTime(2026, 11, 30, 23, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(local, ThaiDate.PaymentCalendarDate(local));
        var formDay = new DateTime(2026, 12, 1);
        Assert.Equal(formDay, ThaiDate.PaymentCalendarDate(formDay));
    }

    [Fact]
    public void ฝ่ายค้านMED4_หลังยกเลิกทางeTax_ใช้ผลตัวตัดสินเดียว_งวดบริการออกใบกำกับรายงวดได้()
    {
        var p1 = new EtaxCancelLivePayment(Guid.NewGuid(), D0.AddDays(5), false, null, null, 0m, false);
        var p2 = new EtaxCancelLivePayment(Guid.NewGuid(), D0.AddDays(35), false, null, null, 0m, false);
        var plan = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 0m, undue: 49_000m, events: Pay(374_500m, 35)));
        var facts = new EtaxCancelFollowUpFacts(DocumentType.Invoice, 49_000m, true, D0.AddDays(5), false, new[] { p1, p2 }, null, null, FirstPaymentPlan: plan);
        var r = DocumentVoidPreconditions.EtaxCancellationFollowUp(facts);
        Assert.True(r.Allowed);
        Assert.Equal(p1.PaymentId, r.IssueVatReceiptForPaymentId);
        // ทิศตรงข้าม: ไม่ส่งผลตัวตัดสิน ⇒ กติกาเดิม (หลายงวด ⇒ ปฏิเสธพร้อมทางไปต่อ)
        Assert.False(DocumentVoidPreconditions.EtaxCancellationFollowUp(facts with { FirstPaymentPlan = null }).Allowed);
    }

    // ── ด่านเส้นมือ (VatInvoiceSettlementGate) ────────────────────────────────

    [Fact]
    public void ด่านรับบางส่วนใบเดียว_งานบริการผ่าน_ขายสินค้าปฏิเสธเหมือนเดิม()
    {
        Assert.Null(VatInvoiceSettlementGate.PartialRefusalForSingleDocument(DocumentType.Invoice, 49_000m, 374_500m, 749_000m, "INV-1", vatParkedUntilPayment: true));
        Assert.NotNull(VatInvoiceSettlementGate.PartialRefusalForSingleDocument(DocumentType.Invoice, 49_000m, 374_500m, 749_000m, "INV-1", vatParkedUntilPayment: false));
        // ด่านเดิมของทางเข้าอื่น (จัดสรรหลายใบ/เงินรอจัดสรร/ย้าย) ไม่เปลี่ยน
        Assert.NotNull(VatInvoiceSettlementGate.PartialRefusal(DocumentType.Invoice, 49_000m, 374_500m, 749_000m, "INV-1"));
    }

    [Fact]
    public void ด่านหลังตัดสิน_รับบางส่วนแล้วออกใบกำกับไม่ได้_ปฏิเสธ_ทิศตรงข้ามผ่าน()
    {
        var cn = SettlementTaxInvoiceRole.Decide(F(Me(300_000m, day: 20), 342_000m, parked: false, events: Cn(107_000m, 5)));
        Assert.NotNull(VatInvoiceSettlementGate.InstallmentWithoutTaxInvoiceRefusal(DocumentType.Invoice, 49_000m, 342_000m, cn, "INV-1"));
        var inst = SettlementTaxInvoiceRole.Decide(F(Me(374_500m), 374_500m, undue: 49_000m));
        Assert.Null(VatInvoiceSettlementGate.InstallmentWithoutTaxInvoiceRefusal(DocumentType.Invoice, 49_000m, 374_500m, inst, "INV-1"));
        // ปิดยอด ⇒ ไม่ใช่การรับบางส่วน (ผ่านเสมอ)
        Assert.Null(VatInvoiceSettlementGate.InstallmentWithoutTaxInvoiceRefusal(DocumentType.Invoice, 49_000m, 0m, cn, "INV-1"));
    }

    // ── ข้อความผ่าน DocumentLabels (MED-5) ──────────────────────────────────

    [Fact]
    public void MED5_บรรทัดและหมายเหตุของใบเสร็จรับชำระ_ตามภาษาของใบ_ไม่มีไทยในใบอังกฤษ()
    {
        var en = Accounting.Services.Implementations.Pdf.DocumentLabels.For("en");
        var th = Accounting.Services.Implementations.Pdf.DocumentLabels.For("th");
        Assert.Equal("Payment received for Tax Invoice No. TIV-1", en.SettlementReceiptLine(DocumentType.TaxInvoice, "TIV-1", installment: false));
        Assert.Equal("รับชำระเงินตามใบกำกับภาษีเลขที่ TIV-1", th.SettlementReceiptLine(DocumentType.TaxInvoice, "TIV-1", installment: false));
        var note = en.SettlementReceiptNote(DocumentType.Invoice, "INV-1", new DateTime(2026, 11, 5), "BankTransfer", SettlementTaxInvoiceRole.Mode.Installment);
        Assert.DoesNotMatch("[฀-๿]", note + en.SettlementUnitItem + en.SettlementReceiptLine(DocumentType.Invoice, "INV-1", true));
        Assert.Contains("(วันที่รับ 05/11/2026, BankTransfer)", th.SettlementReceiptNote(DocumentType.Invoice, "INV-1", new DateTime(2026, 11, 5), "BankTransfer",
            SettlementTaxInvoiceRole.Mode.None));
    }
}
