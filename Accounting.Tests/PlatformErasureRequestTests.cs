using System.Reflection;
using Accounting.Controllers;
using Accounting.Helpers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฐ) — คำขอลบข้อมูลระดับแพลตฟอร์มที่ผู้ใช้ยื่นเอง: กติกา (ค้าง = คืนฉบับเดิม · ยื่นถี่ = ปฏิเสธ · SLA 30 วัน ·
/// ดำเนินการได้เฉพาะค้าง) + ด่านสิทธิ์ (ดำเนินการลบ = SystemAdmin เท่านั้น · ยื่น = ต้องล็อกอิน · ไม่มี AllowAnonymous ระดับคลาสลบล้าง)
/// </summary>
public class PlatformErasureRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Pending_request_is_returned_not_duplicated()
        => Assert.Equal(PlatformErasureRequestPolicy.Outcome.ReturnExisting, PlatformErasureRequestPolicy.Decide(true, Now.AddMinutes(-1), Now));

    [Fact]
    public void Too_soon_after_last_request_is_refused_then_allowed_after_interval()
    {
        Assert.Equal(PlatformErasureRequestPolicy.Outcome.TooSoon, PlatformErasureRequestPolicy.Decide(false, Now.AddHours(-23), Now));
        Assert.Equal(PlatformErasureRequestPolicy.Outcome.Create, PlatformErasureRequestPolicy.Decide(false, Now.AddHours(-24), Now));
        Assert.Equal(PlatformErasureRequestPolicy.Outcome.Create, PlatformErasureRequestPolicy.Decide(false, null, Now));
    }

    [Fact]
    public void Sla_is_30_days_and_only_pending_can_be_resolved()
    {
        Assert.Equal(Now.AddDays(30), PlatformErasureRequestPolicy.DueAt(Now));
        Assert.True(PlatformErasureRequestPolicy.CanResolve(PlatformErasureRequestPolicy.StatusPending));
        Assert.False(PlatformErasureRequestPolicy.CanResolve(PlatformErasureRequestPolicy.StatusCompleted));
        Assert.False(PlatformErasureRequestPolicy.CanResolve(PlatformErasureRequestPolicy.StatusRejected));
    }

    [Fact]
    public void Admin_erasure_endpoints_require_SystemAdmin()
    {
        var attr = typeof(AdminPlatformErasureController).GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault();
        Assert.NotNull(attr);
        Assert.Equal("SystemAdmin", attr!.Roles);
        Assert.Empty(typeof(AdminPlatformErasureController).GetCustomAttributes<AllowAnonymousAttribute>());
        foreach (var m in typeof(AdminPlatformErasureController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Self_service_endpoints_require_login_and_are_not_overridden_by_class_AllowAnonymous()
    {
        Assert.Empty(typeof(LegalController).GetCustomAttributes<AllowAnonymousAttribute>());
        foreach (var name in new[] { nameof(LegalController.RequestErasure), nameof(LegalController.MyData), nameof(LegalController.Accept) })
        {
            var m = typeof(LegalController).GetMethod(name)!;
            Assert.NotEmpty(m.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>());
        }
        // ทิศตรงข้าม: หน้าเอกสารสาธารณะยังเปิดได้โดยไม่ล็อกอิน
        Assert.NotEmpty(typeof(LegalController).GetMethod(nameof(LegalController.GetDocument))!.GetCustomAttributes<AllowAnonymousAttribute>());
    }
}
