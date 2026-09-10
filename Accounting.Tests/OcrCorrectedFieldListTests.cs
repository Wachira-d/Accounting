using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกตัวป้อนของ KPI คู่ (`OcrQualityKpi`): "ช่องไหนที่**คน**แก้" — นับเฉพาะช่องที่ส่งค่ามา
/// (null = ไม่แตะ) และการรวมหลายรอบต้องไม่นับซ้ำ. ตัวชี้วัดเดิมนับจาก `UpdatedAt` ที่ระบบเองขยับ
/// ⇒ อัตราแก้ ≈ 100% ตลอดกาล — ถ้าตัวนี้เพี้ยน ตัวชี้วัดคุณภาพทั้งไปป์ไลน์อ่านไม่ได้
/// </summary>
public class OcrCorrectedFieldListTests
{
    [Fact]
    public void คำขอที่ไม่แก้อะไรเลย_ต้องได้ลิสต์ว่าง()
    {
        Assert.Empty(OcrCorrectedFieldList.From(new OcrCorrectionRequest()));
    }

    [Fact]
    public void นับเฉพาะช่องที่ส่งค่ามา_ไม่นับNotes()
    {
        var fields = OcrCorrectedFieldList.From(new OcrCorrectionRequest(
            VendorName: "หจก. แอม แฮปปี้เนส",
            TotalAmount: 667m,
            Notes: "เหตุผลทางธุรกิจที่ผู้ใช้พิมพ์เอง"));
        Assert.Equal(new[] { "VendorName", "TotalAmount" }, fields);
    }

    [Fact]
    public void ค่าเท็จหรือศูนย์ก็นับว่าแก้_เพราะไม่ใช่null()
    {
        // ผู้ใช้ปลดติ๊ก "มีหัก ณ ที่จ่าย" = แก้จริง แม้ค่าเป็น false
        var fields = OcrCorrectedFieldList.From(new OcrCorrectionRequest(HasWht: false, SubTotal: 0m));
        Assert.Contains("HasWht", fields);
        Assert.Contains("SubTotal", fields);
    }

    [Fact]
    public void รวมหลายรอบ_ไม่ซ้ำ_เรียงตามตัวอักษร()
    {
        var merged = OcrCorrectedFieldList.Merge("VendorName, TotalAmount", new[] { "TotalAmount", "BuyerName" });
        Assert.Equal("BuyerName,TotalAmount,VendorName", merged);
        Assert.Equal("BuyerName", OcrCorrectedFieldList.Merge(null, new[] { "BuyerName" }));
        Assert.Equal("", OcrCorrectedFieldList.Merge("  ", Array.Empty<string>()));
    }
}
