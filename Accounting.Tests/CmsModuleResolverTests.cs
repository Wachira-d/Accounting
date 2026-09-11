using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>ตัวตัดสิน "โมดูล CMS ที่ใช้อยู่" — ด่านของเมนูหลัก (cmsModule:) และแท็บ cms-edit.
/// เคสที่ต้องล็อกที่สุด: เว็บที่พักเป็น SiteType.Booking แต่ต้องได้ lodging ไม่ใช่ bookings</summary>
public class CmsModuleResolverTests
{
    private static CmsModuleFacts None => new(false, false, false, false, false, false, false);

    [Fact]
    public void ไม่มีเว็บเลย_ต้องไม่มีโมดูลใด()
    {
        Assert.Empty(CmsModuleResolver.Resolve(None));
    }

    [Fact]
    public void เว็บที่พัก_ได้_lodging_และ_leads_แต่ไม่ได้_bookings()
    {
        var mods = CmsModuleResolver.Resolve(None with { HasHotelSite = true, HasLodgingProperty = true, HasAnySite = true });
        Assert.Contains(CmsModuleResolver.Lodging, mods);
        Assert.Contains(CmsModuleResolver.Leads, mods);
        Assert.DoesNotContain(CmsModuleResolver.Bookings, mods);
        Assert.DoesNotContain(CmsModuleResolver.Orders, mods);
    }

    [Fact]
    public void เว็บที่พักที่_property_ถูกลบไปแล้ว_ยังนับเป็น_lodging_ตามเจตนา()
    {
        var mods = CmsModuleResolver.Resolve(None with { HasHotelSite = true, HasAnySite = true });
        Assert.Contains(CmsModuleResolver.Lodging, mods);
    }

    [Fact]
    public void ที่พักที่ยังไม่ผูกเว็บ_ก็ต้องเห็นเมนูที่พัก()
    {
        var mods = CmsModuleResolver.Resolve(None with { HasLodgingProperty = true });
        Assert.Equal(new[] { CmsModuleResolver.Lodging }, mods);
    }

    [Fact]
    public void สปาที่มีบริการจองคิว_ได้_bookings_ไม่ได้_lodging()
    {
        var mods = CmsModuleResolver.Resolve(None with { HasBookingServices = true, HasAnySite = true });
        Assert.Contains(CmsModuleResolver.Bookings, mods);
        Assert.DoesNotContain(CmsModuleResolver.Lodging, mods);
    }

    [Theory]
    [InlineData(SiteType.Ecommerce, true)]
    [InlineData(SiteType.ServiceCatalog, true)]
    [InlineData(SiteType.Hybrid, true)]
    [InlineData(SiteType.Corporate, false)]
    [InlineData(SiteType.Booking, false)]
    public void ชนิดเว็บที่ขายของ_เปิด_orders_ตั้งแต่ยังไม่มีออเดอร์(SiteType t, bool expected)
    {
        Assert.Equal(expected, CmsModuleResolver.IsCommerceSiteType(t));
        var mods = CmsModuleResolver.Resolve(None with { HasCommerceSite = CmsModuleResolver.IsCommerceSiteType(t), HasAnySite = true });
        Assert.Equal(expected, mods.Contains(CmsModuleResolver.Orders));
    }

    [Fact]
    public void ลำดับผลลัพธ์คงที่_orders_bookings_lodging_leads()
    {
        var mods = CmsModuleResolver.Resolve(new CmsModuleFacts(true, true, true, true, true, true, true));
        Assert.Equal(new[] { "orders", "bookings", "lodging", "leads" }, mods);
    }
}
