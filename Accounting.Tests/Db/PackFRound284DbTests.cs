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
}
