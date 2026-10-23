using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม M 2026-10-22 — งบกำไรขาดทุนเปรียบเทียบ + drill-down บนฐานจริง:
/// ผลรวมคอลัมน์ (เดือน/ไตรมาส/ปี ตามรอบบัญชี เม.ย.) = <c>GetProfitAndLossAsync</c> ทั้งงวด · ร่างไม่นับ · ใบปิดบัญชีไม่นับ ·
/// ใบที่ยกเลิก (Reversed + ใบกลับรายการ) หักล้างกัน · บริษัทอื่นไม่ปน · drill-down รวม = ช่องที่คลิก ·
/// เอกสารต้นทาง: เอกสาร / รอบเงินเดือน / กลับรายการ / บันทึกมือ (ซื่อสัตย์ว่าไม่มีเอกสาร)
/// </summary>
[Trait("Category", "Db")]
public class PnlComparisonDbTests
{
    private readonly ITestOutputHelper _out;
    public PnlComparisonDbTests(ITestOutputHelper output) => _out = output;

    private static DateTime U(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, Guid Rev, Guid Exp, Guid Branch, Guid DocId, Guid RunId, Guid OriginalJe);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบงบเปรียบเทียบ", TaxId = "0105556000010", FiscalYearStartMonth = 4 };
        var other = new Company { Name = "บริษัทอื่น (ห้ามปน)", TaxId = "0105556000011", FiscalYearStartMonth = 4 };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();

        ChartOfAccount A(Guid cid, string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = cid, AccountCode = code, AccountName = name, AccountType = t, Level = 4 };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = A(company.Id, "11111", "เงินสด", AccountType.Asset);
        var re = A(company.Id, "32000", "กำไรสะสม", AccountType.Equity);
        var rev = A(company.Id, "41000", "รายได้จากการขาย", AccountType.Revenue);
        var exp = A(company.Id, "53000", "ค่าใช้จ่ายบริหาร", AccountType.Expense);
        var oCash = A(other.Id, "11111", "เงินสด", AccountType.Asset);
        var oRev = A(other.Id, "41000", "รายได้จากการขาย", AccountType.Revenue);
        var branch = new Branch { CompanyId = company.Id, Code = "B1", Name = "สาขาหนึ่ง" };
        db.Branches.Add(branch);
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าทดสอบงบ", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INV-M-0001",
            Status = DocumentStatus.Approved, DocumentDate = U(2026, 4, 10), SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m, Currency = "THB",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var n = 0;
        JournalEntry Je(Guid cid, DateTime date, Guid dr, Guid cr, decimal amt, JournalEntryStatus st = JournalEntryStatus.Posted,
            Guid? srcDoc = null, Guid? branchId = null, bool closing = false, Guid? original = null)
        {
            n++;
            var je = new JournalEntry
            {
                CompanyId = cid, EntryNumber = $"JV-M-{n:D4}", EntryDate = date, Status = st, Description = $"รายการ {n}",
                SourceDocumentId = srcDoc, BranchId = branchId, IsClosingEntry = closing, OriginalEntryId = original,
                TotalDebit = amt, TotalCredit = amt,
            };
            je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
            return je;
        }
        Je(company.Id, U(2025, 6, 15), cash.Id, rev.Id, 400m);                                   // ปีก่อน
        Je(company.Id, U(2026, 4, 10), cash.Id, rev.Id, 1_000m, srcDoc: doc.Id);                 // จากเอกสาร
        Je(company.Id, U(2026, 5, 20), exp.Id, cash.Id, 300m);                                   // บันทึกมือ
        var payJe = Je(company.Id, U(2026, 5, 25), exp.Id, cash.Id, 700m);                       // เงินเดือน (ผูกย้อนกลับ)
        Je(company.Id, U(2026, 6, 15), cash.Id, rev.Id, 2_000m, branchId: branch.Id);
        Je(company.Id, U(2026, 6, 16), cash.Id, rev.Id, 9_999m, st: JournalEntryStatus.Draft);   // ร่าง — ไม่นับ
        var orig = Je(company.Id, U(2026, 7, 1), cash.Id, rev.Id, 500m, st: JournalEntryStatus.Reversed);
        await db.SaveChangesAsync();
        Je(company.Id, U(2026, 8, 1), rev.Id, cash.Id, 500m, original: orig.Id);                 // ใบกลับรายการ
        Je(company.Id, U(2027, 3, 31), rev.Id, re.Id, 3_000m, closing: true);                    // ใบปิดบัญชี — ไม่นับ
        Je(other.Id, U(2026, 6, 15), oCash.Id, oRev.Id, 7_777m);                                 // บริษัทอื่น

