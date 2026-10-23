using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **มัดจำออเดอร์ร้านออนไลน์ — วิธีออกเอกสารภาษีที่ร้านเลือกได้** (ทีม Q · คำตัดสินข้อ 156 · <see cref="SiteOrderDepositInvoicing"/>)
///
/// เทสต์สองทิศ (กฎเหล็ก #4 H):
///   • <b>ค่าเริ่มต้น = ผลเดิมทุกสตางค์</b> — ออเดอร์เดิม (โหมด null) และร้านที่ไม่ตั้งค่า เดินเส้นใบเต็มใบ + ตัดสต็อกตั้งแต่งวดแรกเหมือนเดิม ·
///     ใบขายไม่มีส่วนหักมัดจำ (ยอดเท่าออเดอร์)
///   • <b>โหมดมัดจำ</b> — ยอดภาษีรวมทุกใบ = ภาษีของยอดขายเต็ม · ฐานไม่ซ้ำ · สต็อกตัดครั้งเดียว (เฉพาะตอนมีใบขาย) · ยกเลิกหลังมัดจำบอกทางไปต่อ
///     (ไม่ยกเลิกใบมัดจำเอง) · คืนครบแล้วไม่มีข้อความค้าง
/// </summary>
public class SiteOrderDepositInvoicingTests
{
    private const string New = DepositInvoicingMode.DepositTaxInvoice;

    private static SiteOrder Order(decimal shipping, decimal discount, decimal vatRate, params (decimal Qty, decimal Price)[] items)
    {
        var o = new SiteOrder { OrderNumber = "WEB-0001", ShippingAmount = shipping, DiscountAmount = discount };
        var n = 1;
        foreach (var (qty, price) in items)
            o.Lines.Add(new SiteOrderLine
            {
                LineOrder = n, ProductName = $"เค้กตามสั่ง {n++}", Quantity = qty, Unit = "ชิ้น", UnitPrice = price,
                VatRate = vatRate, TotalAmount = qty * price,
            });
        o.TotalAmount = o.Lines.Sum(l => l.TotalAmount) + o.ShippingAmount - o.DiscountAmount;
        return o;
    }

    /// <summary>ใบรับเงินมัดจำ 1 ใบ — ยอดคิดด้วยตัวคำนวณเดียวกับเอกสารจริง (ราคารวม VAT · บรรทัดเดียว)</summary>
    private static LodgingDepositSnapshot Deposit(string number, decimal gross, decimal rate)
    {
        var line = new List<DocumentLineRequest>
        {
            new(Description: "มัดจำ", Quantity: 1m, UnitPrice: gross, Unit: "รายการ", DiscountPercent: 0m,
                VatRate: rate, WithholdingTaxRate: 0m, AccountId: null, ProjectId: null),
        };
        var t = DocumentService.PreviewTotals(line, true, 0m);
        return new LodgingDepositSnapshot(Guid.NewGuid(), number, Guid.Empty, t.Net, t.Vat, t.Total,
            VatPending: false, RealizedBase: 0m, RefundedGross: 0m, Nature: DepositNature.PartOfPrice);
    }

    private static LodgingCheckoutDepositPlan PlanFinal(SiteOrder o, IReadOnlyList<LodgingDepositSnapshot> deposits, out List<DocumentLineRequest> lines)
    {
        var l = CmsCommerceService.BuildOrderErpLines(o, true);
        lines = l;
        var full = DocumentService.PreviewTotals(l, true, 0m);
        return LodgingDepositSettlement.PlanCheckout(deposits, full.Net, d => DocumentService.PreviewTotals(l, true, 0m, d).Total);
    }

