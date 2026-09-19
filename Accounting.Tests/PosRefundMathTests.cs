using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คืนเงิน POS — สัดส่วนต้องอิง "เงินที่ลูกค้าจ่ายจริง" ไม่ใช่ราคาป้าย
///
/// ═══ ที่มา (DECISION_AUDIT_2026-09-18 · D8-2 · P0 เงินจริง) ═══
/// <c>RefundOrderAsync</c> เดิมคิด
/// <c>orderLevelDiscount = DiscountAmount + CouponDiscountAmount</c>
/// แต่ <c>PosService.RecalculateOrder</c> เก็บ
/// <c>DiscountAmount = ส่วนลด% + CouponDiscountAmount</c> อยู่แล้ว ⇒ **คูปองถูกหักสองครั้ง**
/// ⇒ ลูกค้าได้เงินคืน**น้อยกว่าที่จ่ายมา** และไม่มีเทสต์แม้แต่ตัวเดียว
/// (grep <c>discountFactor</c>/<c>CouponDiscountAmount</c> ใน Accounting.Tests = 0)
///
/// เทสต์นี้มีสองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งที่พิสูจน์ว่า "บิลที่พัง (มีคูปอง) กลับมาถูก"
///   • ครึ่งที่พิสูจน์ว่า "บิลที่ถูกอยู่แล้ว (ไม่มีคูปอง) ผลเท่าเดิมเป๊ะ"
///     — ยึดค่าที่สูตร**เก่า**เคยให้ เพื่อกันการแก้เกินตัว
/// </summary>
public class PosRefundMathTests
{
    private const decimal VatRate = 7m;

    /// <summary>สูตร **เก่า** (ก่อนแก้) — ใช้เป็นเส้นฐานของครึ่ง "ห้ามทำใบที่ถูกอยู่แล้วพัง"</summary>
    private static decimal OldDiscountFactor(decimal lineGrossTotal, PosOrder o)
        => lineGrossTotal > 0
            ? Math.Max(0m, (lineGrossTotal - (o.DiscountAmount + o.CouponDiscountAmount)) / lineGrossTotal)
            : 1m;

    /// <summary>สร้างบิลที่เดินสูตรจริงของระบบ (<c>PosService.RecalculateOrder</c>)
    /// — ห้ามกรอก <c>DiscountAmount</c> เอง มิฉะนั้นเทสต์จะพิสูจน์สูตรที่เทสต์แต่งขึ้น</summary>
    private static PosOrder Order(
        decimal discountPct = 0m, decimal coupon = 0m, decimal serviceChargePct = 0m,
        params decimal[] lineGross)
    {
        var o = new PosOrder
        {
            DiscountPercent = discountPct,
            CouponDiscountAmount = coupon,
            ServiceChargePercent = serviceChargePct,
        };
        foreach (var g in lineGross)
        {
            var vat = Math.Round(g * VatRate / (100 + VatRate), 2, MidpointRounding.AwayFromZero);
            o.Items.Add(new PosOrderItem
            {
                Quantity = 1,
                UnitPrice = g,
                SubTotal = g,
                TotalAmount = g,
                VatAmount = vat,
            });
        }
        PosService.RecalculateOrder(o, VatRate);
        return o;
    }

    private static decimal LineGrossTotal(PosOrder o)
        => o.Items.Where(i => !i.IsDeleted).Sum(i => i.TotalAmount);

    private static PosRefundAmounts RefundAll(PosOrder o)
        => PosRefundMath.Compute(LineGrossTotal(o), o.DiscountAmount,
            o.Items.Where(i => !i.IsDeleted)
                .Select(i => new PosRefundLine(i.TotalAmount, i.VatAmount, i.Quantity, i.Quantity)));

    // ─────────── ครึ่งที่ 1: บิลที่พังต้องกลับมาถูก ───────────

