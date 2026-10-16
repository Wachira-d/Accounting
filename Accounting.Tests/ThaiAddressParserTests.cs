using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ThaiAddressParser — เน้น fallback สำหรับที่อยู่ "ไม่มีคำนำหน้า" (ต./อ./จ.)
/// ที่ contact/company import/OCR เก็บเป็นก้อนเดียว ทำให้ 50 ทวิ/ใบกำกับพิมพ์
/// ที่อยู่โดยไม่มีคำนำหน้า (เคสที่ผู้ใช้รายงาน). ครอบ IMP-U + DOC output ตาม TEST_PLAN
/// </summary>
public class ThaiAddressParserTests
{
    // ── เคสที่ผู้ใช้รายงาน: prefix-less ต่างจังหวัด ──────────────────────

    [Fact]
    public void Prefixless_provincial_address_splits_into_parts()
    {
        var p = ThaiAddressParser.Parse("44 หมู่ 9 หนองเทียง พนัสนิคม ชลบุรี 20140");

        Assert.Equal("44", p.BuildingNumber);
        Assert.Equal("9", p.Moo);
        Assert.Equal("หนองเทียง", p.SubDistrict);
        Assert.Equal("พนัสนิคม", p.District);
        Assert.Equal("ชลบุรี", p.Province);
        Assert.Equal("20140", p.PostalCode);
    }

    [Fact]
    public void Prefixless_address_province_is_matched_from_the_end()
    {
        // ยึดจังหวัดจาก token ท้าย (รายชื่อ 77 จังหวัด) แล้วอนุมานอำเภอ/ตำบล
        var p = ThaiAddressParser.Parse("99 บางแก้ว บางพลี สมุทรปราการ 10540");
        Assert.Equal("สมุทรปราการ", p.Province);
        Assert.Equal("บางพลี", p.District);
        Assert.Equal("บางแก้ว", p.SubDistrict);
    }

    // ── ต้องไม่รบกวนที่อยู่ที่มีคำนำหน้าถูกต้องอยู่แล้ว ──────────────────

    [Fact]
    public void Prefixed_address_is_unaffected_by_the_fallback()
    {
        var p = ThaiAddressParser.Parse("99 หมู่ 5 ต.บางพลี อ.บางพลี จ.สมุทรปราการ 10540");
        Assert.Equal("บางพลี", p.SubDistrict);
        Assert.Equal("บางพลี", p.District);
        Assert.Equal("สมุทรปราการ", p.Province);
    }

    [Fact]
    public void Bangkok_prefixed_address_still_parses()
    {
        var p = ThaiAddressParser.Parse("123/45 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110");
        Assert.Equal("คลองเตย", p.SubDistrict);
        Assert.Equal("คลองเตย", p.District);
        Assert.Equal("กรุงเทพมหานคร", p.Province);
    }

    [Fact]
    public void District_followed_by_province_without_prefix_does_not_swallow_the_province()
    {
        // ทิศเดียวกับ Bangkok_prefixed: token ที่สองเป็นชื่อจังหวัดไม่มี จ. นำ ⇒ ไม่ใช่ชื่ออำเภอสองคำ
        var p = ThaiAddressParser.Parse("44 หมู่ 9 ตำบลหนองเทียง อำเภอพนัสนิคม ชลบุรี 20140");
        Assert.Equal("หนองเทียง", p.SubDistrict);
        Assert.Equal("พนัสนิคม", p.District);
        Assert.Equal("ชลบุรี", p.Province);
    }

    [Fact]
    public void Opposite_direction_prefixed_markers_still_split_cleanly()
    {
        // ทิศตรงข้าม: ที่อยู่ที่มี marker ครบทุกช่องต้องได้ผลเดิมทุกตัว (ไม่ถูกแตะโดยการแก้ token ที่สอง)
        var p = ThaiAddressParser.Parse("88 ถ.พระราม 4 แขวงสีลม เขตบางรัก กรุงเทพมหานคร 10500");
        Assert.Equal("สีลม", p.SubDistrict);
        Assert.Equal("บางรัก", p.District);
        Assert.Equal("กรุงเทพมหานคร", p.Province);
        var q = ThaiAddressParser.Parse("1 ต.ช้างเผือก อ.เมือง จ.เชียงใหม่ 50300");
        Assert.Equal("ช้างเผือก", q.SubDistrict);
        Assert.Equal("เมือง", q.District);
        Assert.Equal("เชียงใหม่", q.Province);
    }

    [Fact]
    public void Second_token_that_is_a_road_soi_moo_or_phone_is_not_part_of_the_area_name()
    {
        // ทิศตรงข้ามของการเปิดให้ token ที่สอง: character class เดิมกันคำพวกนี้ไว้โดยบังเอิญ — ห้ามถดถอย
        var p = ThaiAddressParser.Parse("1 ต.สุเทพ ถ.ห้วยแก้ว อ.เมือง จ.เชียงใหม่ 50200");
        Assert.Equal("สุเทพ", p.SubDistrict);
        Assert.Equal("เมือง", p.District);
        var q = ThaiAddressParser.Parse("5 แขวงบางนา ซอย 3 เขตบางนา กรุงเทพฯ 10260 โทร 02-111-2222");
        Assert.Equal("บางนา", q.SubDistrict);
        Assert.Equal("บางนา", q.District);
        var r = ThaiAddressParser.Parse("9 ตำบลบ้านเหล่า หมู่ 4 อำเภอเมือง โทร 081");
        Assert.Equal("บ้านเหล่า", r.SubDistrict);
        Assert.Equal("เมือง", r.District);
    }

    // ── ขอบเขต fallback ──────────────────────────────────────────────

    [Fact]
    public void Province_only_prefixless_sets_province_without_guessing_locality()
    {
        // มีแค่ชื่อจังหวัด → ตั้งจังหวัด ไม่เดาตำบล/อำเภอจากเลขบ้าน
        var p = ThaiAddressParser.Parse("เชียงใหม่");
        Assert.Equal("เชียงใหม่", p.Province);
        Assert.Null(p.District);
        Assert.Null(p.SubDistrict);
    }

    [Fact]
    public void Unknown_province_name_is_not_forced()
    {
        // ไม่มี marker และไม่มี token ไหนตรงรายชื่อ 77 จังหวัด → ไม่เดา
        var p = ThaiAddressParser.Parse("123 หมู่บ้านสุขใจ โซนเอ ก้อนเดียว");
        Assert.Null(p.Province);
        Assert.Null(p.District);
    }

    [Fact]
    public void Empty_input_returns_all_null()
    {
        var p = ThaiAddressParser.Parse("");
        Assert.Null(p.Province);
        Assert.Null(p.District);
        Assert.Null(p.SubDistrict);
        Assert.Null(p.PostalCode);
    }

    [Fact]
    public void Thai_numeral_moo_is_normalized()
    {
        var p = ThaiAddressParser.Parse("44 หมู่ ๙ หนองเทียง พนัสนิคม ชลบุรี 20140");
        Assert.Equal("9", p.Moo);
    }
}
