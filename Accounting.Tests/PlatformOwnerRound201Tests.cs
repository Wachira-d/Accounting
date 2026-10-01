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
        Assert.False(OwnershipTransferPolicy.MayAssign(UserRole.PlatformSupport, callerIsPlatformAdmin: false));
        Assert.True(OwnershipTransferPolicy.MayAssign(UserRole.Accountant, callerIsPlatformAdmin: false));
    }

    [Fact]
    public void C4_Transfer_AllowedFor_Owner_And_ActiveSupportOfThisCompany()
    {
        // support ของบริษัทนี้ที่ยังเป็นแอดมินแพลตฟอร์ม → ส่งมอบให้ลูกค้า
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, true, UserRole.PlatformSupport, true, null));
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, false, UserRole.Owner, true, UserRole.Accountant));
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, false, UserRole.SystemAdmin, true, null));
    }

    [Fact]
    public void C4_Transfer_Denied_WithReason()
    {
        var key = OwnershipTransferPolicy.Decide(true, true, UserRole.Owner, true, null);
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyApiKey, key);
        Assert.Equal(403, OwnershipTransferPolicy.StatusCode(key));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, false, UserRole.Accountant, true, null));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, false, null, true, null));
        var missing = OwnershipTransferPolicy.Decide(false, true, UserRole.PlatformSupport, false, null);
        Assert.Equal(404, OwnershipTransferPolicy.StatusCode(missing));
        Assert.Contains("สมัคร", OwnershipTransferPolicy.Message(missing));   // ทางไปต่อ
        Assert.Equal(409, OwnershipTransferPolicy.StatusCode(OwnershipTransferPolicy.Decide(false, false, UserRole.Owner, true, UserRole.Owner)));
    }

    [Fact]
    public void C4_PlatformAdminWithoutSupportSeat_OrRevokedSupport_CannotTransfer()
    {
        // PL-S2 (ข้อ 105): แอดมินแพลตฟอร์มที่ไม่ได้เป็น support ของบริษัทนั้น (ไม่ใช่สมาชิก/เป็นแค่นักบัญชี) ⇒ ปฏิเสธ — เจ้าของเดิมต้องทำเอง
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, true, null, true, null));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, true, UserRole.Accountant, true, null));
        // PL-S1: support ที่ถูกถอดสิทธิ์แอดมินแพลตฟอร์มแล้ว ⇒ ปฏิเสธ
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyNotAllowed, OwnershipTransferPolicy.Decide(false, false, UserRole.PlatformSupport, true, null));
    }

    [Fact]
    public void C4_Support_CannotTransferToSelf_OwnerPathUnchanged()
    {
        var self = OwnershipTransferPolicy.Decide(false, true, UserRole.PlatformSupport, true, UserRole.PlatformSupport, targetIsSelf: true);
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenySelf, self);
        Assert.Equal(403, OwnershipTransferPolicy.StatusCode(self));
        // ทิศตรงข้าม: support โอนให้ลูกค้า (ไม่ใช่ตัวเอง) ผ่าน · Owner ที่โอนให้ตัวเองตก 409 (เป็นเจ้าของอยู่แล้ว) ไม่ใช่ DenySelf
        Assert.Equal(OwnershipTransferPolicy.Outcome.Allow, OwnershipTransferPolicy.Decide(false, true, UserRole.PlatformSupport, true, null, targetIsSelf: false));
        Assert.Equal(OwnershipTransferPolicy.Outcome.DenyTargetAlreadyOwner, OwnershipTransferPolicy.Decide(false, false, UserRole.Owner, true, UserRole.Owner, targetIsSelf: true));
    }

    [Fact]
    public void C4_MembersCannotAssignPlatformRoles()
    {
        // คำตัดสินข้อ 107: SystemAdmin (99) และ PlatformSupport ตั้งผ่านหน้าทีม/คำเชิญไม่ได้ · บทบาทลูกค้าตั้งได้เหมือนเดิม
        Assert.False(OwnershipTransferPolicy.MayAssign(UserRole.SystemAdmin, callerIsPlatformAdmin: false));
        Assert.False(OwnershipTransferPolicy.MayAssign(UserRole.PlatformSupport, callerIsPlatformAdmin: false));
        Assert.True(OwnershipTransferPolicy.MayAssign(UserRole.Owner, callerIsPlatformAdmin: false));
        Assert.True(OwnershipTransferPolicy.MayAssign(UserRole.Viewer, callerIsPlatformAdmin: false));
        // ทิศตรงข้าม: แอดมินแพลตฟอร์มตั้ง SystemAdmin ได้ · แต่ PlatformSupport ไม่มีใครตั้งได้ (เกิดทางเดียวตอนแอดมินสร้างบริษัท)
        Assert.True(OwnershipTransferPolicy.MayAssign(UserRole.SystemAdmin, callerIsPlatformAdmin: true));
        Assert.False(OwnershipTransferPolicy.MayAssign(UserRole.PlatformSupport, callerIsPlatformAdmin: true));
        Assert.Contains("support", OwnershipTransferPolicy.AssignDeniedMessage(UserRole.PlatformSupport));
    }
}
