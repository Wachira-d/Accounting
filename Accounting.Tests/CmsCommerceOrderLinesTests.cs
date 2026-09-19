using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ออเดอร์หน้าร้าน (CMS storefront) → บรรทัดเอกสาร ERP** (`CmsCommerceService.BuildOrderErpLines`)
///
/// ═══ ที่มา (รอบ 184 · P0 — ผู้ใช้เสียเงินอยู่จริงตอนตั้งทีม) ═══
/// เดิมส่วนลดถูกส่งเป็น**บรรทัดติดลบ** (<c>UnitPrice: -order.DiscountAmount</c>) แต่
/// <c>DocumentService.ValidateDocumentLinesAsync</c> โยน "ราคาต่อหน่วยต้องไม่ติดลบ" เสมอ ⇒
/// <b>ออเดอร์หน้าร้านทุกใบที่มีส่วนลด จ่ายเงินแล้วแต่ไม่เคยมีเอกสาร ไม่มี JE ไม่มีลูกหนี้
/// ไม่มีภาษีขาย</b>
///
/// เทสต์สองครึ่งตามกฎเหล็ก #4 H:
///   • ครึ่งที่พิสูจน์ว่าออเดอร์ที่มีส่วนลด/ค่าส่ง **สร้างเอกสารได้** (ทุกบรรทัดผ่าน
///     <c>DocumentLineKind.Judge</c> ซึ่งเป็นด่านเดียวกับที่ <c>DocumentService</c> ใช้)
///     และยอดรวมเท่าเงินที่ลูกค้าจ่ายเป๊ะ
///   • ครึ่งที่พิสูจน์ว่าออเดอร์ธรรมดา (ไม่มีส่วนลด) ได้บรรทัด**เหมือนเดิมทุกสตางค์**
/// </summary>
public class CmsCommerceOrderLinesTests
{
    private static SiteOrder Order(decimal shipping, decimal discount, params (decimal Qty, decimal Price)[] items)
    {
        var o = new SiteOrder { ShippingAmount = shipping, DiscountAmount = discount };
        var n = 1;
        foreach (var (qty, price) in items)
            o.Lines.Add(new SiteOrderLine
            {
                LineOrder = n,
                ProductName = $"สินค้า {n++}",
                Quantity = qty,
                Unit = "ชิ้น",
                UnitPrice = price,
                VatRate = 7m,
                VatAmount = qty * price * 7m / 107m,
                TotalAmount = qty * price,
            });
        // สูตรเดียวกับ CmsCommerceService (ราคาในตะกร้าเป็นราคารวม VAT)
        o.SubTotal = o.Lines.Sum(l => l.TotalAmount - l.VatAmount);
        o.VatAmount = o.Lines.Sum(l => l.VatAmount);
        o.TotalAmount = o.Lines.Sum(l => l.TotalAmount) + o.ShippingAmount - o.DiscountAmount;
        if (o.TotalAmount < 0m) o.TotalAmount = 0m;
        return o;
    }

