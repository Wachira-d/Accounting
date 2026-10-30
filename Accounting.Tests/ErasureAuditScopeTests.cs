using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 214 (ฑ-2 · คำตัดสินข้อ 173) — การลบตามคำขอต้องไม่ทิ้งค่าเดิมไว้ใน audit (append-only) · แถวเก่าที่มี PII ไม่โชว์ต่อบทบาทในบริษัท ·
/// เจ้าของที่แค่ถูกระงับไม่ทำให้ระบบตั้งเจ้าของใหม่เอง · แพ็กเกจบัญชีที่ค้างมีทางไปต่อ (แอดมินยกเลิก — เฉพาะแพ็กเกจที่ไม่มีบริษัทผูก)
/// </summary>
public class ErasureAuditScopeTests
{
    [Fact]
    public void Erasure_scope_masks_every_non_empty_string_but_keeps_non_text()
    {
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.ErasureValue("สมชาย ใจดี"));
        Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.ErasureValue("[ANONYMIZED]"));
        Assert.Null(AuditRedaction.ErasureValue(null));
        Assert.Equal("", AuditRedaction.ErasureValue(""));
        Assert.True((bool)AuditRedaction.ErasureValue(true)!);
        Assert.Equal(12.5m, AuditRedaction.ErasureValue(12.5m));
    }

    [Fact]
    public void Ocr_scan_pii_list_is_the_same_list_as_the_eraser()
    {
        foreach (var f in OcrScanPii.PersonalDataFields)
            Assert.Equal(AuditRedaction.PiiMask, AuditRedaction.Value("OcrScanResult", f, "ข้อความ"));
        // ทิศตรงข้าม: ช่องที่ไม่ใช่ PII ของสแกนยังเก็บค่า
        Assert.Equal("Completed", AuditRedaction.Value("OcrScanResult", "ScanStatus", "Completed"));
    }

    [Fact]
    public void Read_time_redaction_masks_pii_of_that_entity_only()
    {
        const string userJson = "{\"Email\":\"somchai@example.com\",\"FullName\":\"สมชาย\",\"Status\":\"Active\"}";
        var masked = AuditRedaction.RedactJson(userJson, "User")!;
        Assert.DoesNotContain("somchai@example.com", masked);
        Assert.DoesNotContain("สมชาย", masked);
        Assert.Contains("Active", masked);
        // ทิศตรงข้าม: แก้ผู้ติดต่อตามปกติยังเห็นประวัติค่า (Contact ไม่อยู่ในรายการ PII ระดับแสดงผล) · ชนิดที่ไม่รู้จักไม่ถูกแตะ
        const string contactJson = "{\"Phone\":\"0812345678\"}";
        Assert.Contains("0812345678", AuditRedaction.RedactJson(contactJson, "Contact"));
        Assert.Contains("0812345678", AuditRedaction.RedactJson(contactJson, null));
        Assert.Null(AuditRedaction.RedactJson(null, "User"));
        Assert.Equal("not-json", AuditRedaction.RedactJson("not-json", "User"));
    }

    [Fact]
    public void Privacy_self_service_paths_drop_email_ip_ua_from_middleware_audit()
    {
        Assert.True(AuditRedaction.IsPrivacySelfServicePath("/api/legal/my-data/erasure-request"));
        Assert.True(AuditRedaction.IsPrivacySelfServicePath("/api/legal/acceptance"));
        Assert.False(AuditRedaction.IsPrivacySelfServicePath("/api/companies/x/documents"));
        Assert.False(AuditRedaction.IsPrivacySelfServicePath(null));
    }

    private static CompanyUser Cu(UserRole role, UserStatus status, bool deleted = false)
        => new() { Role = role, User = new User { Status = status, IsDeleted = deleted, Email = "x", PasswordHash = "x", FullName = "x" } };

    [Fact]
    public void Acting_owner_is_one_definition_for_list_assign_and_erasure()
    {
        var f = OwnerlessCompanyRepair.ActingOwner.Compile();
        Assert.True(f(Cu(UserRole.Owner, UserStatus.Active)));
        Assert.True(f(Cu(UserRole.SystemAdmin, UserStatus.PendingVerification)));
        Assert.False(f(Cu(UserRole.Owner, UserStatus.Inactive)));
        Assert.False(f(Cu(UserRole.Owner, UserStatus.Suspended)));
        Assert.False(f(Cu(UserRole.Owner, UserStatus.Active, deleted: true)));
        Assert.False(f(Cu(UserRole.Accountant, UserStatus.Active)));
    }

    [Fact]
    public void Suspended_owner_needs_admin_decision_not_auto_promote()
    {
        var creator = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var withSuspended = new List<OwnerlessCompanyRepair.Member>
        {
            new(creator, UserRole.Accountant, t),
            new(owner, UserRole.Owner, t, IsActive: false, IsSuspended: true),
        };
        var p = OwnerlessCompanyRepair.Decide(creator.ToString(), withSuspended);
        Assert.Equal(OwnerlessCompanyRepair.Kind.NeedsAdmin, p.Kind);
        Assert.Contains("ระงับ", p.Reason);
        // ทิศตรงข้าม: เจ้าของเดิมถูกปิด (Inactive ไม่ใช่ระงับ) ⇒ ผู้สร้างที่ยัง active ถูกตั้งเองตามเดิม
        var withClosed = new List<OwnerlessCompanyRepair.Member>
        {
            new(creator, UserRole.Accountant, t),
            new(owner, UserRole.Owner, t, IsActive: false),
        };
        Assert.Equal(OwnerlessCompanyRepair.Kind.AutoPromote, OwnerlessCompanyRepair.Decide(creator.ToString(), withClosed).Kind);
    }

    [Fact]
    public void Account_plan_blocker_has_a_remedy_and_cancel_refuses_plans_with_companies()
    {
        var label = PlatformErasureRequestPolicy.AccountPlanBlockerLabel("โปร");
        Assert.Contains("ติดต่อแอดมิน", label);
        Assert.True(PlatformErasureRequestPolicy.IsAccountPlanBlocker(label));
        Assert.False(PlatformErasureRequestPolicy.IsAccountPlanBlocker("บริษัท “ก” (เจ้าของคนสุดท้าย)"));

        Assert.Null(PlatformErasureRequestPolicy.AccountPlanCancelBlock(new[] { ((string?)"โปร", 0) }));
        var busy = PlatformErasureRequestPolicy.AccountPlanCancelBlock(new[] { ((string?)"โปร", 0), ((string?)"ทีม", 2) });
        Assert.NotNull(busy);
        Assert.Contains("ทีม", busy);
        Assert.Contains("2 บริษัท", busy);
        Assert.NotNull(PlatformErasureRequestPolicy.AccountPlanCancelBlock(Array.Empty<(string?, int)>()));
    }
}