        var run = new PayrollRun
        {
            CompanyId = company.Id, PayrollNumber = "PR-202605-M", Name = "เงินเดือน พฤษภาคม 2569", Year = 2026, Month = 5,
            PeriodStart = U(2026, 5, 1), PeriodEnd = U(2026, 5, 31), PayDate = U(2026, 5, 25), Status = "Paid",
            TotalGrossSalary = 700m, TotalNetPay = 700m, EmployeeCount = 1, JournalEntryId = payJe.Id,
        };
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();
        return new Seed(company.Id, rev.Id, exp.Id, branch.Id, doc.Id, run.Id, orig.Id);
    }

    [Fact]
    public async Task ผลรวมคอลัมน์_ทุกความละเอียด_เท่างบกำไรขาดทุนทั้งงวด_ร่างและใบปิดบัญชีไม่นับ_บริษัทอื่นไม่ปน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        var from = U(2026, 4, 1); var to = U(2027, 3, 31);

        var full = await svc.GetProfitAndLossAsync(s.CompanyId, from, to);
        Assert.Equal(3_000m, full.TotalRevenue);            // 1,000 + 2,000 + 500 − 500 · ไม่มี 9,999 (ร่าง) · ไม่มี −3,000 (ปิดบัญชี) · ไม่มี 7,777
        Assert.Equal(1_000m, full.TotalExpenses);

        foreach (var g in new[] { PnlCompareGranularity.Month, PnlCompareGranularity.Quarter, PnlCompareGranularity.Year })
        {
            var cmp = await svc.GetProfitAndLossComparisonAsync(s.CompanyId, from, to, g, comparePriorYear: true);
            Assert.Equal(g == PnlCompareGranularity.Month ? 12 : g == PnlCompareGranularity.Quarter ? 4 : 1, cmp.Periods.Count);
            Assert.Equal(full.TotalRevenue, cmp.TotalRevenue.Total);
            Assert.Equal(full.TotalExpenses, cmp.TotalExpenses.Total);
            Assert.Equal(full.NetIncome, cmp.NetIncome.Total);
            Assert.Equal(full.TotalRevenue, cmp.TotalRevenue.Amounts.Sum());
            foreach (var row in cmp.Revenue.Concat(cmp.Expenses))
                Assert.Equal(full.Revenue.Concat(full.Expenses).Single(x => x.AccountId == row.AccountId).Amount, row.Total);
            Assert.Equal(400m, cmp.TotalRevenue.PriorTotal);   // มิ.ย. 2568 อยู่ในรอบก่อน
        }

        var monthly = await svc.GetProfitAndLossComparisonAsync(s.CompanyId, from, to, PnlCompareGranularity.Month, comparePriorYear: true);
        Assert.Equal(new[] { 1_000m, 0m, 2_000m, 500m, -500m, 0m, 0m, 0m, 0m, 0m, 0m, 0m }, monthly.TotalRevenue.Amounts);
        Assert.Equal(400m, monthly.TotalRevenue.PriorAmounts![2]);
        Assert.Equal(400m, monthly.TotalRevenue.ChangeVsPriorYear![2]);   // 2,000 เทียบ 400 = +400%

        // ทิศตรงข้าม: ตัวกรองสาขา ⇒ เหลือเฉพาะรายการของสาขา (ตัวกรองชุดเดียวกับงบกำไรขาดทุน)
        var byBranch = await svc.GetProfitAndLossComparisonAsync(s.CompanyId, from, to, PnlCompareGranularity.Quarter, false, branchId: s.Branch);
        Assert.Equal(2_000m, byBranch.TotalRevenue.Total);
        Assert.Equal((await svc.GetProfitAndLossAsync(s.CompanyId, from, to, branchId: s.Branch)).TotalRevenue, byBranch.TotalRevenue.Total);
    }

    [Fact]
    public async Task drilldown_ผลรวมเท่าช่อง_และบอกเอกสารต้นทางตามจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        var user = Guid.NewGuid();
        var monthly = await svc.GetProfitAndLossComparisonAsync(s.CompanyId, U(2026, 4, 1), U(2027, 3, 31), PnlCompareGranularity.Month, false);

        // ทุกช่องของทุกผัง: ผลรวม drill-down = ตัวเลขในช่อง (รวมเดือนที่มีใบปิดบัญชี — มี.ค. 2570 ต้องเป็น 0 ทั้งสองฝั่ง)
        foreach (var row in monthly.Revenue.Concat(monthly.Expenses))
            for (var i = 0; i < monthly.Periods.Count; i++)
            {
                var p = monthly.Periods[i];
                var d = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, row.AccountId!.Value, p.Start, p.EndInclusive);
                Assert.Equal(row.Amounts[i], d.Total);
            }

        var april = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, s.Rev, U(2026, 4, 1), U(2026, 4, 30));
        var docLine = Assert.Single(april.Lines);
        Assert.Equal("Document", docLine.Source.Kind);
        Assert.Equal("/pages/documents.html?openDoc=" + s.DocId, docLine.Source.Href);
        Assert.Contains("INV-M-0001", docLine.Source.Label);
        Assert.Equal(JournalSourceLink.JournalHref(docLine.JournalEntryId), docLine.JournalHref);

        var may = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, s.Exp, U(2026, 5, 1), U(2026, 5, 31));
        Assert.Equal(1_000m, may.Total);
        Assert.Equal(2, may.TotalCount);
        Assert.Equal(1, may.WithoutSourceCount);
        Assert.Contains(may.Lines, l => l.Source.Kind == "Payroll" && l.Source.Href == "/pages/payroll.html?runId=" + s.RunId && l.Amount == 700m);
        Assert.Contains(may.Lines, l => l.Source.Kind == "Manual" && l.Source.Href == null && l.Amount == 300m);

        var aug = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, s.Rev, U(2026, 8, 1), U(2026, 8, 31));
        var rev = Assert.Single(aug.Lines);
        Assert.Equal(-500m, rev.Amount);
        Assert.Equal("Reversal", rev.Source.Kind);
        Assert.Equal(JournalSourceLink.JournalHref(s.OriginalJe), rev.Source.Href);

        var march = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, s.Rev, U(2027, 3, 1), U(2027, 3, 31));
        Assert.Empty(march.Lines);                          // ใบปิดบัญชีไม่อยู่ใน drill-down (งบไม่นับ)

        // แบ่งหน้า: รวม/จำนวนทั้งหมดคงที่ แถวต่อหน้าตาม pageSize
        var paged = await svc.GetProfitAndLossDrilldownAsync(s.CompanyId, user, s.Exp, U(2026, 4, 1), U(2027, 3, 31), page: 2, pageSize: 1);
        Assert.Single(paged.Lines);
        Assert.Equal(1_000m, paged.Total);
        Assert.Equal(2, paged.TotalCount);
    }

    [Fact]
    public async Task drilldown_ผังของบริษัทอื่น_ไม่พบ_และผังที่ไม่ใช่กำไรขาดทุน_ถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var s2 = await SeedAsync(db);
        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.GetProfitAndLossDrilldownAsync(s.CompanyId, Guid.NewGuid(), s2.Rev, U(2026, 4, 1), U(2026, 4, 30)));
        var cashId = req.ChartOfAccounts.Where(a => a.CompanyId == s.CompanyId && a.AccountCode == "11111").Select(a => a.Id).First();
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.GetProfitAndLossDrilldownAsync(s.CompanyId, Guid.NewGuid(), cashId, U(2026, 4, 1), U(2026, 4, 30)));
    }
}
