using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ใบกำกับภาษีเต็มรูปจากบิล POS — ยอดบนใบต้องเท่าเงินที่ลูกค้าจ่าย
///
/// ═══ ที่มา (DECISION_AUDIT_2026-09-18 · D8-1 · P0) ═══
/// <c>IssueTaxInvoiceAsync</c> เดิมสร้างบรรทัดจาก <c>item.TotalAmount</c>/<c>item.VatAmount</c>
/// ซึ่งเป็นยอด **ก่อน** หักส่วนลดท้ายบิล/คูปอง และ **ไม่มี** ค่าบริการ ⇒
/// บิล 1,000 ลด 10% ลูกค้าจ่าย 900 แต่ใบกำกับเขียน 1,000:
///   • ผู้ซื้อเคลมภาษีซื้อ**เกินจริง** (§82/5(1) ใบไม่ถูกต้อง)
///   • ผู้ขายรายงานภาษีขาย ≠ GL ที่ POS ลงไว้ ⇒ ภ.พ.30 กับงบไม่ตรงกัน
///
/// เทสต์นี้มีสองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งที่พิสูจน์ว่าบิลที่พัง (มีส่วนลด/คูปอง/ค่าบริการ) ออกใบถูกต้อง
///   • ครึ่งที่พิสูจน์ว่าบิลธรรมดา (ไม่มีส่วนลดอะไรเลย) ได้บรรทัด**เท่าเดิมทุกบาท**
///     และไม่มีบรรทัดปรับปรุงงอกขึ้นมา
/// </summary>
public class PosTaxInvoiceLinesTests
{
    private const decimal VatRate = 7m;

    private static PosOrder Order(
        decimal discountPct = 0m, decimal coupon = 0m, string? couponCode = null,
        decimal serviceChargePct = 0m, decimal tip = 0m,
        params decimal[] lineGross)
    {
        var o = new PosOrder
        {
            DiscountPercent = discountPct,
            CouponDiscountAmount = coupon,
            CouponCode = couponCode,
            ServiceChargePercent = serviceChargePct,
            TipAmount = tip,
        };
        var n = 1;
        foreach (var g in lineGross)
            o.Items.Add(new PosOrderItem
            {
                ItemName = $"สินค้า {n}",
                ItemCode = $"P{n++}",
                Unit = "ชิ้น",
                Quantity = 1,
                UnitPrice = g,
                SubTotal = g,
                TotalAmount = g,
                VatAmount = Math.Round(g * VatRate / (100 + VatRate), 2, MidpointRounding.AwayFromZero),
            });
        PosService.RecalculateOrder(o, VatRate);
        return o;
    }

    /// <summary>เดินเส้นเดียวกับ <c>IssueTaxInvoiceAsync</c> เป๊ะ — เทสต์ต้องล็อก
    /// "สิ่งที่ระบบทำจริง" ไม่ใช่สูตรที่เทสต์แต่งขึ้นเอง</summary>
    private static IReadOnlyList<PosInvoiceLine> Build(PosOrder o)
        => PosTaxInvoiceLines.Build(
            o.Items.Where(x => !x.IsDeleted)
                .Select(x => new PosInvoiceSourceLine(x.ItemName, x.ItemCode, x.Unit, x.Quantity, x.TotalAmount)),
            billDiscountAmount: o.DiscountAmount,
            couponAmount: o.CouponDiscountAmount,
            couponCode: o.CouponCode,
            serviceChargeAmount: o.ServiceChargeAmount,
            roundingAmount: o.RoundingAmount,
            headGrossPayable: PosTaxInvoiceLines.GrossPayableForGoods(o.TotalAmount, o.RoundingAmount),
            headVat: o.VatAmount);

    private static decimal Gross(IReadOnlyList<PosInvoiceLine> lines)
        => lines.Sum(l => l.AmountNet) + lines.Sum(l => l.VatAmount);

    // ─────────── ครึ่งที่ 1: บิลที่พังต้องกลับมาถูก ───────────

