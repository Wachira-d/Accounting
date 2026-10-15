using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// X4 · 2026-10-14 — "ไฟล์ส่งออก = สิ่งที่หน้าจอแสดง" บน PostgreSQL จริง:
/// <list type="bullet">
/// <item>ทะเบียนเงินเดือน: แถวในไฟล์ = แถวรายคนของ <c>GetPayrollRunAsync</c> (DTO ตัวเดียวกับตารางบนหน้ารอบ) · ผลรวมสุทธิ = ยอดหัวรอบ</item>
/// <item>รายการโอน: ยอดโอนรวม = สุทธิรวม − หักคืนเงินทดรอง (= เส้น Cr ธนาคารใน JE) · เลขบัญชีถอดรหัสครบ (คอลัมน์เข้ารหัส PDPA)</item>
/// <item>ทะเบียนพนักงาน: เดินทุกหน้าได้ครบ (ไม่ใช่แค่หน้าแรก) และตัวกรองสถานะทำงานตรงกับรายการ</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class ModuleExportDbTests
{
    private readonly ITestOutputHelper _out;
    public ModuleExportDbTests(ITestOutputHelper output) => _out = output;

    private static string Csv(byte[] b) => Encoding.UTF8.GetString(b, 3, b.Length - 3);

    private static async Task<(Guid Cid, Guid RunId)> SeedPaidRunAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบส่งออกเงินเดือน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var a = new Employee { CompanyId = company.Id, EmployeeCode = "E001", TitleTh = "นาย", FirstNameTh = "เอ", LastNameTh = "ทดสอบ",
            StartDate = start.AddYears(-1), BankName = "กสิกรไทย", BankAccountNumber = "012-3-45678-9", BankAccountName = "นายเอ ทดสอบ" };
        var b = new Employee { CompanyId = company.Id, EmployeeCode = "E002", TitleTh = "นาง", FirstNameTh = "บี", LastNameTh = "ทดสอบ",
            StartDate = start.AddYears(-1) };   // ไม่มีเลขบัญชี — ต้องอยู่ในไฟล์พร้อมสถานะ "ขาดเลขบัญชี"
        db.Set<Employee>().AddRange(a, b);
        var run = new PayrollRun
        {
            CompanyId = company.Id, PayrollNumber = "PR-202609-X4", Name = "เงินเดือน กันยายน 2569",
            Year = 2026, Month = 9, PeriodStart = start, PeriodEnd = end, PayDate = end, Status = "Paid",
            TotalGrossSalary = 50000m, TotalNetPay = 47000m, EmployeeCount = 2,
        };
        run.Details.Add(new PayrollDetail { CompanyId = company.Id, EmployeeId = a.Id, BaseSalary = 30000m, GrossIncome = 30000m,
            SocialSecurityEmployee = 750m, WithholdingTax = 450m, TotalDeductions = 1200m, NetPay = 28800m, AdvanceRecovered = 3000m,
            ProvidentFundEmployer = 900m });
        run.Details.Add(new PayrollDetail { CompanyId = company.Id, EmployeeId = b.Id, BaseSalary = 20000m, GrossIncome = 20000m,
            SocialSecurityEmployee = 750m, WithholdingTax = 1050m, TotalDeductions = 1800m, NetPay = 18200m });
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();
        return (company.Id, run.Id);
    }

    [Fact]
    public async Task ทะเบียนเงินเดือน_แถวในไฟล์เท่ากับตารางรายคน_และสุทธิรวมเท่ายอดหัวรอบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, runId) = await SeedPaidRunAsync(db);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var run = await new PayrollService(reqDb).GetPayrollRunAsync(cid, runId);
        var lines = run.Details!;
        Assert.Equal(2, lines.Count);
        var cols = PayrollExportSheets.Register(run, run.DefaultPaymentSourceLabel);
        var totals = PayrollExportSheets.RegisterTotals(lines);
        var file = ModuleExport.File(TabularFormat.Csv, "reg", "reg", cols, lines, new[] { totals });
        var rows = Csv(file.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + lines.Count + 1, rows.Length);                 // หัว + รายคน + แถวรวม

        object? V(string header) => cols.First(c => c.Header == header).Value(totals);
        Assert.Equal(run.TotalNetPay, V("เงินได้สุทธิ"));
        // echo ใหม่: หักคืนเงินทดรอง/PVD นายจ้าง มาจากฐานจริง (เดิม DTO ไม่ส่ง ⇒ ไฟล์กระทบ JE ไม่ได้)
        Assert.Equal(3000m, V("หักคืนเงินทดรอง (ตอนจ่าย)"));
        Assert.Equal(900m, V("กองทุนสำรองฯ นายจ้าง"));
        Assert.Equal(44000m, V("เงินโอนจริง"));
    }

    [Fact]
    public async Task รายการโอน_ยอดรวมเท่าเงินออกจากธนาคาร_แถวขาดเลขบัญชียังอยู่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, runId) = await SeedPaidRunAsync(db);

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var sheet = await new PayrollService(reqDb).GetBankTransferSheetAsync(cid, runId);
        var rows = PayrollExportSheets.BankTransferRows(sheet);
        Assert.Equal(2, rows.Count);
        Assert.Equal(47000m - 3000m, rows.Sum(r => r.Decision.TransferAmount));   // = Cr ธนาคารตอนจ่าย (NetPay − AdvanceRecovered)
        var a = rows.Single(r => r.Line.EmployeeCode == "E001");
        Assert.Equal("0123456789", a.Decision.AccountDigits);                     // ถอดรหัสจากคอลัมน์เข้ารหัสครบ
        Assert.Equal(25800m, a.Decision.TransferAmount);
        Assert.True(a.Decision.Ready);
        var b = rows.Single(r => r.Line.EmployeeCode == "E002");
        Assert.False(b.Decision.Ready);
        Assert.Equal("ขาดเลขบัญชี", b.Decision.Status);
    }

    [Fact]
    public async Task รายการโอน_รอบยังไม่คำนวณ_ถูกปฏิเสธพร้อมทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, runId) = await SeedPaidRunAsync(db);
        var run = await db.Set<PayrollRun>().FindAsync(runId);
        run!.Status = "Draft";
        await db.SaveChangesAsync();

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new PayrollService(reqDb).GetBankTransferSheetAsync(cid, runId));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("คำนวณ", ex.Message);
    }

    [Fact]
    public async Task ทะเบียนพนักงาน_เดินทุกหน้าได้ครบ_และกรองสถานะตรงกับรายการ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบส่งออกพนักงาน", TaxId = "0105556000003" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 25; i++)
            db.Set<Employee>().Add(new Employee { CompanyId = company.Id, EmployeeCode = $"E{i:D3}", FirstNameTh = "พนักงาน", LastNameTh = $"{i}",
                StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), IsActive = i <= 22 });
        await db.SaveChangesAsync();

        using var reqDb = DbTestDatabase.TryCreateContext()!;
        var svc = new PayrollService(reqDb);
        // หน้าละ 10 ⇒ 3 หน้า — ไฟล์ต้องได้ครบ ไม่ใช่ 10 แถวแรก
        var all = await ModuleExport.CollectAllPagesAsync(p => svc.GetEmployeesAsync(company.Id, new PagedRequest(p, 10), false, false));
        Assert.Equal(25, all.Count);
        Assert.Equal(25, all.Select(e => e.EmployeeCode).Distinct().Count());
        var active = await ModuleExport.CollectAllPagesAsync(p => svc.GetEmployeesAsync(company.Id, new PagedRequest(p, 10), false, false, isActive: true));
        Assert.Equal(22, active.Count);
        var listed = await svc.GetEmployeesAsync(company.Id, new PagedRequest(1, 200), false, false, isActive: false);
        Assert.Equal(3, listed.TotalCount);                                  // รายการบนจอใช้ตัวกรองเดียวกัน
        Assert.All(all, e => Assert.Equal(0m, e.BaseSalary));                // ไม่มีสิทธิ์ดูเงินเดือน = service ปิดแล้ว (ไฟล์ออกช่องว่าง)
    }
}
