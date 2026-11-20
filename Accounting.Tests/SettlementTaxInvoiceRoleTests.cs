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
    private static SettlementTaxInvoiceRole.Facts F(SettlementReceiptTotals.ThisSettlement me, decimal outstandingAfter,
        DocumentType type = DocumentType.Invoice, bool parked = true, decimal otherVat = 0m, decimal total = 749_000m,
        params SettlementReceiptTotals.LedgerEvent[] events)
        => new(type, 700_000m, 49_000m, total, false, parked, otherVat, me, events, outstandingAfter);

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
    public void MED1_งวดแรก20000ผ่านทางอัตโนมัติ_งวดปิด_ไม่ใช่งวดเดียว_ถือVATที่เหลือ()
    {
        // งวดแรก 20,000 (ใบกำกับรายงวดถือไปแล้ว 1,308.41) · งวดปิด 708,000 + หัก 21,000 ⇒ ไม่ใช่ FullCopy (เดิมคัดลอกทั้งใบ 728,000)
        var f = F(Me(708_000m, wht: 21_000m, day: 20), 0m, total: 728_000m, otherVat: 1_308.41m, events: Pay(20_000m, 5));
        var d = SettlementTaxInvoiceRole.Decide(f);
        Assert.False(SettlementTaxInvoiceRole.IsSingleShotFull(f));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 47_691.59m), (d.Mode, d.VatAmount));
        Assert.Equal(708_000m, d.TotalAmount);   // ยอดบนใบ = ยอดที่ตัดหนี้ครั้งนี้ (ไม่ใช่ 728,000 ของทั้งใบ)
    }

    [Fact]
    public void MED1_ขายสินค้า_ภาษีไม่พัก_งวดปิดไม่ใช่ใบกำกับ_คงเดิม()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(708_000m, wht: 21_000m, day: 20), 0m, parked: false, total: 728_000m, events: Pay(20_000m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.VatNotParked, false), (d.Mode, d.Reason, d.NeedsHumanDecision));
    }

    // ── MED-2: ใบลดหนี้ก่อนรับ ──────────────────────────────────────────────────

    [Fact]
    public void MED2_ใบลดหนี้ก่อนรับ_ไม่คัดลอกVATเต็มใบ_ให้คนตัดสิน()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(642_000m, day: 20), 0m, events: Cn(107_000m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.CreditNoteBeforeReceipt, true), (d.Mode, d.Reason, d.NeedsHumanDecision));
        Assert.NotNull(SettlementTaxInvoiceRole.ReasonText(d.Reason));
    }

    [Fact]
    public void MED2_ทิศตรงข้าม_ใบลดหนี้หลังรับครบ_ไม่กระทบใบกำกับของการรับครั้งนั้น()
    {
        // ออกย้อนหลังหลังมีใบลดหนี้ที่ออกทีหลัง (คืนเงิน) — การรับครั้งนั้นยังเป็นงวดเดียวเต็มใบ
        var d = SettlementTaxInvoiceRole.Decide(F(Me(749_000m, day: 5), 0m, events: Cn(107_000m, 20)) with { SourcesOutstandingNow = null });
        Assert.Equal(SettlementTaxInvoiceRole.Mode.FullCopy, d.Mode);
    }

    // ── MED-3 / คำตัดสิน 4.1: งานบริการรับเป็นงวด ───────────────────────────────

    [Fact]
    public void MED3_บริการผ่อน2งวด_ใบกำกับทุกงวด_VATตามส่วน_รวมเท่าVATทั้งใบ()
    {
        var first = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 374_500m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 350_000m, 24_500m, 374_500m), (first.Mode, first.SubTotal, first.VatAmount, first.TotalAmount));
        var second = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 35), 0m, otherVat: first.VatAmount, events: Pay(374_500m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m), (second.Mode, second.VatAmount));
        Assert.Equal(49_000m, first.VatAmount + second.VatAmount);
    }

    [Fact]
    public void MED3_สามงวดเศษสตางค์_งวดปิดถือส่วนที่เหลือ_ผลรวมตรงทุกสตางค์()
    {
        var a = SettlementTaxInvoiceRole.Decide(F(Me(249_666.67m, day: 1), 499_333.33m));
        var b = SettlementTaxInvoiceRole.Decide(F(Me(249_666.67m, day: 2), 249_666.66m, otherVat: a.VatAmount, events: Pay(249_666.67m, 1)));
        var c = SettlementTaxInvoiceRole.Decide(F(Me(249_666.66m, day: 3), 0m, otherVat: a.VatAmount + b.VatAmount,
            events: new[] { Pay(249_666.67m, 1), Pay(249_666.67m, 2) }));
        Assert.Equal(49_000m, a.VatAmount + b.VatAmount + c.VatAmount);
        Assert.Equal(16_333.33m, a.VatAmount);
        Assert.Equal(a.SubTotal + a.VatAmount, 249_666.67m);
    }

    [Fact]
    public void งวดมีภาษีหัก_ถอดVATจากยอดตัดหนี้รวมภาษีหัก()
    {
        // ยอดสุทธิ 728,000 · งวดแรกรับ 364,000 + หัก 10,500 ⇒ ยอดรวมภาษีหัก 374,500 ⇒ VAT 24,500
        var d = SettlementTaxInvoiceRole.Decide(F(Me(364_000m, wht: 10_500m), 364_000m, total: 728_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 350_000m, 24_500m, 10_500m, 364_000m),
            (d.Mode, d.SubTotal, d.VatAmount, d.WithholdingTaxAmount, d.TotalAmount));
    }

    [Fact]
    public void ใบกำกับของการขายนี้ถือVATครบแล้ว_ไม่ออกใบที่สอง()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(749_000m), 0m, otherVat: 49_000m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.FullVatAlreadyIssued), (d.Mode, d.Reason));
    }

    [Fact]
    public void ตัดยอดด้วยช่องทางที่ไล่ไม่ได้_เช่นหักมัดจำ_ไม่เดา()
    {
        // ยอดค้างหลังรับ = 0 แต่รายการที่ไล่ได้รวมแค่ 600,000 (หักมัดจำ 149,000 ไม่อยู่ในรายการ)
        var d = SettlementTaxInvoiceRole.Decide(F(Me(600_000m), 0m));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.UnexplainedPriorSettlement, true), (d.Mode, d.Reason, d.NeedsHumanDecision));
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
    public void ใบเก่าภาษีย้ายทั้งก้อน_รับยังไม่ครบ_ไม่ออกใบกำกับรายงวด_ให้คนตัดสิน()
    {
        var d = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 374_500m) with { VatReportedWholesale = true });
        Assert.Equal((SettlementTaxInvoiceRole.Why.WholesaleVatNotFullyPaid, true), (d.Reason, d.NeedsHumanDecision));
        // รับครบแล้ว (ออกทุกงวดพร้อมกัน — งวดแรกยังไม่มีรายการก่อนหน้า) ⇒ ใบกำกับรายงวดตามส่วน
        var first = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 5), 0m, events: Pay(374_500m, 35)) with { VatReportedWholesale = true });
        Assert.Equal((SettlementTaxInvoiceRole.Mode.Installment, 24_500m), (first.Mode, first.VatAmount));
    }

    [Fact]
    public void งวดก่อนไม่มีใบกำกับ_งวดปิดไม่ถือVATที่เหลือทั้งก้อน_ทิศตรงข้ามงวดก่อนมีใบกำกับผ่าน()
    {
        var uncovered = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 35), 0m, otherVat: 0m, events: Pay(374_500m, 5)));
        Assert.Equal((SettlementTaxInvoiceRole.Mode.None, SettlementTaxInvoiceRole.Why.PriorInstallmentWithoutTaxInvoice, true),
            (uncovered.Mode, uncovered.Reason, uncovered.NeedsHumanDecision));
        var covered = SettlementTaxInvoiceRole.Decide(F(Me(374_500m, day: 35), 0m, otherVat: 24_500m, events: Pay(374_500m, 5)));
        Assert.Equal(SettlementTaxInvoiceRole.Mode.Installment, covered.Mode);
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
        var cn = SettlementTaxInvoiceRole.Decide(F(Me(300_000m, day: 20), 342_000m, events: Cn(107_000m, 5)));
        Assert.NotNull(VatInvoiceSettlementGate.InstallmentWithoutTaxInvoiceRefusal(DocumentType.Invoice, 49_000m, 342_000m, cn, "INV-1"));
        var inst = SettlementTaxInvoiceRole.Decide(F(Me(374_500m), 374_500m));
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