    [Fact]
    public void บิลมีคูปอง_คืนทั้งใบต้องได้เท่าที่จ่ายจริง()
    {
        // ป้าย 1,000 · คูปอง 100 ⇒ RecalculateOrder: DiscountAmount = 100, TotalAmount = 900
        var o = Order(coupon: 100m, lineGross: new[] { 600m, 400m });
        Assert.Equal(100m, o.DiscountAmount);
        Assert.Equal(900m, o.TotalAmount);

        var r = RefundAll(o);
        Assert.Equal(900m, r.Gross);                       // = เงินที่ลูกค้าจ่าย
        Assert.Equal(0.9m, r.DiscountFactor);
    }

    [Fact]
    public void บั๊กเดิม_คูปองถูกหักสองครั้ง_ลูกค้าได้คืนน้อยไป_ต้องไม่กลับมา()
    {
        // negative test — ต้องเห็นตัวเลขที่ผู้ใช้เสียหายจริงก่อน จึงเชื่อว่าแก้ถูกตัว
        var o = Order(coupon: 100m, lineGross: new[] { 1000m });
        var oldFactor = OldDiscountFactor(LineGrossTotal(o), o);   // (1000 − 200)/1000 = 0.8
        Assert.Equal(0.8m, oldFactor);

        var oldGross = Math.Round(1000m * oldFactor, 2, MidpointRounding.AwayFromZero);
        var now = RefundAll(o);
        Assert.Equal(800m, oldGross);                      // เดิมคืนแค่ 800
        Assert.Equal(900m, now.Gross);                     // ต้องคืน 900
        Assert.Equal(100m, now.Gross - oldGross);          // ลูกค้าเคยขาดไป ฿100/ใบ
    }

    [Fact]
    public void ลดเปอร์เซ็นต์_บวกคูปอง_บวกค่าบริการ_คืนถูก()
    {
        // ป้าย 1,000 · ลด 10% = 100 · คูปอง 50 ⇒ DiscountAmount = 150 ⇒ ฐานสินค้า 850
        // ค่าบริการ 10% ของ 850 = 85 ⇒ ลูกค้าจ่าย 935 (ค่าบริการไม่คืน ตามกติกาเดิม)
        var o = Order(discountPct: 10m, coupon: 50m, serviceChargePct: 10m, lineGross: new[] { 1000m });
        Assert.Equal(150m, o.DiscountAmount);
        Assert.Equal(85m, o.ServiceChargeAmount);
        Assert.Equal(935m, o.TotalAmount);

        var r = RefundAll(o);
        Assert.Equal(850m, r.Gross);                       // เฉพาะส่วนสินค้า
        Assert.Equal(0.85m, r.DiscountFactor);

        // บั๊กเดิมจะหักคูปองซ้ำ ⇒ 800 (ลูกค้าขาดไป ฿50)
        var oldGross = Math.Round(1000m * OldDiscountFactor(LineGrossTotal(o), o), 2, MidpointRounding.AwayFromZero);
        Assert.Equal(800m, oldGross);
    }

    [Fact]
    public void คืนบางส่วน_บิลมีคูปอง_ได้ตามสัดส่วนของบรรทัดนั้น()
    {
        // ป้าย 2 บรรทัด ๆ ละ 500 · คูปอง 200 ⇒ factor 0.8 ⇒ คืนบรรทัดเดียว = 400
        var o = Order(coupon: 200m, lineGross: new[] { 500m, 500m });
        var first = o.Items.First();
        var r = PosRefundMath.Compute(LineGrossTotal(o), o.DiscountAmount,
            new[] { new PosRefundLine(first.TotalAmount, first.VatAmount, first.Quantity, first.Quantity) });
        Assert.Equal(400m, r.Gross);
    }

    [Fact]
    public void คืนครึ่งบรรทัด_สัดส่วนจำนวนต้องคูณด้วย()
    {
        var o = Order(coupon: 100m, lineGross: new[] { 1000m });
        var item = o.Items.First();
        item.Quantity = 4;                                  // 4 ชิ้น รวม 1,000
        var r = PosRefundMath.Compute(LineGrossTotal(o), o.DiscountAmount,
            new[] { new PosRefundLine(item.TotalAmount, item.VatAmount, 4m, 1m) });
        Assert.Equal(225m, r.Gross);                        // 1,000 × 1/4 × 0.9
    }

