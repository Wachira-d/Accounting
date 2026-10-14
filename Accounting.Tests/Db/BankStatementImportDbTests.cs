using System.Text;
using Accounting.Controllers.V1;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Import;
using Accounting.Services.Import.Targets;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// **ตัวเขียน statement ธนาคารตัวเดียว** บน PostgreSQL จริง (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §1 แถว 6–9)
///
/// <para>ที่มา: ตัวเขียน 4 ตัว (หน้า "ธนาคาร" · open-banking · นำเข้าทั่วไป · API v1) ความหมายยอดคงเหลือ 2 แบบ · open-banking ตอบ "OFX ใหม่ N รายการ"
/// โดยไม่บันทึกอะไร (HTTP 200 โกหก) · CSV ภาษาไทย TIS-620 อ่านเป็นอักขระเสีย · statement ไม่มียอดคงเหลือนำเข้าไม่ได้เลย</para>
/// <para>ล็อกทั้งสองทิศ: ไฟล์ที่เคยพังลงได้ถูกต้อง · ไฟล์เดิมนำเข้าซ้ำไม่ลงซ้ำ · ขัดแย้งในโหมดถามผู้ใช้ไม่บันทึกอะไรเลย (รวม tracker) ·
/// API v1 กันซ้ำด้วย ExternalId อย่างเดียวเหมือนเดิม · statement เก่าไม่ย้อนยอดตามธนาคาร · ย้อนการนำเข้าบล็อกรายการที่จับคู่แล้ว</para>
/// </summary>
[Trait("Category", "Db")]
public class BankStatementImportDbTests
{
    private readonly ITestOutputHelper _out;
    public BankStatementImportDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid AccountId)> SeedAsync(AccountingDbContext db, decimal currentBalance = 0m)
    {
        var company = new Company { Name = "บริษัททดสอบ statement จำกัด", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var account = new BankAccount
        {
            CompanyId = company.Id, AccountName = "กสิกร ออมทรัพย์", BankName = "กสิกรไทย", AccountNumber = "123-4-56789-0",
            CurrentBalance = currentBalance,
        };
        db.BankAccounts.Add(account);
        await db.SaveChangesAsync();
        return (company.Id, account.Id);
    }

    private static Task<List<BankTransaction>> TxnsAsync(AccountingDbContext db, Guid cid, Guid accountId)
        => db.BankTransactions.AsNoTracking().Where(t => t.CompanyId == cid && t.BankAccountId == accountId)
            .OrderBy(t => t.TransactionDate).ToListAsync();

    private static ImportBankStatementRequest CsvRequest(Guid accountId, byte[] bytes, string fileName = "kbank.csv",
        bool force = false, bool skipConflicts = false)
        => new(accountId, "CSV", Convert.ToBase64String(bytes), force, fileName, null, skipConflicts);

    // ═══════════════════════ หน้า "ธนาคาร": TIS-620 · ซ้ำ · ไม่มียอด ═══════════════════════

    [Fact]
    public async Task Csv_TIS620_แบบKBank_นำเข้าได้_ไทยไม่เสีย_ยอดตามธนาคาร_และนำเข้าซ้ำไม่ลงซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, acc) = await SeedAsync(db);
        var svc = new BankService(db);
        var bytes = BankStatementParsingTests.Tis620(BankStatementParsingTests.KBankCsv);

        var first = await svc.ImportBankStatementAsync(cid, CsvRequest(acc, bytes));

        Assert.Equal(3, first.Imported);
        Assert.Equal(0, first.Conflicts);
        Assert.DoesNotContain(first.Warnings!, w => w.Contains("ไม่ต่อเนื่อง"));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await TxnsAsync(check, cid, acc);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.DoesNotContain('\uFFFD', r.Description ?? ""));
            Assert.Equal(new DateTime(2026, 8, 1), rows[0].TransactionDate.Date);       // 01/08/2569 (พ.ศ.) → ค.ศ. 2026
            Assert.Equal(BankTransactionType.Deposit, rows[0].TransactionType);
            Assert.Equal(1500m, rows[0].Amount);
            Assert.Equal(11500m, rows[0].BalanceAfter);
            Assert.StartsWith("รับโอนเงิน", rows[0].Description);
            Assert.Equal(BankTransactionType.Withdrawal, rows[1].TransactionType);
            var account = await check.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == acc && a.CompanyId == cid);
            Assert.Equal(9485m, account.StatementBalance);
            Assert.Equal(9485m, account.CurrentBalance);
            Assert.Equal(new DateTime(2026, 8, 15), account.StatementBalanceDate!.Value.Date);
        }

        // ไฟล์เดิมซ้ำ — ไม่ลงซ้ำ ยอดไม่เดินซ้ำ
        var again = await new BankService(DbTestDatabase.TryCreateContext()!).ImportBankStatementAsync(cid, CsvRequest(acc, bytes));
        Assert.Equal(0, again.Imported);
        Assert.Equal(3, again.Skipped);
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(3, (await TxnsAsync(check2, cid, acc)).Count);
    }

    [Fact]
    public async Task Csv_UTF8_ไฟล์เดียวกัน_ได้ผลเท่ากับTIS620()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var res = await new BankService(db).ImportBankStatementAsync(cid,
            CsvRequest(acc, Encoding.UTF8.GetBytes(BankStatementParsingTests.KBankCsv)));
        Assert.Equal(3, res.Imported);
        Assert.DoesNotContain(res.Warnings!, w => w.Contains("TIS-620"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await TxnsAsync(check, cid, acc);
        Assert.Contains(rows, r => r.Description != null && r.Description.Contains("ร้านรวมมิตร"));
    }

    [Fact]
    public async Task Csv_ไม่มียอดคงเหลือ_นำเข้าได้พร้อมคำเตือน_ไม่แตะยอดบนการ์ด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db, currentBalance: 777m);

        var res = await new BankService(db).ImportBankStatementAsync(cid,
            CsvRequest(acc, Encoding.UTF8.GetBytes(BankStatementParsingTests.NoBalanceCsv), "no-balance.csv"));

        Assert.Equal(2, res.Imported);
        Assert.Contains(res.Warnings!, w => w.Contains("ไม่มีคอลัมน์ยอดคงเหลือ"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var account = await check.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == acc && a.CompanyId == cid);
        Assert.Equal(777m, account.CurrentBalance);      // ไม่บวกยอดเอง (ความหมายเดิมของทางนำเข้าทั่วไป) และไม่ทับด้วยศูนย์
        Assert.Null(account.StatementBalance);
        Assert.Equal(2, (await TxnsAsync(check, cid, acc)).Count);
    }

    [Fact]
    public async Task Qif_หน้าธนาคาร_ล้มดังข้อความคำตัดสิน151_ไม่บันทึกอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var qif = Encoding.ASCII.GetBytes("!Type:Bank\nD08/01/2026\nT1500.00\n^\n");
        var ex = await Assert.ThrowsAsync<ImportFormatException>(() =>
            new BankService(db).ImportBankStatementAsync(cid, new ImportBankStatementRequest(acc, "QIF", Convert.ToBase64String(qif))));
        Assert.Equal(Accounting.Services.Helpers.BankStatementFileReader.QifMessage, ex.Message);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await TxnsAsync(check, cid, acc));
    }

    // ═══════════════════════ ขัดแย้ง: ถาม · เก็บเดิม · ใช้ใหม่ ═══════════════════════

    [Fact]
    public async Task ขัดแย้งโหมดถาม_ไม่บันทึกอะไรเลยรวมtracker_เก็บเดิมลงเฉพาะรายการใหม่_ใช้ใหม่ทับคำอธิบาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var svc = new BankService(db);
        var day = new DateTime(2026, 8, 5);
        await svc.ImportParsedStatementRowsAsync(cid, acc, new[] { new BankStatementLineInput(day, "โอนเข้า ลูกค้า ก", 0m, 500m, 1500m) });

        var file = new[]
        {
            new BankStatementLineInput(day, "PROMPTPAY CUSTOMER A", 0m, 500m, 1500m),     // วัน+ยอด+ทิศตรง คำอธิบายต่าง = ขัดแย้ง
            new BankStatementLineInput(day.AddDays(1), "ค่าธรรมเนียม", 10m, 0m, 1490m),
        };
        var report = await svc.ImportParsedStatementRowsAsync(cid, acc, file);
        Assert.True(report.RolledBack);
        Assert.Equal(1, report.Conflicts);
        await db.SaveChangesAsync();   // tracker ต้องไม่มีแถวค้างที่ "ย้อนแล้ว"
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Single(await TxnsAsync(check, cid, acc));

        var keep = await new BankService(DbTestDatabase.TryCreateContext()!).ImportParsedStatementRowsAsync(cid, acc, file,
            new BankStatementWriteOptions(OnConflict: BankStatementConflictPolicy.KeepExisting));
        Assert.False(keep.RolledBack);
        Assert.Equal(1, keep.Imported);
        Assert.Equal(BankStatementLineOutcome.Conflict, keep.Lines[0].Outcome);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await TxnsAsync(check, cid, acc);
            Assert.Equal(2, rows.Count);
            Assert.Equal("โอนเข้า ลูกค้า ก", rows[0].Description);
        }

        var overwrite = await new BankService(DbTestDatabase.TryCreateContext()!).ImportParsedStatementRowsAsync(cid, acc, file,
            new BankStatementWriteOptions(OnConflict: BankStatementConflictPolicy.Overwrite));
        Assert.Equal(BankStatementLineOutcome.Updated, overwrite.Lines[0].Outcome);
        Assert.Equal(BankStatementLineOutcome.Duplicate, overwrite.Lines[1].Outcome);
        using var check3 = DbTestDatabase.TryCreateContext()!;
        var after = await TxnsAsync(check3, cid, acc);
        Assert.Equal(2, after.Count);
        Assert.Equal("PROMPTPAY CUSTOMER A", after[0].Description);
    }

    [Fact]
    public async Task Statementเก่ากว่ายอดที่มี_ไม่ย้อนยอดตามธนาคาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var svc = new BankService(db);
        await svc.ImportParsedStatementRowsAsync(cid, acc, new[] { new BankStatementLineInput(new DateTime(2026, 8, 31), "ดอกเบี้ย", 0m, 5m, 9485m) });
        var old = await svc.ImportParsedStatementRowsAsync(cid, acc, new[] { new BankStatementLineInput(new DateTime(2026, 7, 10), "ฝาก", 0m, 100m, 7000m) });

        Assert.Equal(1, old.Imported);
        Assert.Contains(old.Warnings, w => w.Contains("เก่ากว่า"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var account = await check.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == acc && a.CompanyId == cid);
        Assert.Equal(9485m, account.StatementBalance);
        Assert.Equal(new DateTime(2026, 8, 31), account.StatementBalanceDate!.Value.Date);
    }

    [Fact]
    public async Task เก็บไฟล์ต้นทาง_เป็นไฟล์แนบของบัญชี_เมื่อมีรายการลง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var files = new RecordingFiles();
        var svc = new BankService(db, files: files);
        var user = Guid.NewGuid();
        var src = new BankStatementSourceFile("kbank.csv", "text/csv", new byte[] { 1, 2, 3 });

        var res = await svc.ImportParsedStatementRowsAsync(cid, acc,
            new[] { new BankStatementLineInput(new DateTime(2026, 8, 1), "ฝาก", 0m, 100m, 100m) },
            new BankStatementWriteOptions(SourceFile: src, UploadedByUserId: user));
        Assert.NotNull(res.SourceFileAttachmentId);
        var call = Assert.Single(files.Calls);
        Assert.Equal((cid, "BankStatement", acc, user), (call.CompanyId, call.EntityType, call.EntityId, call.UserId));

        // ทิศตรงข้าม: ไม่มีอะไรลง (ซ้ำทั้งไฟล์) ⇒ ไม่เก็บไฟล์ซ้ำ
        var again = await svc.ImportParsedStatementRowsAsync(cid, acc,
            new[] { new BankStatementLineInput(new DateTime(2026, 8, 1), "ฝาก", 0m, 100m, 100m) },
            new BankStatementWriteOptions(SourceFile: src, UploadedByUserId: user));
        Assert.Null(again.SourceFileAttachmentId);
        Assert.Single(files.Calls);
    }

    // ═══════════════════════ open-banking: OFX ลงจริง ═══════════════════════

    [Fact]
    public async Task OpenBanking_OFX_บันทึกรายการจริง_ยอดปิดเป็นยอดตามธนาคาร_FITIDกันซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var ob = new OpenBankingService(db, new ConfigurationBuilder().Build(), null!, new BankService(db));
        var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(BankStatementParsingTests.Ofx1));

        var first = await ob.ImportFileAsync(cid, acc, "OFX", b64);

        Assert.Equal(3, first.TotalTransactions);
        Assert.Equal(3, first.NewTransactions);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await TxnsAsync(check, cid, acc);
            Assert.Equal(3, rows.Count);                                            // เดิม: 0 แถว แต่ตอบว่า "ใหม่ 3"
            Assert.Equal(new[] { "KB20260801001", "KB20260802001", "KB20260815001" }, rows.Select(r => r.Reference).ToArray());
            Assert.Equal(new[] { 11500m, 9500m, 9485m }, rows.Select(r => r.BalanceAfter).ToArray());   // ถอยจาก LEDGERBAL
            var account = await check.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == acc && a.CompanyId == cid);
            Assert.Equal(9485m, account.CurrentBalance);
            Assert.Equal(new DateTime(2026, 8, 31), account.StatementBalanceDate!.Value.Date);
        }

        // ไฟล์เดิมซ้ำ — FITID ตรง = ข้ามทั้งหมด
        var again = await new OpenBankingService(DbTestDatabase.TryCreateContext()!, new ConfigurationBuilder().Build(), null!)
            .ImportFileAsync(cid, acc, "OFX", b64);
        Assert.Equal(0, again.NewTransactions);
        Assert.Equal(3, again.DuplicateSkipped);
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(3, (await TxnsAsync(check2, cid, acc)).Count);
    }

    [Fact]
    public async Task OpenBanking_QIF_ล้มดัง_ไม่ใช่ตอบว่าใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var ob = new OpenBankingService(db, new ConfigurationBuilder().Build(), null!, new BankService(db));
        var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("!Type:Bank\nD08/01/2026\nT1500.00\n^\nD08/02/2026\nT-20.00\n^\n"));

        var ex = await Assert.ThrowsAsync<ImportFormatException>(() => ob.ImportFileAsync(cid, acc, "QIF", b64));
        Assert.Equal("qif-unsupported", ex.Code);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await TxnsAsync(check, cid, acc));
        Assert.False(await check.Set<BankFeedImport>().AnyAsync(i => i.CompanyId == cid));
    }

    // ═══════════════════════ API v1: ความหมายเดิม ═══════════════════════

    [Fact]
    public async Task ApiV1_กันซ้ำด้วยExternalIdอย่างเดียว_บรรทัดไม่มีExternalIdลงทุกครั้ง_ไม่แตะยอด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db, currentBalance: 5000m);
        var day = new DateTime(2026, 9, 1);
        var req = new List<BankV1Controller.StatementLineRequest>
        {
            new(day, 1200m, "In", "รับโอน", "REF-1", "ลูกค้า ก", "EXT-001"),
            new(day, -300m, "Out", "ค่าส่งของ", null, null, null),
        };

        var first = await new BankService(db).ImportParsedStatementRowsAsync(cid, acc,
            BankV1Controller.ToStatementLines(req), BankV1Controller.StatementWriteOptions());
        Assert.Equal(2, first.Imported);

        var second = await new BankService(DbTestDatabase.TryCreateContext()!).ImportParsedStatementRowsAsync(cid, acc,
            BankV1Controller.ToStatementLines(req), BankV1Controller.StatementWriteOptions());
        Assert.Equal(1, second.Imported);   // บรรทัดไม่มี ExternalId = ลงอีก (ความหมายเดิมของ v1 ที่ลูกค้าพึ่ง)
        Assert.Equal(1, second.Skipped);    // EXT-001 ซ้ำ = ข้าม (และไม่ถูกคิดเงินรอบสอง)
        Assert.Equal(BankStatementLineOutcome.Duplicate, second.Lines[0].Outcome);

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await TxnsAsync(check, cid, acc);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.Reference == "EXT-001");
        Assert.All(rows, r => Assert.Equal("api:v1", r.CreatedBy));
        Assert.Contains(rows, r => r.Payee == "ลูกค้า ก");
        var account = await check.BankAccounts.AsNoTracking().SingleAsync(a => a.Id == acc && a.CompanyId == cid);
        Assert.Equal(5000m, account.CurrentBalance);
        Assert.Null(account.StatementBalance);
    }

    // ═══════════════════════ ศูนย์นำเข้า: ตรวจ · ลง · ย้อน ═══════════════════════

    [Fact]
    public async Task ศูนย์นำเข้า_ตรวจซ้ำแนะนำข้าม_ลงผ่านตัวเขียน_ย้อนได้เฉพาะที่ยังไม่จับคู่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, acc) = await SeedAsync(db);
        var bank = new BankService(db);
        await bank.ImportParsedStatementRowsAsync(cid, acc, new[] { new BankStatementLineInput(new DateTime(2026, 8, 1), "รับโอนเงิน", 0m, 1500m, 11500m) });

        var target = new BankStatementImportTarget(db, bank);
        var ctx = new ImportTargetContext(cid, Guid.NewGuid(), "banktransactions", new ImportActor(Guid.NewGuid(), "ผู้ทดสอบ"),
            new ImportCommitOptions(), BatchOptions: new Dictionary<string, string?> { ["bankAccountId"] = acc.ToString() });
        ImportTargetRow Row(int no, string date, string desc, string? debit, string? credit, string? amount = null) => new(Guid.NewGuid(), no,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            { ["date"] = date, ["description"] = desc, ["debit"] = debit, ["credit"] = credit, ["amount"] = amount },
            ImportRowAction.Create);
        var rows = new[]
        {
            Row(1, "2026-08-01", "รับโอนเงิน", null, "1500"),                     // ซ้ำของเดิมตรงตัว
            Row(2, "2026-08-02", "ชำระค่าสินค้า", null, null, "2,000.00 DR"),     // ช่องเดียวแบบ DR = เงินออก
            Row(3, "2026-08-03", "ค่าธรรมเนียม", "15", null),
        };

        var v = await target.ValidateAsync(ctx, rows, CancellationToken.None);
        Assert.Equal(ImportRowAction.Skip, v[0].Action);
        Assert.Contains(v[0].Issues, i => i.Code == "duplicate-existing");
        Assert.Null(v[1].Action);
        Assert.DoesNotContain(v.SelectMany(x => x.Issues), i => i.Severity == ImportIssueSeverity.Error);
        // บัญชียังไม่ผูกผัง = คำเตือนพร้อมทางไปต่อ (ไม่บล็อก) ครั้งเดียวต่อบัญชี ไม่ซ้ำทุกแถว
        Assert.Contains(v[0].Issues, i => i.Code == "bank-account-no-gl" && i.Severity == ImportIssueSeverity.Warning);
        Assert.DoesNotContain(v[1].Issues, i => i.Code == "bank-account-no-gl");

        var toCommit = rows.Zip(v).Select(x => x.First with { Action = x.Second.Action ?? x.First.Action }).ToList();
        var committed = await target.CommitGroupAsync(ctx, toCommit, CancellationToken.None);
        Assert.True(committed[0].Skipped);
        Assert.True(committed[1].Success && committed[1].EntityId != null);
        Assert.True(committed[2].Success && committed[2].EntityId != null);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var dr = await check.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == committed[1].EntityId && t.CompanyId == cid);
            Assert.Equal(BankTransactionType.Withdrawal, dr.TransactionType);
            Assert.Equal(2000m, dr.Amount);
        }

        // รายการที่ 3 ถูกกระทบยอดแล้ว ⇒ ย้อนไม่ได้ + ทางไปต่อ · รายการที่ 2 ย้อนได้
        using (var mark = DbTestDatabase.TryCreateContext()!)
        {
            var t3 = await mark.BankTransactions.SingleAsync(t => t.Id == committed[2].EntityId && t.CompanyId == cid);
            t3.ReconciliationStatus = ReconciliationStatus.Matched;
            await mark.SaveChangesAsync();
        }
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var target2 = new BankStatementImportTarget(db2, new BankService(db2));
        var done = new[]
        {
            new ImportCommittedRow(toCommit[1].RowId, 2, "BankTransaction", committed[1].EntityId!.Value, ImportRowAction.Create),
            new ImportCommittedRow(toCommit[2].RowId, 3, "BankTransaction", committed[2].EntityId!.Value, ImportRowAction.Create),
        };
        var checks = await target2.CanUndoAsync(ctx, done, CancellationToken.None);
        Assert.True(checks[0].CanUndo);
        Assert.False(checks[1].CanUndo);
        Assert.False(string.IsNullOrWhiteSpace(checks[1].WayForward));

        var undone = await target2.UndoAsync(ctx, done, "ทดสอบย้อน", CancellationToken.None);
        Assert.True(undone[0].Success);
        Assert.False(undone[1].Success);
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check2.BankTransactions.AnyAsync(t => t.Id == committed[1].EntityId && t.CompanyId == cid));
        Assert.True(await check2.BankTransactions.AnyAsync(t => t.Id == committed[2].EntityId && t.CompanyId == cid));
    }

    [Fact]
    public async Task ศูนย์นำเข้า_บัญชีที่เลือกไม่ใช่ของบริษัทนี้_บล็อกพร้อมทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var (_, otherAcc) = await SeedAsync(db);
        var target = new BankStatementImportTarget(db, new BankService(db));
        var ctx = new ImportTargetContext(cid, Guid.NewGuid(), "banktransactions", new ImportActor(Guid.NewGuid(), "ผู้ทดสอบ"),
            new ImportCommitOptions(), BatchOptions: new Dictionary<string, string?> { ["bankAccountId"] = otherAcc.ToString() });
        var row = new ImportTargetRow(Guid.NewGuid(), 1,
            new Dictionary<string, string?> { ["date"] = "2026-08-01", ["credit"] = "100" }, ImportRowAction.Create);

        var v = await target.ValidateAsync(ctx, new[] { row }, CancellationToken.None);
        var issue = Assert.Single(v[0].Issues, i => i.Severity == ImportIssueSeverity.Error);
        Assert.Equal("bank-account-not-found", issue.Code);
        Assert.Contains("เลือกบัญชีใหม่", issue.Message);

        var committed = await target.CommitGroupAsync(ctx, new[] { row }, CancellationToken.None);
        Assert.False(committed[0].Success);
        Assert.False(await db.BankTransactions.AnyAsync(t => t.BankAccountId == otherAcc && t.CompanyId != cid));
    }

    /// <summary>ระบบไฟล์แนบปลอม — จดการเรียก (ล็อกว่าไฟล์ต้นทางถูกส่งไปเก็บด้วยชนิด/เจ้าของ/ผู้อัปโหลดที่ถูก)</summary>
    private sealed class RecordingFiles : IFileAttachmentService
    {
        public List<(Guid CompanyId, string EntityType, Guid EntityId, Guid UserId)> Calls { get; } = new();

        public Task<FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId,
            string originalFileName, string contentType, byte[] content, Guid uploadedByUserId)
        {
            Calls.Add((companyId, entityType, entityId, uploadedByUserId));
            return Task.FromResult(new FileAttachmentResponse(Guid.NewGuid(), originalFileName, originalFileName, contentType,
                content.LongLength, "/dev/null", null, entityType, entityId, "ผู้ทดสอบ", DateTime.UtcNow));
        }

        public Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId, string fileName,
            string originalFileName, string contentType, long fileSize, string storagePath, Guid uploadedByUserId)
            => throw new NotSupportedException();
        public Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId)
            => throw new NotSupportedException();
        public Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId) => throw new NotSupportedException();
        public Task<byte[]?> ReadBytesAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false) => throw new NotSupportedException();
    }
}
