using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Migration;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 2</b> (erp-review/2026-10-14/EXPORT_AUDIT.md §"ฝ่ายค้านรอบส่งออก (2026-10-15)") บนฐาน PostgreSQL จริง:
/// <list type="bullet">
/// <item>P1-2 ย้ายระบบกลางปี (ยอดนำเข้า 1 ก.ค. มีกำไรขาดทุน ม.ค.–มิ.ย.) ปิดปี ⇒ ยอดยกมาไม่มีแถวรายได้/ค่าใช้จ่าย · กำไรสะสมรับกำไรทั้งปี ·
///   รายการก่อนวันฐานในรอบเดียวกันไม่นับ · งบทดลองลงตัว · ทิศตรงข้าม: บริษัทปีปฏิทินไม่มียอดนำเข้า ผลเดิม (ยอดยกมาสุทธิฝั่งเดียว)</item>
/// <item>P2-6 รอบ เม.ย.–มี.ค. ยกยอดไป 1 เม.ย. · P2-7 ยกยอดด้วยมือต้องปิดปีก่อน + ไม่ทับยอดนำเข้า + audit</item>
/// <item>P1-3 ผังของผู้ใช้ + ผังเฉพาะกิจที่ระบบใส่ (54960 · 11341 · ผังธนาคาร) ⇒ ผังตั้งต้น = ผังของผู้ใช้</item>
/// <item>P1-4 ผังแม่แบบ + นำเข้า PEAK 113101/212101 ⇒ 11310/21210 ยังเป็นผังลงรายการและเป็นผังตั้งต้น · ผังที่มียอดยกมาไม่ถูกทำเป็นบัญชีคุม ·
///   P2-8 ผูกหัวบัญชีแล้วลูกปรับระดับตาม + audit</item>
/// <item>P2-5 งบทดลองกรองสาขาข้ามปีลงตัว (รายได้/ค่าใช้จ่ายเริ่มต้นรอบ) · บรรทัดสาขา A ที่ปันเข้ามิติสาขา B นับใน A เท่านั้น ·
///   งบกำไรขาดทุนกรองมิติ = รายงานกำไรขาดทุนตามมิติ (ปัน 30/70 + จำนวนเงิน)</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class AdversarialExportRound2DbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialExportRound2DbTests(ITestOutputHelper output) => _out = output;

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(Guid CompanyId, Guid Cash, Guid Capital, Guid Re, Guid Revenue, Guid Expense);

    private static async Task<Seed> SeedCompanyAsync(AccountingDbContext db, string name, string taxId, int fiscalStartMonth = 1)
    {
        var company = new Company { Name = name, TaxId = taxId, FiscalYearStartMonth = fiscalStartMonth };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        ChartOfAccount A(string code, string n, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = n, AccountType = t, Level = 4 };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = A("11111", "เงินสด", AccountType.Asset);
        var capital = A("31000", "ทุน", AccountType.Equity);
        var re = A("32000", "กำไรสะสม", AccountType.Equity);
        var rev = A("41000", "รายได้", AccountType.Revenue);
        var exp = A("51000", "ค่าใช้จ่าย", AccountType.Expense);
        await db.SaveChangesAsync();
        return new Seed(company.Id, cash.Id, capital.Id, re.Id, rev.Id, exp.Id);
    }

    private static int _n;
    private static JournalEntry Je(AccountingDbContext db, Guid companyId, DateTime date, Guid dr, Guid cr, decimal amt, Guid? branchId = null)
    {
        var n = Interlocked.Increment(ref _n);
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = $"JV-R2-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = "ทดสอบรอบ 2", TotalDebit = amt, TotalCredit = amt, BranchId = branchId,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
        db.JournalEntries.Add(je);
        return je;
    }

    private static async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> OpeningRowsAsync(AccountingDbContext db, Guid companyId, int year, int month)
        => await db.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.FiscalPeriod.Year == year && o.FiscalPeriod.Month == month)
            .GroupBy(o => o.AccountId)
            .Select(g => new { g.Key, D = g.Sum(x => x.OpeningDebit), C = g.Sum(x => x.OpeningCredit) })
            .ToDictionaryAsync(x => x.Key, x => (x.D, x.C));

    private static decimal Net((decimal Debit, decimal Credit) x) => x.Debit - x.Credit;

    // ═══════════════ P1-2 ย้ายระบบกลางปี ═══════════════

    [Fact]
    public async Task ย้ายระบบกลางปี_ปิดปี_ยอดยกมาไม่มีกำไรขาดทุน_กำไรสะสมรับกำไรทั้งปี_รายการก่อนฐานไม่นับ_งบทดลองลงตัว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทย้ายระบบกลางปี R2", "0105556000101");
        // งบทดลองนำเข้า ณ 1 ก.ค. 2025: กำไรขาดทุน ม.ค.–มิ.ย. (รายได้ 80,000 · ค่าใช้จ่าย 30,000) — ลงตัว
        var jul = new FiscalPeriod { CompanyId = s.CompanyId, Name = "2025/07", Year = 2025, Month = 7, StartDate = D(2025, 7, 1), EndDate = D(2025, 7, 31) };
        db.FiscalPeriods.Add(jul);
        await db.SaveChangesAsync();
        OpeningBalance Ob(Guid acc, decimal d, decimal c) => new()
        { CompanyId = s.CompanyId, FiscalPeriodId = jul.Id, AccountId = acc, OpeningDebit = d, OpeningCredit = c, Notes = OpeningRoll.ImportedNotePrefix + " r2" };
        db.OpeningBalances.AddRange(Ob(s.Cash, 150_000m, 0m), Ob(s.Capital, 0m, 100_000m), Ob(s.Revenue, 0m, 80_000m), Ob(s.Expense, 30_000m, 0m));
        Je(db, s.CompanyId, D(2025, 3, 15), s.Cash, s.Revenue, 5_000m);    // ก่อนวันฐานในรอบเดียวกัน — ฐานรวมไว้แล้ว ⇒ ไม่นับ
        Je(db, s.CompanyId, D(2025, 8, 1), s.Cash, s.Revenue, 20_000m);
        Je(db, s.CompanyId, D(2025, 9, 1), s.Expense, s.Cash, 4_000m);
        await db.SaveChangesAsync();

        YearEndClosing closing;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        // กำไรทั้งปี = (80,000 + 20,000) − (30,000 + 4,000) — เดิม 16,000 (ปิดแค่รายการ ⇒ รวมรายการก่อนฐาน 5,000 แต่ไม่รวมยอดนำเข้า)
        Assert.Equal(66_000m, closing.TransferredAmount);

        using var req = DbTestDatabase.TryCreateContext()!;
        var jan = await OpeningRowsAsync(req, s.CompanyId, 2026, 1);
        Assert.False(jan.ContainsKey(s.Revenue));        // เดิมแถวรายได้/ค่าใช้จ่ายของฐานถูกยกไปปีหน้า
        Assert.False(jan.ContainsKey(s.Expense));
        Assert.Equal(166_000m, Net(jan[s.Cash]));
        Assert.Equal(-100_000m, Net(jan[s.Capital]));
        Assert.Equal(-66_000m, Net(jan[s.Re]));
        Assert.Equal(0m, jan.Values.Sum(Net));            // เดิมไม่ลงตัว (รายการก่อนฐานถูกปิดเข้ากำไรสะสมแต่ไม่ถูกยก)

        var svc = new AccountingService(req);
        var tbYearEnd = await svc.GetTrialBalanceAsync(s.CompanyId, D(2025, 12, 31));
        Assert.Equal(tbYearEnd.TotalDebit, tbYearEnd.TotalCredit);
        Assert.All(tbYearEnd.Items.Where(i => i.AccountType is AccountType.Revenue or AccountType.Expense),
            i => Assert.Equal(0m, i.DebitBalance - i.CreditBalance));   // ปิดหมด รวมยอดนำเข้า
        var tbJan = await svc.GetTrialBalanceAsync(s.CompanyId, D(2026, 1, 31));
        Assert.Equal(tbJan.TotalDebit, tbJan.TotalCredit);
        var reTb = tbJan.Items.Single(i => i.AccountCode == "32000");
        Assert.Equal(66_000m, reTb.CreditBalance - reTb.DebitBalance);
        Assert.All(await svc.FindOpeningRollMismatchesAsync(s.CompanyId), y => Assert.Empty(y.Mismatches));
    }

    [Fact]
    public async Task ทิศตรงข้าม_บริษัทปีปฏิทินไม่มียอดนำเข้า_ปิดปีได้ผลเดิม_ยอดยกมาสุทธิฝั่งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทปีปฏิทิน R2", "0105556000102");
        Je(db, s.CompanyId, D(2025, 1, 5), s.Cash, s.Capital, 100_000m);
        Je(db, s.CompanyId, D(2025, 4, 1), s.Cash, s.Revenue, 10_000m);
        Je(db, s.CompanyId, D(2025, 6, 1), s.Expense, s.Cash, 3_000m);
        await db.SaveChangesAsync();
        YearEndClosing closing;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        Assert.Equal(7_000m, closing.TransferredAmount);

        using var req = DbTestDatabase.TryCreateContext()!;
        var rows = await req.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == s.CompanyId && o.FiscalPeriod.Year == 2026 && o.FiscalPeriod.Month == 1).ToListAsync();
        var cash = rows.Single(o => o.AccountId == s.Cash);
        Assert.Equal(107_000m, cash.OpeningDebit);      // สุทธิฝั่งเดียว (เดิม 110,000 / 3,000)
        Assert.Equal(0m, cash.OpeningCredit);
        Assert.Equal(-7_000m, rows.Where(o => o.AccountId == s.Re).Sum(o => o.OpeningDebit - o.OpeningCredit));
        Assert.Equal(0m, rows.Sum(o => o.OpeningDebit - o.OpeningCredit));
        Assert.DoesNotContain(rows, o => o.AccountId == s.Revenue || o.AccountId == s.Expense);
        Assert.True(await req.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(OpeningBalance)
            && a.NewValues!.Contains("opening-roll")));   // P2-7: การยกยอดมี audit
    }

    // ═══════════════ P2-6 รอบ เม.ย.–มี.ค. · P2-7 ยกยอดด้วยมือ ═══════════════

    [Fact]
    public async Task รอบเมษาถึงมีนา_ยกยอดไปหนึ่งเมษา_งบทดลองทุกเดือนลงตัว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทรอบเมษา R2", "0105556000103", fiscalStartMonth: 4);
        Je(db, s.CompanyId, D(2025, 4, 10), s.Cash, s.Capital, 50_000m);
        Je(db, s.CompanyId, D(2025, 5, 1), s.Cash, s.Revenue, 12_000m);
        Je(db, s.CompanyId, D(2026, 2, 1), s.Expense, s.Cash, 2_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
            Assert.Equal(10_000m, closing.TransferredAmount);
        }

        using var req = DbTestDatabase.TryCreateContext()!;
        var apr = await OpeningRowsAsync(req, s.CompanyId, 2026, 4);
        Assert.Equal(60_000m, Net(apr[s.Cash]));
        Assert.Equal(-10_000m, Net(apr[s.Re]));
        Assert.Empty(await OpeningRowsAsync(req, s.CompanyId, 2026, 1));   // เดิมยกไป ม.ค. ⇒ งบทดลอง ม.ค.–มี.ค. ไม่ลงตัว
        var svc = new AccountingService(req);
        foreach (var asOf in new[] { D(2026, 1, 31), D(2026, 3, 31), D(2026, 4, 30) })
        {
            var tb = await svc.GetTrialBalanceAsync(s.CompanyId, asOf);
            Assert.Equal(tb.TotalDebit, tb.TotalCredit);
        }
        var aprTb = await svc.GetTrialBalanceAsync(s.CompanyId, D(2026, 4, 30));
        Assert.DoesNotContain(aprTb.Items, i => i.AccountType == AccountType.Revenue && i.DebitBalance != i.CreditBalance);
    }

    [Fact]
    public async Task ยกยอดด้วยมือ_ต้องปิดปีก่อน_ไม่ทับยอดนำเข้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทยกยอดมือ R2", "0105556000104");
        Je(db, s.CompanyId, D(2025, 2, 1), s.Cash, s.Revenue, 1_000m);
        await db.SaveChangesAsync();
        using (var c0 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(c0).RollOpeningBalancesAsync(s.CompanyId, 2026, "u"));
            Assert.Contains("ยังไม่ได้ปิดปี 2025", ex.Message);
        }
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        // ทิศตรงข้าม: หลังปิดปี ยกซ้ำได้ (ยอดเท่าเดิม ไม่เขียนแถวที่ไม่เปลี่ยน)
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await new AccountingService(c2).RollOpeningBalancesAsync(s.CompanyId, 2026, "u"));
        // ผู้ใช้ลงยอดนำเข้าเองในงวด ม.ค. 2026 ⇒ ยกยอดด้วยมือไม่ทับ (ล้มพร้อมทางไปต่อ)
        var row = db.OpeningBalances.Single(o => o.CompanyId == s.CompanyId && o.AccountId == s.Cash && o.FiscalPeriod.Year == 2026);
        row.OpeningDebit = 999m;
        row.Notes = OpeningRoll.ImportedNotePrefix + " manual";
        await db.SaveChangesAsync();
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(c3).RollOpeningBalancesAsync(s.CompanyId, 2026, "u"));
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(999m, after.OpeningBalances.Single(o => o.Id == row.Id).OpeningDebit);
    }

    // ═══════════════ P1-3 ผังตั้งต้นในผังของผู้ใช้ ═══════════════

    [Fact]
    public async Task ผังของผู้ใช้_ผังเฉพาะกิจของระบบไม่ใช่ผังตั้งต้น_ค่าใช้จ่ายและลูกหนี้ได้ผังของผู้ใช้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทผังของตัวเอง R2", TaxId = "0105556000105" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        ChartOfAccount A(string code, AccountType t, bool system = false)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = "ผัง " + code, AccountType = t, Level = 2, IsSystemAccount = system };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        A(DocumentRounding.AccountCode, AccountType.Expense, system: true);   // migration ใส่ให้ทุกบริษัท (ไม่มีแม่ · ผังระบบ)
        A("5200", AccountType.Expense);
        A("5100", AccountType.Expense);
        A("11341", AccountType.Asset, system: true);                         // ผังพัก wallet ของช่องทางรอบโอน
        A("11301", AccountType.Asset);
        // ผังย่อยธนาคารที่ระบบสร้าง (รูปรหัส BankService — รอบ 3 P1: ตัดสินจากวิธีที่ระบบสร้าง ไม่ใช่จากตัวชี้ LinkedAccountId)
        var bankLinked = A(SpecialPurposeAccount.BankSubAccountCode("11101", 1), AccountType.Asset, system: true);
        A("11105", AccountType.Asset);
        await db.SaveChangesAsync();
        db.BankAccounts.Add(new BankAccount { CompanyId = company.Id, AccountName = "กระแสรายวัน", BankName = "กสิกร", AccountNumber = "1112223334", LinkedAccountId = bankLinked.Id });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var expense = await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.AccountType == AccountType.Expense && a.IsActive);
        Assert.Equal("5100", expense!.AccountCode);        // เดิม 54960 (ผังระบบก่อน)
        var ar = await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.AccountCode.StartsWith("113") && a.IsActive);
        Assert.Equal("11301", ar!.AccountCode);            // เดิม 11341
        var cash = await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.AccountCode.StartsWith("111") && a.IsActive);
        Assert.Equal("11105", cash!.AccountCode);          // ผังย่อยธนาคารที่ระบบสร้างไม่ใช่เงินสดตั้งต้น
        // ทิศตรงข้าม: หาด้วยรหัสเต็มยังได้ผังเฉพาะกิจ (ขาผลต่างปัดเศษ/ผังพักยังลงได้)
        Assert.NotNull(await req.ChartOfAccounts.DefaultPostingCandidates(req)
            .FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.AccountCode == DocumentRounding.AccountCode));
    }

    // ═══════════════ P1-4 / P2-8 ผูกบัญชีแม่ ═══════════════

    private static async Task<(Guid CompanyId, Dictionary<string, ChartOfAccount> Acc)> SeedTemplateAsync(AccountingDbContext db, string taxId)
    {
        var company = new Company { Name = "บริษัทผังแม่แบบ R2 " + taxId, TaxId = taxId };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, ChartOfAccount>();
        void T(string code, AccountType type, int level, string? parent)
        {
            var a = new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = "แม่แบบ " + code, AccountType = type, Level = level,
                IsSystemAccount = true, ParentAccountId = parent == null ? null : acc[parent].Id,
            };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        T("1", AccountType.Asset, 1, null); T("11", AccountType.Asset, 2, "1"); T("113", AccountType.Asset, 3, "11");
        T("11310", AccountType.Asset, 4, "113"); T("11320", AccountType.Asset, 4, "113");
        T("2", AccountType.Liability, 1, null); T("21", AccountType.Liability, 2, "2"); T("212", AccountType.Liability, 3, "21");
        T("21210", AccountType.Liability, 4, "212"); T("21220", AccountType.Liability, 4, "212");
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    private static CompetitorTable PeakTable(params (string Code, string Name, string Type)[] rows)
        => new(new[] { "accountCode", "accountName", "accountType" },
            rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["accountCode"] = r.Code, ["accountName"] = r.Name, ["accountType"] = r.Type,
            }).ToList(), "utf-8", ImportFileKind.Csv);

    [Fact]
    public async Task นำเข้าผังPEAK_113101_212101_ไม่ผูกใต้ผังลงรายการของแม่แบบ_ลูกหนี้เจ้าหนี้ตั้งต้นคงเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedTemplateAsync(db, "0105556000106");
        using (var imp = DbTestDatabase.TryCreateContext()!)
        {
            var res = await new PeakAccountsAdapter(imp).ImportAsync(cid,
                PeakTable(("113101", "ลูกหนี้ PEAK", "Asset"), ("212101", "เจ้าหนี้ PEAK", "Liability")),
                new ImportOptions(DryRun: false, SkipDuplicates: true, SourceTag: "r2"), default);
            Assert.Equal(2, res.RowsImported);
        }
        using var req = DbTestDatabase.TryCreateContext()!;
        var byCode = await req.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.Equal(acc["113"].Id, byCode["113101"].ParentAccountId);   // เดิมใต้ 11310 (ผังลงรายการ ⇒ กลายเป็นบัญชีคุม)
        Assert.Equal(4, byCode["113101"].Level);
        Assert.Equal(acc["212"].Id, byCode["212101"].ParentAccountId);
        var facts = await req.LoadPostingFactsAsync(cid, new[] { acc["11310"].Id, acc["21210"].Id });
        Assert.False(facts[acc["11310"].Id].IsGroup);
        Assert.False(facts[acc["21210"].Id].IsGroup);
        Assert.Equal("11310", (await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstAsync(a => a.CompanyId == cid && a.AccountCode.StartsWith("113") && a.IsActive)).AccountCode);   // เดิมพลิกเป็น 11320
        Assert.Equal("21210", (await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstAsync(a => a.CompanyId == cid && a.AccountCode.StartsWith("212") && a.IsActive)).AccountCode);
    }

    [Fact]
    public async Task ผังที่มียอดยกมาไม่ถูกทำเป็นบัญชีคุม_ผูกหัวบัญชีแล้วลูกปรับระดับตามพร้อมaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedTemplateAsync(db, "0105556000107");
        // ผังผู้ใช้ไม่มีแม่ "1130" ที่มีลูก "113001" อยู่แล้ว (ระดับเก่า 1 → 2) · "2125" มียอดยกมา (ไม่มีรายการ JE)
        var h1130 = new ChartOfAccount { CompanyId = cid, AccountCode = "1130", AccountName = "หัวลูกหนี้เดิม", AccountType = AccountType.Asset, Level = 1 };
        db.ChartOfAccounts.Add(h1130);
        await db.SaveChangesAsync();
        var c113001 = new ChartOfAccount { CompanyId = cid, AccountCode = "113001", AccountName = "ลูกหนี้ ก", AccountType = AccountType.Asset, Level = 2, ParentAccountId = h1130.Id };
        var used2125 = new ChartOfAccount { CompanyId = cid, AccountCode = "2125", AccountName = "เจ้าหนี้เดิม", AccountType = AccountType.Liability, Level = 1 };
        db.ChartOfAccounts.AddRange(c113001, used2125);
        var p = new FiscalPeriod { CompanyId = cid, Name = "2026/01", Year = 2026, Month = 1, StartDate = D(2026, 1, 1), EndDate = D(2026, 1, 31) };
        db.FiscalPeriods.Add(p);
        await db.SaveChangesAsync();
        db.OpeningBalances.Add(new OpeningBalance { CompanyId = cid, FiscalPeriodId = p.Id, AccountId = used2125.Id, OpeningCredit = 5_000m });
        await db.SaveChangesAsync();

        using (var imp = DbTestDatabase.TryCreateContext()!)
        {
            var res = await new PeakAccountsAdapter(imp).ImportAsync(cid, PeakTable(("212501", "เจ้าหนี้ย่อย", "Liability")),
                new ImportOptions(DryRun: false, SkipDuplicates: true, SourceTag: "r2"), default);
            Assert.Contains(res.Errors, e => e.Contains("2125"));   // ผูกไม่ได้ — บอกเหตุผล (เดิมผูก ⇒ 2125 ที่มียอดยกมากลายเป็นบัญชีคุม)
        }
        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            var result = await ChartParentLinker.RepairAsync(rep, cid, new[] { h1130.Id }, Guid.NewGuid().ToString());
            Assert.Equal(1, result.Linked);
        }
        using var req = DbTestDatabase.TryCreateContext()!;
        var byCode = await req.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.Null(byCode["212501"].ParentAccountId);
        Assert.Equal(acc["113"].Id, byCode["1130"].ParentAccountId);
        Assert.Equal(4, byCode["1130"].Level);
        Assert.Equal(5, byCode["113001"].Level);   // เดิมค้างระดับ 2
        Assert.True(await req.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityId == c113001.Id.ToString() && a.NewValues!.Contains("coa-relevel")));
    }

    // ═══════════════ P2-5 ตัวกรองสาขา/มิติ ═══════════════

    [Fact]
    public async Task งบทดลองกรองสาขาข้ามปีลงตัว_รายได้เริ่มต้นรอบ_บรรทัดสาขาAที่ปันเข้ามิติสาขาBนับในAเท่านั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทหลายสาขา R2", "0105556000108");
        var dimB = new AccountingDimension { CompanyId = s.CompanyId, Code = "BRB", Name = "มิติสาขา B", DimensionType = DimensionType.Branch };
        db.AccountingDimensions.Add(dimB);
        await db.SaveChangesAsync();
        var branchA = new Branch { CompanyId = s.CompanyId, Code = "A", Name = "สาขา A" };
        var branchB = new Branch { CompanyId = s.CompanyId, Code = "B", Name = "สาขา B", DimensionId = dimB.Id };
        db.Branches.AddRange(branchA, branchB);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2025, 5, 1), s.Cash, s.Revenue, 7_000m, branchA.Id);    // ปีก่อน สาขา A
        Je(db, s.CompanyId, D(2026, 2, 1), s.Cash, s.Revenue, 3_000m, branchA.Id);    // ปีนี้ สาขา A
        var crossed = Je(db, s.CompanyId, D(2026, 2, 5), s.Expense, s.Cash, 1_000m, branchA.Id);   // สาขา A แต่ปันเข้ามิติของ B
        await db.SaveChangesAsync();
        foreach (var l in crossed.Lines)
            db.JournalLineDimensions.Add(new JournalLineDimension { CompanyId = s.CompanyId, JournalEntryLineId = l.Id, DimensionId = dimB.Id });
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        var tbA = await svc.GetTrialBalanceAsync(s.CompanyId, D(2026, 3, 31), branchId: branchA.Id);
        Assert.Equal(tbA.TotalDebit, tbA.TotalCredit);
        var revA = tbA.Items.Single(i => i.AccountCode == "41000");
        Assert.Equal(3_000m, revA.CreditBalance - revA.DebitBalance);      // เดิม 10,000 (สะสมข้ามปี — ใบปิดปีไม่มีสาขา)
        var prior = tbA.Items.Single(i => i.AccountName == LedgerFilterRule.PriorPnlRowName);
        Assert.Equal(7_000m, prior.CreditBalance - prior.DebitBalance);
        var expA = tbA.Items.Single(i => i.AccountCode == "51000");
        Assert.Equal(1_000m, expA.DebitBalance - expA.CreditBalance);
        // สาขา B ไม่เห็นบรรทัดของสาขา A (เดิมนับเต็มทั้งสองสาขา)
        var tbB = await svc.GetTrialBalanceAsync(s.CompanyId, D(2026, 3, 31), branchId: branchB.Id);
        Assert.DoesNotContain(tbB.Items, i => i.AccountCode == "51000");
        // ทิศตรงข้าม: ไม่กรอง ⇒ ยอดยกมาทั้งบริษัท + รายการ (ไม่มีแถวกำไรขาดทุนก่อนรอบ)
        var all = await svc.GetTrialBalanceAsync(s.CompanyId, D(2026, 3, 31));
        Assert.Equal(all.TotalDebit, all.TotalCredit);
        Assert.DoesNotContain(all.Items, i => i.AccountName == LedgerFilterRule.PriorPnlRowName);
        var cashAll = all.Items.Single(i => i.AccountCode == "11111");
        Assert.Equal(9_000m, cashAll.DebitBalance - cashAll.CreditBalance);
        // บัญชีแยกประเภทกรองสาขาเดียวกัน: ยอดยกไปเท่างบทดลองทุกบัญชี
        var gl = await svc.GetGeneralLedgerAsync(s.CompanyId, D(2026, 1, 1), D(2026, 3, 31), branchId: branchA.Id);
        foreach (var a in gl.Accounts)
        {
            var t = tbA.Items.Single(i => i.AccountCode == a.AccountCode);
            var tbNet = a.AccountType is AccountType.Asset or AccountType.Expense ? t.DebitBalance - t.CreditBalance : t.CreditBalance - t.DebitBalance;
            Assert.Equal(tbNet, a.ClosingBalance);
        }
    }

    [Fact]
    public async Task งบกำไรขาดทุนกรองมิติ_เท่ารายงานตามมิติ_ปันสามสิบเจ็ดสิบและจำนวนเงิน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทปันส่วนมิติ R2", "0105556000109");
        var d1 = new AccountingDimension { CompanyId = s.CompanyId, Code = "D1", Name = "แผนก 1", DimensionType = DimensionType.Department };
        var d2 = new AccountingDimension { CompanyId = s.CompanyId, Code = "D2", Name = "แผนก 2", DimensionType = DimensionType.Department };
        db.AccountingDimensions.AddRange(d1, d2);
        await db.SaveChangesAsync();
        var exp = Je(db, s.CompanyId, D(2026, 3, 2), s.Expense, s.Cash, 1_000m);
        var rev = Je(db, s.CompanyId, D(2026, 3, 3), s.Cash, s.Revenue, 2_000m);
        await db.SaveChangesAsync();
        var expLine = exp.Lines.Single(l => l.DebitAmount > 0);
        var revLine = rev.Lines.Single(l => l.CreditAmount > 0);
        db.JournalLineDimensions.AddRange(
            new JournalLineDimension { CompanyId = s.CompanyId, JournalEntryLineId = expLine.Id, DimensionId = d1.Id, AllocatedPercent = 30m },
            new JournalLineDimension { CompanyId = s.CompanyId, JournalEntryLineId = expLine.Id, DimensionId = d2.Id, AllocatedPercent = 70m },
            new JournalLineDimension { CompanyId = s.CompanyId, JournalEntryLineId = revLine.Id, DimensionId = d1.Id, AllocatedAmount = 400m },
            new JournalLineDimension { CompanyId = s.CompanyId, JournalEntryLineId = revLine.Id, DimensionId = d2.Id, AllocatedAmount = 1_600m });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var acc = new AccountingService(req);
        var dims = new DimensionalAccountingService(req);
        foreach (var (dim, expExpected, revExpected) in new[] { (d1.Id, 300m, 400m), (d2.Id, 700m, 1_600m) })
        {
            var pnl = await acc.GetProfitAndLossAsync(s.CompanyId, D(2026, 3, 1), D(2026, 3, 31), dimensionId: dim);
            var byDim = await dims.GetDimensionPnLAsync(s.CompanyId, dim, D(2026, 3, 1), D(2026, 3, 31));
            Assert.Equal(expExpected, pnl.TotalExpenses);   // เดิม 1,000 ทั้งสองมิติ
            Assert.Equal(revExpected, pnl.TotalRevenue);
            Assert.Equal(byDim.TotalExpenses, pnl.TotalExpenses);
            Assert.Equal(byDim.TotalRevenue, pnl.TotalRevenue);
        }
        // ทิศตรงข้าม: ไม่กรอง ⇒ เต็มจำนวน
        var whole = await acc.GetProfitAndLossAsync(s.CompanyId, D(2026, 3, 1), D(2026, 3, 31));
        Assert.Equal(1_000m, whole.TotalExpenses);
        Assert.Equal(2_000m, whole.TotalRevenue);
    }
}
