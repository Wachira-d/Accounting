using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านทีม K ข้อ 2/3 — จับโซนค่าส่ง: โซนจำกัดพื้นที่ + ที่อยู่ว่าง ⇒ ไม่ตรง (เดิมตรงทุกโซน ⇒ ไม่กรอกจังหวัดได้อัตราถูกสุด) ·
/// ชื่อจังหวัดเทียบรูปมาตรฐาน (กทม./กรุงเทพฯ) · ทิศตรงข้าม: โซนไม่จำกัดพื้นที่ยังตรงเหมือนเดิมแม้ไม่กรอก
/// </summary>
public class ShippingZoneMatchTests
{
    private static SiteShippingZone Zone(string? provinces = null, string? postal = null, string? countries = null)
        => new() { Name = "z", Provinces = provinces, PostalCodePatterns = postal, CountryCodes = countries };

    private static CalculateShippingRequest Addr(string? province = null, string? postal = null, string? country = null)
        => new() { Province = province, PostalCode = postal, CountryCode = country };

    [Fact]
    public void โซนจำกัดจังหวัด_ไม่กรอกจังหวัด_ไม่ตรง()
    {
        Assert.False(CmsShippingService.ZoneMatchesAddress(Zone(provinces: "กรุงเทพมหานคร,นนทบุรี"), Addr()));
        Assert.False(CmsShippingService.ZoneMatchesAddress(Zone(postal: "10*"), Addr(province: "กรุงเทพมหานคร")));
    }

    [Theory]
    [InlineData("กทม.")]
    [InlineData("กรุงเทพฯ")]
    [InlineData("จ.กรุงเทพมหานคร")]
    [InlineData("Bangkok")]
    public void ชื่อกรุงเทพหลายแบบ_ตรงโซนกรุงเทพ(string typed)
        => Assert.True(CmsShippingService.ZoneMatchesAddress(Zone(provinces: "กรุงเทพมหานคร"), Addr(province: typed)));

    [Fact]
    public void โซนกำหนดเป็นกทม_ลูกค้าพิมพ์เต็ม_ก็ตรง_และจังหวัดอื่นไม่ตรง()
    {
        Assert.True(CmsShippingService.ZoneMatchesAddress(Zone(provinces: "กทม"), Addr(province: "กรุงเทพมหานคร")));
        Assert.False(CmsShippingService.ZoneMatchesAddress(Zone(provinces: "กทม"), Addr(province: "เชียงใหม่")));
    }

    [Fact]
    public void รหัสไปรษณีย์ตามแพตเทิร์น()
    {
        Assert.True(CmsShippingService.ZoneMatchesAddress(Zone(postal: "10*,11*"), Addr(postal: "11120")));
        Assert.False(CmsShippingService.ZoneMatchesAddress(Zone(postal: "10*,11*"), Addr(postal: "50200")));
    }

    [Fact]
    public void ทิศตรงข้าม_โซนไม่จำกัดพื้นที่_ไม่กรอกก็ตรง_และประเทศว่างถือเป็นไทย()
    {
        Assert.True(CmsShippingService.ZoneMatchesAddress(Zone(), Addr()));
        Assert.True(CmsShippingService.ZoneMatchesAddress(Zone(countries: "TH"), Addr()));
        Assert.False(CmsShippingService.ZoneMatchesAddress(Zone(countries: "LA"), Addr()));
    }

    [Fact]
    public void ตัวทำชื่อจังหวัดมาตรฐาน()
    {
        Assert.Equal("เชียงใหม่", ThaiAddressParser.NormalizeProvince(" จังหวัดเชียงใหม่ "));
        Assert.Null(ThaiAddressParser.NormalizeProvince("  "));
    }
}
