using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 243a (คำตัดสิน 243-1…243-7 · DESIGN G-9/G-10) — ตัดสูญเครดิตภาษีถูกหัก 11910 บน PostgreSQL จริง:
/// ลง JE สมดุล Dr 58010 (กลุ่ม 580 ⇒ ภ.ง.ด.50 ตัดออกจากรายจ่าย) / Cr 11910 + audit chain + สถานะ · กดซ้ำได้ JE เดียว · งวดปิด = ปฏิเสธ ·
/// ได้ใบภายหลัง ⇒ กลับรายการ JE + คืนเครดิต · ทะเบียนกระทบ GL ตรงทั้งก่อน/หลัง · แถวของบริษัทอื่น = ไม่พบ
/// </summary>
[Trait("Category", "Db")]
public class WhtCreditWriteOffDbTests
{
    private readonly ITestOutputHelper _out;
    public WhtCreditWriteOffDbTests(ITestOutputHelper output) => _out = output;

    private static readonly WhtCreditService.WriteOffActor Actor = new(Guid.NewGuid(), "ผู้ทำบัญชีทดสอบ");
    private const string Reason = "ทวงแล้ว 3 ครั้ง ลูกค้าปิดกิจการ ไม่ออก 50 ทวิ";

    private static WhtCreditService Svc(AccountingDbContext db) => new(db, NullLogger<WhtCreditService>.Instance);

    private sealed record Seed(Guid CompanyId, Guid OtherCompanyId, Guid CreditId, Guid OtherCreditId, int TaxYear);