    /// <summary>ยอดรวมของเอกสารที่ <c>DocumentService.ComputeLineAmounts</c> จะได้จากบรรทัดชุดนี้
    /// เมื่อ <c>PricesIncludeVat=true</c> — ในโหมดนั้น net+VAT ของบรรทัด = gross − ส่วนลด เป๊ะ
    /// (net = round(afterDiscount × 100/107), VAT = afterDiscount − net)</summary>
    private static decimal DocumentTotal(IEnumerable<Accounting.Models.DTOs.Document.DocumentLineRequest> lines)
        => lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero)
                          - (l.DiscountAmount ?? 0m));

    // ─────────── ครึ่งที่ 1: ออเดอร์ที่พังต้องสร้างเอกสารได้ ───────────

    [Fact]
    public void ออเดอร์มีส่วนลด_ทุกบรรทัดต้องผ่านด่านเดียวกับ_DocumentService()
    {
        var o = Order(shipping: 0m, discount: 100m, (1m, 600m), (1m, 400m));
        var lines = CmsCommerceService.BuildOrderErpLines(o);

        Assert.All(lines, l =>
        {
            var v = DocumentLineKind.Judge(l.Quantity, l.UnitPrice, l.DiscountAmount ?? 0m);
            Assert.True(v.Ok, v.Reason);
        });
        // พฤติกรรมเดิมที่ห้ามกลับมา: บรรทัด "ส่วนลด" ที่ราคาต่อหน่วยติดลบ
        Assert.DoesNotContain(lines, l => l.UnitPrice < 0m);
        Assert.Equal(2, lines.Count);                       // ไม่มีบรรทัดปรับปรุงงอกขึ้นมา
    }

    [Fact]
    public void ยอดเอกสารต้องเท่ากับเงินที่ลูกค้าจ่ายเป๊ะ_รวมค่าส่งและส่วนลด()
    {
        var o = Order(shipping: 50m, discount: 100m, (2m, 300m), (1m, 400m));
        Assert.Equal(950m, o.TotalAmount);                  // 1,000 + 50 − 100
        Assert.Equal(950m, DocumentTotal(CmsCommerceService.BuildOrderErpLines(o)));
    }

    [Fact]
    public void ส่วนลดที่หารไม่ลงตัวต้องไม่ทำให้ยอดขาดหรือเกินแม้สตางค์เดียว()
    {
        var o = Order(shipping: 0m, discount: 33.33m, (1m, 333.33m), (1m, 111.11m), (1m, 555.56m));
        var lines = CmsCommerceService.BuildOrderErpLines(o);
        Assert.Equal(o.TotalAmount, DocumentTotal(lines));
        Assert.Equal(33.33m, lines.Sum(l => l.DiscountAmount ?? 0m));
    }

    [Fact]
    public void ส่วนลดเกินยอดบิล_ต้องไม่ทำให้บรรทัดไหนติดลบ()
    {
        var o = Order(shipping: 0m, discount: 5000m, (1m, 100m));
        Assert.Equal(0m, o.TotalAmount);                    // storefront clamp ที่ 0
        var lines = CmsCommerceService.BuildOrderErpLines(o);
        Assert.All(lines, l => Assert.True(DocumentLineKind.Judge(l.Quantity, l.UnitPrice, l.DiscountAmount ?? 0m).Ok));
        Assert.Equal(0m, DocumentTotal(lines));
        Assert.Equal(100m, lines.Sum(l => l.DiscountAmount ?? 0m));   // หักได้มากสุด = ยอดบิล
    }

    [Fact]
    public void บริษัทไม่จด_VAT_ทุกบรรทัดต้องอัตรา_0_และส่วนลดยังเฉลี่ยครบ()
    {
        var o = Order(shipping: 0m, discount: 100m, (1m, 1000m));
        var lines = CmsCommerceService.BuildOrderErpLines(o, vatRegistered: false);
        Assert.All(lines, l => Assert.Equal(0m, l.VatRate));
        Assert.Equal(100m, lines.Sum(l => l.DiscountAmount ?? 0m));
        Assert.Equal(o.TotalAmount, DocumentTotal(lines));
    }

    // ─────────── ครึ่งที่ 2: ออเดอร์ที่ถูกอยู่แล้ว ห้ามขยับ ───────────

    [Fact]
    public void ออเดอร์ไม่มีส่วนลด_บรรทัดต้องเหมือนเดิมทุกสตางค์และไม่มีส่วนลดงอก()
    {
        var o = Order(shipping: 0m, discount: 0m, (2m, 107m), (1m, 214m));
        var lines = CmsCommerceService.BuildOrderErpLines(o);

        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { "สินค้า 1", "สินค้า 2" }, lines.Select(l => l.Description).ToArray());
        Assert.Equal(new[] { 2m, 1m }, lines.Select(l => l.Quantity).ToArray());
        Assert.Equal(new[] { 107m, 214m }, lines.Select(l => l.UnitPrice).ToArray());
        Assert.All(lines, l => Assert.Equal(7m, l.VatRate));
        Assert.All(lines, l => Assert.Null(l.DiscountAmount));   // ไม่มีส่วนลด = ไม่แตะช่องนี้เลย
        Assert.Equal(o.TotalAmount, DocumentTotal(lines));
    }

    [Fact]
    public void ออเดอร์มีค่าส่งแต่ไม่มีส่วนลด_ค่าส่งต้องเป็นบรรทัดบวกอัตรา_0_เหมือนเดิม()
    {
        var o = Order(shipping: 50m, discount: 0m, (1m, 1000m));
        var lines = CmsCommerceService.BuildOrderErpLines(o);

        Assert.Equal(2, lines.Count);
        var ship = lines[^1];
        Assert.Equal("ค่าจัดส่ง", ship.Description);
        Assert.Equal(50m, ship.UnitPrice);
        Assert.Equal(1m, ship.Quantity);
        Assert.Equal(0m, ship.VatRate);
        Assert.Null(ship.DiscountAmount);
        Assert.Equal(1050m, DocumentTotal(lines));
    }

    [Fact]
    public void ไม่มีค่าส่ง_ต้องไม่มีบรรทัดค่าจัดส่งโผล่มา()
    {
        var lines = CmsCommerceService.BuildOrderErpLines(Order(0m, 0m, (1m, 500m)));
        Assert.DoesNotContain(lines, l => l.Description == "ค่าจัดส่ง");
    }
}
