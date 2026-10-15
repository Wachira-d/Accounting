using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 4</b> (ฝ่ายค้านบน 774ea7bc · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 4") บนฐาน PostgreSQL จริง:
/// <list type="bullet">
/// <item>P2-a รอบ เม.ย.–มี.ค. ที่ยังมียอดยกรุ่นเก่าไป ม.ค.: ปิดปี/ยกยอดด้วยมือล้มดังพร้อมทางไปต่อ (ไม่เขียนอะไร) · ซ่อมแล้วปิดปีได้ยอดถูก</item>
/// <item>P2-d งวดแรกของรอบมียอดนำเข้า: แถว ม.ค. รุ่นเก่ายังถูกรายงาน + ยกเลิกได้ · แถวนำเข้าไม่ถูกแตะ</item>
/// <item>P2-b ผังค่าใช้จ่ายที่ถูกลบแต่มีรายการ ⇒ ใบปิดปีล้าง (ปิดปีถัดไปไม่ล้ม) · ผังสินทรัพย์ที่ถูกลบแต่มียอด ⇒ ล้มก่อนเขียน → ปุ่มกู้คืน → ปิดปีได้</item>
/// <item>P2-c ผังที่ผู้ใช้สร้างใต้ผังลงรายการของแม่แบบเองไม่ถูกรายงาน/ย้าย · ผังที่ระบบผูก (เครื่องหมายนำเข้า / audit การผูก) ถูกรายงาน</item>
/// </list>
/// ทุกเทสต์ปิดปีที่สิ้นรอบแล้วเท่านั้น (ไม่ขึ้นกับนาฬิกา — ด่าน "วันที่ลงบัญชีต้องไม่เป็นวันที่ในอนาคต")
/// </summary>
[Trait("Category", "Db")]
public class AdversarialExportRound4DbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialExportRound4DbTests(ITestOutputHelper output) => _out = output;

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
    private static JournalEntry Je(AccountingDbContext db, Guid companyId, DateTime date, Guid dr, Guid cr, decimal amt)
    {
        var n = Interlocked.Increment(ref _n);
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = $"JV-R4-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = "ทดสอบรอบ 4", TotalDebit = amt, TotalCredit = amt,
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

    /// <summary>รอบ เม.ย.–มี.ค. ปิดปี 2023 ด้วยโค้ดปัจจุบัน แล้วจำลองสภาพของโค้ดก่อน de823512: ไม่มีแถว เม.ย. 2024 · มีแถวยกยอดไป ม.ค. 2024 (คิดแบบปีปฏิทิน — เงินสดผิด)</summary>
    private static async Task<(Seed S, Guid JanPeriodId)> SeedLegacyAprilCompanyAsync(AccountingDbContext db, string name, string taxId)
    {
        var s = await SeedCompanyAsync(db, name, taxId, fiscalStartMonth: 4);
        Je(db, s.CompanyId, D(2023, 4, 10), s.Cash, s.Capital, 50_000m);
        Je(db, s.CompanyId, D(2023, 5, 1), s.Cash, s.Revenue, 12_000m);
        Je(db, s.CompanyId, D(2024, 2, 1), s.Expense, s.Cash, 2_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2023, s.Re, null, Guid.NewGuid().ToString());
        db.OpeningBalances.RemoveRange(db.OpeningBalances.Where(o => o.CompanyId == s.CompanyId));
        await db.SaveChangesAsync();
        var jan = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 1);
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jan.Id, AccountId = s.Cash, OpeningDebit = 70_000m, Notes = OpeningRoll.RolledNote(2024) },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jan.Id, AccountId = s.Capital, OpeningCredit = 50_000m, Notes = null });
        Je(db, s.CompanyId, D(2024, 6, 1), s.Cash, s.Revenue, 5_000m);   // รายการของรอบ 2024 (เม.ย. 2024 – มี.ค. 2025)
        await db.SaveChangesAsync();
        return (s, jan.Id);
    }

    // ═══════════════ P2-a ปิดปี/ยกยอดด้วยมือ ห้ามคิดบนงวด ม.ค. รุ่นเก่าที่ยังไม่ซ่อม ═══════════════

    [Fact]
    public async Task รอบเมษามียอดยกรุ่นเก่าไปมกรา_ปิดปีถัดไปล้มพร้อมทางไปต่อ_ซ่อมแล้วปิดได้ยอดถูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (s, _) = await SeedLegacyAprilCompanyAsync(db, "บริษัทรอบเมษายังไม่ซ่อม R4", "0105556000401");

        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            // เดิม: ปิดได้ — ฐาน = ม.ค. 2024 รุ่นเก่า (70,000) ⇒ ยอดยกมา เม.ย. 2025 คลาด
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString()));
            Assert.Contains("ม.ค. 2567", ex.Message);
            Assert.Contains("ซ่อมปี 2567", ex.Message);
            Assert.Contains("ตรวจข้อมูลจากบั๊กที่แก้แล้ว", ex.Message);
        }
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            // ยกยอดด้วยมือ (ปี 2024 = จากการปิดปี 2023) เดินด่านเดียวกัน
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(c3).RollOpeningBalancesAsync(s.CompanyId, 2024, "u"));
            Assert.Contains("ซ่อมปี 2567", ex.Message);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await chk.YearEndClosings.AnyAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2024));   // ไม่เขียนอะไร
            Assert.False(await chk.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == "YE-2024"));
            Assert.Empty(await OpeningRowsAsync(chk, s.CompanyId, 2024, 4));
        }

        // ทางไปต่อ: ซ่อมปี 2024 (ปุ่ม "แก้" ของหน้าตรวจข้อมูล) ⇒ เขียน เม.ย. 2024 + ยกเลิก ม.ค. 2024 ⇒ ปิดปี 2024 ได้
        using (var fix = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, (await new AccountingService(fix).RepairOpeningRollAsync(s.CompanyId, new[] { 2024 }, Guid.NewGuid().ToString())).RepairedYears);
        YearEndClosing closing;
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c4).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());
        Assert.Equal(5_000m, closing.TransferredAmount);

        using var req = DbTestDatabase.TryCreateContext()!;
        var apr25 = await OpeningRowsAsync(req, s.CompanyId, 2025, 4);
        Assert.Equal(65_000m, Net(apr25[s.Cash]));        // 60,000 + 5,000 (ฐาน ม.ค. รุ่นเก่า ⇒ 75,000)
        Assert.Equal(-15_000m, Net(apr25[s.Re]));
        Assert.Equal(-50_000m, Net(apr25[s.Capital]));
    }

    // ═══════════════ P2-d งวดแรกของรอบมียอดนำเข้า + แถว ม.ค. รุ่นเก่า ═══════════════

    [Fact]
    public async Task งวดแรกของรอบมียอดนำเข้า_แถวมกรารุ่นเก่ายังถูกรายงาน_ซ่อมยกเลิกเฉพาะแถวรุ่นเก่า_แถวนำเข้าไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (s, janId) = await SeedLegacyAprilCompanyAsync(db, "บริษัทรอบเมษามียอดนำเข้า R4", "0105556000402");
        // ผู้ใช้ลงยอดยกมา เม.ย. 2024 เองจากระบบเดิม (แถวนำเข้า)
        var apr = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 4);
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = apr.Id, AccountId = s.Cash, OpeningDebit = 60_000m, Notes = OpeningRoll.ImportedNotePrefix + " r4" },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = apr.Id, AccountId = s.Capital, OpeningCredit = 50_000m, Notes = OpeningRoll.ImportedNotePrefix + " r4" },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = apr.Id, AccountId = s.Re, OpeningCredit = 10_000m, Notes = OpeningRoll.ImportedNotePrefix + " r4" });
        await db.SaveChangesAsync();

        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            var y = Assert.Single(await new AccountingService(rep).FindOpeningRollMismatchesAsync(s.CompanyId));
            Assert.True(y.HasImportedRows);
            Assert.Equal(2, y.LegacyRolledRows);                         // เดิม 0 ⇒ หน้าตรวจข้อมูลข้ามทั้งปี
            Assert.Contains("แถวนำเข้าไม่ถูกแตะ", y.Note);
            var report = await new DataIntegrityReportService(rep, new AccountingService(rep)).GetAsync(s.CompanyId);
            var row = Assert.Single(report.OpeningRollMismatches);
            Assert.Equal(DataIntegrityReportService.OpeningRollFixPath(2024), row.FixPath);
        }
        // ด่านปิดปีบล็อกจนกว่าจะซ่อม (มีทางไปต่อจริง — ปุ่มข้างบน)
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString()));
        using (var fix = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, (await new AccountingService(fix).RepairOpeningRollAsync(s.CompanyId, new[] { 2024 }, Guid.NewGuid().ToString())).RepairedYears);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var janRows = await req.OpeningBalances.IgnoreQueryFilters().AsNoTracking().Where(o => o.FiscalPeriodId == janId).ToListAsync();
            Assert.All(janRows, r => Assert.True(r.IsDeleted));
            var aprRows = await req.OpeningBalances.AsNoTracking().Where(o => o.FiscalPeriodId == apr.Id).ToListAsync();
            Assert.Equal(3, aprRows.Count);                              // แถวนำเข้าไม่ถูกแตะ
            Assert.All(aprRows, r => Assert.StartsWith(OpeningRoll.ImportedNotePrefix, r.Notes));
            Assert.Equal(60_000m, aprRows.Single(r => r.AccountId == s.Cash).OpeningDebit);
            var after = Assert.Single(await new AccountingService(req).FindOpeningRollMismatchesAsync(s.CompanyId));
            Assert.Equal(0, after.LegacyRolledRows);
        }
        // ซ่อมแล้ว ⇒ ปิดปี 2024 ได้ (ฐาน = ยอดนำเข้า เม.ย. 2024)
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c3).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());
        using var fin = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(65_000m, Net((await OpeningRowsAsync(fin, s.CompanyId, 2025, 4))[s.Cash]));
    }

    // ═══════════════ P2-b ผังที่ถูกลบ ═══════════════

    [Fact]
    public async Task ผังค่าใช้จ่ายที่ถูกลบแต่มีรายการ_ใบปิดปีล้างให้_ปิดปีถัดไปไม่ล้ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทลบผังค่าใช้จ่าย R4", "0105556000403");
        var travel = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "51500", AccountName = "ค่าเดินทาง", AccountType = AccountType.Expense, Level = 4 };
        db.ChartOfAccounts.Add(travel);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2024, 1, 10), s.Cash, s.Capital, 20_000m);
        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Revenue, 8_000m);
        Je(db, s.CompanyId, D(2024, 4, 1), travel.Id, s.Cash, 1_000m);
        await db.SaveChangesAsync();
        travel.IsDeleted = true;   // ลบผังค่าใช้จ่ายที่มีรายการในรอบ
        await db.SaveChangesAsync();

        YearEndClosing closing;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());
        Assert.Equal(7_000m, closing.TransferredAmount);                 // เดิม 8,000 (ผังที่ลบหลุดจากใบปิด)
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var travelLine = await chk.JournalEntryLines.AsNoTracking()
                .SingleAsync(l => l.JournalEntryId == closing.ClosingJournalEntryId && l.AccountId == travel.Id);
            Assert.Equal(1_000m, travelLine.CreditAmount);                // ใบปิดปีล้างผังที่ลบ
            var jan25 = await OpeningRowsAsync(chk, s.CompanyId, 2025, 1);
            Assert.False(jan25.ContainsKey(travel.Id));
            Assert.Equal(-7_000m, Net(jan25[s.Re]));
        }
        // เดิม: ปิดปีถัดไปล้มทุกครั้ง (ยอดค้างของผังที่ลบ = "ผังที่ถูกลบยังมียอด")
        Je(db, s.CompanyId, D(2025, 2, 1), s.Cash, s.Revenue, 500m);
        await db.SaveChangesAsync();
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(500m, (await new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString())).TransferredAmount);
        // ทิศตรงข้าม: ทางบันทึกมืออื่นยังลงผังที่ลบไม่ได้ (ด่านยอมผังที่ลบเฉพาะใบปิดปี)
        using var c3 = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(c3).CreateJournalEntryAsync(s.CompanyId,
            new CreateJournalEntryRequest(EntryDate: D(2025, 3, 1), Description: "ลงผังที่ลบ", Reference: null,
                Lines: new List<JournalLineRequest> { new(travel.Id, 100m, 0m, "x"), new(s.Cash, 0m, 100m, "x") }, JournalType: JournalType.General),
            Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task ผังสินทรัพย์ที่ถูกลบแต่มียอด_ปิดปีล้มก่อนเขียน_รายงานมีปุ่มกู้คืน_กู้คืนแล้วปิดได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทลบผังสินทรัพย์ R4", "0105556000404");
        var other = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "11500", AccountName = "ลูกหนี้อื่น", AccountType = AccountType.Asset, Level = 4 };
        db.ChartOfAccounts.Add(other);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2024, 3, 1), other.Id, s.Capital, 5_000m);
        Je(db, s.CompanyId, D(2024, 4, 1), s.Cash, s.Revenue, 1_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());
        other.IsDeleted = true;
        Je(db, s.CompanyId, D(2025, 2, 1), s.Cash, s.Revenue, 500m);
        await db.SaveChangesAsync();
        var jeCount = await db.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId);

        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<DeletedAccountsWithBalanceException>(() =>
                new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString()));
            Assert.Contains("11500", ex.Message);
            Assert.Contains("ยังไม่มีอะไรถูกบันทึก", ex.Message);
            Assert.Equal(other.Id, Assert.Single(ex.Accounts).AccountId);
        }
        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(jeCount, await rep.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));   // ใบปิดปีไม่ถูกสร้าง
            var report = await new DataIntegrityReportService(rep, new AccountingService(rep)).GetAsync(s.CompanyId);
            var restoreRow = Assert.Single(report.OpeningRollMismatches, r => r.Id == other.Id);
            Assert.Equal(DataIntegrityReportService.AccountRestoreFixPath(other.Id), restoreRow.FixPath);   // ทางไปต่อที่มีจริง
            Assert.Contains("11500", restoreRow.Detail);
        }

        var owner = Guid.NewGuid().ToString();
        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var restored = await new AccountingService(r1).RestoreAccountAsync(s.CompanyId, other.Id, owner);
            Assert.Equal("11500", restored.AccountCode);
        }
        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await r2.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == other.Id.ToString()
                && a.NewValues!.Contains(OpeningRoll.AccountRestoreAction)));
            // ทิศตรงข้าม: กู้คืนผังที่ไม่ได้ถูกลบ = ล้มพร้อมเหตุผล · ผังของบริษัทอื่น = ไม่พบ
            await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(r2).RestoreAccountAsync(s.CompanyId, other.Id, owner));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => new AccountingService(r2).RestoreAccountAsync(Guid.NewGuid(), other.Id, owner));
        }
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c3).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        using var fin = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(5_000m, Net((await OpeningRowsAsync(fin, s.CompanyId, 2026, 1))[other.Id]));   // ยกไปตามปกติ
    }

    // ═══════════════ P2-c ผังใต้ผังลงรายการของแม่แบบ — เฉพาะที่ระบบผูก ═══════════════

    [Fact]
    public async Task ผังที่ผู้ใช้สร้างใต้ผังแม่แบบเองไม่ถูกรายงาน_ผังที่ระบบผูกถูกรายงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทผังแม่แบบ R4", TaxId = "0105556000405" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        var acc = new Dictionary<string, ChartOfAccount>();
        void T(string code, int level, string? parent)
        {
            var a = new ChartOfAccount
            {
                CompanyId = cid, AccountCode = code, AccountName = "แม่แบบ " + code, AccountType = AccountType.Asset, Level = level,
                IsSystemAccount = true, ParentAccountId = parent == null ? null : acc[parent].Id,
            };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        T("1", 1, null); T("11", 2, "1"); T("111", 3, "11"); T("11122", 4, "111"); T("113", 3, "11"); T("11310", 4, "113");
        await db.SaveChangesAsync();

        // ผู้ใช้สร้างบัญชีย่อยใต้ 11122 เองที่หน้าผังบัญชี (ตั้งใจ — ไม่ใช่ผลของบั๊ก)
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new AccountingService(u);
            await svc.CreateAccountAsync(cid, new CreateAccountRequest("111221", "ออมทรัพย์ สาขา 1", null, AccountType.Asset, acc["11122"].Id, null));
            await svc.CreateAccountAsync(cid, new CreateAccountRequest("111222", "ออมทรัพย์ สาขา 2", null, AccountType.Asset, acc["11122"].Id, null));
        }
        // ผังที่ระบบผูก: (ก) นำเข้าจากโปรแกรมเดิม (CreatedBy "Migrate:") (ข) ผูกจากปุ่มรายงานรอบแรก (audit coa-parent-link)
        var imported = new ChartOfAccount { CompanyId = cid, AccountCode = "113101", AccountName = "ลูกหนี้ PEAK", AccountType = AccountType.Asset, Level = 5,
            ParentAccountId = acc["11310"].Id, CreatedBy = ChartParentLink.MigrateActorPrefix + "PEAK:r4" };
        var linked = new ChartOfAccount { CompanyId = cid, AccountCode = "113102", AccountName = "ลูกหนี้ไฟล์", AccountType = AccountType.Asset, Level = 5,
            ParentAccountId = acc["11310"].Id };
        db.ChartOfAccounts.AddRange(imported, linked);
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = cid, EntityType = nameof(ChartOfAccount), EntityId = linked.Id.ToString(), Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = ChartParentLink.ParentLinkAction, accountCode = "113102", parentCode = "11310" }),
            Timestamp = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        using (var rep = DbTestDatabase.TryCreateContext()!)
        {
            var rows = (await new DataIntegrityReportService(rep, new AccountingService(rep)).GetAsync(cid)).TemplateLeafImportedChildren;
            var ids = rows.Select(r => r.Id).ToHashSet();
            Assert.Contains(imported.Id, ids);
            Assert.Contains(linked.Id, ids);
            Assert.Equal(2, rows.Count);                                 // 111221/111222 ที่ผู้ใช้สร้างไม่ถูกรายงาน (รอบ 3 รายงานด้วย)
        }
        using var fix = DbTestDatabase.TryCreateContext()!;
        var user = await fix.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.CompanyId == cid && a.AccountCode == "111221");
        var result = await new AccountingService(fix).RescueTemplateLeafChildrenAsync(cid, new[] { user.Id }, Guid.NewGuid().ToString());
        Assert.Equal(0, result.Linked);                                  // ปุ่มย้ายก็ไม่ย้ายผังของผู้ใช้
        Assert.Contains(result.Skipped, x => x.Contains("ผู้ใช้สร้าง"));
        Assert.Equal(acc["11122"].Id, (await fix.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.Id == user.Id)).ParentAccountId);
    }
}
