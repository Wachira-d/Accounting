using Accounting.Models.DTOs.Payroll;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// 2026-10-05/08 ผู้ใช้รายงาน: "เพิ่มพนักงานเข้ารอบนี้" (คนที่ซิงค์มา) ได้ 500 DbUpdateConcurrencyException ทุกครั้ง
/// (REF:29896242 · 60A3EAEE · 6BC04040) — <c>BaseEntity.Id</c> ตั้ง Guid ไว้แล้ว ⇒ แถว PayrollDetail ใหม่ที่ EF เจอผ่าน
/// <c>run.Details</c> ของรอบที่ติดตามอยู่ถูกตีเป็น Modified ⇒ UPDATE แถวที่ไม่มีจริง (affected 0)
///
/// <para>เทสต์ pure จับไม่ได้ (สถานะ change tracker เกิดเฉพาะตอน SaveChanges บน DbContext จริง) ⇒ รันเส้นจริงบน PostgreSQL ·
/// ทิศตรงข้าม: เพิ่มคนเดิมซ้ำต้องยังถูกกัน (409) และจำนวนแถวไม่เปลี่ยน</para>
/// </summary>
[Trait("Category", "Db")]
public class PayrollAddDetailDbTests
{
    private readonly ITestOutputHelper _out;
    public PayrollAddDetailDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid RunId, Guid InRunEmpId, Guid NewEmpId)> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบเพิ่มพนักงาน", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var a = new Employee { CompanyId = company.Id, EmployeeCode = "E001", FirstNameTh = "อัมรา", LastNameTh = "รุ่งเรือง", StartDate = start.AddYears(-1) };
        var b = new Employee { CompanyId = company.Id, EmployeeCode = "E002", FirstNameTh = "สมชาย", LastNameTh = "ซิงค์มา", StartDate = start.AddDays(10) };
        db.Set<Employee>().AddRange(a, b);
        var run = new PayrollRun
        {
            CompanyId = company.Id, PayrollNumber = "PR-202609-T", Name = "เงินเดือน กันยายน 2569",
            Year = 2026, Month = 9, PeriodStart = start, PeriodEnd = end, PayDate = end, Status = "Calculated",
        };
        run.Details.Add(new PayrollDetail { CompanyId = company.Id, EmployeeId = a.Id, BaseSalary = 12000m, GrossIncome = 12000m, NetPay = 12000m });
        db.Set<PayrollRun>().Add(run);   // รอบใหม่ทั้งก้อน ⇒ ลูกเป็น Added ตามรอบ (เส้นนำเข้า/คำนวณ)
        await db.SaveChangesAsync();
        return (company.Id, run.Id, a.Id, b.Id);
    }

    [Fact]
    public async Task เพิ่มพนักงานเข้ารอบที่คำนวณแล้ว_บันทึกได้_ไม่ชนConcurrency()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, runId, _, newEmp) = await SeedAsync(db);

        // DbContext ใหม่ = สภาพเดียวกับคำขอจริง (รอบถูกโหลดและติดตามใหม่ภายในเมธอด)
        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var svc = new PayrollService(reqDb);
        await svc.AddPayrollDetailAsync(cid, runId,
            new AddPayrollDetailRequest(newEmp, Reason: "ออกระหว่างเดือน", SocialSecurityBase: 7000m,
                WithholdingTax: 0m, BaseSalary: 7000m),
            "db-test", null);

        using var check = DbTestDatabase.TryCreateContext()!;
        var details = await check.Set<PayrollDetail>().AsNoTracking()
            .Where(d => d.CompanyId == cid && d.PayrollRunId == runId && !d.IsDeleted).ToListAsync();
        Assert.Equal(2, details.Count);
        var added = Assert.Single(details, d => d.EmployeeId == newEmp);
        Assert.Equal(7000m, added.BaseSalary);
        Assert.Equal(350m, added.SocialSecurityEmployee);   // 5% ของฐาน 7,000 (ม.33)
    }

    [Fact]
    public async Task ทิศตรงข้าม_เพิ่มคนที่อยู่ในรอบแล้ว_ถูกกัน409_แถวไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, runId, inRun, _) = await SeedAsync(db);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<Accounting.Helpers.BusinessRuleException>(() =>
            new PayrollService(reqDb).AddPayrollDetailAsync(cid, runId,
                new AddPayrollDetailRequest(inRun, Reason: "เพิ่มซ้ำทดสอบ", SocialSecurityBase: 12000m,
                    WithholdingTax: 0m, BaseSalary: 12000m),
                "db-test", null));
        Assert.Equal(409, ex.StatusCode);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Set<PayrollDetail>().CountAsync(d => d.CompanyId == cid && d.PayrollRunId == runId && !d.IsDeleted));
    }
}
