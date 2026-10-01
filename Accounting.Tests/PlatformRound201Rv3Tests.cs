using System.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL · ฝ่ายค้านรอบสาม (P1-1 · P1-2 · P2-1 · P2-2) — ตัวตัดสินบริสุทธิ์ทุกตัวมีสองทิศ
/// </summary>
public class PlatformRound201Rv3Tests
{
    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.Unspecified)]
    public void P1_1_ReadCommitted_family_may_seal(IsolationLevel level) => Assert.Null(AuditChainScope.IsolationBlockReason(level));

    [Theory]
    [InlineData(IsolationLevel.Serializable)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Snapshot)]
    public void P1_1_Snapshot_levels_fail_loud_with_the_way_out(IsolationLevel level)
    {
        var why = AuditChainScope.IsolationBlockReason(level);
        Assert.NotNull(why);
        Assert.Contains(level.ToString(), why);
        Assert.Contains("FOR UPDATE", why);      // ทางไปต่อของผู้เขียนโค้ด
    }

    [Fact]
    public void P2_1_InsertSql_numbers_placeholders_row_by_row_and_fits_the_parameter_cap()
    {
        Assert.Equal("INSERT INTO \"T\" (\"a\", \"b\", \"c\") VALUES ({0}, {1}, {2}), ({3}, {4}, {5})",
            AuditChainScope.InsertSql("T", new[] { "a", "b", "c" }, 2));
        Assert.Equal("INSERT INTO \"T\" (\"a\") VALUES ({0})", AuditChainScope.InsertSql("T", new[] { "a" }, 1));
        Assert.True(AuditChainScope.InsertBatchRows * 13 <= 65535);   // 13 คอลัมน์ของแถว audit
        Assert.True(AuditChainScope.InsertBatchRows >= 100);          // ไม่ถอยกลับไปทีละแถว
    }

    [Fact]
    public void P1_2_Admin_page_cannot_make_or_unmake_owners()
    {
        Assert.NotNull(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Accountant, UserRole.Owner));
        Assert.NotNull(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Owner, UserRole.Viewer));
        Assert.NotNull(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.PlatformSupport, UserRole.Owner));   // ทางลัดแทน DenySelf
        Assert.Contains("โอนความเป็นเจ้าของ", OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Owner, UserRole.Viewer));
        Assert.NotNull(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Viewer, UserRole.PlatformSupport));
    }

    [Fact]
    public void P1_2_Admin_page_still_changes_ordinary_roles()
    {
        Assert.Null(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Accountant, UserRole.Viewer));
        Assert.Null(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.PlatformSupport, UserRole.Accountant));
        Assert.Null(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Viewer, UserRole.SystemAdmin));       // แอดมินแพลตฟอร์มตั้งได้ (ข้อ 107)
        Assert.Null(OwnershipTransferPolicy.AdminRoleChangeBlock(UserRole.Owner, UserRole.Owner));             // ไม่เปลี่ยน = ไม่ใช่การโอน
    }

    [Fact]
    public void P2_2_Stale_platform_role_invitations_are_refused_at_acceptance()
    {
        Assert.NotNull(OwnershipTransferPolicy.InvitationRoleBlock(UserRole.SystemAdmin, inviterIsPlatformAdmin: false));
        Assert.NotNull(OwnershipTransferPolicy.InvitationRoleBlock(UserRole.PlatformSupport, inviterIsPlatformAdmin: true));
        Assert.Contains("คำเชิญใหม่", OwnershipTransferPolicy.InvitationRoleBlock(UserRole.PlatformSupport, inviterIsPlatformAdmin: false));
        // ทิศตรงข้าม: บทบาทของบริษัทรับได้เหมือนเดิม · SystemAdmin ที่แอดมินแพลตฟอร์มเชิญ (ยังเป็นแอดมิน) รับได้
        Assert.Null(OwnershipTransferPolicy.InvitationRoleBlock(UserRole.Accountant, inviterIsPlatformAdmin: false));
        Assert.Null(OwnershipTransferPolicy.InvitationRoleBlock(UserRole.Owner, inviterIsPlatformAdmin: false));
        Assert.Null(OwnershipTransferPolicy.InvitationRoleBlock(UserRole.SystemAdmin, inviterIsPlatformAdmin: true));
    }
}