    [Fact]
    public void บรรทัดที่ถูกลบไม่นับเป็นฐาน_ไม่งั้นคืนเกิน()
    {
        // PosOrderItem ไม่มี global query filter ⇒ บรรทัดที่ลบแล้วไหลมากับ Include
        var o = Order(discountPct: 10m, lineGross: new[] { 1000m });
        o.Items.Add(new PosOrderItem { Quantity = 1, SubTotal = 500m, TotalAmount = 500m, IsDeleted = true });

        Assert.Equal(1000m, LineGrossTotal(o));             // ฐานต้องไม่รวมบรรทัดที่ลบ
        Assert.Equal(900m, RefundAll(o).Gross);

        // ถ้านับบรรทัดที่ลบด้วย (พฤติกรรมเดิม) ฐาน = 1,500 ⇒ factor 0.933 ⇒ คืนเกิน
        var wrongFactor = PosRefundMath.DiscountFactor(1500m, o.DiscountAmount);
        Assert.True(wrongFactor > 0.9m);
    }

    // ─────────── ครึ่งที่ 2: บิลที่ถูกอยู่แล้ว ห้ามเปลี่ยน ───────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(5, 10)]
    [InlineData(100, 0)]
    public void ไม่มีคูปอง_ผลต้องเท่าสูตรเดิมเป๊ะ(double discountPct, double serviceChargePct)
    {
        var o = Order((decimal)discountPct, coupon: 0m, serviceChargePct: (decimal)serviceChargePct,
            lineGross: new[] { 321.50m, 678.50m });
        Assert.Equal(0m, o.CouponDiscountAmount);

        var lineGross = LineGrossTotal(o);
        var oldFactor = OldDiscountFactor(lineGross, o);
        Assert.Equal(oldFactor, PosRefundMath.DiscountFactor(lineGross, o.DiscountAmount));

        var oldGross = Math.Round(lineGross * oldFactor, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(oldGross, RefundAll(o).Gross);
    }

    [Fact]
    public void บิลไม่มีส่วนลดเลย_คืนเต็มราคาป้ายและ_VAT_ตรงกับที่ตรึงไว้()
    {
        var o = Order(lineGross: new[] { 107m });
        var r = RefundAll(o);
        Assert.Equal(1m, r.DiscountFactor);
        Assert.Equal(107m, r.Gross);
        Assert.Equal(7m, r.Vat);
        Assert.Equal(100m, r.Net);
    }

    [Fact]
    public void VAT_ที่คืนต้องลดตามสัดส่วนเดียวกับยอด()
    {
        var o = Order(coupon: 107m, lineGross: new[] { 1070m });   // factor 0.9
        var r = RefundAll(o);
        Assert.Equal(963m, r.Gross);
        Assert.Equal(63m, r.Vat);                                  // 70 × 0.9
        Assert.Equal(900m, r.Net);
    }

    // ─────────── ขอบเขต / ค่าที่ไม่มีทางถูก ───────────

    [Fact]
    public void ส่วนลดเกินราคาป้าย_ห้ามคืนติดลบ()
        => Assert.Equal(0m, PosRefundMath.DiscountFactor(1000m, 1500m));

    [Fact]
    public void บิลยอดศูนย์_ห้ามหารศูนย์()
        => Assert.Equal(1m, PosRefundMath.DiscountFactor(0m, 0m));

    [Fact]
    public void ส่วนลดติดลบ_ห้ามคืนเกินราคาป้าย()
        => Assert.Equal(1m, PosRefundMath.DiscountFactor(1000m, -200m));

    [Fact]
    public void บรรทัดจำนวนศูนย์_ข้ามไปไม่พัง()
    {
        var r = PosRefundMath.Compute(1000m, 0m, new[] { new PosRefundLine(500m, 35m, 0m, 1m) });
        Assert.Equal(0m, r.Gross);
    }
}
