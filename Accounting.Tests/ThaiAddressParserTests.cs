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