    [Fact]
    public void บิลลด_10_เปอร์เซ็นต์_ใบกำกับต้องเป็น_900_ไม่ใช่_1000()
    {
        var o = Order(discountPct: 10m, lineGross: new[] { 600m, 400m });
        Assert.Equal(900m, o.TotalAmount);

        // พฤติกรรมเดิม (บรรทัดจาก item.TotalAmount ล้วน) = 1,000 — ต้องไม่กลับมา
        Assert.Equal(1000m, o.Items.Sum(i => i.TotalAmount));

        var lines = Build(o);
        Assert.Equal(900m, Gross(lines));
        Assert.Equal(3, lines.Count);                       // สินค้า 2 + ส่วนลด 1
        var discount = lines.Single(l => l.Description == PosTaxInvoiceLines.BillDiscountLabel);
        Assert.True(discount.AmountNet < 0m);
        Assert.True(discount.VatAmount < 0m);               // ส่วนลดลดฐานภาษีจริง §79
    }

    [Fact]
    public void บิลมีค่าบริการ_10_เปอร์เซ็นต์_ยอดต้องรวมค่าบริการ()
    {
        var o = Order(serviceChargePct: 10m, lineGross: new[] { 1000m });
        Assert.Equal(1100m, o.TotalAmount);

        var lines = Build(o);
        Assert.Equal(1100m, Gross(lines));
        var sc = lines.Single(l => l.Description == PosTaxInvoiceLines.ServiceChargeLabel);
        Assert.Equal(100m, sc.AmountNet + sc.VatAmount);
    }

    [Fact]
    public void ผลรวม_VAT_รายบรรทัด_ต้องเท่ากับ_VAT_หัวใบเสมอ()
    {
        // เศษเยอะ ๆ เพื่อบีบให้การปัดรายบรรทัดพัง ถ้าไม่ได้โยนเศษให้บรรทัดใหญ่สุด
        var o = Order(discountPct: 7.5m, coupon: 33.33m, couponCode: "NEWYEAR",
            serviceChargePct: 10m, lineGross: new[] { 333.33m, 111.11m, 555.55m });
        var lines = Build(o);
        Assert.Equal(o.VatAmount, lines.Sum(l => l.VatAmount));
        Assert.Equal(PosTaxInvoiceLines.GrossPayableForGoods(o.TotalAmount, o.RoundingAmount), Gross(lines));
    }

    [Fact]
    public void คูปองต้องเป็นบรรทัดของตัวเองพร้อมรหัสคูปอง_ไม่ถูกนับซ้ำกับส่วนลดเปอร์เซ็นต์()
    {
        // ลด 10% (=100) + คูปอง 50 ⇒ DiscountAmount = 150 (คูปองรวมอยู่แล้ว)
        var o = Order(discountPct: 10m, coupon: 50m, couponCode: "SAVE50", lineGross: new[] { 1000m });
        Assert.Equal(150m, o.DiscountAmount);

        var lines = Build(o);
        Assert.Equal(850m, Gross(lines));
        var coupon = lines.Single(l => l.Description.StartsWith(PosTaxInvoiceLines.CouponLabel, StringComparison.Ordinal));
        Assert.Contains("SAVE50", coupon.Description);
        Assert.Equal(-50m, coupon.AmountNet + coupon.VatAmount);      // คูปองหัก 50 ครั้งเดียว
        var pct = lines.Single(l => l.Description == PosTaxInvoiceLines.BillDiscountLabel);
        Assert.Equal(-100m, pct.AmountNet + pct.VatAmount);           // ส่วนที่เหลือคือส่วนลด%
    }

    [Fact]
    public void ทิปต้องไม่อยู่บนใบกำกับ_เพราะไม่ใช่ค่าตอบแทนการขาย()
    {
        var o = Order(tip: 100m, lineGross: new[] { 1000m });
        Assert.Equal(1100m, o.NetAmount);                   // ลูกค้าจ่าย 1,100 (รวมทิป)
        var lines = Build(o);
        Assert.Equal(1000m, Gross(lines));                  // ใบกำกับ 1,000
        Assert.DoesNotContain(lines, l => l.Description.Contains("ทิป", StringComparison.Ordinal));
    }

