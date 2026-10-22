using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 ทีม I — สิทธิ์และ License: ผู้สร้างบริษัทเป็นเจ้าของ · ตัวซ่อมบริษัทไม่มีเจ้าของ · ด่านเจ้าของคนสุดท้าย (สองทิศ) ·
/// ตารางบทบาทมาตรฐาน/ตารางสิทธิ์/บทบาทสำเร็จรูป · ตัวตนแพ็กเกจ (License ชนะแถวบริษัท)
/// </summary>
public class TeamPermissionLicenseRound205Tests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── ข้อ 1: ผู้สร้างบริษัท ─────────────────────────────────────────────────────────────

    [Fact]
    public void Creator_IsOwner_OnEveryEntry_IncludingPlatformAdminOwnCompany()
    {
        var row = CompanyCreatorMembership.NewRow(B, A, creatorIsPlatformAdmin: true, openForCustomer: false);
        Assert.Equal(UserRole.Owner, row.Role);            // บั๊กเดิม: แอดมินแพลตฟอร์มสร้างบริษัทของตัวเองแล้วได้ support ⇒ ไม่มีเจ้าของ
        Assert.Equal(A, row.UserId);
        Assert.Equal(B, row.CompanyId);
        Assert.True(row.IsDefault);
        Assert.Equal(UserRole.Owner, CompanyCreatorMembership.NewRow(B, A, false, false).Role);
    }

    [Fact]
    public void Creator_GetsSupport_OnlyWhenPlatformAdminOpensForCustomer()
    {
        Assert.Equal(UserRole.PlatformSupport, CompanyCreatorMembership.NewRow(B, A, true, openForCustomer: true).Role);
        Assert.Equal(UserRole.Owner, CompanyCreatorMembership.NewRow(B, A, false, openForCustomer: true).Role);   // ผู้ใช้ทั่วไปติ๊กเองไม่มีผล
    }

    // ── ข้อ 1: ตัวซ่อมบริษัทไม่มีเจ้าของ ──────────────────────────────────────────────────

    [Fact]
    public void Repair_CreatorStillCustomerMember_AutoPromotesCreator()
    {
        var p = OwnerlessCompanyRepair.Decide(A.ToString(), new[]
        {
            new OwnerlessCompanyRepair.Member(B, UserRole.Staff, T0),
            new OwnerlessCompanyRepair.Member(A, UserRole.Accountant, T0.AddDays(1)),
        });
        Assert.Equal(OwnerlessCompanyRepair.Kind.AutoPromote, p.Kind);
        Assert.Equal(A, p.UserId);
    }

    [Fact]
    public void Repair_UnknownCreator_SoleCustomerMember_AutoPromotes()
    {
        var p = OwnerlessCompanyRepair.Decide("system", new[] { new OwnerlessCompanyRepair.Member(B, UserRole.Staff, T0) });
        Assert.Equal(OwnerlessCompanyRepair.Kind.AutoPromote, p.Kind);
        Assert.Equal(B, p.UserId);
    }

    [Fact]
    public void Repair_SupportCreator_IsNotGuessed_NeedsAdmin()
    {
        // ผู้สร้างเป็น support: แยกไม่ได้ว่าเปิดให้ลูกค้าหรือบริษัทของแอดมินเอง — ห้ามเดา
        var alone = OwnerlessCompanyRepair.Decide(A.ToString(), new[] { new OwnerlessCompanyRepair.Member(A, UserRole.PlatformSupport, T0) });
        Assert.Equal(OwnerlessCompanyRepair.Kind.NeedsAdmin, alone.Kind);
        Assert.Null(alone.UserId);
        Assert.Contains(A, alone.Candidates);

        var withCustomer = OwnerlessCompanyRepair.Decide(A.ToString(), new[]
        {
            new OwnerlessCompanyRepair.Member(A, UserRole.PlatformSupport, T0),
            new OwnerlessCompanyRepair.Member(B, UserRole.Staff, T0.AddDays(1)),
        });
        Assert.Equal(OwnerlessCompanyRepair.Kind.NeedsAdmin, withCustomer.Kind);
        Assert.Equal(new[] { A, B }, withCustomer.Candidates);
    }

    [Fact]
    public void Repair_ManyMembers_OrEmpty_NeedsAdmin()
    {
        var many = OwnerlessCompanyRepair.Decide(null, new[]
        {
            new OwnerlessCompanyRepair.Member(C, UserRole.Staff, T0.AddDays(2)),
            new OwnerlessCompanyRepair.Member(B, UserRole.Accountant, T0),
        });
        Assert.Equal(OwnerlessCompanyRepair.Kind.NeedsAdmin, many.Kind);
        Assert.Equal(new[] { B, C }, many.Candidates);   // เรียงตามวันเข้าร่วม

        var empty = OwnerlessCompanyRepair.Decide(A.ToString(), Array.Empty<OwnerlessCompanyRepair.Member>());
        Assert.Equal(OwnerlessCompanyRepair.Kind.NeedsAdmin, empty.Kind);
        Assert.Empty(empty.Candidates);
    }

    [Fact]
    public void HasOwner_CountsOwnerAndSystemAdmin()
    {
        Assert.True(OwnerlessCompanyRepair.HasOwner(new[] { UserRole.Staff, UserRole.Owner }));
        Assert.True(OwnerlessCompanyRepair.HasOwner(new[] { UserRole.SystemAdmin }));
        Assert.False(OwnerlessCompanyRepair.HasOwner(new[] { UserRole.PlatformSupport, UserRole.Accountant }));
    }

    [Fact]
    public void AdminAssign_OnlyOwnerlessCompany_ExistingMember_SelfOnlyIfCreator()
    {
        Assert.NotNull(OwnerlessCompanyRepair.AdminAssignBlock(companyHasOwner: true, targetIsMember: true, targetIsSelf: false, callerIsCreator: false));
        Assert.NotNull(OwnerlessCompanyRepair.AdminAssignBlock(false, targetIsMember: false, targetIsSelf: false, callerIsCreator: false));
        Assert.NotNull(OwnerlessCompanyRepair.AdminAssignBlock(false, true, targetIsSelf: true, callerIsCreator: false));   // ยึดบริษัทลูกค้าไม่ได้
        Assert.Null(OwnerlessCompanyRepair.AdminAssignBlock(false, true, targetIsSelf: true, callerIsCreator: true));       // บริษัทของตัวเอง
        Assert.Null(OwnerlessCompanyRepair.AdminAssignBlock(false, true, targetIsSelf: false, callerIsCreator: false));    // ตั้งลูกค้า
    }

    // ── ข้อ 2: เจ้าของคนสุดท้าย (สองทิศ) ─────────────────────────────────────────────────

    [Fact]
    public void LastOwner_CannotBeDemotedOrRemoved()
    {
        Assert.NotNull(LastOwnerGuard.Block(UserRole.Owner, UserRole.Accountant, otherOwnerCount: 0));
        Assert.NotNull(LastOwnerGuard.Block(UserRole.Owner, null, otherOwnerCount: 0));
        Assert.NotNull(LastOwnerGuard.Block(UserRole.SystemAdmin, UserRole.Viewer, otherOwnerCount: 0));
    }

    [Fact]
    public void Owner_CanBeDemoted_WhenAnotherOwnerExists_AndNonOwnersUntouched()
    {
        Assert.Null(LastOwnerGuard.Block(UserRole.Owner, UserRole.Accountant, otherOwnerCount: 1));
        Assert.Null(LastOwnerGuard.Block(UserRole.Owner, null, otherOwnerCount: 1));
        Assert.Null(LastOwnerGuard.Block(UserRole.Owner, UserRole.SystemAdmin, otherOwnerCount: 0));   // ยังเป็นบทบาทเจ้าของ
        Assert.Null(LastOwnerGuard.Block(UserRole.Staff, UserRole.Viewer, otherOwnerCount: 0));        // ไม่ใช่เจ้าของ = ไม่แตะ
        Assert.Null(LastOwnerGuard.Block(UserRole.Staff, null, otherOwnerCount: 0));
    }

    // ── ข้อ 2: ตารางบทบาทมาตรฐาน · ตารางสิทธิ์ · บทบาทสำเร็จรูป ──────────────────────────────

    [Fact]
    public void BuiltIn_OwnerGrantsAll_AccountantCore_SupportSetupOnly()
    {
        Assert.True(BuiltInRolePermissions.Grants(UserRole.Owner, PermissionKeys.PiiView));
        Assert.True(BuiltInRolePermissions.Grants(UserRole.SystemAdmin, PermissionKeys.UsersManage));
        Assert.True(BuiltInRolePermissions.Grants(UserRole.Accountant, PermissionKeys.DocumentCreate));
        Assert.False(BuiltInRolePermissions.Grants(UserRole.Accountant, PermissionKeys.PiiView));
        Assert.False(BuiltInRolePermissions.Grants(UserRole.Accountant, PermissionKeys.UsersManage));
        Assert.True(BuiltInRolePermissions.Grants(UserRole.PlatformSupport, PermissionKeys.CompanySettingsEdit));
        Assert.False(BuiltInRolePermissions.Grants(UserRole.PlatformSupport, PermissionKeys.PayrollView));
        Assert.False(BuiltInRolePermissions.Grants(UserRole.Viewer, PermissionKeys.DocumentCreate));
        Assert.False(BuiltInRolePermissions.Grants(null, PermissionKeys.DocumentCreate));
        Assert.Empty(BuiltInRolePermissions.DefaultKeys(UserRole.Staff));
    }

    [Fact]
    public void Matrix_EveryKeyIsInCatalog_AndAreasUnique()
    {
        var catalog = PermissionKeys.Catalog.Select(c => c.Key).ToHashSet();
        foreach (var k in PermissionMatrix.AllKeys())
            Assert.True(catalog.Contains(k), $"คีย์ในตารางสิทธิ์ไม่อยู่ในแคตตาล็อก: {k}");
        Assert.Equal(PermissionMatrix.Rows.Count, PermissionMatrix.Rows.Select(r => r.Area).Distinct().Count());
        Assert.Equal(4, PermissionMatrix.ActionLabelsTh.Count);
        var rev = PermissionMatrix.Rows.Single(r => r.Area == "doc-revenue");
        Assert.Equal(PermissionKeys.DocumentRevenueApprove, rev.Approve);
        Assert.Equal(PermissionKeys.DocumentRevenueVoid, rev.Delete);
    }

    [Fact]
    public void Templates_PermKeysInCatalog_ThaiPresetsPresent_ExternalAccountantScoped()
    {
        var catalog = PermissionKeys.Catalog.Select(c => c.Key).ToHashSet();
        foreach (var t in RoleTemplates.All)
            foreach (var k in t.Permissions.Where(PermissionKeys.IsPermissionKey))
                Assert.True(catalog.Contains(k), $"แม่แบบ {t.Key} อ้างคีย์ที่ไม่มี: {k}");
        foreach (var key in new[] { "shop-technician", "cashier-th", "external-accountant", "support-staff" })
            Assert.NotNull(RoleTemplates.Find(key));
        Assert.Equal(RoleTemplates.All.Count, RoleTemplates.All.Select(t => t.Key).Distinct().Count());

        var ext = RoleTemplates.Find("external-accountant")!.Permissions;
        Assert.DoesNotContain(PermissionKeys.PayrollView, ext);
        Assert.DoesNotContain(PermissionKeys.PiiView, ext);
        Assert.DoesNotContain(PermissionKeys.BankPaymentInit, ext);
        Assert.DoesNotContain(PermissionKeys.DocumentVoid, ext);
        Assert.Contains(PermissionKeys.TaxFile, ext);

        // id เมนูที่ไม่มีใน layout.js (บั๊กเดิม — สมาชิกโหมดเข้มงวดไม่เห็นเมนู) ห้ามกลับมา
        var dead = new[] { "inventory", "warehouses", "chart-of-accounts", "vat", "cms-edit", "executive-dashboard" };
        foreach (var t in RoleTemplates.All)
            Assert.Empty(t.Permissions.Intersect(dead));
    }

    // ── ข้อ 3: ตัวตนแพ็กเกจ ───────────────────────────────────────────────────────────────

    [Fact]
    public void PlanIdentity_LicenseWins_OverStaleCompanyRow()
    {
        var id = PlanIdentityResolver.Resolve(SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, T0, "Free",
            new PlanIdentityResolver.LicenseSide(SubscriptionPlan.Enterprise, "Enterprise", SubscriptionStatus.Active, T0.AddYears(1)));
        Assert.Equal(SubscriptionPlan.Enterprise, id.Plan);
        Assert.Equal("Enterprise", id.PlanName);
        Assert.Equal(SubscriptionStatus.Active, id.Status);
        Assert.Equal(T0.AddYears(1), id.EndDate);
        Assert.Equal(PlanIdentityResolver.SourceLicense, id.Source);
    }

    [Fact]
    public void PlanIdentity_NoLicense_UsesCompanyRowUnchanged()
    {
        var id = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Active, T0, "Pro", null);
        Assert.Equal(SubscriptionPlan.Pro, id.Plan);
        Assert.Equal("Pro", id.PlanName);
        Assert.Equal(PlanIdentityResolver.SourceCompany, id.Source);
    }
}
