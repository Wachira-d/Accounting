using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ยกเลิกการปิดปี (คำสั่งเจ้าของ 2026-10-16 · DECISIONS ข้อ 152 · <see cref="YearEndReopenRule"/> · <c>AccountingService.ReopenYearEndAsync</c>) บน PostgreSQL จริง —
/// ปิด → ยกเลิก ได้ตัวเลขเท่าก่อนปิด · แก้แล้วปิดซ้ำได้ (index partial) · ด่านทุกข้อล้มก่อนเขียน · ใบปิดปี/ใบกลับยังถูกล็อก · เจ้าของเท่านั้น ·
/// ยอดนำเข้าไม่ถูกแตะ · สองคนกดพร้อมกันสำเร็จคนเดียว · ล็อกแบบภาษีรายเดือนคงอยู่ · ปิดปีผ่านงวด ธ.ค. ที่ปิดชั่วคราวได้
/// <para>ทุกเทสต์ปิดปีที่สิ้นรอบแล้วเท่านั้น (ปี ≤ 2025 · ไม่ขึ้นกับนาฬิกา)</para>
/// </summary>
[Trait("Category", "Db")]
public class YearEndReopenDbTests
{
    private readonly ITestOutputHelper _out;
    public YearEndReopenDbTests(ITestOutputHelper output) => _out = output;

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
            CompanyId = companyId, EntryNumber = $"JV-REOPEN-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = "ทดสอบยกเลิกการปิดปี", TotalDebit = amt, TotalCredit = amt,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
        db.JournalEntries.Add(je);
        return je;
    }

    /// <summary>บริษัทปีปฏิทิน: ทุน 50,000 · รายได้ 12,000 · ค่าใช้จ่าย 2,000 ในปี 2024 (กำไร 10,000) · งวดปี 2024 ครบ 12 เดือน</summary>
    private static async Task<Seed> Seed2024Async(AccountingDbContext db, string name, string taxId)
    {
        var s = await SeedCompanyAsync(db, name, taxId);
        Je(db, s.CompanyId, D(2024, 1, 10), s.Cash, s.Capital, 50_000m);
        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Revenue, 12_000m);
        Je(db, s.CompanyId, D(2024, 6, 1), s.Expense, s.Cash, 2_000m);
        await db.SaveChangesAsync();
        await new AccountingService(db).EnsureFiscalYearPeriodsAsync(s.CompanyId, 2024);
        return s;
    }

    private static async Task<YearEndClosing> CloseAsync(Seed s, int year)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await new AccountingService(c).YearEndCloseAsync(s.CompanyId, year, s.Re, null, Guid.NewGuid().ToString());
    }

    private static async Task<YearEndReopenResult> ReopenAsync(Guid companyId, int year, string? reason = "พบค่าใช้จ่ายลงผิด", bool ack = false)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await new AccountingService(c).ReopenYearEndAsync(companyId, year, reason, ack, Guid.NewGuid().ToString());
    }

    private static async Task<List<string>> TbAsync(Guid companyId, DateTime asOf)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var tb = await new AccountingService(c).GetTrialBalanceAsync(companyId, asOf);
        return tb.Items.Where(i => i.DebitBalance - i.CreditBalance != 0m)
            .OrderBy(i => i.AccountCode, StringComparer.Ordinal)
            .Select(i => $"{i.AccountCode}:{i.DebitBalance - i.CreditBalance:0.00}")
            .ToList();
    }

    private static async Task<(decimal Revenue, decimal Expense, decimal Net)> PnlAsync(Guid companyId, int year)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var p = await new AccountingService(c).GetProfitAndLossAsync(companyId, D(year, 1, 1), D(year, 12, 31));
        return (p.TotalRevenue, p.TotalExpenses, p.NetIncome);
    }

    private static async Task<Dictionary<Guid, decimal>> OpeningNetAsync(AccountingDbContext db, Guid companyId, int year, int month)
        => await db.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.FiscalPeriod.Year == year && o.FiscalPeriod.Month == month)
            .GroupBy(o => o.AccountId)
            .Select(g => new { g.Key, Net = g.Sum(x => x.OpeningDebit) - g.Sum(x => x.OpeningCredit) })
            .ToDictionaryAsync(x => x.Key, x => x.Net);

    private sealed record Footprint(int Journals, int Audits, int Closings, int Openings, string PeriodStatuses);

    /// <summary>"ไม่มีอะไรถูกเขียน" — จำนวนใบสำคัญ · audit · แถวปิดปี (รวมที่ลบ) · ยอดยกมา (รวมที่ลบ) · สถานะงวด</summary>
    private static async Task<Footprint> FootprintAsync(Guid companyId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var periods = await c.FiscalPeriods.AsNoTracking().Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.Year).ThenBy(p => p.Month).Select(p => p.Status).ToListAsync();
        return new Footprint(
            await c.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.CompanyId == companyId),
            await c.AuditLogs.CountAsync(a => a.CompanyId == companyId),
            await c.YearEndClosings.IgnoreQueryFilters().CountAsync(y => y.CompanyId == companyId && !y.IsDeleted),
            await c.OpeningBalances.IgnoreQueryFilters().CountAsync(o => o.CompanyId == companyId && !o.IsDeleted),
            string.Join(",", periods));
    }

    private static Task<List<AuditLog>> ChainAsync(AccountingDbContext db, Guid companyId)
        => db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).OrderBy(a => a.Id).ToListAsync();

    // ═══════════════ 1) ปิด → ยกเลิก = ตัวเลขเท่าก่อนปิด · audit chain สะอาด ═══════════════

    [Fact]
    public async Task ปิดแล้วยกเลิก_งบทดลองกำไรขาดทุนยอดยกมาเท่าก่อนปิด_ทุกงวดเปิด_audit_chain_สะอาด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 1", "0105556001601");
        // งวด พ.ย. ปิดชั่วคราวไว้ก่อนปิดปี — หลังยกเลิก ทุกงวดกลับเป็นเปิด (คำตอบเจ้าของข้อ 3)
        var nov = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 11);
        await new AccountingService(db).SoftClosePeriodAsync(s.CompanyId, nov.Id, "u");

        var tbDec = await TbAsync(s.CompanyId, D(2024, 12, 31));
        var tbJan = await TbAsync(s.CompanyId, D(2025, 1, 31));
        var pnl = await PnlAsync(s.CompanyId, 2024);
        Assert.Equal((12_000m, 2_000m, 10_000m), pnl);

        var closing = await CloseAsync(s, 2024);
        using (var mid = DbTestDatabase.TryCreateContext()!)
        {
            var jan = await OpeningNetAsync(mid, s.CompanyId, 2025, 1);
            Assert.Equal(60_000m, jan[s.Cash]);
            Assert.Equal(-10_000m, jan[s.Re]);
        }

        var result = await ReopenAsync(s.CompanyId, 2024, "  พบค่าใช้จ่ายลงผิดปี  ");
        Assert.Equal(closing.ClosingJournalEntryId, result.ClosingJournalEntryId);
        Assert.Equal(12, result.PeriodsReopened);
        Assert.Equal(3, result.RolledRowsRemoved);
        Assert.Empty(result.AcknowledgedFiledReturns);

        // ตัวเลขเท่าก่อนปิด
        Assert.Equal(tbDec, await TbAsync(s.CompanyId, D(2024, 12, 31)));
        Assert.Equal(tbJan, await TbAsync(s.CompanyId, D(2025, 1, 31)));
        Assert.Equal(pnl, await PnlAsync(s.CompanyId, 2024));

        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await OpeningNetAsync(chk, s.CompanyId, 2025, 1));                     // ยอดยกมาเท่าก่อนปิด (ไม่มี)
        Assert.Equal(3, await chk.OpeningBalances.IgnoreQueryFilters().CountAsync(o => o.CompanyId == s.CompanyId && o.IsDeleted));

        // ทุกงวดของปีเปิด · ล้างตราล็อก/ปิดชั่วคราว/ใบปิดปี
        var periods = await chk.FiscalPeriods.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.Year == 2024).ToListAsync();
        Assert.Equal(12, periods.Count);
        Assert.All(periods, p =>
        {
            Assert.Equal(FiscalPeriodStatus.Open, p.Status);
            Assert.Null(p.LockedAt);
            Assert.Null(p.LockedBy);
            Assert.Null(p.ClosedAt);
            Assert.Null(p.YearEndJournalEntryId);
        });

        // แถวปิดปี: ลบแบบ soft + ผู้/เวลา/เหตุผล/ใบกลับ · ไม่เห็นในชุดปกติ
        Assert.False(await chk.YearEndClosings.AnyAsync(y => y.CompanyId == s.CompanyId));
        var row = await chk.YearEndClosings.IgnoreQueryFilters().AsNoTracking().SingleAsync(y => y.Id == closing.Id);
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.ReopenedAt);
        Assert.False(string.IsNullOrEmpty(row.ReopenedBy));
        Assert.Equal("พบค่าใช้จ่ายลงผิดปี", row.ReopenReason);
        Assert.Equal(result.ReversalJournalEntryId, row.ReopenJournalEntryId);

        // ใบเดิม = Reversed + ชี้ใบกลับ · ใบกลับ = ลงวันเดียวกัน · ติดธงใบปิด · บรรทัดกระจก
        var original = await chk.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == closing.ClosingJournalEntryId);
        var reversal = await chk.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == result.ReversalJournalEntryId);
        Assert.Equal(JournalEntryStatus.Reversed, original.Status);
        Assert.Equal(reversal.Id, original.ReversedByEntryId);
        Assert.Equal(JournalEntryStatus.Posted, reversal.Status);
        Assert.True(reversal.IsClosingEntry);
        Assert.True(reversal.IsAutoGenerated);
        Assert.Equal(original.Id, reversal.OriginalEntryId);
        Assert.Equal(original.EntryDate, reversal.EntryDate);
        Assert.Equal(original.Lines.Count, reversal.Lines.Count);
        foreach (var l in original.Lines)
            Assert.Contains(reversal.Lines, r => r.AccountId == l.AccountId && r.DebitAmount == l.CreditAmount && r.CreditAmount == l.DebitAmount);

        // audit: ทุกแถวที่เปลี่ยน (12 งวด + ใบเดิม + ใบกลับ + ยอดยกมา 3 + แถวปิดปี) · chain ไม่แตก/ไม่ถูกแก้
        var marker = "\"action\":\"" + YearEndReopenRule.AuditAction + "\"";
        var reopenAudits = await chk.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.NewValues != null && a.NewValues.Contains(marker)).ToListAsync();
        Assert.Equal(12, reopenAudits.Count(a => a.EntityType == nameof(FiscalPeriod)));
        Assert.Equal(2, reopenAudits.Count(a => a.EntityType == nameof(JournalEntry)));
        Assert.Equal(3, reopenAudits.Count(a => a.EntityType == nameof(OpeningBalance)));
        var closingAudit = Assert.Single(reopenAudits, a => a.EntityType == nameof(YearEndClosing));
        using (var doc = System.Text.Json.JsonDocument.Parse(closingAudit.NewValues!))
        {
            Assert.Equal("พบค่าใช้จ่ายลงผิดปี", doc.RootElement.GetProperty("reason").GetString());
            Assert.Equal(YearEndReopenRule.RuleCode, doc.RootElement.GetProperty("ruleCode").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("rolledRowsRemoved").GetInt32());
        }
        Assert.All(reopenAudits, a => Assert.NotNull(a.RowHash));
        var analysis = AuditHashChain.Analyze(await ChainAsync(chk, s.CompanyId));
        Assert.False(analysis.HasIntegrityFindings);
    }

    // ═══════════════ 2) ยกเลิก → ปรับปรุง → ปิดซ้ำ: ไม่ชน index · ยอดยกมาสะท้อนการปรับปรุง ═══════════════

    [Fact]
    public async Task ยกเลิกแล้วปรับปรุงแล้วปิดซ้ำ_ไม่ชนดัชนี_ยอดยกมาสะท้อนการปรับปรุง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 2", "0105556001602");
        await CloseAsync(s, 2024);

        var adjust = new CreateJournalEntryRequest(D(2024, 12, 15), "ปรับปรุงค่าใช้จ่ายค้างจ่าย", null, new List<JournalLineRequest>
        {
            new(s.Expense, 1_000m, 0m, "ค่าใช้จ่าย"), new(s.Cash, 0m, 1_000m, "เงินสด"),
        });
        // ทิศตรงข้าม: ก่อนยกเลิก งวด ธ.ค. ล็อก ⇒ บันทึกไม่ได้
        using (var locked = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAnyAsync<Exception>(() => new AccountingService(locked).CreateJournalEntryAsync(s.CompanyId, adjust, "u"));

        await ReopenAsync(s.CompanyId, 2024);
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c).CreateJournalEntryAsync(s.CompanyId, adjust, "u");

        var reclosed = await CloseAsync(s, 2024);                // เดิม: 23505 IX_YearEndClosings_CompanyYear / IX_OpeningBalances_Lookup
        Assert.Equal(9_000m, reclosed.TransferredAmount);

        using var chk = DbTestDatabase.TryCreateContext()!;
        var jan = await OpeningNetAsync(chk, s.CompanyId, 2025, 1);
        Assert.Equal(59_000m, jan[s.Cash]);
        Assert.Equal(-9_000m, jan[s.Re]);
        Assert.Equal(-50_000m, jan[s.Capital]);
        Assert.Equal(1, await chk.YearEndClosings.CountAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2024));
        Assert.Equal(2, await chk.YearEndClosings.IgnoreQueryFilters().CountAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2024));
        Assert.Equal(6, await chk.OpeningBalances.IgnoreQueryFilters().CountAsync(o => o.CompanyId == s.CompanyId));
        Assert.Equal(new[] { "11111:59000.00", "31000:-50000.00", "32000:-9000.00" }, await TbAsync(s.CompanyId, D(2025, 1, 31)));
        Assert.Equal((12_000m, 3_000m, 9_000m), await PnlAsync(s.CompanyId, 2024));
        // ยกเลิกอีกรอบได้ (ใบปิดใบที่สอง) — ไม่ชนดัชนีเช่นกัน
        Assert.Equal(3, (await ReopenAsync(s.CompanyId, 2024, "ตรวจอีกรอบ")).RolledRowsRemoved);
    }

    // ═══════════════ 3) ปีหลังปิดแล้ว ⇒ บล็อก ไม่มีอะไรถูกเขียน ═══════════════

    [Fact]
    public async Task ปีหลังปิดแล้ว_บล็อกพร้อมทางไปต่อ_ไม่มีอะไรถูกเขียน_เปิดปีล่าสุดก่อนแล้วเปิดปีก่อนได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทยกเลิกปิดปี 3", "0105556001603");
        Je(db, s.CompanyId, D(2023, 2, 1), s.Cash, s.Capital, 40_000m);
        Je(db, s.CompanyId, D(2023, 5, 1), s.Cash, s.Revenue, 5_000m);
        Je(db, s.CompanyId, D(2024, 5, 1), s.Cash, s.Revenue, 7_000m);
        await db.SaveChangesAsync();
        await new AccountingService(db).EnsureFiscalYearPeriodsAsync(s.CompanyId, 2023);
        await CloseAsync(s, 2023);
        await CloseAsync(s, 2024);

        var before = await FootprintAsync(s.CompanyId);
        var ex = await Assert.ThrowsAsync<YearEndReopenException>(() => ReopenAsync(s.CompanyId, 2023));
        Assert.Equal(YearEndReopenOutcome.LaterYearClosed, ex.Outcome);
        Assert.Contains("เปิดปีล่าสุดก่อน", ex.Message);
        Assert.Contains("2567", ex.Message);
        Assert.Equal(before, await FootprintAsync(s.CompanyId));

        // ทางไปต่อ: เปิด 2024 ก่อน แล้ว 2023 ได้
        await ReopenAsync(s.CompanyId, 2024);
        await ReopenAsync(s.CompanyId, 2023);
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.False(await chk.YearEndClosings.AnyAsync(y => y.CompanyId == s.CompanyId));
        Assert.Equal(new[] { "11111:52000.00", "31000:-40000.00", "41000:-12000.00" }, await TbAsync(s.CompanyId, D(2025, 1, 31)));
    }

    // ═══════════════ 4) ยื่น ภ.ง.ด.50 แล้ว: ไม่รับทราบ = 409 ไม่เขียน · รับทราบ = ผ่าน + เลขอ้างอิงใน audit ═══════════════

    private static AccountingController Controller(AccountingDbContext db, Guid userId)
        => new(new AccountingService(db), new CompanyService(db, new AccountingService(db)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
                },
            },
        };

    private static async Task<Guid> MemberAsync(AccountingDbContext db, Guid companyId, UserRole role)
    {
        var u = new User { Email = $"reopen-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = role });
        await db.SaveChangesAsync();
        return u.Id;
    }

    [Fact]
    public async Task ยื่นภงด50แล้ว_ไม่รับทราบ409พร้อมรายการแบบ_ไม่เขียนอะไร_รับทราบแล้วผ่านและเลขอ้างอิงอยู่ในaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 4", "0105556001604");
        var owner = await MemberAsync(db, s.CompanyId, UserRole.Owner);
        db.Set<ComplianceFiling>().AddRange(
            new ComplianceFiling { CompanyId = s.CompanyId, FilingType = "RD_CIT", FormCode = "PND50", Year = 2024, DueDate = D(2025, 5, 31),
                Status = "Filed", FiledDate = D(2025, 5, 20), ConfirmationNumber = "RD50-REF-7781" },
            new ComplianceFiling { CompanyId = s.CompanyId, FilingType = "DBD_FinancialStatement", FormCode = "SBC3", Year = 2024, DueDate = D(2025, 5, 31),
                Status = "Accepted", SubmissionReference = "DBD-REF-1192" },
            // ไม่นับ: ร่าง · ปีอื่น · ภ.พ.30
            new ComplianceFiling { CompanyId = s.CompanyId, FilingType = "RD_CIT", FormCode = "PND51", Year = 2024, Month = 6, DueDate = D(2024, 8, 31), Status = "Validated" },
            new ComplianceFiling { CompanyId = s.CompanyId, FilingType = "RD_CIT", FormCode = "PND50", Year = 2023, DueDate = D(2024, 5, 31), Status = "Filed" },
            new ComplianceFiling { CompanyId = s.CompanyId, FilingType = "RD_VAT", FormCode = "PP30", Year = 2024, Month = 12, DueDate = D(2025, 1, 15), Status = "Filed" });
        await db.SaveChangesAsync();
        await CloseAsync(s, 2024);

        var before = await FootprintAsync(s.CompanyId);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Controller(c1, owner).ReopenYearEnd(s.CompanyId, 2024, new YearEndReopenRequest("แก้ค่าเสื่อม", false));
            var obj = Assert.IsType<ObjectResult>(r.Result);
            Assert.Equal(409, obj.StatusCode);
            var body = Assert.IsType<ApiResponse<object>>(obj.Value);
            Assert.False(body.Success);
            Assert.Contains("ยื่นแบบ ภ.ง.ด.50 เพิ่มเติม", body.Message);
            Assert.Contains("งบการเงินฉบับแก้ไข", body.Message);
            var json = System.Text.Json.JsonSerializer.Serialize(body.Data);
            Assert.Contains("RD50-REF-7781", json);
            Assert.Contains("DBD-REF-1192", json);
            Assert.DoesNotContain("PND51", json);
            Assert.DoesNotContain("PP30", json);
        }
        Assert.Equal(before, await FootprintAsync(s.CompanyId));

        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Controller(c2, owner).ReopenYearEnd(s.CompanyId, 2024, new YearEndReopenRequest("แก้ค่าเสื่อม", true));
            var ok = Assert.IsType<OkObjectResult>(r.Result);
            var result = Assert.IsType<YearEndReopenResult>(Assert.IsType<ApiResponse<object>>(ok.Value).Data);
            Assert.Equal(2, result.AcknowledgedFiledReturns.Count);
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        var marker = "\"action\":\"" + YearEndReopenRule.AuditAction + "\"";
        var audit = await chk.AuditLogs.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(YearEndClosing)
            && a.NewValues != null && a.NewValues.Contains(marker));
        Assert.Contains("RD50-REF-7781", audit.NewValues);
        Assert.Contains("DBD-REF-1192", audit.NewValues);
        Assert.Contains("\"acknowledgeFiled\":true", audit.NewValues);
        Assert.Equal(owner, audit.UserId);
    }

    // ═══════════════ 5) ยอดนำเข้าไม่ถูกแตะ ═══════════════

    [Fact]
    public async Task ยอดนำเข้าไม่ถูกแตะ_ทั้งฐานกลางปีและงวดแรกของปีถัดไป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        // (ก) ฐานนำเข้ากลางปี 1 ก.ค. 2024 — ปิดปียกไป ม.ค. 2025 · ยกเลิก: แถว ม.ค. (ยกยอด) ถูกยกเลิก แถว ก.ค. (นำเข้า) คงเดิม
        var s = await SeedCompanyAsync(db, "บริษัทยกเลิกปิดปี 5ก", "0105556001605");
        await new AccountingService(db).EnsureFiscalYearPeriodsAsync(s.CompanyId, 2024);
        var jul = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 7);
        var note = OpeningRoll.ImportedNotePrefix + " ทดสอบ";
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jul.Id, AccountId = s.Cash, OpeningDebit = 40_000m, Notes = note },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jul.Id, AccountId = s.Capital, OpeningCredit = 30_000m, Notes = note },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jul.Id, AccountId = s.Revenue, OpeningCredit = 10_000m, Notes = note });
        Je(db, s.CompanyId, D(2024, 9, 1), s.Cash, s.Revenue, 3_000m);
        await db.SaveChangesAsync();
        await CloseAsync(s, 2024);
        var r = await ReopenAsync(s.CompanyId, 2024);
        Assert.Equal(3, r.RolledRowsRemoved);
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var julRows = await chk.OpeningBalances.IgnoreQueryFilters().AsNoTracking().Where(o => o.FiscalPeriodId == jul.Id).ToListAsync();
            Assert.Equal(3, julRows.Count);
            Assert.All(julRows, o => { Assert.False(o.IsDeleted); Assert.Equal(note, o.Notes); });
            Assert.Equal(40_000m, julRows.Single(o => o.AccountId == s.Cash).OpeningDebit);
            Assert.Empty(await OpeningNetAsync(chk, s.CompanyId, 2025, 1));
        }

        // (ข) งวดแรกของปีถัดไปมียอดนำเข้า — ปิดปีไม่ยกทับ · ยกเลิก: ไม่ลบแถวใดเลย
        var s2 = await SeedCompanyAsync(db, "บริษัทยกเลิกปิดปี 5ข", "0105556001615");
        Je(db, s2.CompanyId, D(2024, 2, 1), s2.Cash, s2.Capital, 20_000m);
        Je(db, s2.CompanyId, D(2024, 4, 1), s2.Cash, s2.Revenue, 1_000m);
        await db.SaveChangesAsync();
        await new AccountingService(db).EnsureFiscalYearPeriodsAsync(s2.CompanyId, 2025);
        var jan25 = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s2.CompanyId && p.Year == 2025 && p.Month == 1);
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s2.CompanyId, FiscalPeriodId = jan25.Id, AccountId = s2.Cash, OpeningDebit = 21_000m, Notes = note },
            new OpeningBalance { CompanyId = s2.CompanyId, FiscalPeriodId = jan25.Id, AccountId = s2.Capital, OpeningCredit = 21_000m, Notes = note });
        await db.SaveChangesAsync();
        await CloseAsync(s2, 2024);
        Assert.Equal(0, (await ReopenAsync(s2.CompanyId, 2024)).RolledRowsRemoved);
        using var chk2 = DbTestDatabase.TryCreateContext()!;
        var janRows = await chk2.OpeningBalances.IgnoreQueryFilters().AsNoTracking().Where(o => o.FiscalPeriodId == jan25.Id).ToListAsync();
        Assert.Equal(2, janRows.Count);
        Assert.All(janRows, o => Assert.False(o.IsDeleted));
    }

    // ═══════════════ 6) ใบปิดปีเดิม + ใบกลับ ยังกลับรายการ/ยกเลิก/ลบไม่ได้ ═══════════════

    [Fact]
    public async Task หลังยกเลิก_ใบปิดปีเดิมและใบกลับ_กลับรายการยกเลิกลบไม่ได้ทุกทาง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 6", "0105556001606");
        var closing = await CloseAsync(s, 2024);
        var r = await ReopenAsync(s.CompanyId, 2024);
        var before = await FootprintAsync(s.CompanyId);

        foreach (var id in new[] { closing.ClosingJournalEntryId, r.ReversalJournalEntryId })
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            var svc = new AccountingService(c);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ReverseJournalEntryAsync(s.CompanyId, id, D(2024, 12, 31)));
            Assert.Equal(ClosingEntryLock.RuleCode, ex.RuleCode);
            Assert.Contains("ยกเลิกการปิดปี", ex.Message);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ReverseJournalEntryAsync(s.CompanyId, id, D(2024, 12, 31), null, systemTriggered: true));
            // ใบเดิม (Reversed) ถูกด่านสถานะกันก่อน · ใบกลับ (Posted) ถูกด่านใบปิดกัน — ทั้งคู่ล้มก่อนเขียน
            await Assert.ThrowsAnyAsync<Exception>(() => svc.VoidJournalEntryAsync(s.CompanyId, id));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteJournalEntryAsync(s.CompanyId, id));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.BatchDeleteJournalEntriesAsync(s.CompanyId, new List<Guid> { id }));
            await Assert.ThrowsAnyAsync<Exception>(() => svc.BatchVoidJournalEntriesAsync(s.CompanyId, new List<Guid> { id }));   // ด่าน "ตัวกลับ"/ใบปิด
        }
        Assert.Equal(before, await FootprintAsync(s.CompanyId));
    }

    // ═══════════════ 7) ไม่ใช่เจ้าของ ⇒ 403 ═══════════════

    [Fact]
    public async Task ไม่ใช่เจ้าของ_403_ไม่มีอะไรถูกเขียน_เจ้าของทำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 7", "0105556001607");
        var accountant = await MemberAsync(db, s.CompanyId, UserRole.Accountant);
        var owner = await MemberAsync(db, s.CompanyId, UserRole.Owner);
        await CloseAsync(s, 2024);
        var before = await FootprintAsync(s.CompanyId);

        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Controller(c1, accountant).ReopenYearEnd(s.CompanyId, 2024, new YearEndReopenRequest("ลองเปิด", true)));
            Assert.Equal(403, ex.StatusCode);
        }
        Assert.Equal(before, await FootprintAsync(s.CompanyId));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.IsType<OkObjectResult>((await Controller(c2, owner).ReopenYearEnd(s.CompanyId, 2024, new YearEndReopenRequest("แก้", false))).Result);
    }

    // ═══════════════ 8) ยกยอดรุ่นเก่าไป ม.ค. ที่ยังไม่ซ่อม ⇒ บล็อก · ซ่อมแล้วยกเลิกได้ ═══════════════

    [Fact]
    public async Task ยกยอดรุ่นเก่าไปมกรายังไม่ซ่อม_บล็อกพร้อมทางไปต่อ_ซ่อมแล้วยกเลิกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        // รอบ เม.ย.–มี.ค. ปิดปี 2023 แล้วจำลองสภาพโค้ดก่อน de823512 (แบบเดียวกับ AdversarialExportRound4DbTests)
        var s = await SeedCompanyAsync(db, "บริษัทยกเลิกปิดปีรุ่นเก่า 8", "0105556001608", fiscalStartMonth: 4);
        Je(db, s.CompanyId, D(2023, 4, 10), s.Cash, s.Capital, 50_000m);
        Je(db, s.CompanyId, D(2023, 5, 1), s.Cash, s.Revenue, 12_000m);
        Je(db, s.CompanyId, D(2024, 2, 1), s.Expense, s.Cash, 2_000m);
        await db.SaveChangesAsync();
        await CloseAsync(s, 2023);
        (await db.YearEndClosings.SingleAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2023)).CreatedAt = D(2024, 4, 5);
        db.OpeningBalances.RemoveRange(db.OpeningBalances.Where(o => o.CompanyId == s.CompanyId));
        await db.SaveChangesAsync();
        var jan = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 1);
        db.OpeningBalances.AddRange(
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jan.Id, AccountId = s.Cash, OpeningDebit = 70_000m, Notes = OpeningRoll.RolledNote(2024) },
            new OpeningBalance { CompanyId = s.CompanyId, FiscalPeriodId = jan.Id, AccountId = s.Capital, OpeningCredit = 50_000m, Notes = null });
        await db.SaveChangesAsync();

        var before = await FootprintAsync(s.CompanyId);
        var ex = await Assert.ThrowsAsync<YearEndReopenException>(() => ReopenAsync(s.CompanyId, 2023));
        Assert.Equal(YearEndReopenOutcome.LegacyRollUnrepaired, ex.Outcome);
        Assert.Contains("ซ่อมยอดยกมารุ่นเก่าก่อน", ex.Message);
        Assert.Contains("2 แถว", ex.Message);
        Assert.Equal(before, await FootprintAsync(s.CompanyId));

        // ทางไปต่อ: ซ่อมปี 2024 (เขียน เม.ย. 2024 + ยกเลิกแถว ม.ค.) แล้วยกเลิกการปิดปี 2023 ได้ — แถว เม.ย. ที่ซ่อมเขียนถูกยกเลิก
        using (var fix = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, (await new AccountingService(fix).RepairOpeningRollAsync(s.CompanyId, new[] { 2024 }, Guid.NewGuid().ToString())).RepairedYears);
        var r = await ReopenAsync(s.CompanyId, 2023);
        Assert.True(r.RolledRowsRemoved > 0);
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await OpeningNetAsync(chk, s.CompanyId, 2024, 4));
        Assert.Empty(await OpeningNetAsync(chk, s.CompanyId, 2024, 1));
    }

    // ═══════════════ 9) สองคนกดพร้อมกัน ⇒ สำเร็จคนเดียว ═══════════════

    [Fact]
    public async Task สองคนกดยกเลิกพร้อมกัน_สำเร็จคนเดียว_อีกคนได้ยังไม่ได้ปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 9", "0105556001609");
        await CloseAsync(s, 2024);

        async Task<Exception?> Try()
        {
            try { await ReopenAsync(s.CompanyId, 2024); return null; }
            catch (Exception e) { return e; }
        }
        var results = await Task.WhenAll(Task.Run(Try), Task.Run(Try));
        Assert.Single(results, r => r == null);
        var failed = Assert.IsType<YearEndReopenException>(Assert.Single(results, r => r != null));
        Assert.Equal(YearEndReopenOutcome.NotClosed, failed.Outcome);

        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await chk.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId && j.Reference == "YE-2024-REOPEN"));
        var marker = "\"action\":\"" + YearEndReopenRule.AuditAction + "\"";
        Assert.Equal(1, await chk.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(YearEndClosing)
            && a.NewValues != null && a.NewValues.Contains(marker)));
    }

    // ═══════════════ 10) ใบปิดปีที่มีผังรายได้/ค่าใช้จ่ายที่ลบ ⇒ ใบกลับลงตัวครบ ═══════════════

    [Fact]
    public async Task ใบปิดปีที่มีผังที่ลบ_ใบกลับครบทุกบรรทัดและลงตัว_งบทดลองเท่าก่อนปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทยกเลิกปิดปีผังที่ลบ 10", "0105556001610");
        var travel = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "51500", AccountName = "ค่าเดินทาง", AccountType = AccountType.Expense, Level = 4 };
        db.ChartOfAccounts.Add(travel);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2024, 1, 10), s.Cash, s.Capital, 20_000m);
        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Revenue, 8_000m);
        Je(db, s.CompanyId, D(2024, 4, 1), travel.Id, s.Cash, 1_000m);
        await db.SaveChangesAsync();
        travel.IsDeleted = true;
        await db.SaveChangesAsync();
        await new AccountingService(db).EnsureFiscalYearPeriodsAsync(s.CompanyId, 2024);
        var tbBefore = await TbAsync(s.CompanyId, D(2025, 1, 31));

        await CloseAsync(s, 2024);
        var r = await ReopenAsync(s.CompanyId, 2024);
        using var chk = DbTestDatabase.TryCreateContext()!;
        var lines = await chk.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == r.ReversalJournalEntryId).ToListAsync();
        Assert.Equal(3, lines.Count);
        Assert.Equal(8_000m, lines.Sum(l => l.DebitAmount));
        Assert.Equal(8_000m, lines.Sum(l => l.CreditAmount));
        Assert.Equal(1_000m, Assert.Single(lines, l => l.AccountId == travel.Id).DebitAmount);
        Assert.Equal(tbBefore, await TbAsync(s.CompanyId, D(2025, 1, 31)));
        // ทิศตรงข้าม: ผังที่ลบยังลงรายการด้วยมือไม่ได้ (ด่านยอมเฉพาะชุดของใบกลับ)
        await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(chk).CreateJournalEntryAsync(s.CompanyId,
            new CreateJournalEntryRequest(D(2024, 12, 1), "มือ", null, new List<JournalLineRequest> { new(travel.Id, 10m, 0m, "x"), new(s.Cash, 0m, 10m, "y") }), "u"));
    }

    // ═══════════════ 11) ล็อกแบบ ภ.พ.30 / ภ.ง.ด. รายเดือนที่ยื่นแล้วยังอยู่หลังงวดเปิด ═══════════════

    [Fact]
    public async Task ยกเลิกการปิดปีแล้ว_ล็อกแบบภาษีรายเดือนที่ยื่นแล้วยังอยู่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 11", "0105556001611");
        var vatDoc = Guid.NewGuid();
        var whtDoc = Guid.NewGuid();
        var vat = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VAT, Year = 2024, Month = 12, Status = TaxReportStatus.Filed,
            FiledDate = D(2025, 1, 15), FilingLockedAt = D(2025, 1, 15), RdAckNumber = "PP30-ACK-1" };
        vat.Lines.Add(new TaxReportLine { LineOrder = 1, TransactionDate = D(2024, 12, 5), DocumentId = vatDoc, IncomeAmount = 1_000m, TaxAmount = 70m });
        var wht = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.WithholdingTax53, Year = 2024, Month = 12, Status = TaxReportStatus.Filed,
            FiledDate = D(2025, 1, 7), FilingLockedAt = D(2025, 1, 7), RdAckNumber = "PND53-ACK-1" };
        wht.Lines.Add(new TaxReportLine { LineOrder = 1, TransactionDate = D(2024, 12, 6), DocumentId = whtDoc, IncomeAmount = 1_000m, TaxRate = 3m, TaxAmount = 30m });
        db.TaxReports.AddRange(vat, wht);
        await db.SaveChangesAsync();
        await CloseAsync(s, 2024);
        await ReopenAsync(s.CompanyId, 2024);

        using var chk = DbTestDatabase.TryCreateContext()!;
        var dec = await chk.FiscalPeriods.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 12);
        Assert.Equal(FiscalPeriodStatus.Open, dec.Status);                 // งวดบัญชีเปิด
        var tax = new TaxService(chk);
        Assert.True(await tax.IsDocumentFilingLockedAsync(s.CompanyId, vatDoc));   // แต่เอกสารในแบบที่ยื่นแล้วยังล็อก
        Assert.True(await tax.IsDocumentFilingLockedAsync(s.CompanyId, whtDoc));
        var reports = await chk.TaxReports.AsNoTracking().Where(t => t.CompanyId == s.CompanyId).ToListAsync();
        Assert.All(reports, t => { Assert.Equal(TaxReportStatus.Filed, t.Status); Assert.NotNull(t.FilingLockedAt); });
    }

    // ═══════════════ 12) งวด ธ.ค. ปิดชั่วคราว ⇒ ปิดปีได้ (บั๊กข้างเคียง) ═══════════════

    [Fact]
    public async Task งวดธันวาปิดชั่วคราว_ปิดปีได้_ล็อกและมีaudit_งวดอื่นปิดชั่วคราวไม่ต้องเปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await Seed2024Async(db, "บริษัทยกเลิกปิดปี 12", "0105556001612");
        var svc = new AccountingService(db);
        var dec = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 12);
        var oct = await db.FiscalPeriods.SingleAsync(p => p.CompanyId == s.CompanyId && p.Year == 2024 && p.Month == 10);
        await svc.SoftClosePeriodAsync(s.CompanyId, dec.Id, "u");
        await svc.SoftClosePeriodAsync(s.CompanyId, oct.Id, "u");

        var closing = await CloseAsync(s, 2024);                 // เดิม: "ไม่สามารถบันทึกรายการในงวด 2024/12 ได้ เนื่องจากงวดถูกปิดแล้ว"
        Assert.Equal(10_000m, closing.TransferredAmount);
        using var chk = DbTestDatabase.TryCreateContext()!;
        var periods = await chk.FiscalPeriods.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.Year == 2024).ToListAsync();
        Assert.All(periods, p => Assert.Equal(FiscalPeriodStatus.Locked, p.Status));
        Assert.Equal(closing.ClosingJournalEntryId, periods.Single(p => p.Month == 12).YearEndJournalEntryId);
        var marker = "\"action\":\"" + AccountingService.YearEndCloseThroughSoftClosedAction + "\"";
        var audit = Assert.Single(await chk.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.NewValues != null && a.NewValues.Contains(marker)).ToListAsync());
        Assert.Equal(dec.Id.ToString(), audit.EntityId);          // เฉพาะงวดของวันที่ใบปิดปี (ต.ค. ไม่ถูกเปิด)
        Assert.False(AuditHashChain.Analyze(await ChainAsync(chk, s.CompanyId)).HasIntegrityFindings);
    }
}
