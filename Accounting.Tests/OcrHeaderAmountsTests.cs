using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เลขจริงจากสแกน 2026-09-10 (บริษัท ลักกี้ เวย์ · HS6909180): กระดาษพิมพ์ “รวมทั้งสิ้น 667.00”
/// ก่อน “ราคาสินค้า 623.36” ⇒ engine ติดป้าย SubTotal/Total สลับ · เทสต์ล็อก**ทั้งสองทิศ**:
/// ใบที่สลับต้องถูกสลับกลับ · ใบที่ถูกอยู่แล้ว (รวม Makro ผสมยกเว้น · ใบ 0%) ห้ามถูกแตะ
/// </summary>
public class OcrHeaderAmountsTests
{
    [Fact]
    public void ลักกี้เวย์_667_43_64_623_36_คือป้ายสลับ()
    {
        Assert.True(OcrHeaderAmounts.IsSwapped(667m, 43.64m, 623.36m));
        var (sub, vat, total, swapped) = OcrHeaderAmounts.Normalize(667m, 43.64m, 623.36m);
        Assert.True(swapped);
        Assert.Equal(623.36m, sub);
        Assert.Equal(43.64m, vat);
        Assert.Equal(667m, total);
    }

    [Theory]
    [InlineData(1000, 49, 1049)]        // Makro ผสม 7%/ยกเว้น — ถูกอยู่แล้ว
    [InlineData(1000, 70, 1070)]        // มาตรฐาน
    [InlineData(50000, 0, 50000)]       // ส่งออก 0% — VAT=0 ห้ามสลับ
    [InlineData(623.36, 43.64, 667)]    // ป้ายถูกแล้ว (หลังสลับ) ต้องเงียบ ไม่สลับกลับไปกลับมา
    [InlineData(700, 43.64, 623.36)]    // sub > total แต่คณิตไม่ลงตัว = อ่านผิดคนละเรื่อง ห้ามเดา
    public void ใบที่ไม่ใช่ป้ายสลับ_ต้องไม่ถูกแตะ(decimal sub, decimal vat, decimal total)
    {
        Assert.False(OcrHeaderAmounts.IsSwapped(sub, vat, total));
        var n = OcrHeaderAmounts.Normalize(sub, vat, total);
        Assert.False(n.Swapped);
        Assert.Equal(sub, n.SubTotal);
        Assert.Equal(total, n.Total);
    }

    [Fact]
    public void ค่าว่างหรือศูนย์_ไม่สลับ()
    {
        Assert.False(OcrHeaderAmounts.IsSwapped(null, 43.64m, 623.36m));
        Assert.False(OcrHeaderAmounts.IsSwapped(667m, null, 623.36m));
        Assert.False(OcrHeaderAmounts.IsSwapped(667m, 0m, 623.36m));
    }

    [Fact]
    public void ตัวจำแนกบรรทัด_เห็นใบลักกี้เวย์เป็นราคารวมVAT_ไม่ใช่ตรงSubTotal()
    {
        // Σ บรรทัด 239+239+189 = 667 · หัวใบที่ persist ไว้สลับ (sub 667 / total 623.36)
        // เดิม: |667 − 667| = 0 ⇒ เคส B “ราคาแยก VAT” ⇒ เอกสารเอา 239 เป็นราคาก่อน VAT แล้วบวก VAT ซ้ำ
        var r = OcrLineReconciler.Classify(grossSum: 667m, headerSubTotal: 667m, headerVat: 43.64m, headerTotal: 623.36m);
        Assert.Equal(OcrLineReconcileCase.PricesIncludeVat, r.Case);
        Assert.True(r.PricesIncludeVat);
        Assert.Equal(0m, r.UnreconciledGap);
    }

    [Fact]
    public void ตัวจำแนกบรรทัด_ใบMakroยังเป็นเคสBเหมือนเดิม()
    {
        var r = OcrLineReconciler.Classify(1000m, 1000m, 49m, 1049m);
        Assert.Equal(OcrLineReconcileCase.LinesMatchSubTotal, r.Case);
        Assert.False(r.PricesIncludeVat);
    }
}
