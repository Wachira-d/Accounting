using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม W10 2026-10-26 (R-55 Db test ทิศกลับ + R-56 pattern ตั้งค่าได้) — ด่านมัดจำของการจองบนฐาน PostgreSQL จริง ผ่านตัวโหลด/ตัวตัดสินเดิม
/// <see cref="BookingDepositLedger"/> + <see cref="BookingDepositDrawdown"/> (ไม่มีสำเนา):
/// <list type="bullet">
/// <item>ตัดเกินยอดคงเหลือ ⇒ ข้อความ 422 <c>INT-DEPOSIT-OVERDRAW</c> · ตัดพอดี ⇒ ผ่าน (ทิศตรงข้าม)</item>
/// <item>ทิศกลับ: กลับรายการใบมัดจำที่ถูก JE อื่นตัดไปแล้ว ⇒ 409 <c>DEP-DRAWN-REMOVE</c> ระบุ JE ที่ตัด · กลับ JE ที่ "ตัด" เอง ⇒ ผ่าน</item>
/// <item>พร้อมกัน 2 คำขอ (คนละ context · คนละธุรกรรม) ตัดมัดจำก้อนเดียว ⇒ ล็อกต่อการจอง (<c>pg_advisory_xact_lock</c>) จัดคิว ⇒ ผ่านได้ไม่เกินยอด</item>
/// <item>R-56: มัดจำลงด้วยเลขอ้างอิงรูปอื่น (<c>BK-77001</c>) — ไม่ตั้งค่า ⇒ ด่านไม่เห็น ⇒ ปล่อยผ่าน (G3 เหมือนเดิม) · ตั้ง pattern ที่คีย์ integration ⇒ ด่านเห็น ⇒ ปฏิเสธ</item>
/// </list>
/// แยกข้อมูลด้วยบริษัทใหม่ต่อเทสต์ · ไม่มีฐาน (ไม่ตั้ง <c>ACCOUNTING_TEST_PG</c>) ⇒ ข้ามพร้อมข้อความ — หลักฐานคือ job <c>db-test</c>
/// </summary>
[Trait("Category", "Db")]
public class BookingDepositDrawdownDbTests
{
    private readonly ITestOutputHelper _out;
    public BookingDepositDrawdownDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, ChartOfAccount Bank, ChartOfAccount Deposit, ChartOfAccount Revenue, JournalEntry DepositJe);

    /// <summary>บริษัทใหม่ + ผัง 3 ตัว + ใบรับมัดจำ Dr ธนาคาร / Cr 21510 1,000 ที่อ้างการจองด้วย <paramref name="depositRef"/></summary>
    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string depositRef, decimal deposit = 1_000m)
    {
        var company = new Company { Name = "บริษัททดสอบมัดจำการจอง", TaxId = "0" + Random.Shared.NextInt64(100_000_000_000L, 999_999_999_999L) };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var bank = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11110", AccountName = "เงินฝากธนาคาร", AccountType = AccountType.Asset, Level = 4 };
        var dep = new ChartOfAccount { CompanyId = company.Id, AccountCode = "21510", AccountName = "เงินมัดจำรับล่วงหน้าค่าห้องพัก", AccountType = AccountType.Liability, Level = 4 };
        var rev = new ChartOfAccount { CompanyId = company.Id, AccountCode = "41110", AccountName = "รายได้ค่าห้องพัก", AccountType = AccountType.Revenue, Level = 4 };
        db.ChartOfAccounts.AddRange(bank, dep, rev);
        await db.SaveChangesAsync();
        var rv = Je(company.Id, "RV-DEP-0001", depositRef, "รับมัดจำ " + depositRef, bank.Id, dep.Id, deposit, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        db.JournalEntries.Add(rv);
        await db.SaveChangesAsync();
        return new Seed(company.Id, bank, dep, rev, rv);
    }

    private static JournalEntry Je(Guid companyId, string no, string? reference, string description, Guid drAccount, Guid crAccount, decimal amount, DateTime date)
    {
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = no, EntryDate = date, Status = JournalEntryStatus.Posted,
            Description = description, Reference = reference, TotalDebit = amount, TotalCredit = amount,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = drAccount, DebitAmount = amount, LineOrder = 1, Description = description });
        je.Lines.Add(new JournalEntryLine { AccountId = crAccount, CreditAmount = amount, LineOrder = 2, Description = description });
        return je;
    }

    /// <summary>บรรทัดของ JE ใหม่ที่จะ "ตัด" มัดจำ: Dr 21510 / Cr รายได้ (เหมือน JV-INT รับรู้รายได้ Checkout)</summary>
    private static List<JournalEntryLine> DrawLines(Seed s, decimal amount, string? lineDesc = null) => new()
    {
        new JournalEntryLine { AccountId = s.Deposit.Id, DebitAmount = amount, LineOrder = 1, Description = lineDesc },
        new JournalEntryLine { AccountId = s.Revenue.Id, CreditAmount = amount, LineOrder = 2, Description = lineDesc },
    };

    private static Dictionary<Guid, ChartOfAccount> Accounts(Seed s) => new[] { s.Bank, s.Deposit, s.Revenue }.ToDictionary(a => a.Id);

    [Fact]
    public async Task ตัดเกินมัดจำคงเหลือ_ปฏิเสธ422_ตัดพอดีผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, "RES-149433");

        var over = await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 1_500m), Accounts(s), "RES-149433-CHK", "รับรู้รายได้ Checkout");
        Assert.NotNull(over);
        Assert.Contains(BookingDepositDrawdown.RuleCode, over);
        Assert.Contains("#149433", over);
        Assert.Contains("1,500.00", over);
        Assert.Contains("1,000.00", over);

        // ทิศตรงข้าม: ตัดพอดี 1,000 ⇒ ผ่าน · ไม่อ้างการจอง ⇒ ไม่แตะ · ตัดบัญชีอื่นที่ไม่ใช่มัดจำ ⇒ ไม่แตะ
        Assert.Null(await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 1_000m), Accounts(s), "RES-149433-CHK", null));
        Assert.Null(await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 1_500m), Accounts(s), "INV-2026-0001", "ขายเงินสด"));
    }

    [Fact]
    public async Task ทิศกลับ_กลับใบมัดจำที่ถูกตัดแล้ว_ปฏิเสธ409_กลับJEที่ตัดเองผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, "RES-149433");
        var draw = Je(s.CompanyId, "JV-INT-0009", "RES-149433-CHK", "รับรู้รายได้ Checkout", s.Deposit.Id, s.Revenue.Id, 600m, new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc));
        db.JournalEntries.Add(draw);
        await db.SaveChangesAsync();

        var block = await BookingDepositLedger.RemovalProblemAsync(db, s.CompanyId, new[] { s.DepositJe.Id },
            new[] { s.DepositJe.Reference, s.DepositJe.Description }, $"กลับรายการ {s.DepositJe.EntryNumber}");
        Assert.NotNull(block);
        Assert.Contains(BookingDepositDrawdown.RemovalRuleCode, block);
        Assert.Contains("JV-INT-0009", block);          // ทางไปต่อระบุ JE ที่ตัด
        Assert.Contains("600.00", block);               // ยอดที่จะติดลบ = 1,000 − 1,000 − 600 ⇒ −600

        // ทิศตรงข้าม: กลับ JE ที่ "ตัด" มัดจำ ⇒ ไม่บล็อก (ยอดกลับขึ้น ไม่ลง)
        Assert.Null(await BookingDepositLedger.RemovalProblemAsync(db, s.CompanyId, new[] { draw.Id },
            new[] { draw.Reference, draw.Description }, $"กลับรายการ {draw.EntryNumber}"));
    }

    [Fact]
    public async Task สองคำขอพร้อมกันตัดมัดจำก้อนเดียว_ล็อกต่อการจองจัดคิว_ผ่านไม่เกินยอด()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(seedDb, "RES-555001");   // มัดจำ 1,000 · สองคำขอขอตัด 600 + 600

        var firstHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // คำขอ A: ด่านผ่าน (ถือล็อกต่อการจองในธุรกรรม) → รอให้ B เริ่มรอล็อก → บันทึก JE ตัด 600 → commit
        async Task<string?> RequestA()
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            var problem = await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 600m), Accounts(s), "RES-555001-CHK", "Checkout A");
            firstHoldsLock.SetResult();
            await secondStarted.Task;
            await Task.Delay(500);   // ให้ B ไปค้างที่ pg_advisory_xact_lock แน่ ๆ ก่อน A commit
            if (problem == null)
            {
                db.JournalEntries.Add(Je(s.CompanyId, "JV-A", "RES-555001-CHK", "Checkout A", s.Deposit.Id, s.Revenue.Id, 600m, DateTime.UtcNow.Date));
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
            return problem;
        }

        // คำขอ B: เริ่มหลัง A ถือล็อก → ด่านต้องรอจน A commit → เห็นคงเหลือ 400 → ปฏิเสธ 600
        async Task<string?> RequestB()
        {
            await firstHoldsLock.Task;
            using var db = DbTestDatabase.TryCreateContext()!;
            await using var tx = await db.Database.BeginTransactionAsync();
            secondStarted.SetResult();
            var problem = await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 600m), Accounts(s), "RES-555001-CHK", "Checkout B");
            await tx.RollbackAsync();
            return problem;
        }

        var results = await Task.WhenAll(RequestA(), RequestB());
        Assert.Null(results[0]);                                      // A ผ่าน (มัดจำ 1,000 พอ)
        Assert.NotNull(results[1]);                                   // B ถูกจัดคิวแล้วเห็นคงเหลือ 400 ⇒ ปฏิเสธ
        Assert.Contains(BookingDepositDrawdown.RuleCode, results[1]);
        Assert.Contains("400.00", results[1]);

        // ยอดในสมุดหลังทั้งคู่: ตัดไป 600 ใบเดียว คงเหลือ 400 — ไม่ติดลบ
        using var check = DbTestDatabase.TryCreateContext()!;
        var snap = (await BookingDepositLedger.LoadManyAsync(check, s.CompanyId, new[] { "555001" }))["555001"];
        Assert.Equal(400m, BookingDepositDrawdown.Balances(snap.Lines)[s.Deposit.Id]);
    }

    [Fact]
    public async Task R56_มัดจำลงด้วยเลขรูปอื่น_ไม่ตั้งค่าปล่อยผ่าน_ตั้งpatternแล้วด่านเห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, "BK-77001");        // ระบบต้นทางใช้ BK-… ไม่ใช่ RES-…
        var lines = DrawLines(s, 1_500m);               // ตัด 1,500 ทั้งที่มัดจำ 1,000

        // ทิศตรงข้าม (G3 เดิม): ไม่มี pattern ⇒ ด่านไม่รู้จัก BK- ⇒ ไม่ตัดสิน ⇒ ผ่าน
        Assert.Null(await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, lines, Accounts(s), "BK-77001", "Checkout"));
        Assert.Equal(2, (await BookingDepositLedger.PatternsAsync(db, s.CompanyId)).Count);

        // ตั้ง pattern ที่คีย์ integration ของบริษัท ⇒ ด่านเห็นเครดิตมัดจำที่ลงด้วย BK-77001 ⇒ ตัดเกิน ⇒ ปฏิเสธ
        db.Set<ExternalIntegration>().Add(new ExternalIntegration
        {
            CompanyId = s.CompanyId, SystemName = "PMS-BK", SystemType = "PMS",
            BookingRefPatternsJson = BookingRefPattern.ToJson(new[] { @"BK-(?<key>\d{4,})" }),
        });
        await db.SaveChangesAsync();
        Assert.Equal(3, (await BookingDepositLedger.PatternsAsync(db, s.CompanyId)).Count);

        var over = await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, lines, Accounts(s), "BK-77001", "Checkout");
        Assert.NotNull(over);
        Assert.Contains(BookingDepositDrawdown.RuleCode, over);
        Assert.Contains("#77001", over);
        // ตัดพอดี 1,000 ด้วยรูปใหม่ ⇒ ผ่าน · pattern ของบริษัทอื่นไม่รั่วมาบริษัทนี้ (tenant)
        Assert.Null(await BookingDepositLedger.DrawdownProblemAsync(db, s.CompanyId, DrawLines(s, 1_000m), Accounts(s), "BK-77001", "Checkout"));
        var other = await SeedAsync(db, "BK-77001");
        Assert.Equal(2, (await BookingDepositLedger.PatternsAsync(db, other.CompanyId)).Count);
        Assert.Null(await BookingDepositLedger.DrawdownProblemAsync(db, other.CompanyId, DrawLines(other, 1_500m), Accounts(other), "BK-77001", "Checkout"));
    }
}
