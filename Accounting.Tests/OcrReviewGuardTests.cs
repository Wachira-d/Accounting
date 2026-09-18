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

    // ── ชื่อ/เลขที่ที่โมเดลเสนอ ต้องมีอยู่บนกระดาษจริง (รอบ 174) ──────────────

    private const string Paper = """
        บ ริษัท ดีแคทลอน (ประเทศไทย) จํากัด
        DECATHLON
        เลขประจําตัวผู้เสียภาษี 0-1055-35099-51-1
        ใบกํากับภาษี เลขที่ INV-2026-0091
        """;

    [Fact]
    public void ชื่อที่ปรากฏบนกระดาษ_แม้_OCR_แทรกช่องว่างกลางคำ_ต้องรับได้()
    {
        // "บ ริษัท ดีแคทลอน…" บนกระดาษ ↔ "บริษัท ดีแคทลอน (ประเทศไทย) จำกัด" ที่โมเดลเสนอ
        var json = """{"corrections":{"vendor_name":"บริษัท ดีแคทลอน (ประเทศไทย) จํากัด"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null, Paper);
        Assert.Equal("บริษัท ดีแคทลอน (ประเทศไทย) จํากัด", r.Accepted["vendor_name"]);
    }

    [Fact]
    public void ชื่อที่ไม่มีบนกระดาษ_ต้องตกไปเป็นคำแนะนำ_ไม่เขียนทับ()
    {
        // โมเดล "รู้" ว่าดีแคทลอนคือใครจากความรู้ทั่วไป แล้วเติมชื่อบริษัทแม่ที่ไม่มีบนใบ
        var json = """{"corrections":{"vendor_name":"บริษัท เดคาทลอน อินเตอร์เนชั่นแนล จำกัด"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null, Paper);
        Assert.DoesNotContain("vendor_name", r.Accepted.Keys);
        Assert.Contains(r.Rejected, x => x.Field == "vendor_name" && x.Reason.Contains("ไม่พบข้อความนี้บนกระดาษ"));
    }

    [Fact]
    public void เลขที่เอกสารที่โมเดลแต่งขึ้น_ต้องตกด่าน()
    {
        var json = """{"corrections":{"document_number":"INV-2026-0092"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null, Paper);
        Assert.DoesNotContain("document_number", r.Accepted.Keys);
    }

    [Fact]
    public void เลขที่เอกสารที่อยู่บนกระดาษ_ต้องรับได้()
    {
        var json = """{"corrections":{"document_number":"INV-2026-0091"}}""";
        var r = OcrReviewGuard.Filter(json, null, null, null, Paper);
        Assert.Equal("INV-2026-0091", r.Accepted["document_number"]);
    }

    [Fact]
    public void ไม่มีข้อความจากกระดาษให้เทียบ_ต้องไม่บล็อก()
        // ทิศตรงข้าม: สแกนที่ engine อ่านข้อความไม่ได้เลย (มีแต่ภาพ) ห้ามกลายเป็น
        // "ปฏิเสธทุกข้อเสนอ" — ด่านที่ฟ้องทุกใบ = ปิดด่านโดยไม่ตั้งใจ
        => Assert.Equal("บริษัท ก",
            OcrReviewGuard.Filter("""{"corrections":{"vendor_name":"บริษัท ก"}}""",
                null, null, null, rawText: null).Accepted["vendor_name"]);

    [Fact]
    public void กระดาษเขียนนิคหิตแยก_โมเดลตอบสระอำ_ต้องถือว่าเป็นคำเดียวกัน()
    {
        // "จํากัด" (นิคหิต ํ + สระอา า) คือสิ่งที่ OCR ไทยคืนมาเป็นปกติ ·
        // "จำกัด" (สระอำ ำ) คือสิ่งที่โมเดล/ทะเบียนตอบ — NFC **ไม่รวมให้**
        // เทสต์ชุดแรกของผมใช้รูปแยกทั้งสองฝั่งพอดี จึงเขียวโดยไม่ได้พิสูจน์เรื่องนี้
        const string paper = "บริษัท ทดสอบ จํากัด";
        var json = """{"corrections":{"vendor_name":"บริษัท ทดสอบ จำกัด"}}""";
        Assert.Equal("บริษัท ทดสอบ จำกัด",
            OcrReviewGuard.Filter(json, null, null, null, paper).Accepted["vendor_name"]);
    }

    [Fact]
    public void วรรณยุกต์ที่_OCR_ทำหล่น_ต้องไม่ทำให้ตกด่าน()
        // "เทรดดิง" บนกระดาษ ↔ "เทรดดิ้ง" ที่โมเดลเสนอ — ด่านนี้มีไว้จับชื่อที่
        // แต่งขึ้นทั้งก้อน ไม่ใช่จับการสะกดวรรณยุกต์
        => Assert.Equal("บริษัท เอบีซี เทรดดิ้ง จำกัด",
            OcrReviewGuard.Filter(
                """{"corrections":{"vendor_name":"บริษัท เอบีซี เทรดดิ้ง จำกัด"}}""",
                null, null, null, "บริษัท เอบีซี เทรดดิง จํากัด").Accepted["vendor_name"]);

    [Fact]
    public void พยัญชนะคนละตัว_ยังต้องตกด่าน()
        // ทิศตรงข้าม: การผ่อนเรื่องวรรณยุกต์ต้องไม่กลายเป็น "รับทุกอย่าง"
        => Assert.DoesNotContain("vendor_name",
            OcrReviewGuard.Filter(
                """{"corrections":{"vendor_name":"บริษัท เอกซ์วายแซด จำกัด"}}""",
                null, null, null, "บริษัท เอบีซี เทรดดิง จํากัด").Accepted.Keys);
}
