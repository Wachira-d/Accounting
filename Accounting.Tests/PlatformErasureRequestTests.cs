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

    // ═══════ ฝ่ายค้านรอบ 214 (ฑ) ═══════

    [Fact]
    public void Audit_redaction_masks_pii_fields_per_entity_only()
    {
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value("User", "Email", "somchai@example.com"));
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value("PlatformErasureRequest", "Reason", "เลิกใช้"));
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value("PdpaConsentRecord", "IpAddress", "203.0.113.1"));
        Assert.Null(AuditRedaction.Value("User", "Email", null));                    // ตั้ง/ล้าง ยังเห็น
        Assert.Equal("", AuditRedaction.Value("User", "Phone", ""));
        Assert.Equal("ร้าน ก", AuditRedaction.Value("Company", "Name", "ร้าน ก"));      // ทิศตรงข้าม: entity อื่น/ช่องอื่นไม่ถูกแตะ
        Assert.Equal("Active", AuditRedaction.Value("User", "Status", "Active"));
        Assert.Equal(AuditRedaction.Mask, AuditRedaction.Value("User", "PasswordHash", "h"));   // กติกาความลับเดิมยังทำงาน
    }

    [Fact]
    public void Token_gate_rejects_erased_or_inactive_users_only()
    {
        Assert.Null(TokenUserGate.Reject(false, default, false));                    // token ที่ subject ไม่ใช่ User — ไม่ตัดสิน
        Assert.NotNull(TokenUserGate.Reject(true, Accounting.Models.Enums.UserStatus.Inactive, false));
        Assert.NotNull(TokenUserGate.Reject(true, Accounting.Models.Enums.UserStatus.Active, true));
        Assert.Null(TokenUserGate.Reject(true, Accounting.Models.Enums.UserStatus.Active, false));
        Assert.Null(TokenUserGate.Reject(true, Accounting.Models.Enums.UserStatus.Suspended, false));
    }

    [Fact]
    public void Notify_address_prefers_current_real_email()
    {
        Assert.Equal("new@example.com", PlatformErasureRequestPolicy.NotifyAddress("new@example.com", "old@example.com"));
        Assert.Equal("old@example.com", PlatformErasureRequestPolicy.NotifyAddress("anon-1@deleted.local", "old@example.com"));
        Assert.Null(PlatformErasureRequestPolicy.NotifyAddress(null, "anon-2@deleted.local"));
        Assert.Contains("บริษัท ก", PlatformErasureRequestPolicy.OwnershipBlockMessage(new[] { "บริษัท ก" }));
    }
}
