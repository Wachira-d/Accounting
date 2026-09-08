using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ปุ่ม "🤖 ตรวจสอบกับ AI" เคยเขียนคำตอบโมเดลลงฟอร์มตรง ๆ ทุกช่อง รวมยอดเงินและ
/// เลขผู้เสียภาษี ⇒ ตัวเลขที่แต่งขึ้นกลายเป็นยอดบนใบกำกับ §86/4 ด้วยการกดปุ่มเดียว
/// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T3-07) — ด่านต้องอยู่ที่เซิร์ฟเวอร์
/// </summary>
public class OcrReviewGuardTests
{
    private const string TaxIdValid = "0105561012345";   // ผ่าน mod-11

    [Fact]
    public void ยอดที่ไม่ลงตัวสามเหลี่ยม_ต้องถูกปฏิเสธทั้งชุด()
    {
        var json = """{"corrections":{"sub_total":1000,"vat_amount":70,"total_amount":2000}}""";
        var r = OcrReviewGuard.Filter(json, 1000m, 70m, 1070m);
        Assert.DoesNotContain("sub_total", r.Accepted.Keys);
        Assert.DoesNotContain("total_amount", r.Accepted.Keys);
        Assert.Contains(r.Rejected, x => x.Field == "amounts");
    }

    [Fact]
    public void ยอดที่ลงตัวกับค่าที่อ่านจากกระดาษ_รับได้()
    {
        // AI แก้เฉพาะ VAT ให้ตรงกับ sub/total ที่อ่านมา
        var json = """{"corrections":{"vat_amount":70}}""";
        var r = OcrReviewGuard.Filter(json, 1000m, 0m, 1070m);
        Assert.Equal("70", r.Accepted["vat_amount"]);
        Assert.Empty(r.Rejected);
    }

    [Fact]
    public void เลขผู้เสียภาษีที่ไม่ผ่าน_mod11_ถูกปฏิเสธ()
    {
        var json = """{"corrections":{"vendor_tax_id":"1234567890123"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null);
        Assert.Empty(r.Accepted);
        Assert.Contains(r.Rejected, x => x.Field == "vendor_tax_id");
    }

    [Fact]
    public void เลขผู้เสียภาษีที่ถูกต้อง_รับได้()
    {
        var json = $$$"""{"corrections":{"vendor_tax_id":"{{{TaxIdValid}}}"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null);
        Assert.Equal(TaxIdValid, r.Accepted["vendor_tax_id"]);
    }

    [Fact]
    public void ค่าที่ถูกปิดบังตาม_PDPA_ห้ามเขียนกลับ()
    {
        var json = """{"corrections":{"vendor_tax_id":"0xxxxxxxxx5","vendor_name":"บริษัท ก"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null);
        Assert.DoesNotContain("vendor_tax_id", r.Accepted.Keys);
        Assert.Contains(r.Rejected, x => x.Field == "vendor_tax_id" && x.Reason.Contains("ปิดบัง"));
        Assert.Equal("บริษัท ก", r.Accepted["vendor_name"]);
    }

    [Theory]
    [InlineData("2026-09-05", true)]
    [InlineData("ไม่ทราบ", false)]
    [InlineData("1899-01-01", false)]
    [InlineData("2099-01-01", false)]
    public void วันที่ต้อง_parse_ได้และอยู่ในช่วงที่เป็นไปได้(string value, bool accepted)
    {
        var json = $$$"""{"corrections":{"document_date":"{{{value}}}"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null);
        Assert.Equal(accepted, r.Accepted.ContainsKey("document_date"));
    }

    [Fact]
    public void JSON_พังหรือไม่มี_corrections_คืนว่างไม่โยน()
    {
        Assert.Empty(OcrReviewGuard.Filter("ไม่ใช่ JSON", null, null, null).Accepted);
        Assert.Empty(OcrReviewGuard.Filter("""{"other":1}""", null, null, null).Accepted);
        Assert.Empty(OcrReviewGuard.Filter(null, null, null, null).Accepted);
    }
}