    [Fact]
    public void ปัดเศษเป็นบรรทัดที่ไม่มี_VAT_และทำให้ยอดตรงกับเงินที่รับ()
    {
        var o = Order(lineGross: new[] { 100.40m });
        Assert.NotEqual(0m, o.RoundingAmount);              // ปัดขึ้นเป็น 100
        var lines = Build(o);
        var rounding = lines.Single(l => l.Description == PosTaxInvoiceLines.RoundingLabel);
        Assert.Equal(0m, rounding.VatAmount);
        Assert.Equal(0m, rounding.VatRate);
        Assert.Equal(o.NetAmount, Gross(lines));            // ไม่มีทิป ⇒ = เงินที่รับจริง
    }

    [Fact]
    public void ยอดไม่ลงตัวต้องโยนทิ้ง_ห้ามออกใบที่ยอดไม่ตรง()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => PosTaxInvoiceLines.Build(
            new[] { new PosInvoiceSourceLine("สินค้า", "P1", "ชิ้น", 1m, 1000m) },
            billDiscountAmount: 0m, couponAmount: 0m, couponCode: null,
            serviceChargeAmount: 0m, roundingAmount: 0m,
            headGrossPayable: 900m,        // หัวใบบอก 900 แต่บรรทัดรวม 1,000
            headVat: 58.88m));
        Assert.Equal("RD-86/4-POS-LINE-SUM", ex.RuleCode);
    }

    // ─────────── ครึ่งที่ 2: บิลที่ถูกอยู่แล้ว ห้ามเปลี่ยน ───────────

    [Fact]
    public void บิลธรรมดาไม่มีส่วนลด_บรรทัดต้องเท่าเดิมทุกบาทและไม่มีบรรทัดงอก()
    {
        var o = Order(lineGross: new[] { 107m, 214m });
        var lines = Build(o);

        Assert.Equal(2, lines.Count);                       // ไม่มีบรรทัดปรับปรุงเพิ่ม
        Assert.Equal(new[] { "สินค้า 1", "สินค้า 2" }, lines.Select(l => l.Description).ToArray());
        Assert.Equal(new[] { "P1", "P2" }, lines.Select(l => l.ProductCode ?? "").ToArray());
        // ยอดรวม VAT ของแต่ละบรรทัด = ยอดเดิมที่ตรึงไว้บนบิล
        Assert.Equal(107m, lines[0].AmountNet + lines[0].VatAmount);
        Assert.Equal(214m, lines[1].AmountNet + lines[1].VatAmount);
        Assert.Equal(o.VatAmount, lines.Sum(l => l.VatAmount));
        Assert.All(lines, l => Assert.Equal(7m, l.VatRate));
    }

    [Fact]
    public void บริษัทที่ไม่มี_VAT_บนบิล_ทุกบรรทัดต้องไม่มี_VAT()
    {
        var o = new PosOrder();
        o.Items.Add(new PosOrderItem { ItemName = "ค่าเรียน", Quantity = 1, SubTotal = 500m, TotalAmount = 500m });
        PosService.RecalculateOrder(o, 0m);                 // บริษัทไม่จด VAT / สินค้ายกเว้น §81
        Assert.Equal(0m, o.VatAmount);

        var lines = Build(o);
        Assert.Equal(500m, Gross(lines));
        Assert.All(lines, l => Assert.Equal(0m, l.VatAmount));
        Assert.All(lines, l => Assert.Equal(0m, l.VatRate));
    }

    [Fact]
    public void หน่วยนับและรหัสสินค้าต้องไหลไปถึงใบกำกับ_ไม่ใช่ถูกแทนด้วยค่าตั้งต้น()
    {
        var o = Order(lineGross: new[] { 321m });
        o.Items.First().Unit = "กล่อง";
        var lines = Build(o);
        Assert.Equal("กล่อง", lines[0].Unit);
        Assert.Equal("P1", lines[0].ProductCode);
    }

    [Fact]
    public void ยอดต่อหน่วยต้องหารด้วยจำนวนจริง_ไม่ใช่ยอดทั้งบรรทัด()
    {
        var o = Order(lineGross: new[] { 428m });
        o.Items.First().Quantity = 4m;
        var lines = Build(o);
        Assert.Equal(4m, lines[0].Quantity);
        Assert.Equal(Math.Round(lines[0].AmountNet / 4m, 4, MidpointRounding.AwayFromZero), lines[0].UnitPriceNet);
    }
}