    /// <summary>บริษัท + ผัง (11910 · 11310 ลูกหนี้ · กลุ่ม 580 ไม่มีผัง 58010 — ระบบต้องสร้างเอง) + JE รับชำระ Dr 11910 1,000 + แถวรอใบ ·
    /// บริษัทอื่นมีแถวของตัวเอง (ห้ามปน)</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, DateTime? paidOnOverride = null)
    {
        // วันรับชำระ = 30 วันก่อน (ไม่ใช่ 10 ม.ค. ตายตัว — ต้นเดือน ม.ค. จะกลายเป็นวันในอนาคตแล้วยอด 11910 ณ วันตัดสูญเป็น 0)
        var paidOn = paidOnOverride ?? DateTime.UtcNow.Date.AddDays(-30);
        var year = paidOn.Year;
        var company = new Company { Name = "บริษัททดสอบตัดสูญ 11910", TaxId = "0105556000052", FiscalYearStartMonth = 1 };
        var other = new Company { Name = "บริษัทอื่น (ห้ามปน)", TaxId = "0105556000061", FiscalYearStartMonth = 1 };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();

        ChartOfAccount A(Guid cid, string code, string name, AccountType t, Guid? parent = null)
        {
            var a = new ChartOfAccount { CompanyId = cid, AccountCode = code, AccountName = name, AccountType = t, Level = parent == null ? 3 : 4,
                ParentAccountId = parent, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var wht = A(company.Id, "11910", "ภาษีถูกหัก ณ ที่จ่าย", AccountType.Asset);
        var ar = A(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        A(company.Id, "580", "ภาษีเงินได้", AccountType.Expense);
        var oWht = A(other.Id, "11910", "ภาษีถูกหัก ณ ที่จ่าย", AccountType.Asset);
        var oAr = A(other.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        await db.SaveChangesAsync();

        void Je(Guid cid, string no, Guid dr, Guid cr, decimal amt)
        {
            var je = new JournalEntry { CompanyId = cid, EntryNumber = no, EntryDate = DateTime.SpecifyKind(paidOn, DateTimeKind.Utc),
                Status = JournalEntryStatus.Posted, Description = "รับชำระ ลูกค้าหัก ณ ที่จ่าย", TotalDebit = amt, TotalCredit = amt };
            je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        Je(company.Id, "RV-T243-0001", wht.Id, ar.Id, 1_000m);
        Je(other.Id, "RV-T243-0001", oWht.Id, oAr.Id, 500m);

        var credit = new WhtCreditReceived { CompanyId = company.Id, TaxYear = year, PayerName = "ลูกค้าไม่ส่งใบ จำกัด", IncomeAmount = 33_333.33m,
            WhtRate = 3m, WhtAmount = 1_000m, Status = WhtCreditStatus.Pending };
        var otherCredit = new WhtCreditReceived { CompanyId = other.Id, TaxYear = year, PayerName = "ลูกค้าบริษัทอื่น", IncomeAmount = 16_666.67m,
            WhtRate = 3m, WhtAmount = 500m, Status = WhtCreditStatus.Pending };
        db.WhtCreditsReceived.AddRange(credit, otherCredit);
        await db.SaveChangesAsync();
        return new Seed(company.Id, other.Id, credit.Id, otherCredit.Id, year);
    }

    /// <summary>ปีภาษีก่อนหน้า (15 มิ.ย.) — ให้ตัดสูญ "ปี N+1" (วันนี้) และปิดปี N (31 ธ.ค.) เป็นวันในอดีตทั้งคู่</summary>
    private static DateTime LastYear() => new(DateTime.UtcNow.Year - 1, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private static Task<decimal> NetByCodeAsync(AccountingDbContext db, Guid companyId, string code)
        => db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId && !l.JournalEntry.IsDeleted
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && l.Account!.AccountCode == code)
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

    private static Task<decimal> Gl11910Async(AccountingDbContext db, Guid companyId)
        => Svc(db).GlWhtBalanceAsync(companyId, new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.Date.AddDays(1));

    [Fact]
    public async Task ตัดสูญ_ลงJEสมดุล_58010กลุ่ม580_ล้าง11910_audit_สถานะ_กดซ้ำได้JEเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);
            Assert.False(r.AlreadyWrittenOff);
            Assert.Equal(1_000m, r.Amount);
        }

        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.CreditId);
        Assert.Equal(WhtCreditStatus.Expired, row.Status);
        Assert.NotNull(row.WriteOffJournalEntryId);
        Assert.Equal(Reason, row.WriteOffReason);
        var je = await read.JournalEntries.AsNoTracking().Include(j => j.Lines).ThenInclude(l => l.Account).ThenInclude(a => a!.ParentAccount)
            .FirstAsync(j => j.Id == row.WriteOffJournalEntryId && j.CompanyId == s.CompanyId);
        Assert.Equal(JournalEntryStatus.Posted, je.Status);
        Assert.Equal(WhtCreditWriteOff.Reference(s.CreditId), je.Reference);
        Assert.Equal(je.Lines.Sum(l => l.DebitAmount), je.Lines.Sum(l => l.CreditAmount));
        var dr = Assert.Single(je.Lines, l => l.DebitAmount > 0);
        var cr = Assert.Single(je.Lines, l => l.CreditAmount > 0);
        Assert.Equal("58010", dr.Account!.AccountCode);
        Assert.Equal("580", dr.Account.ParentAccount!.AccountCode);   // ผังที่ระบบสร้างให้อยู่กลุ่มภาษีเงินได้
        Assert.Equal("11910", cr.Account!.AccountCode);
        Assert.Equal(1_000m, dr.DebitAmount);
        // ภ.ง.ด.50/51 ตัดบรรทัดนี้ออกจากรายจ่าย (ม.65 ตรี (6 ทวิ)) ด้วยตัวกรองเดียวกับรายงาน
        Assert.False(await read.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == je.Id && l.Account!.AccountType == AccountType.Expense)
            .ExceptCitExpense().AnyAsync());
        Assert.Equal(0m, await Gl11910Async(read, s.CompanyId));
        Assert.Equal(0m, await Svc(read).CreditableAmountAsync(s.CompanyId, s.TaxYear));
        var sum = await Svc(read).SummaryAsync(s.CompanyId, s.TaxYear);
        Assert.Equal(1_000m, sum.WrittenOffAmount);
        Assert.Equal(0m, sum.Difference);                                // ทะเบียนกระทบ GL ตรงหลังตัดสูญ
        var audit = await read.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.EntityId == s.CreditId.ToString()).ToListAsync();
        Assert.Contains(audit, a => a.NewValues!.Contains(WhtCreditWriteOff.RuleCode) && a.RowHash != null);

