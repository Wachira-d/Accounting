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
/// ฝ่ายค้านรอบส่งออก 2026-10-15 (erp-review/2026-10-14/EXPORT_AUDIT.md §"ฝ่ายค้านรอบส่งออก") บนฐาน PostgreSQL จริง:
/// <list type="bullet">
/// <item>P1-1 ปิดปีสองครั้งติด ⇒ ยอดยกมาปีที่สาม = รายการทั้งหมดก่อนปีนั้น + ยอดนำเข้าก่อนใช้ระบบ <b>ครั้งเดียว</b> (เดิมนับปีแรกซ้ำ) ·
///   ทิศตรงข้าม: ปิดปีครั้งเดียวได้ยอดเดิม · รายงานตรวจ → ซ่อมเฉพาะปีที่เลือก + audit · ซ่อมซ้ำ = ไม่ทำอะไร · งวดที่มียอดนำเข้าไม่ถูกซ่อม</item>
/// <item>P1-2 ผังนำเข้าผูกบัญชีแม่ · ผังตั้งต้นอัตโนมัติไม่หยิบหัวบัญชีที่นำเข้า · ผังที่ผู้ใช้สร้างเองทั้งชุดยังทำงาน · ซ่อมผังที่นำเข้าไว้ก่อน</item>
/// <item>P2-6 บัญชีแยกประเภทกรองสาขา: ยอดยกไปเท่างบทดลองกรองสาขาเดียวกันทุกบัญชี (รวมรายการที่ติดมิติของสาขา/ปันส่วนมิติ)</item>
/// <item>P2-8 บันทึกการใช้งาน: "ถึงวันที่" แบบวันล้วน = สิ้นวันตามเวลาไทย</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class AdversarialExportRoundDbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialExportRoundDbTests(ITestOutputHelper output) => _out = output;

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // ═══════════════ P1-1 ปิดปีสองครั้ง ═══════════════

    private sealed record RollSeed(Guid CompanyId, Guid Cash, Guid Capital, Guid Re, Guid Revenue, Guid Expense);

    private static async Task<RollSeed> SeedRollAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบยกยอดปิดปี", TaxId = "0105556000004" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        ChartOfAccount A(string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = t, Level = 4 };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = A("11111", "เงินสด", AccountType.Asset);
        var capital = A("31000", "ทุน", AccountType.Equity);
        var re = A("32000", "กำไรสะสม", AccountType.Equity);
        var rev = A("41000", "รายได้", AccountType.Revenue);
        var exp = A("51000", "ค่าใช้จ่าย", AccountType.Expense);
        // ยอดยกมาที่นำเข้าจากระบบเดิม ณ 1 ม.ค. 2023 (ก่อนใช้ระบบ)
        var p2023 = new FiscalPeriod { CompanyId = company.Id, Name = "2023/01", Year = 2023, Month = 1, StartDate = D(2023, 1, 1), EndDate = D(2023, 1, 31) };
        db.FiscalPeriods.Add(p2023);
        await db.SaveChangesAsync();
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p2023.Id, AccountId = cash.Id, OpeningDebit = 100_000m, Notes = OpeningRoll.ImportedNotePrefix + " test" },
            new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p2023.Id, AccountId = capital.Id, OpeningCredit = 100_000m, Notes = OpeningRoll.ImportedNotePrefix + " test" });
        var n = 0;
        void Je(DateTime date, Guid dr, Guid cr, decimal amt)
        {
            n++;
            var je = new JournalEntry { CompanyId = company.Id, EntryNumber = $"JV-ROLL-{n:D3}", EntryDate = date, Status = JournalEntryStatus.Posted,
                Description = "ทดสอบ", TotalDebit = amt, TotalCredit = amt };
            je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        Je(D(2023, 3, 1), cash.Id, rev.Id, 10_000m);
        Je(D(2023, 6, 1), exp.Id, cash.Id, 2_000m);
        Je(D(2024, 3, 1), cash.Id, rev.Id, 20_000m);
        await db.SaveChangesAsync();
        return new RollSeed(company.Id, cash.Id, capital.Id, re.Id, rev.Id, exp.Id);
    }

    private static async Task<Dictionary<Guid, decimal>> JanNetAsync(AccountingDbContext db, Guid companyId, int year)
        => await db.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.FiscalPeriod.Year == year && o.FiscalPeriod.Month == 1)
            .GroupBy(o => o.AccountId)
            .Select(g => new { g.Key, Net = g.Sum(x => x.OpeningDebit) - g.Sum(x => x.OpeningCredit) })
            .ToDictionaryAsync(x => x.Key, x => x.Net);

    [Fact]
    public async Task ปิดปีสองครั้งติด_ยอดยกมาปีที่สาม_เท่ารายการทั้งหมด_บวกยอดนำเข้าครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedRollAsync(db);

        using (var r1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(r1).YearEndCloseAsync(s.CompanyId, 2023, s.Re, null, Guid.NewGuid().ToString());
        using (var r2 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(r2).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        using var req = DbTestDatabase.TryCreateContext()!;
        var jan2024 = await JanNetAsync(req, s.CompanyId, 2024);
        var jan2025 = await JanNetAsync(req, s.CompanyId, 2025);
        // ทิศตรงข้าม (ปิดปีครั้งแรก — ไม่เคยผิด): 100,000 + 10,000 − 2,000
        Assert.Equal(108_000m, jan2024[s.Cash]);
        Assert.Equal(-8_000m, jan2024[s.Re]);
        // ปีที่สาม: รายการก่อน 2025 ทั้งหมด (28,000) + ยอดนำเข้า 100,000 ครั้งเดียว — เดิม 136,000 (นับ 2023 ซ้ำ)
        Assert.Equal(128_000m, jan2025[s.Cash]);
        Assert.Equal(-100_000m, jan2025[s.Capital]);
        Assert.Equal(-28_000m, jan2025[s.Re]);
        Assert.Equal(0m, jan2025.Values.Sum());   // สมดุล
        Assert.False(jan2025.ContainsKey(s.Revenue));  // ยอดยกมาไม่มีบัญชีรายได้ (ปิดเข้ากำไรสะสมแล้ว)

        // ทุกรายงานอ่านฐานเดียวกัน ⇒ งบทดลอง/ยอด ณ วันที่ เห็น 128,000
        var svc = new AccountingService(req);
        var tb = await svc.GetTrialBalanceAsync(s.CompanyId, D(2025, 1, 31));
        var cashTb = tb.Items.Single(i => i.AccountCode == "11111");
        Assert.Equal(128_000m, cashTb.DebitBalance - cashTb.CreditBalance);
        Assert.Equal(128_000m, (await svc.GetAccountBalanceAsOfAsync(s.CompanyId, D(2025, 1, 31)))[s.Cash]);

        // รายงานตรวจไม่พบอะไร (ข้อมูลใหม่ถูกตั้งแต่แรก)
        Assert.All(await svc.FindOpeningRollMismatchesAsync(s.CompanyId), y => Assert.Empty(y.Mismatches));
    }

    [Fact]
    public async Task ยอดยกมาที่เก็บผิดจากบั๊กเดิม_รายงานเห็น_ซ่อมเฉพาะปีที่เลือก_มีaudit_ซ่อมซ้ำไม่ทำอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedRollAsync(db);
        using (var r1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(r1).YearEndCloseAsync(s.CompanyId, 2023, s.Re, null, Guid.NewGuid().ToString());
        using (var r2 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(r2).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        // จำลองข้อมูลที่บั๊กเดิมเขียนไว้: เงินสด ม.ค. 2025 = 136,000 · กำไรสะสม = 36,000 (นับ 2023 ซ้ำ)
        var cashRow = db.OpeningBalances.Single(o => o.CompanyId == s.CompanyId && o.AccountId == s.Cash && o.FiscalPeriod.Year == 2025);
        cashRow.OpeningDebit += 8_000m;
        var reRow = db.OpeningBalances.Single(o => o.CompanyId == s.CompanyId && o.AccountId == s.Re && o.FiscalPeriod.Year == 2025);
        reRow.OpeningCredit += 8_000m;
        cashRow.Notes = null; reRow.Notes = null;   // แถวรุ่นเก่าไม่มีหมายเหตุ
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        var report = await svc.FindOpeningRollMismatchesAsync(s.CompanyId);
        var y2025 = report.Single(y => y.Year == 2025);
        Assert.Equal(2, y2025.Mismatches.Count);
        var cash = y2025.Mismatches.Single(m => m.AccountId == s.Cash);
        Assert.Equal(136_000m, cash.StoredNet);
        Assert.Equal(128_000m, cash.ExpectedNet);
        Assert.Empty(report.Single(y => y.Year == 2024).Mismatches);   // ปีแรกถูกอยู่แล้ว ไม่ถูกแตะ

        var actor = Guid.NewGuid().ToString();
        var result = await svc.RepairOpeningRollAsync(s.CompanyId, new[] { 2025 }, actor);
        Assert.Equal(1, result.RepairedYears);
        Assert.Equal(2, result.RepairedRows);

        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(128_000m, (await JanNetAsync(after, s.CompanyId, 2025))[s.Cash]);
        Assert.Equal(2, await after.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(OpeningBalance)
            && a.NewValues!.Contains("opening-roll-repair")));
        var svc2 = new AccountingService(after);
        Assert.All(await svc2.FindOpeningRollMismatchesAsync(s.CompanyId), y => Assert.Empty(y.Mismatches));
        var again = await svc2.RepairOpeningRollAsync(s.CompanyId, new[] { 2025 }, actor);
        Assert.Equal(0, again.RepairedRows);
        Assert.Contains(again.Skipped, x => x.Contains("ถูกอยู่แล้ว"));
    }

    [Fact]
    public async Task งวดที่มียอดนำเข้า_ไม่ถูกซ่อมอัตโนมัติ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedRollAsync(db);
        using (var r1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(r1).YearEndCloseAsync(s.CompanyId, 2023, s.Re, null, Guid.NewGuid().ToString());
        // ผู้ใช้ลงยอดนำเข้าเองใน ม.ค. 2024 (แทนยอดที่ปิดปีเขียน) — ยอดที่ผู้ใช้ตั้งใจ
        var row = db.OpeningBalances.Single(o => o.CompanyId == s.CompanyId && o.AccountId == s.Cash && o.FiscalPeriod.Year == 2024);
        row.OpeningDebit = 999_999m;
        row.OpeningCredit = 0m; // ยอดที่ผู้ใช้ตั้ง = เดบิตล้วน (ยอดที่ปิดปีเขียนมีทั้งสองข้าง)
        row.Notes = OpeningRoll.ImportedNotePrefix + " manual";
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var report = await new AccountingService(req).FindOpeningRollMismatchesAsync(s.CompanyId);
        var y = report.Single(x => x.Year == 2024);
        Assert.True(y.HasImportedRows);
        Assert.NotNull(y.SkipReason);
        var result = await new AccountingService(req).RepairOpeningRollAsync(s.CompanyId, new[] { 2024 }, "x");
        Assert.Equal(0, result.RepairedRows);
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(999_999m, (await JanNetAsync(after, s.CompanyId, 2024))[s.Cash]);
    }

    // ═══════════════ P1-2 ผังนำเข้า ═══════════════

    private static async Task<(Guid CompanyId, Dictionary<string, ChartOfAccount> Acc)> SeedTemplateChartAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัทผังผสม", TaxId = "0105556000005" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, ChartOfAccount>();
        void T(string code, string name, AccountType type, int level, string? parent)
        {
            var a = new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = level,
                IsSystemAccount = true, ParentAccountId = parent == null ? null : acc[parent].Id,
            };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        T("1", "สินทรัพย์", AccountType.Asset, 1, null);
        T("11", "สินทรัพย์หมุนเวียน", AccountType.Asset, 2, "1");
        T("113", "ลูกหนี้", AccountType.Asset, 3, "11");
        T("11310", "ลูกหนี้การค้า", AccountType.Asset, 4, "113");
        T("4", "รายได้", AccountType.Revenue, 1, null);
        T("41", "รายได้จากการขาย", AccountType.Revenue, 2, "4");
        T("411", "รายได้ขายสินค้า", AccountType.Revenue, 3, "41");
        T("41100", "ขายสินค้า", AccountType.Revenue, 4, "411");
        await db.SaveChangesAsync();
        return (company.Id, acc);
    }

    private static CompetitorTable Table(params (string Code, string Name, string Type)[] rows)
        => new(new[] { "รหัสบัญชี", "ชื่อบัญชี", "ประเภทบัญชี" },
            rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["รหัสบัญชี"] = r.Code, ["ชื่อบัญชี"] = r.Name, ["ประเภทบัญชี"] = r.Type,
            }).ToList(), "utf-8", ImportFileKind.Csv);

    /// <summary>ผังตั้งต้นของการลงบัญชีอัตโนมัติ — คิวรีเดียวกับ <c>DocumentService.FindAccountAsync</c> (prefix) ผ่าน
    /// <see cref="PostingAccountGuard.DefaultFallbackCandidates"/> (รอบ 2 P1-3 · จุดเรียกถูกล็อกด้วย tools/required_call_site_check.py)</summary>
    private static Task<ChartOfAccount?> DefaultByPrefixAsync(AccountingDbContext db, Guid companyId, string prefix)
        => db.ChartOfAccounts.DefaultFallbackCandidates(db)
            .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(prefix) && a.IsActive)
            .FirstOrDefaultAsync();

    [Fact]
    public async Task ผังผสม_หัวบัญชีที่นำเข้าถูกผูกแม่_ลงลูกหนี้อัตโนมัติไปผังแม่แบบ11310()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedTemplateChartAsync(db);

        using (var imp = DbTestDatabase.TryCreateContext()!)
        {
            // ลูกมาก่อนหัวในไฟล์ (ลำดับไม่มีผล — ผูกทั้งชุด) · หัวรายได้ "4100" ไม่มีลูกในไฟล์
            var res = await new ExpressAccountsAdapter(imp).ImportAsync(cid,
                Table(("113001", "ลูกหนี้ ก (ระบบเดิม)", "Asset"), ("1130", "ลูกหนี้ (ระบบเดิม)", "Asset"),
                      ("113002", "ลูกหนี้ ข (ระบบเดิม)", "Asset"), ("4100", "รายได้ (ระบบเดิม)", "Revenue")),
                new ImportOptions(DryRun: false, SkipDuplicates: true, SourceTag: "test"), default);
            Assert.Equal(4, res.RowsImported);
        }

        using var req = DbTestDatabase.TryCreateContext()!;
        var byCode = await req.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.Equal(acc["113"].Id, byCode["1130"].ParentAccountId);
        Assert.Equal(byCode["1130"].Id, byCode["113001"].ParentAccountId);
        Assert.Equal(byCode["1130"].Id, byCode["113002"].ParentAccountId);
        Assert.Equal(acc["41"].Id, byCode["4100"].ParentAccountId);

        // ลงลูกหนี้/รายได้อัตโนมัติ (prefix 113 / 41) ⇒ ผังแม่แบบ — เดิมได้ "1130" / "4100" (รหัสเรียงก่อน + ดูไม่มีบัญชีย่อย)
        Assert.Equal("11310", (await DefaultByPrefixAsync(req, cid, "113"))!.AccountCode);
        Assert.Equal("41100", (await DefaultByPrefixAsync(req, cid, "41"))!.AccountCode);
        // หัวบัญชีที่นำเข้ามีลูกแล้ว = บัญชีคุม (ด่านลงรายการปฏิเสธ)
        var facts = await req.LoadPostingFactsAsync(cid, new[] { byCode["1130"].Id });
        Assert.True(facts[byCode["1130"].Id].IsGroup);
    }

    [Fact]
    public async Task ผังที่ผู้ใช้สร้างเองทั้งชุด_ผังเงินสดระดับ2_ยังเป็นผังตั้งต้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทผังของตัวเอง", TaxId = "0105556000006" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var root = new ChartOfAccount { CompanyId = company.Id, AccountCode = "1", AccountName = "สินทรัพย์", AccountType = AccountType.Asset, Level = 1 };
        db.ChartOfAccounts.Add(root);
        await db.SaveChangesAsync();
        db.ChartOfAccounts.AddRange(
            new ChartOfAccount { CompanyId = company.Id, AccountCode = "1101", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 2, ParentAccountId = root.Id },
            new ChartOfAccount { CompanyId = company.Id, AccountCode = "1102", AccountName = "เงินฝาก", AccountType = AccountType.Asset, Level = 2, ParentAccountId = root.Id });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        Assert.Equal("1101", (await DefaultByPrefixAsync(req, company.Id, "11"))!.AccountCode);
        Assert.Empty(await ChartParentLinker.FindUnlinkedAsync(req, company.Id));   // root ไม่มีรหัสต้น — ไม่ใช่ข้อเสนอ
    }

    [Fact]
    public async Task ผังที่นำเข้าก่อนรอบนี้_รายงานเห็น_ผูกเมื่อกด_และไม่ผูกใต้ผังที่มีรายการแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedTemplateChartAsync(db);
        // นำเข้ารุ่นเก่า: ไม่มีบัญชีแม่ · "4100" ถูกใช้ลงรายการไปแล้ว (หยิบเป็นผังตั้งต้น)
        ChartOfAccount Legacy(string code, AccountType t) { var a = new ChartOfAccount { CompanyId = cid, AccountCode = code, AccountName = "เดิม " + code, AccountType = t, Level = code.Length <= 4 ? 1 : 2 }; db.ChartOfAccounts.Add(a); return a; }
        var h1130 = Legacy("1130", AccountType.Asset);
        var c113001 = Legacy("113001", AccountType.Asset);
        var h4100 = Legacy("4100", AccountType.Revenue);
        var c410001 = Legacy("410001", AccountType.Revenue);
        var je = new JournalEntry { CompanyId = cid, EntryNumber = "JV-OLD-1", EntryDate = D(2026, 5, 1), Status = JournalEntryStatus.Posted, TotalDebit = 500m, TotalCredit = 500m };
        je.Lines.Add(new JournalEntryLine { AccountId = acc["11310"].Id, DebitAmount = 500m, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = h4100.Id, CreditAmount = 500m, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var report = await ChartParentLinker.FindUnlinkedAsync(req, cid);
        Assert.Equal("113", report.Single(r => r.Code == "1130").ProposedParentCode);
        Assert.Null(report.Single(r => r.Code == "1130").BlockedReason);
        Assert.NotNull(report.Single(r => r.Code == "410001").BlockedReason);   // ผูกแล้ว 4100 จะเป็นบัญชีคุมที่มียอดค้าง

        var result = await ChartParentLinker.RepairAsync(req, cid, new[] { h1130.Id, c113001.Id, h4100.Id, c410001.Id }, "tester");
        Assert.Equal(3, result.Linked);   // 1130→113 · 113001→1130 · 4100→41 · (410001 ถูกกัน)
        Assert.Contains(result.Skipped, x => x.StartsWith("410001"));

        using var after = DbTestDatabase.TryCreateContext()!;
        var byCode = await after.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.Equal(acc["113"].Id, byCode["1130"].ParentAccountId);
        Assert.Equal(byCode["1130"].Id, byCode["113001"].ParentAccountId);
        Assert.Null(byCode["410001"].ParentAccountId);
        Assert.Equal(3, await after.AuditLogs.CountAsync(a => a.CompanyId == cid && a.NewValues!.Contains("coa-parent-link")));
        // ซ่อมซ้ำ = ไม่ทำอะไรกับผังที่ผูกแล้ว
        var again = await ChartParentLinker.RepairAsync(after, cid, new[] { h1130.Id }, "tester");
        Assert.Equal(0, again.Linked);
    }

    // ═══════════════ P2-6 บัญชีแยกประเภทกรองสาขา = งบทดลอง ═══════════════

    [Fact]
    public async Task บัญชีแยกประเภทกรองสาขา_ยอดยกไปเท่างบทดลองกรองสาขาเดียวกันทุกบัญชี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทสาขาผูกมิติ", TaxId = "0105556000007" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var dim = new AccountingDimension { CompanyId = company.Id, Code = "BR1", Name = "มิติสาขาหนึ่ง", DimensionType = DimensionType.Branch };
        db.AccountingDimensions.Add(dim);
        await db.SaveChangesAsync();
        var b1 = new Branch { CompanyId = company.Id, Code = "B1", Name = "สาขาหนึ่ง", DimensionId = dim.Id };
        db.Branches.Add(b1);
        var cash = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11111", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 4 };
        var rev = new ChartOfAccount { CompanyId = company.Id, AccountCode = "41000", AccountName = "รายได้", AccountType = AccountType.Revenue, Level = 4 };
        var cap = new ChartOfAccount { CompanyId = company.Id, AccountCode = "31000", AccountName = "ทุน", AccountType = AccountType.Equity, Level = 4 };
        db.ChartOfAccounts.AddRange(cash, rev, cap);
        // ยอดนำเข้าทั้งบริษัท (ไม่มีสาขา) — กรองสาขาแล้วต้องไม่ถูกบวกทั้งสองรายงาน
        var p = new FiscalPeriod { CompanyId = company.Id, Name = "2026/01", Year = 2026, Month = 1, StartDate = D(2026, 1, 1), EndDate = D(2026, 1, 31) };
        db.FiscalPeriods.Add(p);
        await db.SaveChangesAsync();
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p.Id, AccountId = cash.Id, OpeningDebit = 50_000m },
            new OpeningBalance { CompanyId = company.Id, FiscalPeriodId = p.Id, AccountId = cap.Id, OpeningCredit = 50_000m });
        var n = 0;
        JournalEntry Je(DateTime date, Guid? branchId, Guid? headerDim, decimal amt)
        {
            n++;
            var je = new JournalEntry { CompanyId = company.Id, EntryNumber = $"JV-BR-{n:D3}", EntryDate = date, Status = JournalEntryStatus.Posted,
                BranchId = branchId, DimensionId = headerDim, TotalDebit = amt, TotalCredit = amt };
            je.Lines.Add(new JournalEntryLine { AccountId = cash.Id, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = rev.Id, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
            return je;
        }
        Je(D(2026, 2, 1), b1.Id, null, 1_000m);          // ติดสาขาอย่างเดียว (เดิม GL ตัดทิ้ง — กรอง "สาขา และ มิติ")
        Je(D(2026, 2, 5), null, dim.Id, 2_000m);          // ติดมิติของสาขาอย่างเดียว (เดิมงบทดลองไม่เห็น)
        Je(D(2026, 3, 3), b1.Id, null, 400m);
        var alloc = Je(D(2026, 3, 9), null, null, 700m);  // ปันส่วนมิติรายบรรทัด (JournalLineDimension)
        Je(D(2026, 3, 10), null, null, 9_000m);           // สำนักงาน — ไม่อยู่ในสาขา
        await db.SaveChangesAsync();
        foreach (var l in alloc.Lines)
            db.JournalLineDimensions.Add(new JournalLineDimension { CompanyId = company.Id, JournalEntryLineId = l.Id, DimensionId = dim.Id });
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AccountingService(req);
        var gl = await svc.GetGeneralLedgerAsync(company.Id, D(2026, 3, 1), D(2026, 3, 31), branchId: b1.Id);
        var tb = await svc.GetTrialBalanceAsync(company.Id, D(2026, 3, 31), branchId: b1.Id);
        foreach (var a in gl.Accounts)
        {
            var t = tb.Items.Single(i => i.AccountCode == a.AccountCode);
            var tbNet = a.AccountType is AccountType.Asset or AccountType.Expense ? t.DebitBalance - t.CreditBalance : t.CreditBalance - t.DebitBalance;
            Assert.Equal(tbNet, a.ClosingBalance);
        }
        Assert.Equal(1_000m + 2_000m + 400m + 700m, gl.Accounts.Single(a => a.AccountCode == "11111").ClosingBalance);
        Assert.Equal(1_000m + 2_000m, gl.Accounts.Single(a => a.AccountCode == "11111").OpeningBalance);
        Assert.DoesNotContain(tb.Items, i => i.AccountCode == "31000");   // ยอดนำเข้าทั้งบริษัทไม่ถูกยัดเข้าสาขา

        // ทิศตรงข้าม: ไม่กรอง ⇒ ทั้งบริษัท + ยอดนำเข้า (เหมือนเดิม)
        var all = await svc.GetTrialBalanceAsync(company.Id, D(2026, 3, 31));
        var c = all.Items.Single(i => i.AccountCode == "11111");
        Assert.Equal(50_000m + 1_000m + 2_000m + 400m + 700m + 9_000m, c.DebitBalance - c.CreditBalance);
    }

    // ═══════════════ P2-8 บันทึกการใช้งาน ขอบวันเวลาไทย ═══════════════

    [Fact]
    public async Task บันทึกการใช้งาน_ถึงวันที่แบบวันล้วน_ตัดที่เที่ยงคืนเวลาไทย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบเวลา audit", TaxId = "0105556000008" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        AuditLog Row(DateTime ts, string tag) => new()
        {
            CompanyId = company.Id, UserEmail = "p28@example.com", Action = AuditAction.Update,
            EntityType = "P28", EntityId = tag, NewValues = "{}", Timestamp = ts,
        };
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 13, 17, 30, 0, DateTimeKind.Utc), "bkk-14-0030"));   // 14 ต.ค. 00:30 ไทย — อยู่ในวัน
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 14, 16, 30, 0, DateTimeKind.Utc), "bkk-14-2330"));   // 14 ต.ค. 23:30 ไทย — อยู่ในวัน
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc), "bkk-15-0100"));    // 15 ต.ค. 01:00 ไทย — นอกช่วง (เดิมหลุดเข้ามา)
        db.AddChainedAuditLog(Row(new DateTime(2026, 10, 13, 16, 0, 0, DateTimeKind.Utc), "bkk-13-2300"));    // 13 ต.ค. 23:00 ไทย — ก่อนช่วง
        await db.SaveChangesAsync();

        using var req = DbTestDatabase.TryCreateContext()!;
        var svc = new AuditTrailService(req);
        var day = await svc.GetLogsAsync(company.Id, new Accounting.Models.DTOs.Audit.AuditLogQueryRequest(
            new DateTime(2026, 10, 14), new DateTime(2026, 10, 14), PageSize: 50));
        Assert.Equal(new[] { "bkk-14-0030", "bkk-14-2330" }, day.Items.Select(i => i.EntityId).OrderBy(x => x).ToArray());
        // ทิศตรงข้าม: ระบุเวลา (UTC) ⇒ ตรงตัว
        var exact = await svc.GetLogsAsync(company.Id, new Accounting.Models.DTOs.Audit.AuditLogQueryRequest(
            new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 19, 0, 0, DateTimeKind.Utc), PageSize: 50));
        Assert.Equal("bkk-15-0100", Assert.Single(exact.Items).EntityId);
    }
}
