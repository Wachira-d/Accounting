using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ใบกำกับภาษีเต็มรูปจากบิล POS — ยอดบนใบต้องเท่าเงินที่ลูกค้าจ่าย **และทุกบรรทัดต้องเป็นบวก**
///
/// ═══ ที่มา รอบ 183 (D8-1 · P0) ═══
/// <c>IssueTaxInvoiceAsync</c> เดิมสร้างบรรทัดจาก <c>item.TotalAmount</c>/<c>item.VatAmount</c>
/// ซึ่งเป็นยอด **ก่อน** หักส่วนลดท้ายบิล/คูปอง และ **ไม่มี** ค่าบริการ ⇒
/// บิล 1,000 ลด 10% ลูกค้าจ่าย 900 แต่ใบกำกับเขียน 1,000
///
/// ═══ ที่มา รอบ 184 (P0 — ทีมสถาปัตยกรรมเอกสาร/ภาษีขาย) ═══
/// การแก้รอบ 183 ใช้ **บรรทัดติดลบ** แล้วประกอบ <c>Document</c> เองโดยไม่ผ่าน
/// <c>ValidateDocumentLinesAsync</c> ⇒ ระบบเดียวมีกติกาสองชุด (เส้นกลางห้ามติดลบ จน
/// ออเดอร์ CMS ที่มีส่วนลดสร้างเอกสารไม่ได้เลย · POS ติดลบได้เพราะเลี่ยงด่าน) ⇒
/// ยอดหักระดับบิลย้ายไปเฉลี่ยลงบรรทัดผ่าน <c>DocumentLineKind.AllocateDeduction</c>
///
/// เทสต์นี้มีสองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งที่พิสูจน์ว่าบิลที่พัง (มีส่วนลด/คูปอง/ค่าบริการ/ปัดเศษ) ออกใบถูกต้อง**และไม่มีบรรทัดติดลบ**
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

    /// <summary>ทุกบรรทัดต้องผ่านด่านเดียวกับที่ <c>DocumentService</c> ใช้ — นี่คือเงื่อนไข
    /// ที่ทำให้ POS ย้ายเข้าเส้นเอกสารกลางได้ในอนาคตโดยไม่ต้องผ่อนด่าน</summary>
    private static void AssertPassesCentralGate(IReadOnlyList<PosInvoiceLine> lines)
    {
        Assert.All(lines, l =>
        {
            var v = DocumentLineKind.Judge(l.Quantity, l.UnitPriceNet, 0m);
            Assert.True(v.Ok, v.Reason);
            Assert.True(l.AmountNet >= 0m, $"บรรทัด '{l.Description}' ยอดติดลบ");
            Assert.True(l.VatAmount >= 0m, $"บรรทัด '{l.Description}' VAT ติดลบ");
            Assert.True(l.VatRate == 0m || l.VatRate == 7m || l.VatRate == -1m,
                $"บรรทัด '{l.Description}' อัตรา VAT = {l.VatRate} ซึ่งไม่ใช่อัตราตามกฎหมาย");
        });
    }

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
        AssertPassesCentralGate(lines);
        Assert.Equal(2, lines.Count);                       // สินค้า 2 บรรทัด ไม่มีบรรทัดส่วนลดติดลบ
        // ส่วนลดลดฐานภาษีจริง §79 — ไม่ได้ถูกซ่อนเป็นบรรทัด VAT 0%
        Assert.Equal(o.VatAmount, lines.Sum(l => l.VatAmount));
        Assert.True(lines.Sum(l => l.DiscountNet) > 0m);
    }

    [Fact]
    public void ส่วนลดท้ายบิลต้องกลายเป็นบรรทัดติดลบไม่ได้อีก_แต่ยอดหักต้องยังอยู่ครบ()
    {
        var o = Order(discountPct: 10m, lineGross: new[] { 600m, 400m });
        var lines = Build(o);

        Assert.DoesNotContain(lines, l => l.Description == PosTaxInvoiceLines.BillDiscountLabel);
        Assert.DoesNotContain(lines, l => l.AmountNet < 0m);

        // ยอดหักยังอยู่ครบ: ส่วนลด 100 (รวม VAT) = 93.46 ก่อน VAT
        // คลาดได้ ≤ 2 สตางค์ เพราะ DiscountNet เป็นตัวเลข **สำหรับแสดงผล** (ถอด VAT
        // รายบรรทัดแล้วปัด) ส่วนตัวเลขที่ต้องเป๊ะคือ Σ(net+VAT) และ Σ VAT ซึ่งถูกล็อก
        // ไว้ในเทสต์อื่นแล้ว — ห้ามเอา DiscountNet ไปคิดยอดใด ๆ ต่อ
        var expectedDiscountNet = 100m - Math.Round(100m * VatRate / (100m + VatRate), 2, MidpointRounding.AwayFromZero);
        Assert.True(Math.Abs(expectedDiscountNet - lines.Sum(l => l.DiscountNet)) <= 0.02m,
            $"ยอดหักก่อน VAT = {lines.Sum(l => l.DiscountNet)} คาด {expectedDiscountNet}");
        Assert.Equal(900m, Gross(lines));
    }

    [Fact]
    public void บิลมีค่าบริการ_10_เปอร์เซ็นต์_ยอดต้องรวมค่าบริการ()
    {
        var o = Order(serviceChargePct: 10m, lineGross: new[] { 1000m });
        Assert.Equal(1100m, o.TotalAmount);

        var lines = Build(o);
        Assert.Equal(1100m, Gross(lines));
        AssertPassesCentralGate(lines);
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
        AssertPassesCentralGate(lines);
    }

    [Fact]
    public void คูปองต้องหักครั้งเดียว_ไม่ถูกนับซ้ำกับส่วนลดเปอร์เซ็นต์()
    {
        // ลด 10% (=100) + คูปอง 50 ⇒ DiscountAmount = 150 (คูปองรวมอยู่แล้ว)
        var o = Order(discountPct: 10m, coupon: 50m, couponCode: "SAVE50", lineGross: new[] { 1000m });
        Assert.Equal(150m, o.DiscountAmount);

        var lines = Build(o);
        Assert.Equal(850m, Gross(lines));          // หัก 150 ครั้งเดียว ไม่ใช่ 200
        AssertPassesCentralGate(lines);
        Assert.Single(lines);
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
    public void ปัดเศษขึ้นเป็นบรรทัดบวกที่ไม่มี_VAT_และทำให้ยอดตรงกับเงินที่รับ()
    {
        var o = Order(lineGross: new[] { 100.60m });
        Assert.True(o.RoundingAmount > 0m);                 // 100.60 → 101.00
        var lines = Build(o);
        var rounding = lines.Single(l => l.Description == PosTaxInvoiceLines.RoundingLabel);
        Assert.Equal(0m, rounding.VatAmount);
        Assert.Equal(0m, rounding.VatRate);
        Assert.True(rounding.AmountNet > 0m);
        Assert.Equal(o.NetAmount, Gross(lines));            // ไม่มีทิป ⇒ = เงินที่รับจริง
        AssertPassesCentralGate(lines);
    }

    [Fact]
    public void ปัดเศษลงต้องไม่กลายเป็นบรรทัดติดลบ_แต่ยอดต้องยังตรงกับเงินที่รับ()
    {
        var o = Order(lineGross: new[] { 100.40m });
        Assert.True(o.RoundingAmount < 0m);                 // 100.40 → 100.00
        var lines = Build(o);
        Assert.DoesNotContain(lines, l => l.Description == PosTaxInvoiceLines.RoundingLabel);
        Assert.Equal(o.NetAmount, Gross(lines));
        Assert.Equal(o.VatAmount, lines.Sum(l => l.VatAmount));
        AssertPassesCentralGate(lines);
    }

    [Fact]
    public void รายการที่คีย์มาติดลบต้องกลายเป็นยอดหัก_ไม่ใช่บรรทัดติดลบบนใบกำกับ()
    {
        // หน้าร้านบางที่คีย์ "ปรับยอด −50" เป็นรายการ — ห้ามหลุดเป็นบรรทัดติดลบ
        var lines = PosTaxInvoiceLines.Build(
            new[]
            {
                new PosInvoiceSourceLine("สินค้า", "P1", "ชิ้น", 1m, 1000m),
                new PosInvoiceSourceLine("ปรับยอด", null, "ครั้ง", 1m, -50m),
            },
            billDiscountAmount: 0m, couponAmount: 0m, couponCode: null,
            serviceChargeAmount: 0m, roundingAmount: 0m,
            headGrossPayable: 950m,
            headVat: Math.Round(950m * 7m / 107m, 2, MidpointRounding.AwayFromZero));
        Assert.Single(lines);
        Assert.Equal(950m, Gross(lines));
        AssertPassesCentralGate(lines);
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

    [Fact]
    public void ข้อความที่ยอดไม่ตรงต้องบอกที่มาของยอดหัก_ให้แคชเชียร์ไล่ได้()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => PosTaxInvoiceLines.Build(
            new[] { new PosInvoiceSourceLine("สินค้า", "P1", "ชิ้น", 1m, 1000m) },
            billDiscountAmount: 100m, couponAmount: 100m, couponCode: "SAVE100",
            serviceChargeAmount: 0m, roundingAmount: 0m,
            headGrossPayable: 950m,        // ของจริงต้องเป็น 900
            headVat: 58.88m));
        Assert.Contains("SAVE100", ex.Message);
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
        Assert.All(lines, l => Assert.Equal(0m, l.DiscountNet));
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
