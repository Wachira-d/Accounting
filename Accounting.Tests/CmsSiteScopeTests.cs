using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เว็บที่ผู้ใช้เห็นในมุมมอง "ทุกไซต์" (<see cref="CmsSiteScope"/> · ทีม W4 · R-45) — กติกาเดียวกับ <c>[RequireSiteRole]</c> รายเว็บ
///
/// <para>เทสต์ทิศตรงข้าม (เกณฑ์เสร็จ W4): ผู้ใช้ที่ไม่มีบทบาทบนเว็บ B ต้องไม่เห็น B · บทบาทไม่ตรงชุดที่ยอม (เช่น Editor ดูออเดอร์) ไม่เห็น ·
/// สิทธิ์ที่ปิดใช้งาน/เว็บของบริษัทอื่นไม่เปิดอะไร · เจ้าของเห็นทุกเว็บ</para>
/// </summary>
public class CmsSiteScopeTests
{
    private static readonly System.Guid A = System.Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly System.Guid B = System.Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly System.Guid C = System.Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly System.Guid Other = System.Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    [Fact]
    public void เจ้าของบริษัท_เห็นทุกเว็บของบริษัท_ไม่ต้องมีแถวสิทธิ์()
    {
        var ids = CmsSiteScope.AllowedSiteIds(true, new[] { A, B, C }, System.Array.Empty<CmsSiteScope.AccessRow>(), CmsSiteScope.OrderRoles);
        Assert.Equal(new[] { A, B, C }, ids);
    }

    [Fact]
    public void ทิศตรงข้าม_พนักงานมีสิทธิ์เฉพาะA_ไม่เห็นB()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.OrderManager, true) };
        var ids = CmsSiteScope.AllowedSiteIds(false, new[] { A, B, C }, access, CmsSiteScope.OrderRoles);
        Assert.Equal(new[] { A }, ids);
        Assert.DoesNotContain(B, ids);
    }

    [Fact]
    public void บทบาทไม่ตรงชุดที่ยอม_ไม่เห็น_แต่ชุดว่างคือบทบาทใดก็ได้()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.Editor, true) };
        Assert.Empty(CmsSiteScope.AllowedSiteIds(false, new[] { A }, access, CmsSiteScope.OrderRoles));
        Assert.Equal(new[] { A }, CmsSiteScope.AllowedSiteIds(false, new[] { A }, access, CmsSiteScope.AnyRole));
    }

    [Fact]
    public void สิทธิ์ที่ปิดใช้งาน_ไม่เปิดเว็บ()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.Admin, false) };
        Assert.Empty(CmsSiteScope.AllowedSiteIds(false, new[] { A }, access, CmsSiteScope.AnyRole));
    }

    [Fact]
    public void สิทธิ์บนเว็บที่ไม่ใช่ของบริษัทนี้_ถูกตัดด้วยรายการเว็บของบริษัท()
    {
        var access = new[] { new CmsSiteScope.AccessRow(Other, SiteStaffRole.Admin, true), new CmsSiteScope.AccessRow(B, SiteStaffRole.Admin, true) };
        var ids = CmsSiteScope.AllowedSiteIds(false, new[] { A, B }, access, CmsSiteScope.AnyRole);
        Assert.Equal(new[] { B }, ids);
    }

    [Fact]
    public void ไม่มีสิทธิ์เลย_คืนรายการว่าง_ไม่ใช่ทุกเว็บ()
    {
        Assert.Empty(CmsSiteScope.AllowedSiteIds(false, new[] { A, B }, System.Array.Empty<CmsSiteScope.AccessRow>(), CmsSiteScope.AnyRole));
    }

    [Fact]
    public void รายการเว็บไม่ซ้ำ_และคงลำดับของบริษัท()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.Admin, true), new CmsSiteScope.AccessRow(A, SiteStaffRole.Viewer, true), new CmsSiteScope.AccessRow(C, SiteStaffRole.Admin, true) };
        var ids = CmsSiteScope.AllowedSiteIds(false, new[] { C, A, A, B }, access, CmsSiteScope.AnyRole);
        Assert.Equal(new[] { C, A }, ids);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(5000, 200)]
    public void ClampPageSize_บีบเข้าช่วง(int input, int expected) => Assert.Equal(expected, CmsSiteScope.ClampPageSize(input));

    [Fact]
    public void ชุดบทบาทออเดอร์_ตรงกับRequireSiteRoleของGETorders()
    {
        Assert.Equal(new[] { SiteStaffRole.Admin, SiteStaffRole.OrderManager }, CmsSiteScope.OrderRoles);
    }

    // ── รอบ 215 ฝ่ายค้าน S1: รายการเว็บไซต์ (ตัวสลับเว็บ) กรองตามสิทธิ์ ──

    [Fact]
    public void รายการเว็บ_พนักงานมีบทบาทเฉพาะA_ไม่เห็นชื่อBและC()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.Viewer, true) };
        Assert.Equal(new[] { A }, CmsSiteScope.ListableSiteIds(false, new[] { A, B, C }, access, System.Array.Empty<System.Guid>()));
    }

    [Fact]
    public void รายการเว็บ_ทิศตรงข้าม_เจ้าของเห็นทุกเว็บ_ผู้สร้างเห็นเว็บที่ตัวเองสร้าง()
    {
        Assert.Equal(new[] { A, B, C }, CmsSiteScope.ListableSiteIds(true, new[] { A, B, C }, System.Array.Empty<CmsSiteScope.AccessRow>(), System.Array.Empty<System.Guid>()));
        Assert.Equal(new[] { B }, CmsSiteScope.ListableSiteIds(false, new[] { A, B, C }, System.Array.Empty<CmsSiteScope.AccessRow>(), new[] { B }));
    }

    [Fact]
    public void รายการเว็บ_สิทธิ์ปิดใช้_และเว็บบริษัทอื่น_ไม่เปิดอะไร()
    {
        var access = new[] { new CmsSiteScope.AccessRow(A, SiteStaffRole.Admin, false), new CmsSiteScope.AccessRow(Other, SiteStaffRole.Admin, true) };
        Assert.Empty(CmsSiteScope.ListableSiteIds(false, new[] { A, B }, access, new[] { Other }));
    }
}
