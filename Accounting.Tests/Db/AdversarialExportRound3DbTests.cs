using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 3</b> (ฝ่ายค้านบน de823512 · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 3") บนฐาน PostgreSQL จริง:
/// <list type="bullet">
/// <item>P1 ผังที่ config ชี้ (ผังพัก gateway = 11310 · ธนาคารผูก 11111) ยังเป็นผังตั้งต้น · ผังของลูกค้ารหัส 54960 (ไม่ใช่ผังระบบ) เป็นค่าใช้จ่ายตั้งต้นได้ ·
///   ทิศตรงข้าม: 54960 ของระบบในบริษัทผังแม่แบบยังไม่ใช่ค่าใช้จ่ายตั้งต้น</item>
/// <item>P2-1 ไฟล์งบทดลองกรองสาขาหลังปิดปีไม่มีคำเตือนผลต่างเท็จ · P2-3 ฐานตรงต้นรอบ: กำไรของปี = รายการของปีเท่านั้น</item>
/// <item>P2-4 รอบ เม.ย.–มี.ค. ที่มียอดยกรุ่นเก่าไป ม.ค.: รายงานอธิบาย · ซ่อมยกเลิกแถว ม.ค. + เขียน เม.ย. · งบทดลอง ก.พ./เม.ย. ถูก</item>
/// <item>P2-6 ผังที่ถูกลบแต่ยังมียอด: ปิดปีล้มพร้อมชื่อผัง · รายงานบอกเหตุ · P2-7 ผังที่ผูกใต้ผังลงรายการของแม่แบบ: รายงาน + ย้ายพร้อม audit</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class AdversarialExportRound3DbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialExportRound3DbTests(ITestOutputHelper output) => _out = output;

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
            CompanyId = companyId, EntryNumber = $"JV-R3-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = "ทดสอบรอบ 3", TotalDebit = amt, TotalCredit = amt, BranchId = branchId,
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

    // ═══════════════ P1 ผังที่ config ชี้ไม่ถูกตัดจากผังตั้งต้น ═══════════════

    private static async Task<(Guid CompanyId, Dictionary<string, ChartOfAccount> Acc)> SeedTemplateAsync(AccountingDbContext db, string taxId)
    {
        var company = new Company { Name = "บริษัทผังแม่แบบ R3 " + taxId, TaxId = taxId };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, ChartOfAccount>();
        void T(string code, AccountType type, int level, string? parent, bool system = true)
        {
            var a = new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = "แม่แบบ " + code, AccountType = type, Level = level,
                IsSystemAccount = system, ParentAccountId = parent == null ? null : acc[parent].Id,
            };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        T("1", AccountType.Asset, 1, null); T("11", AccountType.Asset, 2, "1");
        T("111", AccountType.Asset, 3, "11"); T("11111", AccountType.Asset, 4, "111"); T("11112", AccountType.Asset, 4, "111");
        T("113", AccountType.Asset, 3, "11"); T("11310", AccountType.Asset, 4, "113"); T("11320", AccountType.Asset, 4, "113");
        T("5", AccountType.Expense, 1, null); T("51", AccountType.Expense, 2, "5"); T("511", AccountType.Expense, 3, "51");
        T("51110", AccountType.Expense, 4, "511");
        T(DocumentRounding.AccountCode, AccountType.Expense, 4, null);   // migration ใส่ให้ทุกบริษัท (ผังระบบ · ไม่มีแม่)
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    private static Task<ChartOfAccount?> ByPrefixAsync(AccountingDbContext db, Guid companyId, string prefix)
        => db.ChartOfAccounts.DefaultFallbackCandidates(db)
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith(prefix) && a.IsActive);

    [Fact]
    public async Task ผังที่configชี้_ผังพักgateway11310_ธนาคารผูก11111_ยังเป็นผังตั้งต้น_ผูกครบทุกผังไม่กลายเป็นnull()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, acc) = await SeedTemplateAsync(db, "0105556000301");
        // ผู้ใช้ตั้งผังพักของผู้ให้บริการรับชำระเงินเป็น 11310 (ตั้งได้ — ผังลงรายการใดก็ได้) + ผูกบัญชีธนาคารกับ 11111
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = cid, ProviderCode = "omise", ClearingAccountId = acc["11310"].Id });
        db.BankAccounts.Add(new BankAccount { CompanyId = cid, AccountName = "ออมทรัพย์", BankName = "กสิกร", AccountNumber = "1112223334", LinkedAccountId = acc["11111"].Id });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal("11310", (await ByPrefixAsync(req, cid, "113"))!.AccountCode);   // รอบ 2: 11320 ⇒ ลูกหนี้แตกสองผัง
            Assert.Equal("11111", (await ByPrefixAsync(req, cid, "111"))!.AccountCode);   // รอบ 2: 11112
            // ทิศตรงข้าม: 54960 ของระบบ (ผังเฉพาะกิจ) ยังไม่ใช่ค่าใช้จ่ายตั้งต้นของบริษัทผังแม่แบบ
            var expense = await req.ChartOfAccounts.DefaultFallbackCandidates(req)
                .FirstOrDefaultAsync(a => a.CompanyId == cid && a.AccountType == AccountType.Expense && a.IsActive);
            Assert.Equal("51110", expense!.AccountCode);
        }

        // ผูกธนาคารครบทุกผัง 111x ⇒ ยังได้เงินสดตั้งต้น (รอบ 2: null ⇒ ล้ม "ไม่มีผังเงินสดตั้งต้น" ทั้งที่ 09da513a ทำงานได้)
        db.BankAccounts.Add(new BankAccount { CompanyId = cid, AccountName = "กระแสรายวัน", BankName = "กสิกร", AccountNumber = "5556667778", LinkedAccountId = acc["11112"].Id });
        await db.SaveChangesAsync();
        using (var req2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("11111", (await ByPrefixAsync(req2, cid, "111"))!.AccountCode);
    }

    [Fact]
    public async Task ผังของลูกค้า_รหัส54960ที่ไม่ใช่ผังระบบ_เป็นค่าใช้จ่ายตั้งต้นได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทผังของตัวเอง R3", TaxId = "0105556000302" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        // migration ใส่ 54960 แบบ ON CONFLICT DO NOTHING ⇒ ลูกค้าที่มี 54960 ของตัวเองอยู่แล้วคงเป็นผังของเขา (ไม่ใช่ผังระบบ)
        db.ChartOfAccounts.AddRange(
            new ChartOfAccount { CompanyId = company.Id, AccountCode = DocumentRounding.AccountCode, AccountName = "ค่าใช้จ่ายเบ็ดเตล็ด", AccountType = AccountType.Expense, Level = 2 },
            new ChartOfAccount { CompanyId = company.Id, AccountCode = "59000", AccountName = "ค่าใช้จ่ายอื่น", AccountType = AccountType.Expense, Level = 2 });
        await db.SaveChangesAsync();
        using var req = DbTestDatabase.TryCreateContext()!;
        var expense = await req.ChartOfAccounts.DefaultFallbackCandidates(req)
            .FirstOrDefaultAsync(a => a.CompanyId == company.Id && a.AccountType == AccountType.Expense && a.IsActive);
        Assert.Equal(DocumentRounding.AccountCode, expense!.AccountCode);   // รอบ 2: ตัดด้วยรหัสอย่างเดียว ⇒ 59000
    }

    // ═══════════════ P2-1 ไฟล์งบทดลองกรองสาขาหลังปิดปี ═══════════════

    [Fact]
    public async Task ไฟล์งบทดลองกรองสาขาหลังปิดปี_ไม่มีคำเตือนผลต่างเท็จ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทหลายสาขา R3", "0105556000303");
        var branchA = new Branch { CompanyId = s.CompanyId, Code = "A", Name = "สาขา A" };
        db.Branches.Add(branchA);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2025, 5, 1), s.Cash, s.Revenue, 7_000m, branchA.Id);
        Je(db, s.CompanyId, D(2026, 2, 1), s.Cash, s.Revenue, 3_000m, branchA.Id);
        Je(db, s.CompanyId, D(2026, 2, 5), s.Expense, s.Cash, 1_000m, branchA.Id);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());

        using var req = DbTestDatabase.TryCreateContext()!;
        var export = new AccountingReportExportService(req, new AccountingService(req));
        var file = await export.TrialBalanceAsync(s.CompanyId, D(2026, 1, 1), D(2026, 3, 31), null, branchA.Id, null, TabularFormat.Csv);
        var csv = Encoding.UTF8.GetString(file.Content);
        Assert.Contains(LedgerFilterRule.PriorPnlRowName, csv);          // แถวกำไรขาดทุนก่อนรอบยังอยู่
        Assert.DoesNotContain("ยอดยกมา + เคลื่อนไหว ≠ ยอดยกไป", csv);   // เดิมขึ้นทุกไฟล์ (ผลต่าง = กำไรปีก่อน 7,000)
    }

    // ═══════════════ P2-3 ฐานตรงต้นรอบ ═══════════════

    [Fact]
    public async Task ฐานนำเข้าตรงต้นรอบมีกำไรขาดทุน_กำไรของปีเท่ารายการของปี_กำไรสะสมรวมถูก_แยกบรรทัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทฐานต้นรอบ R3", "0105556000304");
        // งบทดลองก่อนปิดบัญชี ณ 31 ธ.ค. 2024 (มีรายได้/ค่าใช้จ่ายของปี 2024) นำเข้าเป็นยอดยกมา 1 ม.ค. 2025
        var jan = new FiscalPeriod { CompanyId = s.CompanyId, Name = "2025/01", Year = 2025, Month = 1, StartDate = D(2025, 1, 1), EndDate = D(2025, 1, 31) };
        db.FiscalPeriods.Add(jan);
        await db.SaveChangesAsync();
        OpeningBalance Ob(Guid a, decimal d, decimal c) => new()
        { CompanyId = s.CompanyId, FiscalPeriodId = jan.Id, AccountId = a, OpeningDebit = d, OpeningCredit = c, Notes = OpeningRoll.ImportedNotePrefix + " r3" };
        db.OpeningBalances.AddRange(Ob(s.Cash, 150_000m, 0m), Ob(s.Capital, 0m, 100_000m), Ob(s.Revenue, 0m, 80_000m), Ob(s.Expense, 30_000m, 0m));
        Je(db, s.CompanyId, D(2025, 8, 1), s.Cash, s.Revenue, 20_000m);
        Je(db, s.CompanyId, D(2025, 9, 1), s.Expense, s.Cash, 4_000m);
        await db.SaveChangesAsync();

        YearEndClosing closing;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        Assert.Equal(16_000m, closing.TransferredAmount);   // เดิม 66,000 (กำไรปี 2024 ที่อยู่ในฐานถูกนับเป็นกำไรปี 2025)

        using var req = DbTestDatabase.TryCreateContext()!;
        var reLines = await req.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == closing.ClosingJournalEntryId && l.AccountId == s.Re).ToListAsync();
        Assert.Equal(66_000m, reLines.Sum(l => l.CreditAmount - l.DebitAmount));                        // กำไรสะสมรวมถูก
        Assert.Contains(reLines, l => l.CreditAmount == 16_000m && !l.Description!.Contains(OpeningRoll.PriorYearsPnlLabel));
        Assert.Contains(reLines, l => l.CreditAmount == 50_000m && l.Description!.Contains(OpeningRoll.PriorYearsPnlLabel));
        var jan26 = await OpeningRowsAsync(req, s.CompanyId, 2026, 1);
        Assert.Equal(-66_000m, Net(jan26[s.Re]));
        Assert.False(jan26.ContainsKey(s.Revenue));
        var tb = await new AccountingService(req).GetTrialBalanceAsync(s.CompanyId, D(2025, 12, 31));
        Assert.Equal(tb.TotalDebit, tb.TotalCredit);
        Assert.All(tb.Items.Where(i => i.AccountType is AccountType.Revenue or AccountType.Expense),
            i => Assert.Equal(0m, i.DebitBalance - i.CreditBalance));
    }

    // ═══════════════ P2-4 รอบ เม.ย.–มี.ค. ที่ปิดปีด้วยโค้ดรุ่นเก่า (ยกยอดไป ม.ค.) ═══════════════

    [Fact]
    public async Task รอบเมษาที่มียอดยกรุ่นเก่าไปมกรา_รายงานอธิบาย_ซ่อมยกเลิกแถวมกราและเขียนเมษา_งบทดลองถูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทรอบเมษารุ่นเก่า R3", "0105556000305", fiscalStartMonth: 4);
        Je(db, s.CompanyId, D(2025, 4, 10), s.Cash, s.Capital, 50_000m);
        Je(db, s.CompanyId, D(2025, 5, 1), s.Cash, s.Revenue, 12_000m);
        Je(db, s.CompanyId, D(2026, 2, 1), s.Expense, s.Cash, 2_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());

        // จำลองสภาพของบริษัทที่ปิดปีด้วยโค้ดก่อน de823512: ไม่มีแถว เม.ย. 2026 · มีแถวยกยอดไป ม.ค. 2026 (คิดแบบปีปฏิทิน — เงินสดผิด)
        // รอบ 5 P2-2: ใบปิดปีรุ่นเก่าเกิดก่อนการแก้ (ตัวตรวจใช้เวลาของใบปิดปีเป็นหลักฐาน)
        (await db.YearEndClosings.SingleAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2025)).CreatedAt = D(2026, 4, 2);
        db.OpeningBalances.RemoveRange(db.OpeningBalances.Where(o => o.CompanyId == s.CompanyId));
        await db.SaveChangesAsync();
        var janPeriod = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2026 && p.Month == 1);
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = janPeriod.Id, AccountId = s.Cash, OpeningDebit = 70_000m, Notes = OpeningRoll.RolledNote(2026) },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = janPeriod.Id, AccountId = s.Capital, OpeningCredit = 50_000m, Notes = null });
        await db.SaveChangesAsync();
        using (var before = DbTestDatabase.TryCreateContext()!)
        {
            var tbFeb = await new AccountingService(before).GetTrialBalanceAsync(s.CompanyId, D(2026, 2, 28));
            Assert.Equal(68_000m, tbFeb.Items.Single(i => i.AccountCode == "11111").DebitBalance - tbFeb.Items.Single(i => i.AccountCode == "11111").CreditBalance);
        }

        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            var report = await new AccountingService(rep).FindOpeningRollMismatchesAsync(s.CompanyId);
            var y = Assert.Single(report);
            Assert.Equal(2026, y.Year);
            Assert.Equal(2, y.LegacyRolledRows);
            Assert.Contains("ม.ค.", y.Note);                              // อธิบายกรณี (เดิมทุกบัญชีขึ้น "ไม่ตรง" โดยไม่บอกเหตุ)
            var cash = y.Mismatches.Single(m => m.AccountId == s.Cash);
            Assert.Equal(60_000m, cash.ExpectedNet);                       // ไม่ใช้งวด ม.ค. รุ่นเก่าเป็นฐาน (ถ้าใช้ = 68,000)
        }
        using (var fix = DbTestDatabase.TryCreateContext()!)
        {
            var result = await new AccountingService(fix).RepairOpeningRollAsync(s.CompanyId, new[] { 2026 }, Guid.NewGuid().ToString());
            Assert.Equal(1, result.RepairedYears);
        }

        using var req = DbTestDatabase.TryCreateContext()!;
        var janRows = await req.OpeningBalances.IgnoreQueryFilters().AsNoTracking().Where(o => o.FiscalPeriodId == janPeriod.Id).ToListAsync();
        Assert.Equal(2, janRows.Count);
        Assert.All(janRows, r => Assert.True(r.IsDeleted));                // ยกเลิก (soft) ไม่ลบจริง
        Assert.Equal(2, await req.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains(OpeningRoll.LegacyJanNeutralizedAction)));
        var apr = await OpeningRowsAsync(req, s.CompanyId, 2026, 4);
        Assert.Equal(60_000m, Net(apr[s.Cash]));
        Assert.Equal(-10_000m, Net(apr[s.Re]));
        var svc = new AccountingService(req);
        foreach (var (asOf, cashExpected) in new[] { (D(2026, 2, 28), 60_000m), (D(2026, 4, 30), 60_000m) })
        {
            var tb = await svc.GetTrialBalanceAsync(s.CompanyId, asOf);
            Assert.Equal(tb.TotalDebit, tb.TotalCredit);
            var c = tb.Items.Single(i => i.AccountCode == "11111");
            Assert.Equal(cashExpected, c.DebitBalance - c.CreditBalance);
        }
        var after = Assert.Single(await svc.FindOpeningRollMismatchesAsync(s.CompanyId));
        Assert.Empty(after.Mismatches);
        Assert.Equal(0, after.LegacyRolledRows);
    }

    // ═══════════════ P2-6 ผังที่ถูกลบแต่ยังมียอด ═══════════════

    [Fact]
    public async Task ผังที่ถูกลบแต่ยังมียอด_ปิดปีล้มพร้อมชื่อผัง_ไม่เขียนอะไร_รายงานบอกเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทผังถูกลบ R3", "0105556000306");
        var other = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "11500", AccountName = "ลูกหนี้อื่น", AccountType = AccountType.Asset, Level = 4 };
        db.ChartOfAccounts.Add(other);
        await db.SaveChangesAsync();
        // รอบ 4 (CI 774ea7bc): ปิดปีที่จบแล้วเท่านั้น — ปีที่สิ้นรอบในอนาคตชนด่าน "วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต" (เทสต์ห้ามขึ้นกับนาฬิกา)
        Je(db, s.CompanyId, D(2024, 3, 1), other.Id, s.Capital, 5_000m);
        Je(db, s.CompanyId, D(2024, 4, 1), s.Cash, s.Revenue, 1_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        other.IsDeleted = true;   // ลบผังทั้งที่ยังมียอด 5,000
        Je(db, s.CompanyId, D(2025, 2, 1), s.Cash, s.Revenue, 500m);
        await db.SaveChangesAsync();

        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<DeletedAccountsWithBalanceException>(() =>
                new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString()));
            Assert.Contains("11500", ex.Message);                     // เดิมตัดเงียบ ⇒ ยอดยกมา 2026 ไม่ลงตัว
            Assert.Contains("กู้คืน", ex.Message);
        }
        using var req = DbTestDatabase.TryCreateContext()!;
        Assert.False(await req.YearEndClosings.AnyAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2025));   // ไม่เขียนอะไร
        Assert.False(await req.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == "YE-2025"));
        var y2025 = (await new AccountingService(req).FindOpeningRollMismatchesAsync(s.CompanyId)).Single(y => y.Year == 2025);
        Assert.Contains("11500", y2025.SkipReason);                   // รายงานบอกเหตุ (เดิม "ควรเป็น 0")
        Assert.Empty(y2025.Mismatches);
    }

    // ═══════════════ P2-7 ผังที่ผูกใต้ผังลงรายการของแม่แบบ (รอบแรก 825f5c0e) ═══════════════

    [Fact]
    public async Task ผังใต้ผังลงรายการของแม่แบบ_รายงานตรวจข้อมูลพบ_ย้ายไปหัวบัญชี_ผังแม่แบบกลับเป็นผังลงรายการ_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedTemplateAsync(db, "0105556000307");
        // สภาพที่การผูกรอบแรกทิ้งไว้: ผัง PEAK 113101 ใต้ 11310 (+ ลูกของมันอีกชั้น)
        // รอบ 4 P2-c: ผังจากโปรแกรมเดิมมีเครื่องหมายผู้สร้าง "Migrate:" (AccountImportAdapter) = หลักฐานว่าแม่มาจากการผูกของระบบ
        var peak = new ChartOfAccount { CompanyId = cid, AccountCode = "113101", AccountName = "ลูกหนี้ PEAK", AccountType = AccountType.Asset, Level = 5, ParentAccountId = acc["11310"].Id,
            CreatedBy = ChartParentLink.MigrateActorPrefix + "PEAK:test" };
        db.ChartOfAccounts.Add(peak);
        await db.SaveChangesAsync();
        var grand = new ChartOfAccount { CompanyId = cid, AccountCode = "1131011", AccountName = "ลูกหนี้ PEAK ย่อย", AccountType = AccountType.Asset, Level = 6, ParentAccountId = peak.Id };
        db.ChartOfAccounts.Add(grand);
        await db.SaveChangesAsync();
        using (var r0 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("11320", (await ByPrefixAsync(r0, cid, "113"))!.AccountCode);   // อาการ: ลูกหนี้ตั้งต้นพลิก

        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            var report = await new DataIntegrityReportService(rep).GetAsync(cid);
            var row = Assert.Single(report.TemplateLeafImportedChildren);
            Assert.Equal(peak.Id, row.Id);
            Assert.Equal(DataIntegrityReportService.TemplateLeafRescueFixPath(peak.Id), row.FixPath);
            Assert.Contains("11310", row.Detail);
        }
        using (var fix = DbTestDatabase.TryCreateContext()!)
        {
            var result = await new AccountingService(fix).RescueTemplateLeafChildrenAsync(cid, new[] { peak.Id }, Guid.NewGuid().ToString());
            Assert.Equal(1, result.Linked);
        }
        using var req = DbTestDatabase.TryCreateContext()!;
        var byCode = await req.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.Equal(acc["113"].Id, byCode["113101"].ParentAccountId);
        Assert.Equal(4, byCode["113101"].Level);
        Assert.Equal(5, byCode["1131011"].Level);
        Assert.False((await req.LoadPostingFactsAsync(cid, new[] { acc["11310"].Id }))[acc["11310"].Id].IsGroup);
        Assert.Equal("11310", (await ByPrefixAsync(req, cid, "113"))!.AccountCode);
        Assert.True(await req.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityId == peak.Id.ToString() && a.NewValues!.Contains(ChartParentLink.LeafRescueAction)));
        Assert.True(await req.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityId == grand.Id.ToString() && a.NewValues!.Contains("coa-relevel")));
        // ทิศตรงข้าม: ซ่อมแล้วรายงานว่าง · กดซ้ำ = ข้ามพร้อมเหตุผล
        Assert.Empty((await new DataIntegrityReportService(req).GetAsync(cid)).TemplateLeafImportedChildren);
        var again = await new AccountingService(req).RescueTemplateLeafChildrenAsync(cid, new[] { peak.Id }, Guid.NewGuid().ToString());
        Assert.Equal(0, again.Linked);
        Assert.NotEmpty(again.Skipped);
    }
}
