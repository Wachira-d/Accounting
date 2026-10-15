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
/// ฝ่ายค้านรอบส่งออก 2026-10-15 <b>รอบ 5</b> (ฝ่ายค้านบน eec6947d · erp-review/2026-10-14/EXPORT_AUDIT.md §"รอบ 5") บนฐาน PostgreSQL จริง:
/// <list type="bullet">
/// <item>P1 ใบปิดปีที่ล้างผังที่ลบ: เปิดดูเห็นครบทุกบรรทัด · กลับรายการ/ยกเลิก/ลบใบปิดปีถูกบล็อกพร้อมทางไปต่อ · ใบปกติที่มีผังที่ลบกลับรายการได้ครบและลงตัว</item>
/// <item>P2-2 บริษัทปีปฏิทินที่ปิดปีแล้วเปลี่ยนเดือนเริ่มรอบ (มีประวัติ) ไม่ถูกบล็อกว่ายกยอดรุ่นเก่า · เปลี่ยนเดือนเริ่มรอบหลังปิดปี = บล็อก · ก่อนปิดปี = ได้ + audit</item>
/// <item>P2-3 กู้คืนผังใต้ผังแม่ที่กลายเป็นผังลงรายการ = บล็อก + เสนอหัวบัญชี · กู้คืนใต้หัวบัญชีอื่นได้ · ผังแม่ที่ยังมีลูก = กู้คืนที่เดิมได้</item>
/// </list>
/// ทุกเทสต์ปิดปีที่สิ้นรอบแล้วเท่านั้น (ปี ≤ 2025 · ไม่ขึ้นกับนาฬิกา)
/// </summary>
[Trait("Category", "Db")]
public class AdversarialExportRound5DbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialExportRound5DbTests(ITestOutputHelper output) => _out = output;

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
            CompanyId = companyId, EntryNumber = $"JV-R5-{n:D4}", EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = "ทดสอบรอบ 5", TotalDebit = amt, TotalCredit = amt,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
        db.JournalEntries.Add(je);
        return je;
    }

    // ═══════════════ P1 ใบปิดปีที่มีบรรทัดผังที่ลบ ═══════════════

    [Fact]
    public async Task ใบปิดปีที่ล้างผังที่ลบ_เปิดดูเห็นครบทุกบรรทัด_กลับรายการยกเลิกลบถูกบล็อกพร้อมทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทใบปิดปีผังที่ลบ R5", "0105556000501");
        var travel = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "51500", AccountName = "ค่าเดินทาง", AccountType = AccountType.Expense, Level = 4 };
        db.ChartOfAccounts.Add(travel);
        await db.SaveChangesAsync();
        Je(db, s.CompanyId, D(2024, 1, 10), s.Cash, s.Capital, 20_000m);
        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Revenue, 8_000m);
        Je(db, s.CompanyId, D(2024, 4, 1), travel.Id, s.Cash, 1_000m);
        await db.SaveChangesAsync();
        travel.IsDeleted = true;
        await db.SaveChangesAsync();

        YearEndClosing closing;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            closing = await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        using (var v = DbTestDatabase.TryCreateContext()!)
        {
            // เดิม: ThenInclude ผัง (INNER JOIN ผ่านตัวกรอง) ทิ้งบรรทัดผังที่ลบ ⇒ Dr 8,000 / Cr 7,000 ทั้งที่หัวใบ 8,000/8,000
            var je = await new AccountingService(v).GetJournalEntryAsync(s.CompanyId, closing.ClosingJournalEntryId);
            Assert.Equal(3, je.Lines.Count);
            Assert.Equal(8_000m, je.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(8_000m, je.Lines.Sum(l => l.CreditAmount));
            var travelLine = Assert.Single(je.Lines, l => l.AccountId == travel.Id);
            Assert.Equal(1_000m, travelLine.CreditAmount);
            Assert.EndsWith(JournalLineAccounts.DeletedSuffix, travelLine.AccountName);
        }

        var jeCount = await db.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId);
        using (var r = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new AccountingService(r);
            // เดิม: กลับรายการได้ (TAS 8 ไม่ตรวจงวดใบเดิม) ⇒ ตัวกลับขาดบรรทัดผังที่ลบ (GL คลาด 1,000) + ไม่ติดธงใบปิด
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.ReverseJournalEntryAsync(s.CompanyId, closing.ClosingJournalEntryId, D(2025, 2, 1)));
            Assert.Equal(ClosingEntryLock.RuleCode, ex.RuleCode);
            Assert.Contains("ใบปิดบัญชีสิ้นปี 2567", ex.Message);
            Assert.Contains("ใบสำคัญปรับปรุงกำไรสะสม", ex.Message);
            // ทางระบบ (ยกเลิกเอกสาร → systemTriggered) ก็กลับไม่ได้
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.ReverseJournalEntryAsync(s.CompanyId, closing.ClosingJournalEntryId, D(2025, 2, 1), null, systemTriggered: true));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.VoidJournalEntryAsync(s.CompanyId, closing.ClosingJournalEntryId));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteJournalEntryAsync(s.CompanyId, closing.ClosingJournalEntryId));
            // ทางหลายใบ (เดิมไม่ตรวจงวดเลย ⇒ ลบใบปิดปีทิ้งได้)
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.BatchDeleteJournalEntriesAsync(s.CompanyId, new List<Guid> { closing.ClosingJournalEntryId }));
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.BatchVoidJournalEntriesAsync(s.CompanyId, new List<Guid> { closing.ClosingJournalEntryId }));
        }
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(jeCount, await chk.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));   // ไม่มีอะไรถูกบันทึก
        var stored = await chk.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == closing.ClosingJournalEntryId);
        Assert.Equal(JournalEntryStatus.Posted, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task ใบปกติที่มีบรรทัดผังที่ลบ_กลับรายการได้ครบทุกบรรทัดและลงตัว_แก้ไขด้วยการกลับรายการล้มก่อนเขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทกลับรายการผังที่ลบ R5", "0105556000502");
        var travel = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "51500", AccountName = "ค่าเดินทาง", AccountType = AccountType.Expense, Level = 4 };
        db.ChartOfAccounts.Add(travel);
        await db.SaveChangesAsync();
        var withDeleted = Je(db, s.CompanyId, D(2025, 2, 1), travel.Id, s.Cash, 1_000m);
        var ordinary = Je(db, s.CompanyId, D(2025, 2, 2), s.Cash, s.Revenue, 3_000m);
        var toCorrect = Je(db, s.CompanyId, D(2025, 2, 3), travel.Id, s.Cash, 400m);
        await db.SaveChangesAsync();
        travel.IsDeleted = true;
        await db.SaveChangesAsync();

        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            // แก้ไขด้วยการกลับรายการ: ร่างที่คัดลอกลงผังที่ลบไม่ได้ ⇒ ล้มก่อนกลับรายการ (เดิมกลับไปแล้วค่อยล้ม — ใบเดิมถูกกลับแต่ไม่มีร่าง)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new AccountingService(c).CorrectJournalEntryAsync(s.CompanyId, toCorrect.Id, Guid.NewGuid().ToString()));
            Assert.Contains("51500", ex.Message);
            Assert.Contains("ยังไม่มีอะไรถูกบันทึก", ex.Message);
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(JournalEntryStatus.Posted, (await chk.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == toCorrect.Id)).Status);

        using (var r = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new AccountingService(r);
            // ทิศตรงข้าม: ใบปกติ (ไม่ใช่ใบปิด) ที่มีบรรทัดผังที่ลบ ⇒ กลับรายการได้ ตัวกลับครบทุกบรรทัด (ลงผังเดิม) และลงตัว
            var rev = await svc.ReverseJournalEntryAsync(s.CompanyId, withDeleted.Id, D(2025, 3, 1));
            Assert.Equal(2, rev.Lines.Count);
            Assert.Equal(1_000m, rev.Lines.Single(l => l.AccountId == travel.Id).CreditAmount);
            Assert.Equal(1_000m, rev.Lines.Single(l => l.AccountId == s.Cash).DebitAmount);
            Assert.Equal(rev.TotalDebit, rev.TotalCredit);
            // ใบปกติที่ไม่มีผังที่ลบ — ไม่ถูกแตะ
            var rev2 = await svc.ReverseJournalEntryAsync(s.CompanyId, ordinary.Id, D(2025, 3, 1));
            Assert.Equal(3_000m, rev2.Lines.Sum(l => l.DebitAmount));
            Assert.Equal(3_000m, rev2.Lines.Sum(l => l.CreditAmount));
        }
        using var fin = DbTestDatabase.TryCreateContext()!;
        var revLines = await fin.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.OriginalEntryId == withDeleted.Id)
            .ToListAsync();
        Assert.Equal(revLines.Sum(l => l.DebitAmount), revLines.Sum(l => l.CreditAmount));   // GL ลงตัว (เดิมขาดบรรทัดผังที่ลบ)
        Assert.Equal(JournalEntryStatus.Reversed, (await fin.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == withDeleted.Id)).Status);
    }

    // ═══════════════ P2-2 เปลี่ยนเดือนเริ่มรอบบัญชี ═══════════════

    [Fact]
    public async Task บริษัทปีปฏิทินที่ปิดปีแล้วเปลี่ยนเป็นรอบเมษา_มีประวัติการเปลี่ยน_ไม่ถูกบล็อกว่ายกยอดรุ่นเก่า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทเปลี่ยนรอบหลังปิดปี R5", "0105556000503");
        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Capital, 20_000m);
        Je(db, s.CompanyId, D(2024, 5, 1), s.Cash, s.Revenue, 6_000m);
        await db.SaveChangesAsync();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c1).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        // สภาพจริง: ปิดปี 2024 (ปีปฏิทิน) ด้วยโค้ดก่อนการแก้ ⇒ ยกไป ม.ค. 2025 ถูกต้อง · แล้วค่อยเปลี่ยนเป็นรอบ เม.ย. (ก่อนรอบ 5 เปลี่ยนได้เสมอ)
        (await db.YearEndClosings.SingleAsync(y => y.CompanyId == s.CompanyId && y.FiscalYear == 2024)).CreatedAt = D(2025, 1, 15);
        (await db.Companies.SingleAsync(c => c.Id == s.CompanyId)).FiscalYearStartMonth = 4;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = s.CompanyId, EntityType = "Company", EntityId = s.CompanyId.ToString(), Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { fiscalYearStartMonth = 1 }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = OpeningRoll.FiscalYearStartMonthChangeAction, fiscalYearStartMonth = 4 }),
            Timestamp = D(2025, 2, 1),
        });
        Je(db, s.CompanyId, D(2025, 6, 1), s.Cash, s.Revenue, 500m);
        await db.SaveChangesAsync();

        using (var rep = DbTestDatabase.TryCreateContext()!)
            // เดิม: งวด ม.ค. 2025 ถูกรายงานว่า "ยกยอดรุ่นเก่า" และปุ่ม "แก้" จะยกเลิกยอดยกมาที่ถูกต้อง (เขียนประวัติใหม่)
            Assert.All(await new AccountingService(rep).FindOpeningRollMismatchesAsync(s.CompanyId), y => Assert.Equal(0, y.LegacyRolledRows));
        YearEndClosing closing;
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            // เดิม: ล้มด้วยข้อความยกยอดรุ่นเก่า (ซ่อมปี 2568)
            closing = await new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2025, s.Re, null, Guid.NewGuid().ToString());
        Assert.Equal(500m, closing.TransferredAmount);
        using var fin = DbTestDatabase.TryCreateContext()!;
        var jan = await fin.OpeningBalances.AsNoTracking()
            .Where(o => o.CompanyId == s.CompanyId && o.FiscalPeriod.Year == 2025 && o.FiscalPeriod.Month == 1).CountAsync();
        Assert.Equal(3, jan);                                              // ยอดยกมา ม.ค. 2025 ไม่ถูกยกเลิก
    }

    [Fact]
    public async Task เปลี่ยนเดือนเริ่มรอบ_ก่อนปิดปีได้และมีประวัติ_หลังปิดปีถูกบล็อกพร้อมข้อความ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทล็อกเดือนเริ่มรอบ R5", "0105556000504");
        var userId = Guid.NewGuid();

        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            // ก่อนปิดปีครั้งแรก: เปลี่ยนได้ (ทิศตรงข้าม) + audit เป็นหลักฐานของตัวตรวจยกยอดรุ่นเก่า
            var company = await c1.Companies.SingleAsync(c => c.Id == s.CompanyId);
            var svc = new CompanyService(c1, new AccountingService(c1));
            await svc.ApplyFiscalYearStartMonthAsync(company, userId, 4);
            await c1.SaveChangesAsync();
            await svc.ApplyFiscalYearStartMonthAsync(company, userId, 1);   // เปลี่ยนกลับ (ยังไม่ปิดปี)
            await c1.SaveChangesAsync();
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
        {
            var changes = await chk.AuditLogs.AsNoTracking()
                .Where(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains(OpeningRoll.FiscalYearStartMonthChangeAction)).CountAsync();
            Assert.Equal(2, changes);
            Assert.Equal(1, (await chk.Companies.AsNoTracking().SingleAsync(c => c.Id == s.CompanyId)).FiscalYearStartMonth);
        }

        Je(db, s.CompanyId, D(2024, 3, 1), s.Cash, s.Revenue, 1_000m);
        await db.SaveChangesAsync();
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(c2).YearEndCloseAsync(s.CompanyId, 2024, s.Re, null, Guid.NewGuid().ToString());

        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var company = await c3.Companies.SingleAsync(c => c.Id == s.CompanyId);
            var svc = new CompanyService(c3, new AccountingService(c3));
            // เดิม: เปลี่ยนได้เงียบ ๆ ⇒ ปีที่ปิดถูกตีความใหม่ (ด่านยกยอดรุ่นเก่าบล็อกเท็จ)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ApplyFiscalYearStartMonthAsync(company, userId, 4));
            Assert.Equal(OpeningRoll.FiscalYearStartMonthRuleCode, ex.RuleCode);
            Assert.Contains("2567", ex.Message);
            Assert.Contains("ม.11", ex.Message);
            // ค่าเดิม (หน้าตั้งค่าส่งช่องนี้ทุกครั้ง) ไม่ใช่การเปลี่ยน ⇒ ไม่ล้ม
            await svc.ApplyFiscalYearStartMonthAsync(company, userId, 1);
        }
        using var fin = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, (await fin.Companies.AsNoTracking().SingleAsync(c => c.Id == s.CompanyId)).FiscalYearStartMonth);
    }

    // ═══════════════ P2-3 กู้คืนผังใต้ผังแม่ที่กลายเป็นผังลงรายการ ═══════════════

    [Fact]
    public async Task กู้คืนผังใต้ผังแม่ที่กลายเป็นผังลงรายการ_ถูกบล็อก_กู้คืนใต้หัวบัญชีที่เสนอได้_ผังแม่ที่ยังมีลูกกู้คืนที่เดิมได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedCompanyAsync(db, "บริษัทกู้คืนผังลูก R5", "0105556000505");
        var cid = s.CompanyId;
        var h113 = new ChartOfAccount { CompanyId = cid, AccountCode = "113", AccountName = "ลูกหนี้", AccountType = AccountType.Asset, Level = 3 };
        db.ChartOfAccounts.Add(h113);
        await db.SaveChangesAsync();
        var leaf = new ChartOfAccount { CompanyId = cid, AccountCode = "11310", AccountName = "ลูกหนี้การค้า", AccountType = AccountType.Asset, Level = 4,
            IsSystemAccount = true, ParentAccountId = h113.Id };
        var h111 = new ChartOfAccount { CompanyId = cid, AccountCode = "11122", AccountName = "เงินฝากออมทรัพย์", AccountType = AccountType.Asset, Level = 4 };
        db.ChartOfAccounts.AddRange(leaf, h111);
        await db.SaveChangesAsync();
        var child = new ChartOfAccount { CompanyId = cid, AccountCode = "113101", AccountName = "ลูกหนี้ PEAK", AccountType = AccountType.Asset, Level = 5,
            ParentAccountId = leaf.Id, IsDeleted = true };
        var bank1 = new ChartOfAccount { CompanyId = cid, AccountCode = "111221", AccountName = "ออมทรัพย์ 1", AccountType = AccountType.Asset, Level = 5, ParentAccountId = h111.Id };
        var bank2 = new ChartOfAccount { CompanyId = cid, AccountCode = "111222", AccountName = "ออมทรัพย์ 2", AccountType = AccountType.Asset, Level = 5,
            ParentAccountId = h111.Id, IsDeleted = true };
        db.ChartOfAccounts.AddRange(child, bank1, bank2);
        Je(db, cid, D(2025, 2, 1), leaf.Id, s.Revenue, 1_000m);   // 11310 กลับมาเป็นผังลงรายการและมีรายการแล้ว
        await db.SaveChangesAsync();
        var owner = Guid.NewGuid().ToString();

        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new AccountingService(r1);
            // เดิม: กู้คืนได้ ⇒ 11310 กลายเป็นบัญชีคุมที่มียอด (ด่านลงรายการเข้มปฏิเสธ · ลูกหนี้ตั้งต้นพลิก)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RestoreAccountAsync(cid, child.Id, owner));
            Assert.Contains("11310", ex.Message);
            Assert.Contains("หัวบัญชี 113", ex.Message);                  // ทางไปต่อ: หัวบัญชีที่เสนอ
            Assert.Contains("ยังไม่มีอะไรถูกบันทึก", ex.Message);
            var plan = await svc.PlanAccountRestoreAsync(cid, child.Id);
            Assert.NotNull(plan.BlockedReason);
            Assert.Equal(h113.Id, plan.SuggestedParentId);
            // หัวบัญชีผิดประเภท ⇒ ล้มพร้อมเหตุผล
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RestoreAccountAsync(cid, child.Id, owner, s.Revenue));
        }
        using (var chk = DbTestDatabase.TryCreateContext()!)
            Assert.True((await chk.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == child.Id)).IsDeleted);

        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            // ทางไปต่อ: กู้คืนไว้ใต้หัวบัญชีที่เสนอ (ปุ่มหน้าตรวจข้อมูลส่ง newParentId)
            var restored = await new AccountingService(r2).RestoreAccountAsync(cid, child.Id, owner, h113.Id);
            Assert.Equal(h113.Id, restored.ParentAccountId);
        }
        using (var r3 = DbTestDatabase.TryCreateContext()!)
        {
            var a = await r3.ChartOfAccounts.AsNoTracking().SingleAsync(x => x.Id == child.Id);
            Assert.Equal(4, a.Level);                                       // ระดับตามหัวบัญชีใหม่ (113 = 3)
            Assert.True(await r3.AuditLogs.AnyAsync(x => x.CompanyId == cid && x.EntityId == child.Id.ToString()
                && x.NewValues!.Contains(OpeningRoll.AccountRestoreAction)));
            // ทิศตรงข้าม: ผังแม่ที่ยังมีลูกอยู่ (เป็นบัญชีคุมอยู่แล้ว) ⇒ กู้คืนที่เดิมได้
            var b2 = await new AccountingService(r3).RestoreAccountAsync(cid, bank2.Id, owner);
            Assert.Equal(h111.Id, b2.ParentAccountId);
        }
    }
}
