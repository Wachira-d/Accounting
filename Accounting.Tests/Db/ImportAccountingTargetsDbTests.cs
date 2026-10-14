using System.Reflection;
using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Migration;
using Accounting.Services.Import;
using Accounting.Services.Import.Targets;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม D — ปลายทางบัญชีของศูนย์นำเข้า + migration wizard บน PostgreSQL จริง (CI job db-test)
/// <list type="bullet">
/// <item>งบทดลองยกมา: ลงตัว = ลง · ไม่ลงตัว = บล็อก + ทางไปต่อ (ผู้ใช้เลือกบัญชีผลต่าง) แล้วลงได้ · คำเตือนรับทราบแล้วลงได้ / ไม่รับทราบ = ปฏิเสธ ·
/// ย้อนคืนยอดเดิมที่ถูกทับ (เดิมคืนไม่ได้)</item>
/// <item>เอกสาร: ร่าง + เลข DRAFT · เลขเดิม → Reference · ยอดคำนวณโดย DocumentService (ไม่ใช่ไฟล์)</item>
/// <item>รับ/จ่ายชำระ: ผ่าน CreatePaymentAsync ⇒ มี JE (เดิมแก้ PaidAmount ตรง GL ไม่ขยับ)</item>
/// <item>สินทรัพย์: ที่ดินไม่คิดค่าเสื่อม (ทิศตรงข้าม: คอมพิวเตอร์มีตารางค่าเสื่อม)</item>
/// <item>โปรแกรมอื่น: express_products ไปสินค้า (ไม่ใช่ผู้ติดต่อ) · Express TIS-620 อ่านภาษาไทยถูก + ชนิดผู้ติดต่อจากเลขภาษี + บทบาทจากหัวตาราง</item>
/// <item>กระทบยอดหลังย้ายระบบ: ตัวเลขลูกหนี้/สต็อก/สินทรัพย์ตรงกับที่ seed</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class ImportAccountingTargetsDbTests
{
    private readonly ITestOutputHelper _out;
    public ImportAccountingTargetsDbTests(ITestOutputHelper output) => _out = output;

    // ───────────────────────── seed ─────────────────────────

    private sealed record Seed(Guid CompanyId, Guid PeriodId, Dictionary<string, Guid> Accounts, Guid UserId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool withPeriod = true)
    {
        var company = new Company { Name = "บริษัททดสอบย้ายระบบ " + tag, TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var accounts = new Dictionary<string, Guid>();
        foreach (var (code, name, type) in new[]
        {
            ("11110", "เงินสด", AccountType.Asset),
            ("11310", "ลูกหนี้การค้า", AccountType.Asset),
            ("11500", "สินค้าคงเหลือ", AccountType.Asset),
            ("12220", "คอมพิวเตอร์", AccountType.Asset),
            ("18320", "ค่าเสื่อมราคาสะสม-คอมพิวเตอร์", AccountType.Asset),
            ("21210", "เจ้าหนี้การค้า", AccountType.Liability),
            ("31100", "ทุนจดทะเบียน", AccountType.Equity),
            ("39900", "ผลต่างจากการยกยอด", AccountType.Equity),
            ("41100", "รายได้จากการขาย", AccountType.Revenue),
            ("51100", "ค่าบริการ", AccountType.Expense),
        })
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            accounts[code] = a.Id;
        }
        var periodId = Guid.Empty;
        if (withPeriod)
        {
            var p = new FiscalPeriod
            {
                CompanyId = company.Id, Name = "2569/01", Year = 2026, Month = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31), Status = FiscalPeriodStatus.Open,
            };
            db.FiscalPeriods.Add(p);
            periodId = p.Id;
        }
        await db.SaveChangesAsync();
        return new Seed(company.Id, periodId, accounts, Guid.NewGuid());
    }

    private static ImportTargetRow Row(int no, params (string Key, string? Value)[] values)
        => new(Guid.NewGuid(), no, values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase), ImportRowAction.Create);

    private static ImportTargetContext Ctx(Seed s, string entity, IReadOnlyList<string>? ack = null, Dictionary<string, string?>? options = null)
        => new(s.CompanyId, Guid.NewGuid(), entity, new ImportActor(s.UserId, "ผู้ทดสอบ"), new ImportCommitOptions(AcknowledgedWarningCodes: ack),
            BatchOptions: options);

    private static MigrationWizardService Wizard(AccountingDbContext db)
        => new(db, NullLogger<MigrationWizardService>.Instance, new ImportAccountMatcher(db));

    private static OpeningTrialBalanceImportTarget TbTarget(AccountingDbContext db)
        => new(db, Wizard(db), new ImportAccountMatcher(db));

    /// <summary>งบทดลองจากโปรแกรมเดิม (รหัส 4 หลัก · ชื่อตรงผัง) — ยอดเครดิตทุนปรับได้เพื่อทำให้ไม่ลงตัว</summary>
    private static List<ImportTargetRow> TbRows(decimal capital = 25_000m) => new()
    {
        Row(1, ("AccountCode", "1110"), ("AccountName", "เงินสด"), ("Debit", "10000.00"), ("Credit", null)),
        Row(2, ("AccountCode", "1130"), ("AccountName", "ลูกหนี้การค้า"), ("Debit", "45000.00")),
        Row(3, ("AccountCode", "2110"), ("AccountName", "เจ้าหนี้การค้า"), ("Credit", "30000.00")),
        Row(4, ("AccountCode", "3110"), ("AccountName", "ทุนจดทะเบียน"), ("Balance", (-capital).ToString(System.Globalization.CultureInfo.InvariantCulture))),
    };

    // ───────────────────────── งบทดลองยกมา ─────────────────────────

    [Fact]
    public async Task งบทดลองลงตัว_ระบบจับคู่บัญชีให้_ลงยอดยกมาผ่าน_wizard()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db, "TB-OK-" + Guid.NewGuid().ToString("N")[..6]);
        var ctx = Ctx(s, OpeningTrialBalanceImportTarget.Key, options: new() { [OpeningTrialBalanceImportTarget.OptionPeriodId] = s.PeriodId.ToString() });
        var rows = TbRows();

        var v = await TbTarget(db).ValidateAsync(ctx, rows, CancellationToken.None);
        Assert.All(v, x => Assert.DoesNotContain(x.Issues, i => i.Severity == ImportIssueSeverity.Error));
        Assert.Equal(s.Accounts["11110"], v[0].MatchedEntityId);       // ชื่อตรงผัง ⇒ ระบบเลือกให้
        Assert.Equal(s.Accounts["21210"], v[2].MatchedEntityId);

        var withMatch = rows.Select((r, i) => r with { MatchedEntityId = v[i].MatchedEntityId }).ToList();
        var res = await TbTarget(db).CommitGroupAsync(ctx, withMatch, CancellationToken.None);
        Assert.All(res, r => Assert.True(r.Success, r.Error));
        Assert.Single(res.Select(r => r.EntityId).Distinct());

        using var check = DbTestDatabase.TryCreateContext()!;
        var ob = await check.OpeningBalances.AsNoTracking().Where(o => o.CompanyId == s.CompanyId).ToListAsync();
        Assert.Equal(4, ob.Count);
        Assert.Equal(55_000m, ob.Sum(o => o.OpeningDebit));
        Assert.Equal(55_000m, ob.Sum(o => o.OpeningCredit));
        Assert.Equal(25_000m, ob.Single(o => o.AccountId == s.Accounts["31100"]).OpeningCredit);
    }

    [Fact]
    public async Task งบทดลองไม่ลงตัว_บล็อกพร้อมทางไปต่อ_เลือกบัญชีผลต่างแล้วลงได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "TB-DIFF-" + Guid.NewGuid().ToString("N")[..6]);
        var opts = new Dictionary<string, string?> { [OpeningTrialBalanceImportTarget.OptionPeriodId] = s.PeriodId.ToString() };
        var rows = TbRows(capital: 24_000m);                              // ต่าง 1,000

        var v = await TbTarget(db).ValidateAsync(Ctx(s, OpeningTrialBalanceImportTarget.Key, options: opts), rows, CancellationToken.None);
        Assert.All(v, x => Assert.Contains(x.Issues, i => i.Code == OpeningTrialBalanceRules.Unbalanced && i.Severity == ImportIssueSeverity.Error
            && i.Message.Contains("บัญชีผลต่างยกมา")));

        var matched = rows.Select((r, i) => r with { MatchedEntityId = v[i].MatchedEntityId }).ToList();
        var blocked = await TbTarget(db).CommitGroupAsync(Ctx(s, OpeningTrialBalanceImportTarget.Key, options: opts), matched, CancellationToken.None);
        Assert.All(blocked, r => Assert.False(r.Success));
        Assert.Contains("ไม่เท่ากับเครดิต", blocked[0].Error);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.False(await c1.OpeningBalances.AnyAsync(o => o.CompanyId == s.CompanyId));

        // ทางไปต่อ — ผู้ใช้เลือกบัญชีผลต่าง
        opts[OpeningTrialBalanceImportTarget.OptionDifferenceAccountId] = s.Accounts["39900"].ToString();
        var v2 = await TbTarget(db).ValidateAsync(Ctx(s, OpeningTrialBalanceImportTarget.Key, options: opts), rows, CancellationToken.None);
        Assert.All(v2, x => Assert.DoesNotContain(x.Issues, i => i.Severity == ImportIssueSeverity.Error));
        Assert.Contains(v2[0].Issues, i => i.Code == OpeningTrialBalanceRules.DifferencePosted);

        var ok = await TbTarget(db).CommitGroupAsync(Ctx(s, OpeningTrialBalanceImportTarget.Key,
            ack: new[] { OpeningTrialBalanceRules.DifferencePosted }, options: opts), matched, CancellationToken.None);
        Assert.All(ok, r => Assert.True(r.Success, r.Error));
        using var check = DbTestDatabase.TryCreateContext()!;
        var diff = await check.OpeningBalances.AsNoTracking().SingleAsync(o => o.CompanyId == s.CompanyId && o.AccountId == s.Accounts["39900"]);
        Assert.Equal(1_000m, diff.OpeningCredit);
    }

    [Fact]
    public async Task คำเตือนรับทราบแล้วลงได้_ไม่รับทราบถูกปฏิเสธ_ย้อนคืนยอดเดิมที่ถูกทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "TB-ACK-" + Guid.NewGuid().ToString("N")[..6]);
        // ยอดยกมาเดิมของเงินสด (ป้อนมือก่อนย้ายระบบ) — ชุดนี้จะทับ ⇒ คำเตือน tb-overwrite-existing
        db.OpeningBalances.Add(new OpeningBalance
        {
            CompanyId = s.CompanyId, FiscalPeriodId = s.PeriodId, AccountId = s.Accounts["11110"],
            OpeningDebit = 999m, OpeningCredit = 0m, Notes = "ป้อนมือ",
        });
        await db.SaveChangesAsync();

        var w = Wizard(db);
        var session = await w.CreateSessionAsync(s.CompanyId, "งบทดลอง 31/12/2568", MigrationType.OpeningBalances, s.PeriodId, s.UserId.ToString());
        var paste = await w.UploadLegacyTextAsync(s.CompanyId, session.Id,
            "รหัส\tชื่อบัญชี\tเดบิต\tเครดิต\n1110\tเงินสด\t10,000.00\t\n3110\tทุนจดทะเบียน\t\t10,000.00\n\tรวม\t10,000.00\t10,000.00",
            s.UserId.ToString());
        Assert.Equal(2, paste.Imported);
        Assert.All(paste.Suggestions, x => Assert.True(x.AutoApplied));

        var v = await w.ValidateSessionAsync(s.CompanyId, session.Id);
        Assert.True(v.IsReady);                                          // คำเตือนไม่บล็อก (คำตัดสินข้อ 150)
        Assert.Contains(v.Warnings, x => x.Code == OpeningTrialBalanceRules.OverwriteExisting);

        var noAck = await Assert.ThrowsAsync<BusinessRuleException>(() => w.CommitAsync(s.CompanyId, session.Id, s.UserId.ToString()));
        Assert.Equal("MIGRATION-TB-ACK", noAck.RuleCode);

        await w.CommitAsync(s.CompanyId, session.Id, s.UserId.ToString(), v.Warnings.Select(x => x.Code).ToList());
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var cash = await c1.OpeningBalances.AsNoTracking().SingleAsync(o => o.CompanyId == s.CompanyId && o.AccountId == s.Accounts["11110"]);
            Assert.Equal(10_000m, cash.OpeningDebit);
            Assert.True(await c1.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.NewValues!.Contains("MIGRATION-TB-COMMIT")));
        }

        using (var w2db = DbTestDatabase.TryCreateContext()!)
            await Wizard(w2db).RollbackAsync(s.CompanyId, session.Id, s.UserId.ToString());
        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.OpeningBalances.AsNoTracking().Where(o => o.CompanyId == s.CompanyId).ToListAsync();
        var restored = Assert.Single(rows);                              // ทุนที่ชุดนี้สร้างถูกลบ · เงินสดคืนยอดเดิม
        Assert.Equal(s.Accounts["11110"], restored.AccountId);
        Assert.Equal(999m, restored.OpeningDebit);
        Assert.Equal("ป้อนมือ", restored.Notes);
    }

    [Fact]
    public async Task จับคู่บัญชีของบริษัทอื่น_ถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var a = await SeedAsync(db, "TB-X1-" + Guid.NewGuid().ToString("N")[..6]);
        var b = await SeedAsync(db, "TB-X2-" + Guid.NewGuid().ToString("N")[..6]);
        var w = Wizard(db);
        var session = await w.CreateSessionAsync(a.CompanyId, "ข้ามบริษัท", MigrationType.OpeningBalances, a.PeriodId, a.UserId.ToString());
        await w.UploadLegacyAccountsAsync(a.CompanyId, session.Id, new() { ("9999", "บัญชีแปลก", 1m, 0m) }, a.UserId.ToString());
        await Assert.ThrowsAsync<BusinessRuleException>(() => w.ApplyMappingsAsync(a.CompanyId, session.Id,
            new Dictionary<string, Guid> { ["9999"] = b.Accounts["11110"] }, a.UserId.ToString()));
    }

    // ───────────────────────── เอกสาร ─────────────────────────

    [Fact]
    public async Task นำเข้าเอกสาร_เป็นร่างเลข_DRAFT_เลขเดิมไป_Reference_ยอดคำนวณโดย_DocumentService()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "DOC-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var customer = new Contact
        {
            CompanyId = s.CompanyId, Name = "บริษัท ลูกค้าเก่า จำกัด", TaxId = "0105556000017", BranchCode = "00000",
            ContactType = ContactType.JuristicPerson, IsCustomer = true,
        };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();

        var docs = DocService(db);
        var target = new DocumentImportTarget(db, new DocumentImportBuilder(db, new FakeResolver(db), new ImportAccountMatcher(db)), docs);
        var date = DateTime.UtcNow.AddHours(7).Date.AddDays(-3).ToString("yyyy-MM-dd");
        var rows = new List<ImportTargetRow>
        {
            Row(1, ("DocumentType", "ใบแจ้งหนี้"), ("DocumentNumber", "IV6801-0099"), ("DocumentDate", date), ("ContactName", "บริษัท ลูกค้าเก่า จำกัด"),
                ("ContactTaxId", "0105556000017"), ("Description", "ค่าบริการทำบัญชี"), ("Quantity", "2"), ("UnitPrice", "1000.00"), ("VatRate", "7")),
            Row(2, ("DocumentType", "ใบแจ้งหนี้"), ("DocumentNumber", "IV6801-0099"), ("DocumentDate", date), ("ContactName", "บริษัท ลูกค้าเก่า จำกัด"),
                ("ContactTaxId", "0105556000017"), ("Description", "ค่าที่ปรึกษา"), ("UnitPrice", "500.00"), ("VatRate", "7"),
                // ยอดรวมในไฟล์ (ผิด) ต้องไม่ถูกใช้ — DocumentService คำนวณเอง
                ("TotalAmount", "99999")),
        };
        var ctx = Ctx(s, DocumentImportTarget.Key);
        var v = await target.ValidateAsync(ctx, rows, CancellationToken.None);
        Assert.Single(v.Select(x => x.GroupKey).Distinct());               // สองแถว = เอกสารเดียว
        Assert.All(v, x => Assert.DoesNotContain(x.Issues, i => i.Severity == ImportIssueSeverity.Error));

        var res = await target.CommitGroupAsync(ctx, rows, CancellationToken.None);
        Assert.All(res, r => Assert.True(r.Success, r.Error));
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == res[0].EntityId && d.CompanyId == s.CompanyId);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        Assert.StartsWith("DRAFT-", doc.DocumentNumber);
        Assert.Equal("IV6801-0099", doc.Reference);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(2_500m, doc.SubTotal);
        Assert.Equal(175m, doc.VatAmount);
        Assert.Equal(2_675m, doc.TotalAmount);
        Assert.Equal("Import", doc.OriginModule);

        // นำเข้าซ้ำ ⇒ คำเตือน + ตั้งเป็น "ข้าม" (ไม่สร้างใบที่สองเงียบ ๆ) · ย้อนได้เพราะยังเป็นร่าง
        var again = await target.ValidateAsync(ctx, rows, CancellationToken.None);
        Assert.All(again, x => Assert.Equal(ImportRowAction.Skip, x.Action));
        var undo = await target.CanUndoAsync(ctx, new[] { new ImportCommittedRow(rows[0].RowId, 1, DocumentImportTarget.CommittedEntity, doc.Id, ImportRowAction.Create) },
            CancellationToken.None);
        Assert.True(Assert.Single(undo).CanUndo);
    }

    [Fact]
    public async Task เอกสารชนิดที่ร่างไม่ได้_และผู้ติดต่อไม่พบ_บล็อกพร้อมทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "DOC-NO-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var target = new DocumentImportTarget(db, new DocumentImportBuilder(db, new FakeResolver(db), new ImportAccountMatcher(db)), DocService(db));
        var ctx = Ctx(s, DocumentImportTarget.Key);
        var pv = Row(1, ("DocumentType", "ใบสำคัญจ่าย"), ("DocumentNumber", "PV-1"), ("DocumentDate", "2026-01-10"), ("ContactName", "ร้านไม่มีในระบบ"),
            ("Description", "ค่าน้ำ"), ("UnitPrice", "100"));
        var v = await target.ValidateAsync(ctx, new[] { pv }, CancellationToken.None);
        Assert.Contains(v[0].Issues, i => i.Code == "doc-type-unsupported" && i.Message.Contains("รับ/จ่ายชำระ"));

        var inv = pv with { Values = new Dictionary<string, string?>(pv.Values, StringComparer.OrdinalIgnoreCase) { ["DocumentType"] = "ใบแจ้งหนี้ซื้อ" } };
        var res = await target.CommitGroupAsync(ctx, new[] { inv }, CancellationToken.None);
        Assert.False(res[0].Success);
        Assert.Contains("นำเข้าผู้ติดต่อก่อน", res[0].Error);
        Assert.False(await db.Documents.AnyAsync(d => d.CompanyId == s.CompanyId));
    }

    // ───────────────────────── รับ/จ่ายชำระ ─────────────────────────

    [Fact]
    public async Task นำเข้าการจ่ายชำระ_ผ่าน_CreatePaymentAsync_มี_JE()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "PAY-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var supplier = new Contact { CompanyId = s.CompanyId, Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0105556000017", ContactType = ContactType.JuristicPerson, IsSupplier = true };
        db.Contacts.Add(supplier);
        await db.SaveChangesAsync();
        var pi = new Document
        {
            CompanyId = s.CompanyId, ContactId = supplier.Id, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = "PI-" + s.CompanyId.ToString("N")[..6],
            Reference = "SUP-INV-778", Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-10),
            SubTotal = 5_000m, TotalAmount = 5_000m, BalanceDue = 5_000m, Currency = "THB", WhtCertAutoIssue = false,
        };
        pi.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 5_000m, Amount = 5_000m });
        db.Documents.Add(pi);
        await db.SaveChangesAsync();

        var target = new PaymentImportTarget(db, DocService(db));
        var ctx = Ctx(s, PaymentImportTarget.Key);
        var row = Row(1, ("DocumentNumber", "SUP-INV-778"),                      // เลขเดิม (Reference) ก็หาเจอ
            ("PaymentDate", DateTime.UtcNow.AddHours(7).Date.AddDays(-1).ToString("yyyy-MM-dd")), ("Amount", "2000.00"), ("PaymentMethod", "โอน"));
        var v = await target.ValidateAsync(ctx, new[] { row }, CancellationToken.None);
        Assert.DoesNotContain(v[0].Issues, i => i.Severity == ImportIssueSeverity.Error);

        var res = await target.CommitGroupAsync(ctx, new[] { row }, CancellationToken.None);
        var ok = Assert.Single(res);
        Assert.True(ok.Success, ok.Error);
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.Id == ok.EntityId && p.CompanyId == s.CompanyId);
        Assert.Equal(2_000m, pay.Amount);
        Assert.Equal(PaymentMethod.BankTransfer, pay.PaymentMethod);
        var je = await check.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == pay.PaymentNumber);
        Assert.Equal(je.TotalDebit, je.TotalCredit);
        Assert.Contains(je.Lines, l => l.AccountId == s.Accounts["11110"] && l.CreditAmount == 2_000m);
        Assert.Equal(3_000m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == pi.Id)).BalanceDue);

        // เกินยอดค้าง ⇒ บล็อกตั้งแต่หน้าตรวจ (ทิศตรงข้าม)
        var over = Row(2, ("DocumentNumber", pi.DocumentNumber), ("PaymentDate", DateTime.UtcNow.AddHours(7).Date.ToString("yyyy-MM-dd")), ("Amount", "9000"));
        var v2 = await target.ValidateAsync(ctx, new[] { over }, CancellationToken.None);
        Assert.Contains(v2[0].Issues, i => i.Code == "payment-exceeds-balance");
    }

    // ───────────────────────── สินทรัพย์ ─────────────────────────

    [Fact]
    public async Task ที่ดินไม่คิดค่าเสื่อม_คอมพิวเตอร์มีตารางค่าเสื่อม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "FA-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var target = new FixedAssetImportTarget(db, new FixedAssetService(db));
        var ctx = Ctx(s, FixedAssetImportTarget.Key);
        var land = Row(1, ("AssetCode", "LAND-01"), ("Name", "ที่ดินโฉนด 1234"), ("Category", "ที่ดิน"), ("PurchaseDate", "2023-03-01"),
            ("PurchaseCost", "2500000"), ("DepreciationMethod", null));
        var pc = Row(2, ("AssetCode", "PC-01"), ("Name", "คอมพิวเตอร์"), ("Category", "คอมพิวเตอร์"), ("PurchaseDate", "2023-03-01"),
            ("PurchaseCost", "36000"), ("UsefulLifeMonths", "36"));
        var landBad = Row(3, ("AssetCode", "LAND-02"), ("Name", "ที่ดินแปลงสอง"), ("Category", "ที่ดิน"), ("PurchaseDate", "2023-03-01"),
            ("PurchaseCost", "100000"), ("DepreciationMethod", "เส้นตรง"));

        var v = await target.ValidateAsync(ctx, new[] { land, pc, landBad }, CancellationToken.None);
        Assert.Contains(v[0].Issues, i => i.Code == "fa-not-depreciated");
        Assert.DoesNotContain(v[1].Issues, i => i.Severity == ImportIssueSeverity.Error);
        Assert.Contains(v[2].Issues, i => i.Code == "fa-method-invalid" && i.Severity == ImportIssueSeverity.Error);

        var res = await target.CommitGroupAsync(ctx, new[] { land, pc }, CancellationToken.None);
        Assert.All(res, r => Assert.True(r.Success, r.Error));
        using var check = DbTestDatabase.TryCreateContext()!;
        var assets = await check.FixedAssets.AsNoTracking().Where(a => a.CompanyId == s.CompanyId).ToListAsync();
        var l = assets.Single(a => a.AssetCode == "LAND-01");
        Assert.Equal(DepreciationMethod.None, l.DepreciationMethod);
        Assert.False(await check.AssetDepreciations.AnyAsync(d => d.FixedAssetId == l.Id));
        var p = assets.Single(a => a.AssetCode == "PC-01");
        Assert.Equal(DepreciationMethod.StraightLine, p.DepreciationMethod);
        Assert.True(await check.AssetDepreciations.CountAsync(d => d.FixedAssetId == p.Id) > 0);
    }

    // ───────────────────────── โปรแกรมอื่น ─────────────────────────

    private static CompetitorImportCoordinator Coordinator(AccountingDbContext db) => new(new ICompetitorImportAdapter[]
    {
        // ลำดับเดียวกับ Program.cs (ผู้ติดต่อลงทะเบียนก่อน) — เดิมลำดับนี้ + ชื่อไฟล์ ⇒ express_products ไปผู้ติดต่อ
        new ExpressContactsAdapter(db), new PeakContactsAdapter(db), new FlowAccountContactsAdapter(db),
        new ExpressProductsAdapter(db), new PeakProductsAdapter(db), new FlowAccountProductsAdapter(db),
        new ExpressAccountsAdapter(db), new PeakAccountsAdapter(db), new FlowAccountAccountsAdapter(db),
    }, NullLogger<CompetitorImportCoordinator>.Instance);

    [Fact]
    public async Task express_products_csv_ไปสินค้า_ไม่ใช่ผู้ติดต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "EXP-P-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var bytes = Encoding.UTF8.GetBytes("รหัสสินค้า,ชื่อสินค้า,หน่วย,ราคาขาย\nP001,กาแฟคั่วบด 250 กรัม,ถุง,\"1,250.00\"\nP002,ชาเขียว,กล่อง,89\n");
        var table = CompetitorTable.Open("express_products.csv", bytes);
        var det = Coordinator(db).Detect(table);
        Assert.IsType<ExpressProductsAdapter>(det.Adapter);
        Assert.Equal("Products", det.Adapter!.EntityKind);

        var r = await Coordinator(db).ImportAsync(s.CompanyId, table, new ImportOptions(false, false, "test"), CancellationToken.None);
        Assert.Equal(2, r!.RowsImported);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1_250m, (await check.Products.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.Code == "P001")).SellingPrice);
        Assert.False(await check.Contacts.AnyAsync(c => c.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task Express_ผู้ติดต่อ_TIS620_อ่านไทยถูก_ชนิดจากเลขภาษี_บทบาทจากหัวตาราง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "EXP-C-" + Guid.NewGuid().ToString("N")[..6], withPeriod: false);
        var person = ValidTaxId("310050012345");
        var csv = "รหัสลูกค้า\tชื่อลูกค้า\tเลขประจำตัวผู้เสียภาษี\tที่อยู่\n"
                  + "C001\tบริษัท ทดสอบไทย จำกัด\t0105556000017\tกรุงเทพมหานคร\n"
                  + $"C002\tนายสมชาย ใจดี\t{person}\tเชียงใหม่\n"
                  + "C003\tร้านเลขผิด\t1234567890123\tขอนแก่น\n";
        var tis620 = CodePagesEncodingProvider.Instance.GetEncoding(874)!.GetBytes(csv);
        var table = CompetitorTable.Open("ลูกค้า.txt", tis620);
        Assert.Equal("windows-874", table.Encoding);
        Assert.IsType<ExpressContactsAdapter>(Coordinator(db).Detect(table).Adapter);

        var r = await Coordinator(db).ImportAsync(s.CompanyId, table, new ImportOptions(false, false, "test"), CancellationToken.None);
        Assert.Equal(2, r!.RowsImported);
        Assert.Equal(1, r.RowsFailed);                                    // เลขผิด checksum ⇒ ปฏิเสธพร้อมเหตุผล
        Assert.Contains(r.Errors, e => e.Contains("ร้านเลขผิด") && e.Contains("ไม่ถูกต้อง"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var contacts = await check.Contacts.AsNoTracking().Where(c => c.CompanyId == s.CompanyId).ToListAsync();
        var co = contacts.Single(c => c.Name == "บริษัท ทดสอบไทย จำกัด");
        Assert.Equal(ContactType.JuristicPerson, co.ContactType);
        var p = contacts.Single(c => c.Name == "นายสมชาย ใจดี");
        Assert.Equal(ContactType.Individual, p.ContactType);              // เดิมฮาร์ดโค้ด JuristicPerson ทุกแถว
        Assert.All(contacts, c => { Assert.True(c.IsCustomer); Assert.False(c.IsSupplier); });   // หัว "ลูกค้า" ⇒ ลูกค้าเท่านั้น
    }

    // ───────────────────────── กระทบยอด ─────────────────────────

    [Fact]
    public async Task กระทบยอดหลังย้ายระบบ_ตัวเลขลูกหนี้_สต็อก_สินทรัพย์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, "REC-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var (code, dr, cr) in new[] { ("11310", 45_000m, 0m), ("11500", 12_000m, 0m), ("12220", 36_000m, 0m), ("18320", 0m, 10_000m), ("31100", 0m, 83_000m) })
            db.OpeningBalances.Add(new OpeningBalance
            {
                CompanyId = s.CompanyId, FiscalPeriodId = s.PeriodId, AccountId = s.Accounts[code], OpeningDebit = dr, OpeningCredit = cr,
            });
        var customer = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้ายกมา", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = customer.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "OAR-1",
            Status = DocumentStatus.Approved, DocumentDate = new DateTime(2026, 1, 1), SubTotal = 40_000m, TotalAmount = 40_000m, BalanceDue = 40_000m,
            Currency = "THB",
        });
        db.Products.Add(new Product { CompanyId = s.CompanyId, Code = "SKU-1", Name = "สินค้า", Unit = "ชิ้น", TrackStock = true, CurrentStock = 100, CostPrice = 120m });
        db.FixedAssets.Add(new FixedAsset
        {
            CompanyId = s.CompanyId, AssetCode = "PC-9", Name = "คอมพิวเตอร์", PurchaseDate = new DateTime(2024, 1, 1), PurchaseCost = 36_000m,
            AccumulatedDepreciation = 10_000m, NetBookValue = 26_000m, UsefulLifeMonths = 36,
        });
        await db.SaveChangesAsync();

        var recon = new OpeningBalanceReconciliation(db, new SubLedgerReconciliationService(db), new AccountingService(db));
        var report = await recon.ReconcileAsync(s.CompanyId, new DateTime(2026, 1, 31));
        var ar = Assert.Single(report.Lines, l => l.Area == "AR");
        Assert.Equal(40_000m, ar.SubledgerTotal);
        Assert.Equal(45_000m, ar.LedgerTotal);
        Assert.False(ar.Balanced);
        Assert.Contains("ลูกหนี้คงค้าง", ar.Hint);
        var inv = Assert.Single(report.Lines, l => l.Area == "Inventory");
        Assert.Equal(12_000m, inv.SubledgerTotal);
        Assert.True(inv.Balanced);
        var cost = Assert.Single(report.Lines, l => l.Area == "FixedAssetCost");
        Assert.Equal(36_000m, cost.LedgerTotal);
        Assert.True(cost.Balanced);
        var dep = Assert.Single(report.Lines, l => l.Area == "AccumulatedDepreciation");
        Assert.Equal(10_000m, dep.LedgerTotal);
        Assert.True(dep.Balanced);
        Assert.False(report.AllBalanced);
    }

    // ───────────────────────── ตัวช่วย ─────────────────────────

    private static string ValidTaxId(string twelve)
    {
        for (var d = 0; d <= 9; d++)
            if (ThaiTaxIdValidator.Check(twelve + d).IsValid) return twelve + d;
        throw new InvalidOperationException("ไม่มีหลักตรวจที่ถูก");
    }

    private static DocumentService DocService(AccountingDbContext db)
        => new(db, new AccountingService(db), AllowAll<ISubscriptionService>.Create(), null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    /// <summary>ตัวจับคู่ผู้ติดต่อ/สินค้าสำหรับเทสต์ (MatchAsync ของจริงเป็นของทีม B) — เลขภาษีตรง = เลือกให้ · ไม่มี = ไม่พบ · บัญชีใช้ตัวจริง</summary>
    private sealed class FakeResolver : IImportEntityResolver
    {
        private readonly AccountingDbContext _db;
        public FakeResolver(AccountingDbContext db) => _db = db;

        public async Task<IReadOnlyList<ImportEntityMatchResult>> MatchAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> rows,
            ImportResolveOptions options, CancellationToken ct = default)
        {
            var outList = new List<ImportEntityMatchResult>();
            foreach (var r in rows)
            {
                Guid? id = null;
                if (entityType == "contacts" && ImportTargetValues.Text(r, "TaxId") is { } tax)
                    id = await _db.Contacts.Where(c => c.CompanyId == companyId && c.TaxId == tax).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
                outList.Add(new ImportEntityMatchResult(r.RowId, id, id == null ? "None" : "TaxBranch", id == null ? 0m : 1m, id != null, false, null,
                    Array.Empty<ImportMatchCandidateDto>(), Array.Empty<ImportIssueDto>()));
            }
            return outList;
        }

        public Task<IReadOnlyList<ImportAccountMatchResult>> ResolveAccountsAsync(Guid companyId, IReadOnlyList<ImportAccountQuery> queries,
            ImportResolveOptions options, CancellationToken ct = default)
            => new ImportAccountMatcher(_db).ResolveAsync(companyId, queries, options, ct);
    }
}

/// <summary>ตัวแทน service ที่ "อนุญาตทุกอย่าง" (Task&lt;bool&gt; = true · Task = เสร็จ · อื่น ๆ = ค่าเริ่ม) — สำหรับ dependency ที่เทสต์ไม่ได้ทดสอบ
/// (เช่น โควตาเอกสารของ ISubscriptionService ตอน CreateDocumentAsync) · ไม่ต้องพึ่ง library mock</summary>
public class AllowAll<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, AllowAll<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var rt = targetMethod?.ReturnType ?? typeof(void);
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt == typeof(Task<bool>)) return Task.FromResult(true);
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var arg = rt.GetGenericArguments()[0];
            var value = arg.IsValueType ? Activator.CreateInstance(arg) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, new[] { value });
        }
        if (rt == typeof(bool)) return true;
        return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
    }
}