    // ─────────── ทิศที่ 1: ค่าเริ่มต้น = พฤติกรรมเดิม ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(DepositInvoicingMode.FullAtFirstPayment)]
    [InlineData("ค่าเก่าที่ไม่รู้จัก")]
    public void ค่าเริ่มต้น_มัดจำงวดแรก_ยังออกใบเต็มใบและตัดสต็อกเหมือนเดิม(string? mode)
    {
        var plan = SiteOrderDepositInvoicing.OnPayment(mode, hasSalesDocument: false, handedOver: false,
            orderTotal: 1000m, paidIncludingThis: 300m, new[] { 7m }, paymentAlreadyDeposited: false);
        Assert.Equal(SiteOrderPaymentStep.LegacyFullInvoice, plan.Step);
        Assert.True(plan.DeductsStock);
        Assert.True(plan.RecordsPaymentOnSalesDocument);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void ค่าเริ่มต้น_งวดที่สอง_ยังเป็นเส้นเดิม_ไม่ถอยไปออกใบใหม่()
        => Assert.Equal(SiteOrderPaymentStep.LegacyFullInvoice, SiteOrderDepositInvoicing.OnPayment(null, true, false, 1000m, 1000m, new[] { 7m }, false).Step);

    [Theory]
    [InlineData(SiteOrderStatus.Shipped)]
    [InlineData(SiteOrderStatus.Delivered)]
    public void ค่าเริ่มต้น_ส่งมอบไม่แตะเอกสารหรือสต็อก(SiteOrderStatus s)
    {
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(null, s));
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(DepositInvoicingMode.FullAtFirstPayment, s));
    }

    [Fact]
    public void ค่าเริ่มต้น_ใบขายเต็มยอดออเดอร์ทุกสตางค์_ไม่มีส่วนหักมัดจำ()
    {
        var o = Order(50m, 30m, 7m, (2m, 450m), (1m, 135.55m));
        var plan = PlanFinal(o, Array.Empty<LodgingDepositSnapshot>(), out var lines);
        Assert.Equal(0m, plan.BaseDeducted);
        Assert.Null(plan.DeductionRef);
        Assert.Equal(o.TotalAmount, DocumentService.PreviewTotals(lines, true, 0m, plan.BaseDeducted).Total);
        Assert.Equal(DocumentService.PreviewTotals(lines, true, 0m).Total, DocumentService.PreviewTotals(lines, true, 0m, 0m).Total);
    }

    [Fact]
    public void ค่าตั้ง_ว่างเป็นแบบเดิม_ไม่รู้จักโยนพร้อมรายการที่เลือกได้()
    {
        Assert.Equal(DepositInvoicingMode.FullAtFirstPayment, DepositInvoicingMode.Normalize(null));
        Assert.Equal(DepositInvoicingMode.FullAtFirstPayment, DepositInvoicingMode.Normalize("  "));
        Assert.Equal(New, DepositInvoicingMode.Normalize(" DEPOSIT_TAX_INVOICE "));
        var ex = Assert.Throws<BusinessRuleException>(() => DepositInvoicingMode.Normalize("deferred_vat"));
        Assert.Equal(SiteOrderDepositInvoicing.ConfigRuleCode, ex.RuleCode);
        Assert.All(DepositInvoicingMode.All, o => Assert.False(string.IsNullOrWhiteSpace(o.TaxEffect)));
        Assert.Contains("78(1)(ข)", DepositInvoicingMode.All.Single(o => o.Value == New).TaxEffect);
    }

    // ─────────── ทิศที่ 2: โหมดมัดจำ ───────────

    [Fact]
    public void โหมดมัดจำ_งวดแรกบางส่วน_ออกใบมัดจำ_ไม่ตัดสต็อก_ไม่บันทึกรับชำระซ้ำ()
    {
        var plan = SiteOrderDepositInvoicing.OnPayment(New, false, false, 1000m, 300m, new[] { 7m }, false);
        Assert.Equal(SiteOrderPaymentStep.DepositReceipt, plan.Step);
        Assert.False(plan.DeductsStock);
        Assert.False(plan.RecordsPaymentOnSalesDocument);
    }

    [Fact]
    public void โหมดมัดจำ_ยืนยันซ้ำรายการเดิม_ไม่ทำอะไรซ้ำ()
    {
        var plan = SiteOrderDepositInvoicing.OnPayment(New, false, false, 1000m, 300m, new[] { 7m }, paymentAlreadyDeposited: true);
        Assert.Equal(SiteOrderPaymentStep.AlreadyDeposited, plan.Step);
        Assert.False(plan.DeductsStock);
        Assert.False(plan.RecordsPaymentOnSalesDocument);
        // โหมดเดิมก็เช่นกัน — รายการที่เคยออกใบมัดจำไปแล้ว (ร้านเปลี่ยนโหมดกลางทางไม่เปลี่ยนออเดอร์ แต่กันไว้อีกชั้น)
        Assert.Equal(SiteOrderPaymentStep.AlreadyDeposited, SiteOrderDepositInvoicing.OnPayment(null, false, false, 1000m, 300m, new[] { 7m }, true).Step);
    }

    [Fact]
    public void โหมดมัดจำ_ชำระครบ_ออกใบส่วนที่เหลือพร้อมรับชำระและตัดสต็อก()
    {
        var plan = SiteOrderDepositInvoicing.OnPayment(New, false, false, 1000m, 1000m, new[] { 7m }, false);
        Assert.Equal(SiteOrderPaymentStep.FinalInvoiceThenPayment, plan.Step);
        Assert.True(plan.DeductsStock);
        Assert.True(plan.RecordsPaymentOnSalesDocument);
    }

    [Fact]
    public void โหมดมัดจำ_จ่ายเต็มตั้งแต่ต้น_ได้ใบเดียวเหมือนแบบเดิม()
        => Assert.Equal(SiteOrderPaymentStep.FinalInvoiceThenPayment, SiteOrderDepositInvoicing.OnPayment(New, false, false, 1000m, 1000m, new[] { 7m }, false).Step);

    [Fact]
    public void โหมดมัดจำ_ส่งมอบแล้วค่อยจ่ายส่วนที่เหลือ_บันทึกกับใบที่ออกตอนส่งมอบ()
    {
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(New, SiteOrderStatus.Delivered));
        Assert.True(SiteOrderDepositInvoicing.SettlesOnHandover(New, SiteOrderStatus.Shipped));
        Assert.False(SiteOrderDepositInvoicing.SettlesOnHandover(New, SiteOrderStatus.Processing));
        var plan = SiteOrderDepositInvoicing.OnPayment(New, hasSalesDocument: true, handedOver: true, 1000m, 1000m, new[] { 7m }, false);
        Assert.Equal(SiteOrderPaymentStep.PaymentOnFinalInvoice, plan.Step);
        Assert.True(plan.RecordsPaymentOnSalesDocument);
    }

    [Fact]
    public void โหมดมัดจำ_จ่ายบางส่วนหลังจัดส่งแล้วแต่ยังไม่มีใบ_ออกใบส่วนที่เหลือ_ไม่ใช่ใบมัดจำ()
        => Assert.Equal(SiteOrderPaymentStep.FinalInvoiceThenPayment,
            SiteOrderDepositInvoicing.OnPayment(New, false, handedOver: true, 1000m, 400m, new[] { 7m }, false).Step);

    [Fact]
    public void โหมดมัดจำ_บิลหลายอัตราVAT_ถอยไปแบบเดิมพร้อมเหตุ()
    {
        var plan = SiteOrderDepositInvoicing.OnPayment(New, false, false, 1000m, 300m, new[] { 7m, -1m }, false);
        Assert.Equal(SiteOrderPaymentStep.LegacyFullInvoice, plan.Step);
        Assert.Equal(SiteOrderDepositInvoicing.MixedRateNote, plan.Note);
    }

    [Fact]
    public void โหมดมัดจำ_ภาษีรวมสองใบเท่าภาษีของยอดเต็ม_ฐานไม่ซ้ำ_ยอดรวมเท่าออเดอร์()
    {
        // เค้ก 2 × 450 + 135.55 + ค่าส่ง 50 − ส่วนลด 30 = 1,055.55 · มัดจำ 30% = 316.67
        var o = Order(50m, 30m, 7m, (2m, 450m), (1m, 135.55m));
        var dep = Deposit("RE-001", Math.Round(o.TotalAmount * 0.30m, 2, MidpointRounding.AwayFromZero), 7m);
        var plan = PlanFinal(o, new[] { dep }, out var lines);
        var full = DocumentService.PreviewTotals(lines, true, 0m);
        var final = DocumentService.PreviewTotals(lines, true, 0m, plan.BaseDeducted);

        Assert.Single(plan.Deduct);
        Assert.Empty(plan.Apply);
        Assert.Empty(plan.Excess);
        Assert.Equal("RE-001", plan.DeductionRef);
        Assert.Equal(dep.SubTotal, plan.BaseDeducted);                       // หักฐานเท่าใบกำกับมัดจำพอดี (ไม่ซ้ำ ไม่ขาด)
        Assert.Equal(full.Net, dep.SubTotal + final.Net);                    // ฐานไม่เพี้ยน
        Assert.InRange(dep.VatAmount + final.Vat - full.Vat, -0.01m, 0.01m); // VAT คิดรายใบ ⇒ ต่างได้ไม่เกิน 1 สตางค์
        Assert.InRange(dep.TotalAmount + final.Total - o.TotalAmount, -0.01m, 0.01m);
        Assert.Equal(LodgingDepositSettlement.RoundingDelta(full.Total, plan.GrossDeducted, final.Total),
            dep.TotalAmount + final.Total - o.TotalAmount);
    }

    [Fact]
    public void โหมดมัดจำ_ตัวเลขกลม_สองใบรวมเท่ายอดเต็มเป๊ะ()
    {
        var o = Order(0m, 0m, 7m, (1m, 1070m));
        var dep = Deposit("RE-002", 321m, 7m);   // 300.00 + 21.00
        var plan = PlanFinal(o, new[] { dep }, out var lines);
        var final = DocumentService.PreviewTotals(lines, true, 0m, plan.BaseDeducted);
        Assert.Equal(300m, dep.SubTotal);
        Assert.Equal(21m, dep.VatAmount);
        Assert.Equal(700m, final.Net);
        Assert.Equal(49m, final.Vat);
        Assert.Equal(1070m, dep.TotalAmount + final.Total);
    }

    [Fact]
    public void โหมดมัดจำ_มัดจำสองงวด_หักครบทั้งสองใบในใบสุดท้ายใบเดียว()
    {
        var o = Order(0m, 0m, 7m, (1m, 2140m));
        var d1 = Deposit("RE-010", 535m, 7m);
        var d2 = Deposit("RE-011", 535m, 7m);
        var plan = PlanFinal(o, new[] { d1, d2 }, out var lines);
        var final = DocumentService.PreviewTotals(lines, true, 0m, plan.BaseDeducted);
        Assert.Equal(2, plan.Deduct.Count);
        Assert.Equal("RE-010, RE-011", plan.DeductionRef);
        Assert.Equal(1070m, final.Total);
        Assert.Equal(2140m, d1.TotalAmount + d2.TotalAmount + final.Total);
    }

    [Fact]
    public void บริษัทไม่จดVAT_ใบมัดจำไม่มีVAT_ตัดชำระใบสุดท้ายแทนการหักฐาน()
    {
        Assert.Equal(0m, SiteOrderDepositInvoicing.DepositLineVatRate(false, 7m));
        Assert.Equal(7m, SiteOrderDepositInvoicing.DepositLineVatRate(true, 7m));
        var o = Order(0m, 0m, 0m, (1m, 1000m));
        var dep = Deposit("RE-020", 300m, 0m);
        var l = CmsCommerceService.BuildOrderErpLines(o, false);
        var full = DocumentService.PreviewTotals(l, true, 0m);
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { dep }, full.Net, d => DocumentService.PreviewTotals(l, true, 0m, d).Total);
        Assert.Empty(plan.Deduct);
        Assert.Equal(0m, plan.BaseDeducted);
        var a = Assert.Single(plan.Apply);
        Assert.Equal(300m, a.Gross);
        Assert.Equal(1000m, full.Total);   // ใบสุดท้ายเต็มยอด แล้วมัดจำตัดชำระ 300 ⇒ ค้าง 700
    }

    [Fact]
    public void อัตราเดียวของบิล_หลายอัตราหรือว่างคืนnull()
    {
        Assert.Equal(7m, SiteOrderDepositInvoicing.SingleVatRate(new[] { 7m, 7m }));
        Assert.Null(SiteOrderDepositInvoicing.SingleVatRate(new[] { 7m, 0m }));
        Assert.Null(SiteOrderDepositInvoicing.SingleVatRate(Array.Empty<decimal>()));
    }

    // ─────────── ยกเลิก / คืนเงิน / ลูกค้าไม่มารับ ───────────

    [Fact]
    public void ยกเลิกหลังรับมัดจำ_บอกเลขใบและทางไปต่อ_ไม่ยกเลิกใบมัดจำเอง()
    {
        var dep = Deposit("RE-030", 321m, 7m);
        var note = SiteOrderDepositInvoicing.CancelNotice(new[] { (dep.Number, LodgingDepositSettlement.Remaining(dep).Gross) });
        Assert.NotNull(note);
        Assert.Contains("RE-030", note);
        Assert.Contains("321.00", note);
        Assert.Contains("คืนมัดจำ", note);
        Assert.Contains("ริบ", note);
    }

    [Fact]
    public void ยกเลิกเมื่อคืนมัดจำครบแล้ว_หรือไม่มีมัดจำ_ไม่มีข้อความค้าง()
    {
        var dep = Deposit("RE-031", 321m, 7m) with { RefundedGross = 321m };
        Assert.Equal(0m, LodgingDepositSettlement.Remaining(dep).Gross);
        Assert.Null(SiteOrderDepositInvoicing.CancelNotice(new[] { (dep.Number, LodgingDepositSettlement.Remaining(dep).Gross) }));
        Assert.Null(SiteOrderDepositInvoicing.CancelNotice(Array.Empty<(string, decimal)>()));
    }

    [Fact]
    public void ลูกค้าไม่มารับ_ริบมัดจำบางส่วนแล้ว_ข้อความบอกเฉพาะยอดที่เหลือ()
    {
        var dep = Deposit("RE-032", 321m, 7m) with { RealizedBase = 200m };   // ริบฐาน 200 แล้ว
        var rem = LodgingDepositSettlement.Remaining(dep).Gross;
        Assert.Equal(107m, rem);
        Assert.Contains("107.00", SiteOrderDepositInvoicing.CancelNotice(new[] { (dep.Number, rem) }));
    }

    [Fact]
    public void ข้อความถึงลูกค้า_เฉพาะโหมดมัดจำที่จ่ายเป็นงวด()
    {
        Assert.NotNull(SiteOrderDepositInvoicing.CustomerNote(New, paysInInstallments: true));
        Assert.Null(SiteOrderDepositInvoicing.CustomerNote(New, paysInInstallments: false));
        Assert.Null(SiteOrderDepositInvoicing.CustomerNote(null, paysInInstallments: true));
    }
}