        // กดซ้ำ ⇒ ผลเดิม ไม่มี JE ที่สอง
        using (var again = DbTestDatabase.TryCreateContext()!)
        {
            var r2 = await Svc(again).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);
            Assert.True(r2.AlreadyWrittenOff);
            Assert.Equal(je.Id, r2.JournalEntryId);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId && j.Reference == WhtCreditWriteOff.Reference(s.CreditId)));

        // แก้ไข/ลบแถวที่ตัดสูญพร้อม JE ไม่ได้ (ทะเบียนจะเปลี่ยนโดย GL ไม่ขยับ)
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(check).DeleteAsync(s.CompanyId, s.CreditId));
    }

    [Fact]
    public async Task ได้ใบภายหลัง_กลับรายการJE_คืนเครดิต_ไม่มีสิทธิ์ลงบัญชี_ปฏิเสธ403()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);

        using (var noPerm = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(noPerm).MarkReceivedAsync(s.CompanyId, s.CreditId, "50T-2026-001", null, null, null, journalActor: null));
            Assert.Equal(403, ex.StatusCode);
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).MarkReceivedAsync(s.CompanyId, s.CreditId, "50T-2026-001", DateTime.UtcNow.Date, null, null, Actor);

        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.CreditId);
        Assert.Equal(WhtCreditStatus.Received, row.Status);
        Assert.Equal("50T-2026-001", row.CertificateNumber);
        Assert.NotNull(row.WriteOffReversalJournalEntryId);
        var original = await read.JournalEntries.AsNoTracking().FirstAsync(j => j.Id == row.WriteOffJournalEntryId);
        Assert.Equal(JournalEntryStatus.Reversed, original.Status);
        Assert.Equal(row.WriteOffReversalJournalEntryId, original.ReversedByEntryId);
        var reversal = await read.JournalEntries.AsNoTracking().Include(j => j.Lines).FirstAsync(j => j.Id == row.WriteOffReversalJournalEntryId);
        Assert.Equal(original.Id, reversal.OriginalEntryId);
        Assert.Equal(reversal.Lines.Sum(l => l.DebitAmount), reversal.Lines.Sum(l => l.CreditAmount));
        Assert.Equal(1_000m, await Gl11910Async(read, s.CompanyId));                         // 11910 กลับมาครบ
        Assert.Equal(1_000m, await Svc(read).CreditableAmountAsync(s.CompanyId, s.TaxYear));   // เครดิตได้อีกครั้ง
        var sum = await Svc(read).SummaryAsync(s.CompanyId, s.TaxYear);
        Assert.Equal(0m, sum.WrittenOffAmount);
        Assert.Equal(0m, sum.Difference);
        Assert.True(await read.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.CreditId.ToString()
            && a.NewValues!.Contains(WhtCreditWriteOff.ReversalRuleCode)));
    }

    [Fact]
    public async Task งวดปิด_เหตุผลสั้น_ปฏิเสธ_ไม่มีJE_แถวบริษัทอื่นไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var closedDay = new DateTime(s.TaxYear, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        db.FiscalPeriods.Add(new FiscalPeriod { CompanyId = s.CompanyId, Name = $"{s.TaxYear}/01", Year = s.TaxYear, Month = 1,
            StartDate = new DateTime(s.TaxYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(s.TaxYear, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            Status = FiscalPeriodStatus.Closed });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, closedDay, Actor));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).ExpireAsync(s.CompanyId, s.CreditId, "สั้นไป", null, Actor));
        // บริษัทอื่นอ้าง id แถวของเรา ⇒ ไม่พบ (404) · และกลับกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(req).ExpireAsync(s.OtherCompanyId, s.CreditId, Reason, null, Actor));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(req).ExpireAsync(s.CompanyId, s.OtherCreditId, Reason, null, Actor));

        using var read = DbTestDatabase.TryCreateContext()!;
        var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.CreditId);
        Assert.Equal(WhtCreditStatus.Pending, row.Status);   // ทิศตรงข้าม: ถูกปฏิเสธแล้วแถวไม่ถูกแตะ
        Assert.Null(row.WriteOffJournalEntryId);
        Assert.False(await read.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == WhtCreditWriteOff.Reference(s.CreditId)));
        Assert.Equal(1_000m, await Gl11910Async(read, s.CompanyId));
        var otherRow = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.OtherCreditId);
        Assert.Equal(WhtCreditStatus.Pending, otherRow.Status);
    }

    // ───────── ฝ่ายค้าน S1: ปิดปีกับตัดสูญรายแถวใช้แกนเดียว + ยอดตามปีภาษี ─────────

    [Fact]
    public async Task ปิดปีตัดสูญทีละรายการ_แล้วตัดสูญรายแถวซ้ำไม่ได้_ยอดปีหมดแล้วล้างเพิ่มไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, LastYear());

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 0m, 0m, 1_000m, Reason), "ทดสอบ"));   // ช่องตัดสูญไม่ระบุรายการ = ปฏิเสธ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 0m, 0m, 1_000m, Reason, new[] { s.CreditId }), "ทดสอบ");

        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == s.CreditId);
            Assert.Equal(WhtCreditStatus.Expired, row.Status);
            var je = await read.JournalEntries.AsNoTracking().FirstAsync(j => j.Id == row.WriteOffJournalEntryId);
            Assert.Equal(WhtCreditWriteOff.Reference(s.CreditId), je.Reference);              // แกนตัดสูญรายแถว ไม่ใช่ JE ก้อนปิดปี
            Assert.Equal(new DateTime(s.TaxYear, 12, 31), je.EntryDate.Date);
            Assert.False(await read.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == WhtYearEndReference.For(s.TaxYear)));
            Assert.Equal(0m, await Gl11910Async(read, s.CompanyId));
        }
        using (var again = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Svc(again).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor)).AlreadyWrittenOff);
        using (var again = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(again).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 1m, 0m, 0m, null), "ทดสอบ"));    // ยอดปีหมดแล้ว
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId && j.Reference == WhtCreditWriteOff.Reference(s.CreditId)));
        Assert.Equal(0m, await Gl11910Async(check, s.CompanyId));
    }

    [Fact]
    public async Task ตัดสูญรายแถวในปีถัดไป_แล้วปิดปีเดิม_ไม่ล้างซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, LastYear());
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);          // ลงวันนี้ = ปี N+1

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0m, await Svc(req).TaxYearWhtAvailableAsync(s.CompanyId, s.TaxYear));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 1_000m, 0m, 0m, null), "ทดสอบ"));   // เดิม: ยอดปี N ยังเห็น 1,000 ⇒ ล้างซ้ำ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 0m, 0m, 1_000m, Reason, new[] { s.CreditId }), "ทดสอบ"));
            Assert.Equal(409, ex.StatusCode);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0m, await Gl11910Async(check, s.CompanyId));                               // ไม่ติดลบ
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId
            && (j.Reference == WhtCreditWriteOff.Reference(s.CreditId) || j.Reference == WhtYearEndReference.For(s.TaxYear))));
    }

    // ───────── ฝ่ายค้าน S3: เส้นทั่วไปกลับ/ยกเลิก JE ตัดสูญไม่ได้ ─────────

    [Fact]
    public async Task สมุดรายวัน_กลับรายการหรือยกเลิกJEตัดสูญไม่ได้_ทะเบียนไม่เพี้ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid jeId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            jeId = (await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor)).JournalEntryId;

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(req).ReverseJournalEntryAsync(s.CompanyId, jeId));
            Assert.Contains("ได้รับใบแล้ว", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(req).VoidJournalEntryAsync(s.CompanyId, jeId));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => new AccountingService(req).DeleteJournalEntryAsync(s.CompanyId, jeId));

        using var read = DbTestDatabase.TryCreateContext()!;
        var je = await read.JournalEntries.AsNoTracking().FirstAsync(j => j.Id == jeId);
        Assert.Equal(JournalEntryStatus.Posted, je.Status);
        Assert.Null(je.ReversedByEntryId);
        Assert.Equal(0m, (await Svc(read).SummaryAsync(s.CompanyId, s.TaxYear)).Difference);
    }

    // ───────── ฝ่ายค้าน S4: ต้นทางหายหลังตัดสูญ ⇒ กลับ JE ตัดสูญ (11910 ไม่ติดลบ · 58010 ถูกกลับ) ─────────

    [Fact]
    public async Task ยกเลิกใบต้นทางหลังตัดสูญ_กลับJEตัดสูญ_11910ไม่ติดลบ_58010สุทธิศูนย์_คืนเงินบางส่วนกลับไปรอใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor);
        // จำลองตัวกลับรายการของใบต้นทางที่ถูกยกเลิก (Cr 11910 / Dr ลูกหนี้) — สิ่งที่ DocumentService ลงก่อนเรียก SyncWhtCreditReceivedAsync
        var acc = await db.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == s.CompanyId).ToDictionaryAsync(a => a.AccountCode, a => a.Id);
        var voidJe = new JournalEntry { CompanyId = s.CompanyId, EntryNumber = "RV-T243-VOID", EntryDate = DateTime.UtcNow.Date,
            Status = JournalEntryStatus.Posted, Description = "กลับรายการ — ยกเลิกใบต้นทาง", TotalDebit = 1_000m, TotalCredit = 1_000m };
        voidJe.Lines.Add(new JournalEntryLine { AccountId = acc["11310"], DebitAmount = 1_000m, LineOrder = 1 });
        voidJe.Lines.Add(new JournalEntryLine { AccountId = acc["11910"], CreditAmount = 1_000m, LineOrder = 2 });
        db.JournalEntries.Add(voidJe);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(-1_000m, await Gl11910Async(req, s.CompanyId));                  // ก่อนแก้: ติดลบ

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.True(await Svc(req).UndoWriteOffAsync(s.CompanyId, s.CreditId, "ใบต้นทางถูกยกเลิก", 0m, removeRow: true, "system"));
        using (var req = DbTestDatabase.TryCreateContext()!)   // ซ้ำ = ไม่ทำอะไร
            Assert.False(await Svc(req).UndoWriteOffAsync(s.CompanyId, s.CreditId, "ใบต้นทางถูกยกเลิก", 0m, removeRow: true, "system"));

        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(0m, await Gl11910Async(read, s.CompanyId));
            Assert.Equal(0m, await NetByCodeAsync(read, s.CompanyId, "58010"));
            var row = await read.WhtCreditsReceived.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == s.CreditId);
            Assert.True(row.IsDeleted);
            Assert.NotNull(row.WriteOffReversalJournalEntryId);
            Assert.True(await read.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.CreditId.ToString()
                && a.NewValues!.Contains(WhtCreditWriteOff.UndoRuleCode)));
        }

        // คืนเงินบางส่วนหลังตัดสูญ (เส้นแพลตฟอร์ม) ⇒ กลับ JE + เหลือยอดรอใบ ตัดสินใหม่ได้
        var p = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ExpireAsync(p.CompanyId, p.CreditId, Reason, null, Actor);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).UndoWriteOffAsync(p.CompanyId, p.CreditId, "คืนเงินบางส่วน", 600m, removeRow: false, "platform-billing");
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            var row = await read.WhtCreditsReceived.AsNoTracking().FirstAsync(w => w.Id == p.CreditId);
            Assert.Equal(WhtCreditStatus.Pending, row.Status);
            Assert.Equal(600m, row.WhtAmount);
            Assert.Equal(0m, await NetByCodeAsync(read, p.CompanyId, "58010"));
        }
    }

    // ───────── ฝ่ายค้าน N7: ภ.ง.ด.50 จริง — 58010 ไม่ถูกหักเป็นรายจ่าย และไม่ถูกบวกกลับซ้ำ ─────────

    [Fact]
    public async Task ภงด50_ตัดสูญ58010_ตัดออกจากรายจ่ายครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, LastYear());
        var cash = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "11111", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 4 };
        var rev = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "41000", AccountName = "รายได้จากการขาย", AccountType = AccountType.Revenue, Level = 4 };
        var exp = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "53000", AccountName = "ค่าใช้จ่ายบริหาร", AccountType = AccountType.Expense, Level = 4 };
        db.ChartOfAccounts.AddRange(cash, rev, exp);
        await db.SaveChangesAsync();
        void Je(string no, Guid dr, Guid cr, decimal amt)
        {
            var je = new JournalEntry { CompanyId = s.CompanyId, EntryNumber = no, EntryDate = new DateTime(s.TaxYear, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = JournalEntryStatus.Posted, Description = no, TotalDebit = amt, TotalCredit = amt };
            je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
            db.JournalEntries.Add(je);
        }
        Je("JV-T243-REV", cash.Id, rev.Id, 50_000m);
        Je("JV-T243-EXP", exp.Id, cash.Id, 2_000m);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, new DateTime(s.TaxYear, 12, 1, 0, 0, 0, DateTimeKind.Utc), Actor);

        using var tax = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(tax).GenerateTaxReportAsync(s.CompanyId, new CreateTaxReportRequest(TaxType.CorporateIncomeTax, s.TaxYear, 1));
        Assert.Contains(report.Lines, l => l.Description == "ค่าใช้จ่ายทั้งปี" && l.IncomeAmount == 2_000m);   // 58010 ไม่อยู่ในรายจ่าย
        Assert.Equal(48_000m, report.NetVat);   // 50,000 − 2,000 · ไม่ใช่ 47,000 (หักได้ผิด) หรือ 49,000 (บวกกลับซ้ำ)
    }

    // ───────── รอบ 250 WP-04 (AUDIT_249 §WP-04 ข้อ 2 · E L-1): ด่าน WhtWriteOffJournalLock สองทิศผ่าน AccountingService จริง ─────────

    [Fact]
    public async Task WhtWriteOffJournalLock_กลับJEตัดสูญ409รหัสGL_WHT_WRITEOFF_ENTRY_ทิศตรงข้ามJEธรรมดากลับได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid writeOffJe;
        using (var req = DbTestDatabase.TryCreateContext()!)
            writeOffJe = (await Svc(req).ExpireAsync(s.CompanyId, s.CreditId, Reason, null, Actor)).JournalEntryId;

        foreach (var correct in new[] { false, true })
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            var acc = new AccountingService(req);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => correct
                ? (Task)acc.CorrectJournalEntryAsync(s.CompanyId, writeOffJe, "tester")
                : acc.ReverseJournalEntryAsync(s.CompanyId, writeOffJe));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(WhtWriteOffJournalLock.RuleCode, ex.RuleCode);
            Assert.Equal("GL-WHT-WRITEOFF-ENTRY", ex.RuleCode);
        }
        using (var mid = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(JournalEntryStatus.Posted, (await mid.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == writeOffJe)).Status);

        // ทิศตรงข้าม: JE ธรรมดาของบริษัทเดียวกัน (ไม่มีอ้างอิงตัดสูญ · ไม่มีเอกสารต้นทาง) ⇒ ด่านไม่แตะ กลับรายการได้
        var cash = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "11110", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var fee = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "53100", AccountName = "ค่าธรรมเนียม", AccountType = AccountType.Expense, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(cash, fee);
        var plain = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "JV-T250-" + Guid.NewGuid().ToString("N")[..6], EntryDate = DateTime.UtcNow.Date,
            Status = JournalEntryStatus.Posted, Description = "ค่าธรรมเนียมธนาคาร", TotalDebit = 50m, TotalCredit = 50m,
        };
        plain.Lines.Add(new JournalEntryLine { AccountId = fee.Id, DebitAmount = 50m, LineOrder = 1 });
        plain.Lines.Add(new JournalEntryLine { AccountId = cash.Id, CreditAmount = 50m, LineOrder = 2 });
        db.JournalEntries.Add(plain);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new AccountingService(req).ReverseJournalEntryAsync(s.CompanyId, plain.Id);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(JournalEntryStatus.Reversed, (await check.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == plain.Id)).Status);
        Assert.Equal(JournalEntryStatus.Posted, (await check.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == writeOffJe)).Status);
    }

    // ───────── รอบ 250 WP-04 (ค้างจาก WP-02 §2.4 ข้อ 9 · E L-2): ปิดปีตัดสูญหลายแถว ⇒ ล็อก/ลงทีละแถวตามลำดับ Id คงที่ ─────────

    [Fact]
    public async Task SettleYearEnd_locks_in_id_order()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var paidOn = LastYear();
        var s = await SeedAsync(db, paidOn);
        // อีก 3 แถวของปีเดียวกัน + ยอด 11910 รองรับ (Dr 11910 / Cr 11310 รวม 600)
        var wht = await db.ChartOfAccounts.SingleAsync(a => a.CompanyId == s.CompanyId && a.AccountCode == "11910");
        var ar = await db.ChartOfAccounts.SingleAsync(a => a.CompanyId == s.CompanyId && a.AccountCode == "11310");
        var more = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "RV-T250-" + Guid.NewGuid().ToString("N")[..6], EntryDate = DateTime.SpecifyKind(paidOn, DateTimeKind.Utc),
            Status = JournalEntryStatus.Posted, Description = "รับชำระ ลูกค้าหัก ณ ที่จ่าย (เพิ่ม)", TotalDebit = 600m, TotalCredit = 600m,
        };
        more.Lines.Add(new JournalEntryLine { AccountId = wht.Id, DebitAmount = 600m, LineOrder = 1 });
        more.Lines.Add(new JournalEntryLine { AccountId = ar.Id, CreditAmount = 600m, LineOrder = 2 });
        db.JournalEntries.Add(more);
        var extra = new[] { 100m, 200m, 300m }.Select((amt, i) => new WhtCreditReceived
        {
            CompanyId = s.CompanyId, TaxYear = s.TaxYear, PayerName = $"ลูกค้าไม่ส่งใบ {i + 2} จำกัด", IncomeAmount = amt / 0.03m,
            WhtRate = 3m, WhtAmount = amt, Status = WhtCreditStatus.Pending,
        }).ToList();
        db.WhtCreditsReceived.AddRange(extra);
        await db.SaveChangesAsync();
        var ids = extra.Select(e => e.Id).Append(s.CreditId).ToList();
        // ส่งรายการเรียงกลับด้าน (ลำดับที่ผู้ใช้เลือก) — ตัวปิดปีต้องเรียงตาม Id เอง (ลำดับ uuid ของ PostgreSQL = ลำดับสตริงตัวพิมพ์เล็ก)
        var expectedOrder = ids.OrderBy(i => i.ToString("D"), StringComparer.Ordinal).ToList();
        var requested = expectedOrder.AsEnumerable().Reverse().ToArray();

        string numbers;
        using (var req = DbTestDatabase.TryCreateContext()!)
            numbers = await Svc(req).SettleYearEndAsync(s.CompanyId,
                new WhtCreditService.SettleRequest(s.TaxYear, 0m, 0m, 1_600m, Reason, requested), "ทดสอบ");

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.WhtCreditsReceived.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync();
        Assert.All(rows, r => Assert.Equal(WhtCreditStatus.Expired, r.Status));
        var jeIds = rows.Select(r => r.WriteOffJournalEntryId!.Value).ToList();
        var jeNumbers = await check.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == s.CompanyId && jeIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.EntryNumber);
        var byCredit = rows.ToDictionary(r => r.Id, r => jeNumbers[r.WriteOffJournalEntryId!.Value]);
        // ผลที่คืน = เลข JE ตามลำดับที่ลงจริง (ไม่มี JE ก้อนปิดปี — ช่องหักกับภาษี/ขอคืนเป็น 0) ⇒ ต้องตรงลำดับ Id ไม่ใช่ลำดับที่ส่งมา
        Assert.Equal(expectedOrder.Select(i => byCredit[i]).ToArray(), numbers.Split(" · "));
        Assert.NotEqual(requested.Select(i => byCredit[i]).ToArray(), numbers.Split(" · "));
        Assert.Equal(0m, await Gl11910Async(check, s.CompanyId));
    }
}
