using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL — C-3 ด่านเจ้าของปิดฟีเจอร์ระดับ service (โหมดเงาก่อน · ข้อ 76) · C-4 บทบาทผู้ดูแลแพลตฟอร์ม + โอนความเป็นเจ้าของ (ข้อ 77)
/// ทุกข้อสองทิศ: ที่ต้องปฏิเสธ/บันทึกเงา และที่ต้องผ่านเหมือนเดิม
/// </summary>
public class PlatformOwnerRound201Tests
{
    // ── C-3 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C3_OwnerDisabled_ShadowFirst_PassesButRecords()
    {
        var v = OwnerFeatureMask.Decide(FeatureFlags.Payroll | FeatureFlags.Inventory, FeatureFlags.Payroll, FeatureFlags.Payroll, enforced: false);
        Assert.True(v.Allowed);       // ยังไม่บังคับ = ไม่เข้มขึ้นทันที
        Assert.True(v.ShadowHit);     // แต่บันทึก "จะถูกปิด"
    }

    [Fact]
    public void C3_OwnerDisabled_Enforced_Denies()
    {
        var v = OwnerFeatureMask.Decide(FeatureFlags.Payroll, FeatureFlags.Payroll, FeatureFlags.Payroll, enforced: true);
        Assert.False(v.Allowed);
        Assert.False(v.ShadowHit);
    }

    [Fact]
    public void C3_NotDisabled_OrNotInPlan_UnchangedBehaviour()
    {
        // เจ้าของไม่ได้ปิดฟีเจอร์นี้ (ปิดตัวอื่น) ⇒ ผ่านเงียบเหมือนเดิม แม้บังคับแล้ว
        var other = OwnerFeatureMask.Decide(FeatureFlags.Payroll | FeatureFlags.Inventory, FeatureFlags.Inventory, FeatureFlags.Payroll, enforced: true);
        Assert.True(other.Allowed);
        Assert.False(other.ShadowHit);
        // แพ็กเกจไม่มี ⇒ ไม่ผ่าน (ด่านนี้ไม่เพิ่มสิทธิ์ และไม่บันทึกเงาของเหตุเจ้าของ)
        var notInPlan = OwnerFeatureMask.Decide(FeatureFlags.Inventory, FeatureFlags.Payroll, FeatureFlags.Payroll, enforced: false);
        Assert.False(notInPlan.Allowed);
        Assert.False(notInPlan.ShadowHit);
        Assert.False(OwnerFeatureMask.IsOwnerDisabled(FeatureFlags.Payroll, FeatureFlags.None));
    }

    // ── C-4 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C4_PlatformAdminCreator_GetsSupport_NotOwner()
    {
        Assert.Equal(UserRole.PlatformSupport, OwnershipTransferPolicy.CreatorRole(creatorIsPlatformAdmin: true));
        Assert.Equal(UserRole.Owner, OwnershipTransferPolicy.CreatorRole(creatorIsPlatformAdmin: false));   // ผู้ใช้ทั่วไปเหมือนเดิม
    }

    [Fact]
    public void C4_Support_IsNotOwner_ForOwnerGates_AndHasOnlySetupKeys()
    {
        Assert.Equal(OwnerGateDecision.Outcome.DenyNotOwner, OwnerGateDecision.Decide(false, false, UserRole.PlatformSupport));
        Assert.Contains(PermissionKeys.CompanySettingsEdit, OwnershipTransferPolicy.SupportDefaultKeys);
        Assert.DoesNotContain(PermissionKeys.PayrollView, OwnershipTransferPolicy.SupportDefaultKeys);
        Assert.DoesNotContain(PermissionKeys.DocumentApprove, OwnershipTransferPolicy.SupportDefaultKeys);
        Assert.False(OwnershipTransferPolicy.AssignableByMembers(UserRole.PlatformSupport));
        Assert.True(OwnershipTransferPolicy.AssignableByMembers(UserRole.Accountant));
    }

    [Fact]
    public void C4_Transfer_AllowedFor_SupportOwnerPlatformAdmin()
    {
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, false, UserRole.PlatformSupport, true, null));
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, false, UserRole.Owner, true, UserRole.Accountant));
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, true, null, true, null));
    }

    [Fact]
    public void C4_Transfer_Denied_WithReason()
    {
        var key = OwnershipTransferPolicy.Decide(true, true, UserRole.Owner, true, null);
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyApiKey, key);
        Assert.Equal(403, OwnershipTransferPolicy.StatusCode(key));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, false, UserRole.Accountant, true, null));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, false, null, true, null));
        var missing = OwnershipTransferPolicy.Decide(false, false, UserRole.PlatformSupport, false, null);
        Assert.Equal(404, OwnershipTransferPolicy.StatusCode(missing));
        Assert.Contains("สมัคร", OwnershipTransferPolicy.Message(missing));   // ทางไปต่อ
        Assert.Equal(409, OwnershipTransferPolicy.StatusCode(OwnershipTransferPolicy.Decide(false, false, UserRole.Owner, true, UserRole.Owner)));
    }
}
