using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 206 (R-32 · คำตัดสินข้อ 165) — VAT ค่าจัดส่งบิลหลายอัตรา: ค่าเริ่มต้นอัตราสูงสุด (เดิมทุกสตางค์) · แบ่งตามสัดส่วนเมื่อร้านตั้ง · Σ = ค่าส่งเสมอ</summary>
public class ShippingVatAllocationTests
{
    private static readonly List<(decimal Gross, decimal Rate)> Mixed = new() { (700m, 7m), (300m, 0m) };   // สินค้า 7% 700 · สินค้า 0% 300

    [Fact]
    public void ค่าเริ่มต้นหรือออเดอร์เก่า_ส่วนเดียวอัตราสูงสุด_เท่าเดิม()
    {
        foreach (var mode in new[] { null, "", ShippingVatAllocation.MaxRate })
        {
            var parts = ShippingVatAllocation.Split(mode, true, 100m, Mixed);
            var p = Assert.Single(parts);
            Assert.Equal(7m, p.Rate);
            Assert.Equal(100m, p.Fee);
            Assert.Equal(ShippingFeeRules.VatInShipping(100m, 7m), ShippingVatAllocation.VatIn(parts));
            Assert.Equal("ค่าจัดส่ง", ShippingVatAllocation.LineDescription(p, parts.Count));
        }
    }

    [Fact]
    public void แบ่งตามสัดส่วน_สองอัตรา_รวมเท่าค่าส่ง_เศษเข้ากลุ่มใหญ่()
    {
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, true, 100m, Mixed);
        Assert.Equal(2, parts.Count);
        Assert.Equal(100m, parts.Sum(p => p.Fee));
        Assert.Equal(70m, parts.First(p => p.Rate == 7m).Fee);
        Assert.Equal(30m, parts.First(p => p.Rate == 0m).Fee);
        Assert.Equal(ShippingFeeRules.VatInShipping(70m, 7m), ShippingVatAllocation.VatIn(parts));
        Assert.Contains("VAT 7%", ShippingVatAllocation.LineDescription(parts[0], parts.Count));
        Assert.Contains("VAT 0%", ShippingVatAllocation.LineDescription(parts[1], parts.Count));
    }

    [Fact]
    public void แบ่งตามสัดส่วน_เศษปัด_รวมยังเท่าค่าส่ง()
    {
        var goods = new List<(decimal, decimal)> { (333.33m, 7m), (333.33m, 0m), (333.34m, ThaiVatTypeRule.ExemptRate) };
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, true, 50m, goods);
        Assert.Equal(3, parts.Count);
        Assert.Equal(50m, parts.Sum(p => p.Fee));
        Assert.Contains("ยกเว้น VAT", ShippingVatAllocation.LineDescription(parts.First(p => p.Rate == ThaiVatTypeRule.ExemptRate), 3));
    }

    [Fact]
    public void อัตราเดียวทั้งบิล_แม้ตั้งแบ่งสัดส่วน_ก็ส่วนเดียว()
    {
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, true, 100m, new List<(decimal, decimal)> { (500m, 7m), (200m, 7m) });
        var p = Assert.Single(parts);
        Assert.Equal(7m, p.Rate);
    }

    [Fact]
    public void ร้านไม่จดVAT_ส่วนเดียวอัตรา0_ไม่มีVAT()
    {
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, false, 100m, Mixed);
        var p = Assert.Single(parts);
        Assert.Equal(0m, p.Rate);
        Assert.Equal(0m, ShippingVatAllocation.VatIn(parts));
    }

    [Fact]
    public void ค่าส่งศูนย์_ไม่มีส่วน()
        => Assert.Empty(ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, true, 0m, Mixed));

    [Fact]
    public void Normalize_รับค่ารู้จัก_ค่าว่างเป็นเดิม_ค่าแปลกปฏิเสธ()
    {
        Assert.Equal(ShippingVatAllocation.MaxRate, ShippingVatAllocation.Normalize(null));
        Assert.Equal(ShippingVatAllocation.MaxRate, ShippingVatAllocation.Normalize(" "));
        Assert.Equal(ShippingVatAllocation.Proportional, ShippingVatAllocation.Normalize("Proportional"));
        Assert.Throws<BusinessRuleException>(() => ShippingVatAllocation.Normalize("split"));
        Assert.Equal(ShippingVatAllocation.MaxRate, ShippingVatAllocation.OfOrder("anything-old"));
    }
}
