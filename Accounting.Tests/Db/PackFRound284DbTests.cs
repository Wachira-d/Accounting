using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Import;
using Accounting.Services.Import;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 284 (T4 L-1) บน PostgreSQL จริง: พนักงานเดิมที่เลขบัตรถอดรหัสไม่ได้ (ciphertext ค้างจากคีย์ที่หาย — เขียนข้าม converter) ⇒
/// แถวนำเข้าที่มีเลขบัตรได้คำเตือน <c>employee-id-unreadable</c> (ต้องรับทราบก่อน commit) แทน "ไม่พบของซ้ำ" แบบเงียบ ·
/// ทิศตรงข้าม: บริษัทที่เลขอ่านได้ทุกคน ⇒ ไม่มีคำเตือน + จับคู่คนเดิมด้วยเลขบัตรตามเดิม
/// </summary>
[Trait("Category", "Db")]
public class PackFRound284DbTests
{
    private readonly ITestOutputHelper _out;
    public PackFRound284DbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid Cid, Guid A, Guid B)> SeedAsync(Accounting.Data.AccountingDbContext db, string taxId)
    {
        var company = new Company { Name = "บริษัททดสอบนำเข้าพนักงาน 284", TaxId = taxId, BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new Employee { CompanyId = company.Id, EmployeeCode = "R284-1", TitleTh = "นาย", FirstNameTh = "อ่าน", LastNameTh = "ได้", CitizenId = "1101700207366", StartDate = start };
        var b = new Employee { CompanyId = company.Id, EmployeeCode = "R284-2", TitleTh = "นาง", FirstNameTh = "คีย์", LastNameTh = "หาย", CitizenId = "3100600445768", StartDate = start };
        db.Set<Employee>().AddRange(a, b);
        await db.SaveChangesAsync();
        return (company.Id, a.Id, b.Id);
    }

    private static ImportTargetRow Row(string citizenId) => new(Guid.NewGuid(), 1,
        new Dictionary<string, string?> { ["CitizenId"] = citizenId, ["FirstNameTh"] = "ใหม่" }, ImportRowAction.Create);

    [Fact]
    public async Task R284_L1_เลขเดิมถอดไม่ได้_แถวนำเข้าได้คำเตือนต้องรับทราบ_ไม่เงียบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, b) = await SeedAsync(db, "0105561012346");
        var lost = EncryptionHelper.Encrypt("3100600445768", "r284-key-nobody-has");
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Employees\" SET \"CitizenId\" = {0} WHERE \"Id\" = {1} AND \"CompanyId\" = {2}", lost, b, cid);
        db.ChangeTracker.Clear();

        var resolver = new ImportEntityResolver(db, new DuplicateDetector(db));
        // คนเดิมที่ถอดไม่ได้ — ไฟล์ส่งเลขบัตรเดียวกันมา: เดิม "ไม่พบ" เงียบ ⇒ สร้างพนักงานซ้ำ
        var res = await resolver.MatchAsync(cid, "employees", new[] { Row("3100600445768") }, new ImportResolveOptions(false));
        var m = Assert.Single(res);
        Assert.Null(m.EntityId);
        var issue = Assert.Single(m.Issues);
        Assert.Equal(EmployeeIdDuplicateScan.IssueCode, issue.Code);
        Assert.Equal(ImportIssueSeverity.Warning, issue.Severity);
        Assert.Contains("1 คน", issue.Message);
    }

    [Fact]
    public async Task R284_L1_ทิศตรงข้าม_เลขอ่านได้ทุกคน_จับคู่คนเดิม_ไม่มีคำเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, a, _) = await SeedAsync(db, "0105561012346");
        db.ChangeTracker.Clear();
        var resolver = new ImportEntityResolver(db, new DuplicateDetector(db));
        var res = await resolver.MatchAsync(cid, "employees",
            new[] { Row("1101700207366"), Row("1509900045673") }, new ImportResolveOptions(false));
        Assert.Equal(a, res[0].EntityId);
        Assert.Empty(res[0].Issues);
        Assert.Null(res[1].EntityId);                         // คนใหม่จริง
        Assert.Empty(res[1].Issues);
    }

    private sealed class KeyPerms : Accounting.Services.Interfaces.IPermissionService
    {
        private readonly HashSet<(Guid, string)> _grants;
        public KeyPerms(params (Guid User, string Key)[] g) => _grants = g.ToHashSet();
        public Task<bool> HasPermissionAsync(Guid companyId, Guid userId, string permissionKey) => Task.FromResult(_grants.Contains((userId, permissionKey)));
        public Task<List<string>> GetUserPermissionsAsync(Guid companyId, Guid userId) => Task.FromResult(new List<string>());
    }

    [Fact]
    public async Task R284_Q1_ด่านรายงาน_ไม่ผูกบทบาทผ่าน_บทบาทไม่มีคีย์ไม่ผ่าน_มีคีย์ผ่าน_ไม่ใช่สมาชิกและรอสิทธิ์ไม่ผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบด่านรายงาน 284", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        var role = new CompanyRole { CompanyId = company.Id, Name = "พนักงาน 284" };
        db.CompanyRoles.Add(role);
        User U(string n) { var u = new User { Email = $"r284-{n}-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = n }; db.Users.Add(u); return u; }
        var legacy = U("legacy"); var custom = U("custom"); var keyed = U("keyed"); var pending = U("pending"); var outsider = U("outsider");
        await db.SaveChangesAsync();
        db.CompanyUsers.AddRange(
            new CompanyUser { UserId = legacy.Id, CompanyId = company.Id, Role = UserRole.Staff },
            new CompanyUser { UserId = custom.Id, CompanyId = company.Id, Role = UserRole.Staff, CompanyRoleId = role.Id },
            new CompanyUser { UserId = keyed.Id, CompanyId = company.Id, Role = UserRole.Staff, CompanyRoleId = role.Id },
            new CompanyUser { UserId = pending.Id, CompanyId = company.Id, Role = UserRole.Staff, AccessPending = true });
        await db.SaveChangesAsync();
        var perms = new KeyPerms((keyed.Id, Accounting.Models.Constants.PermissionKeys.ReportsFinancial));
        var keys = new[] { Accounting.Models.Constants.PermissionKeys.ReportsFinancial };
        Assert.True(await FinancialReportAccess.AllowsAsync(db, perms, company.Id, legacy.Id, keys));     // (ก) เดิม
        Assert.False(await FinancialReportAccess.AllowsAsync(db, perms, company.Id, custom.Id, keys));    // บทบาทไม่ติ๊กคีย์
        Assert.True(await FinancialReportAccess.AllowsAsync(db, perms, company.Id, keyed.Id, keys));
        Assert.False(await FinancialReportAccess.AllowsAsync(db, perms, company.Id, pending.Id, keys));
        Assert.False(await FinancialReportAccess.AllowsAsync(db, perms, company.Id, outsider.Id, keys));  // fail closed
        Assert.False(await FinancialReportAccess.AllowsAsync(db, perms, Guid.NewGuid(), legacy.Id, keys)); // บริษัทอื่น
    }

    [Fact]
    public async Task R284A_HIGH1_มือถือไม่มีสิทธิ์_ไม่ได้ยอดเงิน_มีสิทธิ์ได้ตามเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบมือถือ 284", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        db.BankAccounts.Add(new BankAccount { CompanyId = company.Id, AccountName = "บัญชีหลัก", BankName = "กสิกร", AccountNumber = "1234561234", CurrentBalance = 12_345m, IsActive = true });
        await db.SaveChangesAsync();
        var svc = new Accounting.Services.Implementations.MobileApiService(db, null!, null!);
        var full = await svc.GetMobileDashboardAsync(company.Id, includeFinancials: true);
        Assert.Equal((12_345m, false), (full.CashBalance, full.FinancialsHidden));                      // ทิศตรงข้าม: มีสิทธิ์ = เดิม
        var hidden = await svc.GetMobileDashboardAsync(company.Id, includeFinancials: false);
        Assert.Equal((0m, 0m, 0m, 0m, 0m, true), (hidden.CashBalance, hidden.TotalReceivables, hidden.TotalPayables, hidden.TodayRevenue, hidden.MonthRevenue, hidden.FinancialsHidden));
        Assert.False(string.IsNullOrWhiteSpace(hidden.FinancialsHiddenNotice));
    }

    [Fact]
    public async Task R284A_HIGH3_ตอนส่ง_ผู้รับผิดชอบหมดสิทธิ์หรือออกจากบริษัท_ไม่ส่ง_ยังมีสิทธิ์ส่งได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบรายงานตั้งเวลา 284", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        var role = new CompanyRole { CompanyId = company.Id, Name = "ขาย 284" };
        db.CompanyRoles.Add(role);
        User U(string n) { var u = new User { Email = $"r284s-{n}-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = n }; db.Users.Add(u); return u; }
        var legacy = U("legacy"); var custom = U("custom"); var left = U("left");
        await db.SaveChangesAsync();
        db.CompanyUsers.AddRange(
            new CompanyUser { UserId = legacy.Id, CompanyId = company.Id, Role = UserRole.Staff },
            new CompanyUser { UserId = custom.Id, CompanyId = company.Id, Role = UserRole.Staff, CompanyRoleId = role.Id });
        await db.SaveChangesAsync();
        var perms = new KeyPerms();
        ScheduledReport R(Guid? by) => new() { CompanyId = company.Id, Name = "P&L", ReportCode = "PNL", CreatedBy = by?.ToString() };
        Assert.Null(await ScheduledReportAccess.SendProblemAsync(db, perms, R(legacy.Id)));               // ไม่ผูกบทบาท = เดิม
        Assert.NotNull(await ScheduledReportAccess.SendProblemAsync(db, perms, R(custom.Id)));            // บทบาทไม่มีคีย์
        Assert.NotNull(await ScheduledReportAccess.SendProblemAsync(db, perms, R(left.Id)));              // ไม่ใช่สมาชิกแล้ว
        Assert.NotNull(await ScheduledReportAccess.SendProblemAsync(db, perms, R(null)));                 // แถวรุ่นเก่าไม่รู้ผู้รับผิดชอบ
        var granted = new KeyPerms((custom.Id, Accounting.Models.Constants.PermissionKeys.ReportsFinancial), (custom.Id, Accounting.Models.Constants.PermissionKeys.ReportsExport));
        Assert.Null(await ScheduledReportAccess.SendProblemAsync(db, granted, R(custom.Id)));
        var members = await ScheduledReportAccess.MemberEmailsAsync(db, company.Id);
        Assert.Contains(legacy.Email.ToLowerInvariant(), members);
        Assert.DoesNotContain(left.Email.ToLowerInvariant(), members);
    }
}
