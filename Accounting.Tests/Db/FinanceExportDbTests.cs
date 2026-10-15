using System.Globalization;
using System.Security.Claims;
using System.Text;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Budget;
using Accounting.Models.DTOs.Consolidation;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Cheque;
using Accounting.Services.Implementations.PettyCash;
using Accounting.Services.Implementations.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using ChequeEntity = Accounting.Models.Entities.Cheque;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบส่งออก 2026-10-15 (ทีม X5 · erp-review/2026-10-14/EXPORT_AUDIT.md §X5) — ไฟล์ส่งออกหน้าการเงินที่เหลือ
/// "แถว/ยอด = ผลของ service ตัวเดียวกับหน้า" และไม่ถูกตัดที่ pageSize (PagedRequest หนีบ 200) ·
/// งบประมาณเทียบยอดจริง · กำไรขาดทุนโครงการ (รายการต้นทุน 205 บรรทัด) · สินเชื่อ 205 สัญญา · เช็คคงค้าง (ชื่อผู้ติดต่อจากเซิร์ฟเวอร์ ·
/// ไม่รั่วข้ามบริษัท) · ทะเบียนเงินสดย่อย (ถึงวันที่รวมทั้งวัน) · ด่านข้ามบริษัทของงบรวม
/// </summary>
[Trait("Category", "Db")]
public class FinanceExportDbTests
{
    private readonly ITestOutputHelper _out;
    public FinanceExportDbTests(ITestOutputHelper output) => _out = output;

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<(Guid CompanyId, Dictionary<string, Guid> Acc)> SeedCompanyAsync(AccountingDbContext db, string name)
    {
        var company = new Company { Name = name, TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, Guid>();
        void Add(string code, string n, AccountType type)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = n, AccountType = type, Level = 4 };
            db.ChartOfAccounts.Add(a);
            acc[code] = a.Id;
        }
        Add("11111", "เงินสด", AccountType.Asset);
        Add("41000", "รายได้จากการขาย", AccountType.Revenue);
        Add("51000", "ค่าใช้จ่ายสำนักงาน", AccountType.Expense);
        Add("52000", "ค่าเดินทาง", AccountType.Expense);
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    /// <summary>CSV → แถว (ตัด BOM · CRLF · อัญประกาศคู่)</summary>
    private static List<string[]> ParseCsv(byte[] bytes)
    {
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "ต้องมี BOM");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var rows = new List<string[]>();
        var cur = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { cur.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { cur.Add(sb.ToString()); sb.Clear(); rows.Add(cur.ToArray()); cur.Clear(); i++; }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); rows.Add(cur.ToArray()); }
        return rows;
    }

    private static decimal Dec(string s) => s == "" ? 0m : decimal.Parse(s, CultureInfo.InvariantCulture);

    private static int Col(string[] header, string name)
    {
        var i = Array.IndexOf(header, name);
        Assert.True(i >= 0, "ไม่มีคอลัมน์ " + name + " · มี: " + string.Join(" | ", header));
        return i;
    }

    [Fact]
    public async Task งบประมาณเทียบยอดจริง_ไฟล์_ทุกบรรทัดและยอดรวม_เท่ากับ_service_ที่ปุ่มเปรียบเทียบเรียก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, acc) = await SeedCompanyAsync(db, "บริษัททดสอบงบประมาณ X5");
        var budgets = new BudgetService(db);
        var b = await budgets.CreateAsync(cid, new CreateBudgetRequest("งบปี 2026", 2026, new List<BudgetLineRequest>
        {
            new(acc["51000"], Month1: 1_000m, Month2: 1_000m),          // งบ 2,000 · จริง 2,500 ⇒ เกินงบ
            new(acc["52000"], Month3: 900m),                              // งบ 900 · จริง 750 (83%) ⇒ ใกล้ถึงงบ
            new(acc["41000"], Month1: 10_000m),                           // รายได้ (ด้านเครดิต กลับเครื่องหมาย) · จริง 4,000 ⇒ ปกติ
        }), "tester");
        var n = 0;
        void Je(DateTime date, string dr, string cr, decimal amt, JournalEntryStatus st = JournalEntryStatus.Posted)
        {
            n++;
            var je = new JournalEntry { CompanyId = cid, EntryNumber = $"JV-X5B-{n:D3}", EntryDate = date, Status = st, Description = "t", TotalDebit = amt, TotalCredit = amt };
            je.Lines.Add(new JournalEntryLine { AccountId = acc[dr], DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = acc[cr], CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        Je(D(2026, 1, 10), "51000", "11111", 1_500m);
        Je(D(2026, 2, 10), "51000", "11111", 1_000m);
        Je(D(2026, 3, 10), "52000", "11111", 750m);
        Je(D(2026, 1, 20), "11111", "41000", 4_000m);
        Je(D(2026, 4, 1), "51000", "11111", 99_999m, JournalEntryStatus.Draft);   // ร่างไม่นับ (ทั้งหน้าและไฟล์)
        Je(D(2025, 12, 31), "51000", "11111", 77_777m);                           // นอกปีงบ
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var page = await new BudgetService(req).GetBudgetVsActualAsync(cid, b.Id);
        var file = await new FinanceReportExportService(req, budgets: new BudgetService(req)).BudgetVsActualAsync(cid, b.Id, null, TabularFormat.Csv);
        var rows = ParseCsv(file.Content);
        var h = rows[0];
        var (cCode, cBudget, cActual, cVar, cStatus) = (Col(h, "รหัสบัญชี"), Col(h, "งบประมาณทั้งปี"), Col(h, "ยอดจริง"), Col(h, "ผลต่าง (งบ − จริง)"), Col(h, "สถานะ"));
        var data = rows.Skip(1).Take(page.Lines.Count).ToList();
        Assert.Equal(page.Lines.Count + 2, rows.Count);   // หัว + ทุกบรรทัด + แถวรวม
        foreach (var l in page.Lines)   // จับคู่ด้วยรหัสบัญชี (ลำดับบรรทัดงบไม่ได้กำหนด — สองการเรียกอาจคืนคนละลำดับ)
        {
            var r = data.Single(x => x[cCode] == l.AccountCode);
            Assert.Equal(l.BudgetAmount, Dec(r[cBudget]));
            Assert.Equal(l.ActualAmount, Dec(r[cActual]));
            Assert.Equal(l.Variance, Dec(r[cVar]));
            Assert.Equal(l.UsageStatus, r[cStatus]);
        }
        var total = rows[^1];
        Assert.Equal(page.TotalBudget, Dec(total[cBudget]));
        Assert.Equal(page.TotalActual, Dec(total[cActual]));

        // สถานะจากตัวตัดสินกลาง (เดิมคำนวณใน JS ของหน้า) — ทั้งสามทาง
        Assert.Equal(BudgetUsageStatus.Over, page.Lines.Single(l => l.AccountCode == "51000").UsageStatus);
        Assert.Equal(2_500m, page.Lines.Single(l => l.AccountCode == "51000").ActualAmount);   // ร่าง/นอกปีไม่นับ
        Assert.Equal(BudgetUsageStatus.Near, page.Lines.Single(l => l.AccountCode == "52000").UsageStatus);
        var rev = page.Lines.Single(l => l.AccountCode == "41000");
        Assert.Equal(4_000m, rev.ActualAmount);
        Assert.Equal(BudgetUsageStatus.Normal, rev.UsageStatus);
        Assert.Equal(40m, rev.UsedPercent);
    }

    [Fact]
    public async Task กำไรขาดทุนโครงการ_ไฟล์_รายการต้นทุน205บรรทัดไม่ถูกตัด_รวมเท่ากับต้นทุนรวมของแท็บกำไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedCompanyAsync(db, "บริษัททดสอบโครงการ X5");
        var project = new Project { CompanyId = cid, Code = "PJ-X5", Name = "โครงการทดสอบส่งออก", StartDate = D(2026, 1, 1), BudgetAmount = 50_000m, ContractAmount = 80_000m, ActualRevenue = 60_000m };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 205; i++)
            db.ProjectCostEntries.Add(new ProjectCostEntry
            {
                CompanyId = cid, ProjectId = project.Id, EntryDate = D(2026, 1, 1).AddDays(i % 90),
                CostType = i % 3 == 0 ? "Labor" : i % 3 == 1 ? "Material" : "Travel",
                Description = i == 7 ? "=HYPERLINK(\"x\")" : $"ต้นทุนรายการที่ {i}", Quantity = 1, UnitCost = 100.25m + i, Amount = 100.25m + i,
            });
        // อีกโครงการ/อีกบริษัทไม่ปน
        var other = new Project { CompanyId = cid, Code = "PJ-OTHER", Name = "โครงการอื่น", StartDate = D(2026, 1, 1) };
        db.Projects.Add(other);
        await db.SaveChangesAsync();
        db.ProjectCostEntries.Add(new ProjectCostEntry { CompanyId = cid, ProjectId = other.Id, EntryDate = D(2026, 2, 1), CostType = "Labor", Description = "ไม่ใช่ของโครงการนี้", Amount = 9_999m });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new ProjectAccountingService(req);
        var profit = await svc.GetProfitabilityAsync(cid, project.Id);
        // ที่มาของบั๊กเดิม: แท็บต้นทุนเรียกโดยไม่ส่ง pageSize ⇒ 20 แถว · แท็บกำไรตัด 200/หมวด
        Assert.Equal(20, (await svc.GetCostEntriesAsync(cid, project.Id, new Accounting.Models.DTOs.PagedRequest())).Items.Count);

        var export = new FinanceReportExportService(req, projects: svc, accounting: new AccountingService(req));
        var rows = ParseCsv((await export.ProjectPnlAsync(cid, project.Id, "entries", TabularFormat.Csv)).Content);
        var h = rows[0];
        var (cAmt, cDesc) = (Col(h, "ยอด"), Col(h, "รายละเอียด"));
        var data = rows.Skip(1).Take(rows.Count - 2).ToList();
        Assert.Equal(205, data.Count);
        Assert.Equal(profit.TotalCost, data.Sum(r => Dec(r[cAmt])));
        Assert.Equal(profit.TotalCost, Dec(rows[^1][cAmt]));
        Assert.Equal("รวมต้นทุน", rows[^1][cDesc]);
        Assert.DoesNotContain(data, r => r[cDesc] == "ไม่ใช่ของโครงการนี้");
        Assert.Contains(data, r => r[cDesc].StartsWith("'=HYPERLINK"));   // ข้อความสูตรถูกกัน

        // สรุป + หมวด = ตัวเลขของ GetProfitabilityAsync ตรงตัว
        var sum = ParseCsv((await export.ProjectPnlAsync(cid, project.Id, null, TabularFormat.Csv)).Content);
        decimal Kv(string label) => Dec(sum.Single(r => r[0] == label)[1]);
        Assert.Equal(profit.TotalCost, Kv("ต้นทุนรวม"));
        Assert.Equal(profit.TotalRevenue, Kv("รายได้รับรู้แล้ว"));
        Assert.Equal(profit.GrossProfit, Kv("กำไรขั้นต้น"));
        var bd = ParseCsv((await export.ProjectPnlAsync(cid, project.Id, "breakdown", TabularFormat.Csv)).Content);
        Assert.Equal(profit.CostBreakdown.Count + 2, bd.Count);
        Assert.Equal(profit.TotalCost, Dec(bd[^1][Col(bd[0], "ยอด")]));

        // Excel 4 แผ่นสร้างได้ (รวมกระแสเงินสดต้นปี)
        var xlsx = await export.ProjectPnlAsync(cid, project.Id, null, TabularFormat.Xlsx);
        Assert.Equal(TabularExport.XlsxContentType, xlsx.ContentType);
        // ส่วนที่ไม่มีจริง = ล้มดังพร้อมรายชื่อที่ใช้ได้ (ไม่ได้ไฟล์แผ่นแรกเงียบ ๆ)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => export.ProjectPnlAsync(cid, project.Id, "nope", TabularFormat.Csv));
        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public async Task สินเชื่อ_ไฟล์_205สัญญาไม่ถูกตัดที่200_และกรองสถานะเหมือนหน้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedCompanyAsync(db, "บริษัททดสอบสินเชื่อ X5");
        for (var i = 1; i <= 205; i++)
            db.Loans.Add(new Loan
            {
                CompanyId = cid, LoanNumber = $"LN-{i:D4}", LoanType = "BankLoan", Name = $"สินเชื่อ {i}", PrincipalAmount = 1_000m * i,
                OutstandingPrincipal = 900m * i, InterestRate = 5.25m, TermMonths = 12, DisbursementDate = D(2026, 1, 1),
                FirstPaymentDate = D(2026, 2, 1), MaturityDate = D(2027, 1, 1), Status = i <= 3 ? "PaidOff" : "Active",
                LoanAccountId = acc["11111"],
            });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var export = new FinanceReportExportService(req, loans: new LoanService(req));
        var all = ParseCsv((await export.LoansAsync(cid, null, null, null, TabularFormat.Csv)).Content);
        Assert.Equal(205, all.Count - 1);
        var h = all[0];
        Assert.Equal(205, all.Skip(1).Select(r => r[Col(h, "เลขที่สัญญา")]).Distinct().Count());
        Assert.Contains(all.Skip(1), r => r[Col(h, "ผังบัญชีเงินกู้")] == "11111 เงินสด" && r[Col(h, "ประเภท")] == "สินเชื่อธนาคาร");
        // ทิศตรงข้าม: กรองสถานะ = เฉพาะสถานะนั้น (ตัวกรองเดียวกับหน้า)
        var paidOff = ParseCsv((await export.LoansAsync(cid, "PaidOff", null, null, TabularFormat.Csv)).Content);
        Assert.Equal(3, paidOff.Count - 1);
    }

    [Fact]
    public async Task เช็คคงค้าง_ชื่อผู้ติดต่อแปลงที่เซิร์ฟเวอร์_ไม่จำกัด200ราย_และไม่รั่วชื่อข้ามบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedCompanyAsync(db, "บริษัททดสอบเช็ค X5");
        var (otherCid, _) = await SeedCompanyAsync(db, "บริษัทอื่น X5");
        var contacts = Enumerable.Range(1, 205).Select(i => new Contact { CompanyId = cid, Name = $"ผู้ติดต่อ {i:D3}", TaxId = $"0105556{i:D6}", IsCustomer = true }).ToList();
        db.Contacts.AddRange(contacts);
        var foreign = new Contact { CompanyId = otherCid, Name = "ความลับบริษัทอื่น", IsCustomer = true };
        db.Contacts.Add(foreign);
        await db.SaveChangesAsync();
        db.Cheques.Add(new ChequeEntity { CompanyId = cid, ChequeNumber = 1001, ChequeDate = D(2026, 3, 1), Amount = 12_345.67m, ContactId = contacts[204].Id, IsInbound = true, Status = ChequeStatus.DepositedPending, IssuingBankName = "ธนาคารทดสอบ" });
        db.Cheques.Add(new ChequeEntity { CompanyId = cid, ChequeNumber = 1002, ChequeDate = D(2026, 3, 2), Amount = 50m, ContactId = foreign.Id, IsInbound = true, Status = ChequeStatus.DepositedPending });
        db.Cheques.Add(new ChequeEntity { CompanyId = cid, ChequeNumber = 1003, ChequeDate = D(2026, 3, 3), Amount = 70m, IsInbound = true, Status = ChequeStatus.Cleared });   // ขึ้นเงินแล้ว ไม่ค้าง
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new ChequeService(req, NullLogger<ChequeService>.Instance);
        var page = await svc.ListOutstandingRowsAsync(cid, null);
        Assert.Equal(2, page.Count);
        var mine = page.Single(r => r.ChequeNumber == 1001);
        Assert.Equal("ผู้ติดต่อ 205", mine.ContactName);
        Assert.Equal(contacts[204].TaxId, mine.ContactTaxId);
        Assert.Equal("รอเรียกเก็บ", mine.StatusLabel);
        // ข้อมูลเสียที่ชี้ผู้ติดต่อของบริษัทอื่น — ไม่แสดงชื่อ (กรอง CompanyId)
        Assert.Null(page.Single(r => r.ChequeNumber == 1002).ContactName);

        var rows = ParseCsv((await new FinanceReportExportService(req, cheques: svc).OutstandingChequesAsync(cid, null, TabularFormat.Csv)).Content);
        var h = rows[0];
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r[Col(h, "ผู้ติดต่อ")] == "ผู้ติดต่อ 205" && Dec(r[Col(h, "จำนวนเงิน")]) == 12_345.67m);
        Assert.DoesNotContain(rows, r => r.Any(c => c.Contains("ความลับบริษัทอื่น")));
    }

    [Fact]
    public async Task ทะเบียนเงินสดย่อย_ถึงวันที่รวมทั้งวัน_ยอดรวมจากservice_และไฟล์เท่าหน้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedCompanyAsync(db, "บริษัททดสอบเงินสดย่อย X5");
        var fund = new PettyCashFund { CompanyId = cid, Name = "หน้าร้าน", ImprestAmount = 5_000m, CurrentBalance = 3_800m, LinkedAccountId = acc["11111"] };
        db.PettyCashFunds.Add(fund);
        await db.SaveChangesAsync();
        db.PettyCashTransactions.AddRange(
            new PettyCashTransaction { CompanyId = cid, FundId = fund.Id, TransactionDate = D(2026, 2, 28), Amount = 999m, Type = "Disbursement", Description = "ก่อนช่วง" },
            new PettyCashTransaction { CompanyId = cid, FundId = fund.Id, TransactionDate = D(2026, 3, 1), Amount = 5_000m, Type = "Replenishment", Description = "เติมเงินต้นเดือน" },
            new PettyCashTransaction { CompanyId = cid, FundId = fund.Id, TransactionDate = D(2026, 3, 10), Amount = 450.50m, Type = "Disbursement", Description = "ค่าส่งเอกสาร", ExpenseAccountId = acc["51000"], ReceiptReference = "RC-1" },
            new PettyCashTransaction { CompanyId = cid, FundId = fund.Id, TransactionDate = D(2026, 3, 31).AddHours(15), Amount = 749.50m, Type = "Disbursement", Description = "ค่าแท็กซี่ วันสุดท้าย", ExpenseAccountId = acc["52000"], IsNonDeductible = true, NonDeductibleRuleCode = "RD-65TER-9" },
            new PettyCashTransaction { CompanyId = cid, FundId = fund.Id, TransactionDate = D(2026, 4, 1), Amount = 10m, Type = "Disbursement", Description = "หลังช่วง" });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new PettyCashService(req);
        var reg = await svc.GetRegisterAsync(cid, fund.Id, D(2026, 3, 1), D(2026, 3, 31));
        Assert.Equal(3, reg.Rows.Count);                 // วันสุดท้าย 15:00 อยู่ในช่วง · ก่อน/หลังช่วงไม่อยู่
        Assert.Equal(5_000m, reg.TotalIn);
        Assert.Equal(1_200m, reg.TotalOut);
        Assert.Equal("52000", reg.Rows[^1].ExpenseAccountCode);
        Assert.True(reg.Rows[^1].IsNonDeductible);
        // ทิศตรงข้าม: ไม่กรองวันที่ = ทุกรายการของกองทุน
        Assert.Equal(5, (await svc.GetRegisterAsync(cid, fund.Id, null, null)).Rows.Count);

        var rows = ParseCsv((await new FinanceReportExportService(req, pettyCash: svc).PettyCashRegisterAsync(cid, fund.Id, D(2026, 3, 1), D(2026, 3, 31), TabularFormat.Csv)).Content);
        var h = rows[0];
        Assert.Equal(reg.Rows.Count + 2, rows.Count);
        Assert.Equal(reg.TotalIn, Dec(rows[^1][Col(h, "รับเข้า (เติมเงิน)")]));
        Assert.Equal(reg.TotalOut, Dec(rows[^1][Col(h, "จ่ายออก (เบิกจ่าย)")]));
        Assert.Equal("ใช่ RD-65TER-9", rows[^2][Col(h, "รายจ่ายต้องห้าม §65 ตรี")]);
        // กองทุนของบริษัทอื่น = ไม่พบ (ไม่ใช่ทะเบียนว่าง)
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetRegisterAsync(Guid.NewGuid(), fund.Id, null, null));
    }

    private sealed class FakeGuard : ITenantGuard
    {
        private readonly HashSet<Guid> _allowed;
        public FakeGuard(params Guid[] allowed) => _allowed = allowed.ToHashSet();
        public Task<TenantCheckResult> CheckAsync(Guid companyId, ClaimsPrincipal user)
            => Task.FromResult(new TenantCheckResult(_allowed.Contains(companyId), _allowed.Contains(companyId) ? null : "ไม่ใช่สมาชิก"));
    }

    /// <summary>สิทธิ์ "งบการเงิน" ต่อบริษัท (null = มีทุกบริษัท) — ฝ่ายค้านรอบส่งออก 2026-10-15 รอบ 2 P2-10</summary>
    private sealed class FakePermissions : Accounting.Services.Interfaces.IPermissionService
    {
        private readonly HashSet<Guid>? _financialIn;
        public FakePermissions(HashSet<Guid>? financialIn) => _financialIn = financialIn;
        public Task<bool> HasPermissionAsync(Guid companyId, Guid userId, string permissionKey)
            => Task.FromResult(permissionKey == Accounting.Models.Constants.PermissionKeys.ReportsFinancial
                && (_financialIn == null || _financialIn.Contains(companyId)));
        public Task<List<string>> GetUserPermissionsAsync(Guid companyId, Guid userId) => Task.FromResult(new List<string>());
    }

    private static ConsolidationController MakeController(AccountingDbContext db, params Guid[] memberOf)
        => MakeController(db, null, memberOf);

    private static ConsolidationController MakeController(AccountingDbContext db, HashSet<Guid>? financialIn, params Guid[] memberOf)
        => new(new ConsolidationService(db), new FakeGuard(memberOf), new FakePermissions(financialIn))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test")),
                },
            },
        };

    private static int? Status(IActionResult? r) => r switch { ObjectResult o => o.StatusCode ?? 200, StatusCodeResult s => s.StatusCode, _ => null };

    [Fact]
    public async Task งบรวม_กลุ่มของบริษัทอื่น_404_สมาชิกที่ไม่ใช่บริษัทของผู้ใช้_403_ทิศตรงข้ามสมาชิกครบได้งบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (a, _) = await SeedCompanyAsync(db, "บริษัทแม่ A X5");
        var (b, _) = await SeedCompanyAsync(db, "บริษัทเป้าหมาย B X5");
        var (c, _) = await SeedCompanyAsync(db, "บริษัทลูก C X5");
        var groupOfB = new ConsolidationGroup { Name = "กลุ่มของ B", ParentCompanyId = b };
        var groupOfA = new ConsolidationGroup { Name = "กลุ่มของ A", ParentCompanyId = a };
        db.Set<ConsolidationGroup>().AddRange(groupOfB, groupOfA);
        await db.SaveChangesAsync();
        // ข้อมูลที่ persist ไว้ก่อนแก้: ผู้ใช้ A เคยเพิ่ม B (ที่ตัวเองไม่ได้เป็นสมาชิก) เข้ากลุ่มของ A
        db.Set<ConsolidationMember>().AddRange(
            new ConsolidationMember { ConsolidationGroupId = groupOfA.Id, CompanyId = c },
            new ConsolidationMember { ConsolidationGroupId = groupOfA.Id, CompanyId = b });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var userOfAandC = MakeController(req, a, c);
        // 1) กลุ่มของบริษัทอื่นผ่าน route ของ A = 404 (เดิมอ่าน/แก้/ลบได้)
        Assert.Equal(404, Status((await userOfAandC.GetGroup(a, groupOfB.Id)).Result));
        Assert.Equal(404, Status((await userOfAandC.GetBalanceSheet(a, groupOfB.Id, D(2026, 3, 31))).Result));
        Assert.Equal(404, Status((await userOfAandC.DeleteGroup(a, groupOfB.Id)).Result));
        // 2) งบรวมที่มีสมาชิก B (ผู้ใช้ไม่ใช่สมาชิก B) = 403 — ครอบข้อมูลที่ถูกเพิ่มไว้ก่อนแก้
        Assert.Equal(403, Status((await userOfAandC.GetBalanceSheet(a, groupOfA.Id, D(2026, 3, 31))).Result));
        Assert.Equal(403, Status((await userOfAandC.GetPnL(a, groupOfA.Id, D(2026, 1, 1), D(2026, 3, 31))).Result));
        // 3) เพิ่มบริษัทที่ไม่ได้เป็นสมาชิก = 403 · สร้างกลุ่มใต้บริษัทอื่น = 400
        Assert.Equal(403, Status((await userOfAandC.AddMember(a, groupOfA.Id, new AddConsolidationMemberRequest(b, 100m, ConsolidationMethod.Full))).Result));
        Assert.Equal(400, Status((await userOfAandC.CreateGroup(a, new CreateConsolidationGroupRequest("แอบสร้าง", null, b, "THB", 1))).Result));
        // ทิศตรงข้าม: ผู้ใช้ที่เป็นสมาชิกครบทุกบริษัทในกลุ่ม = ได้งบ (200)
        var userOfAll = MakeController(req, a, b, c);
        Assert.Equal(200, Status((await userOfAll.GetBalanceSheet(a, groupOfA.Id, D(2026, 3, 31))).Result));
        Assert.Equal(200, Status((await userOfAll.GetGroup(a, groupOfA.Id)).Result));
    }

    [Fact]
    public async Task งบรวม_ต้องมีสิทธิ์งบการเงิน_ทั้งบริษัทแม่และทุกสมาชิก_ทิศตรงข้ามมีครบได้งบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (a, _) = await SeedCompanyAsync(db, "บริษัทแม่ A P2-10");
        var (c, _) = await SeedCompanyAsync(db, "บริษัทลูก C P2-10");
        var group = new ConsolidationGroup { Name = "กลุ่ม P2-10", ParentCompanyId = a };
        db.Set<ConsolidationGroup>().Add(group);
        await db.SaveChangesAsync();
        db.Set<ConsolidationMember>().Add(new ConsolidationMember { ConsolidationGroupId = group.Id, CompanyId = c });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        // 1) เป็นสมาชิกทั้งสองบริษัท แต่ไม่มีสิทธิ์งบการเงินในบริษัทแม่ (route) = 403 (เดิม 200 — ไม่มีด่านสิทธิ์)
        var noParentRight = MakeController(req, new HashSet<Guid> { c }, a, c);
        Assert.Equal(403, Status((await noParentRight.GetBalanceSheet(a, group.Id, D(2026, 3, 31))).Result));
        Assert.Equal(403, Status((await noParentRight.GetPnL(a, group.Id, D(2026, 1, 1), D(2026, 3, 31))).Result));
        // 2) มีสิทธิ์ในบริษัทแม่ แต่ไม่มีในบริษัทสมาชิก = 403 (เดิมดูแค่สมาชิกภาพ)
        var noMemberRight = MakeController(req, new HashSet<Guid> { a }, a, c);
        Assert.Equal(403, Status((await noMemberRight.GetBalanceSheet(a, group.Id, D(2026, 3, 31))).Result));
        Assert.Equal(403, Status((await noMemberRight.GetEliminations(a, group.Id, D(2026, 3, 31))).Result));
        // เพิ่มสมาชิกที่ไม่มีสิทธิ์งบการเงินในบริษัทนั้น = 403
        var (d, _) = await SeedCompanyAsync(db, "บริษัท D P2-10");
        var addNoRight = MakeController(req, new HashSet<Guid> { a }, a, c, d);
        Assert.Equal(403, Status((await addNoRight.AddMember(a, group.Id, new AddConsolidationMemberRequest(d, 100m, ConsolidationMethod.Full))).Result));
        // ทิศตรงข้าม: มีสิทธิ์ครบทุกบริษัท = ได้งบ
        var full = MakeController(req, new HashSet<Guid> { a, c }, a, c);
        Assert.Equal(200, Status((await full.GetBalanceSheet(a, group.Id, D(2026, 3, 31))).Result));
    }
}
