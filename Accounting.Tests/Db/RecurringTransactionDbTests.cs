using System.Reflection;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีมตรวจรายการประจำ 2026-10-14 — ผ่านเมธอดจริงบน PostgreSQL (กฎ #4 G):
/// (1) ผู้ใช้รายงาน: เลือกช่องทางรับ/จ่ายเป็นบัญชีธนาคารแล้วบันทึกไม่ได้ "Template มี accountId ที่ไม่พบในผังบัญชี" (bankAccountId ถูกค้นในผังบัญชี) ·
/// (2) ถึงรอบ ("สร้างทันที" และ cron) ต้องได้เอกสารที่ BankAccountId ถูกตัว — เดิมทั้งสองเส้นห่อด้วยธุรกรรม ขณะที่ CreateDocumentAsync เปิดธุรกรรมเอง
/// ⇒ "connection is already in a transaction" ทุกครั้ง · (3) ของที่อ้างไว้ถูกปิด/ลบหลังบันทึก ⇒ ล้มดังบนแถว (LastRunError) ไม่ใช่ลงเงินสดเงียบ ๆ ·
/// (4) วันสิ้นสุดนับรวมวันนั้น + เลยวันสิ้นสุด = เสร็จสิ้น + ขยายแล้วกลับมาใช้งาน
/// </summary>
[Trait("Category", "Db")]
public class RecurringTransactionDbTests
{
    private readonly ITestOutputHelper _out;
    public RecurringTransactionDbTests(ITestOutputHelper output) => _out = output;

    /// <summary>ISubscriptionService ที่อนุญาตทุกอย่าง (โควตา/ฟีเจอร์) — เทสต์นี้ไม่ได้ทดสอบแพ็กเกจ</summary>
    public class AllowAll<T> : DispatchProxy where T : class
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var rt = targetMethod!.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt == typeof(Task<bool>)) return Task.FromResult(true);
            if (rt == typeof(bool)) return true;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var arg = rt.GetGenericArguments()[0];
                var value = arg.IsValueType ? Activator.CreateInstance(arg) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, new[] { value });
            }
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }

        public static T Create() => DispatchProxy.Create<T, AllowAll<T>>();
    }

    private static RecurringTransactionService Svc(AccountingDbContext db, IErrorLogService? errorLog = null)
    {
        var acc = new AccountingService(db);
        var docs = new DocumentService(db, acc, AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);
        return new RecurringTransactionService(db, docs, acc, NullLogger<RecurringTransactionService>.Instance,
            errorLog ?? new ErrorLogService(db, NullLogger<ErrorLogService>.Instance));
    }

    /// <summary>ที่เก็บ ErrorLog ที่ล้มเสมอ — จำลองขั้น "หลังสร้างเอกสาร" ที่ล้ม (ฝ่ายค้านรอบ 1 P2 · 3b)</summary>
    private sealed class ThrowingErrorLog : IErrorLogService
    {
        public Task LogErrorAsync(Exception exception, string source, string? userId = null)
            => throw new InvalidOperationException("error log store down (test)");
    }

    private sealed record Seed(Guid CompanyId, Guid OtherCompanyId, Guid ContactId, Guid BankId, Guid InactiveBankId,
        Guid OtherCompanyBankId, Guid CashGlId, Guid InactiveGlId, Guid ExpenseGlId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบรายการประจำ", TaxId = "0105556000025", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105556000017", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        ChartOfAccount Gl(Guid cid, string code, string name, AccountType type, bool active = true) => new()
        {
            CompanyId = cid, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = active,
        };
        var bankGl = Gl(company.Id, "11121", "เงินฝากออมทรัพย์", AccountType.Asset);
        var cash = Gl(company.Id, "11110", "เงินสด", AccountType.Asset);
        var inactiveGl = Gl(company.Id, "11130", "เงินสดย่อย (เลิกใช้)", AccountType.Asset, active: false);
        var expense = Gl(company.Id, "52110", "ค่าทำบัญชี", AccountType.Expense);
        var otherGl = Gl(other.Id, "11121", "เงินฝาก (บริษัทอื่น)", AccountType.Asset);
        db.ChartOfAccounts.AddRange(bankGl, cash, inactiveGl, expense, otherGl);
        await db.SaveChangesAsync();
        BankAccount B(Guid cid, string name, Guid gl, bool active = true) => new()
        {
            CompanyId = cid, AccountName = name, BankName = "กสิกรไทย", AccountNumber = "123-4-56789-0", LinkedAccountId = gl, IsActive = active,
        };
        var bank = B(company.Id, "บัญชีหลัก", bankGl.Id);
        var inactiveBank = B(company.Id, "บัญชีที่ปิดแล้ว", bankGl.Id, active: false);
        var otherBank = B(other.Id, "บัญชีบริษัทอื่น", otherGl.Id);
        db.BankAccounts.AddRange(bank, inactiveBank, otherBank);
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้าและผู้ขาย จำกัด", TaxId = "0105556000041", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, IsSupplier = true,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, other.Id, contact.Id, bank.Id, inactiveBank.Id, otherBank.Id, cash.Id, inactiveGl.Id, expense.Id);
    }

    private static DateTime Today => ThaiDate.CalendarDateUtc(DateTime.UtcNow);

    private static string Template(Guid? bankAccountId = null, Guid? paymentAccountId = null, Guid? expenseCategoryId = null)
        => JsonSerializer.Serialize(new
        {
            lines = new[] { new { description = "ค่าบริการทำบัญชีเดือน <<month>>", quantity = 1, unitPrice = 5000m, vatRate = 7m, withholdingTaxRate = 0m } },
            bankAccountId,
            paymentAccountId,
            expenseCategoryId,
        });

    private static CreateRecurringTransactionRequest Req(Seed s, DocumentType type, string templateData, DateTime? start = null, DateTime? end = null)
        => new("ค่าทำบัญชีรายเดือน", null, RecurringFrequency.Monthly, start ?? Today, end, null, "Document", type,
            s.ContactId, templateData, AutoApprove: false);

    [Theory]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.Invoice)]
    public async Task บัญชีธนาคารจริง_บันทึกได้_สร้างทันทีได้เอกสารที่BankAccountIdถูกตัว(DocumentType type)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, type, Template(bankAccountId: s.BankId,
                expenseCategoryId: type == DocumentType.PaymentVoucher ? s.ExpenseGlId : null)), "tester")).Id;

        RecurringTransactionResponse after;
        using (var req = DbTestDatabase.TryCreateContext()!)
            after = await Svc(req).RunNowAsync(s.CompanyId, id, "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = Assert.Single(await check.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId).ToListAsync());
        Assert.Equal(s.BankId, doc.BankAccountId);
        Assert.Null(doc.PaymentAccountId);
        Assert.Equal(type, doc.DocumentType);
        Assert.Equal(s.ContactId, doc.ContactId);
        Assert.Equal(1, after.TotalRuns);
        Assert.Equal(doc.Id, after.LastDocumentId);
        Assert.Null(after.LastRunError);
        Assert.True(after.NextRunDate > Today);   // รอบนี้กินงวดวันนี้ ⇒ เลื่อนไปงวดหน้า (ไม่ออกซ้ำทาง cron)
    }

    [Fact]
    public async Task ทิศตรงข้าม_บัญชีธนาคารของบริษัทอื่น_ไม่มีอยู่จริง_ปิดใช้งาน_ถูกปฏิเสธพร้อมทางไปต่อ_ไม่โชว์GUID()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var svc = Svc(db);

        foreach (var bad in new[] { s.OtherCompanyBankId, Guid.NewGuid() })
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(bankAccountId: bad)), "tester"));
            Assert.Contains("บัญชีธนาคาร", ex.Message);
            Assert.Contains("เลือกช่องทางรับ/จ่ายใหม่", ex.Message);
            Assert.DoesNotContain(bad.ToString(), ex.Message);
        }
        var inactive = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(bankAccountId: s.InactiveBankId)), "tester"));
        Assert.Contains("บัญชีที่ปิดแล้ว", inactive.Message);
        Assert.Contains("ปิดใช้งาน", inactive.Message);
        Assert.False(await db.RecurringTransactions.AnyAsync(r => r.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ช่องทางรับจ่ายแบบผังบัญชี_ยังตรวจกับผังบัญชีเหมือนเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // ผังบัญชีที่ใช้งานอยู่ = บันทึกได้
        var ok = await svc.CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(paymentAccountId: s.CashGlId)), "tester");
        Assert.Equal(RecurringStatus.Active, ok.Status);
        // ทิศตรงข้าม: รหัสทะเบียนบัญชีธนาคารใส่ในช่องผังบัญชี = ไม่พบในผังบัญชี (ตารางคนละใบ — ไม่ใช่ผ่านเพราะ "มีรหัสนี้ที่ไหนสักที่")
        var wrongTable = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(paymentAccountId: s.BankId)), "tester"));
        Assert.Contains("ผังบัญชี", wrongTable.Message);
        Assert.Contains("ช่องทางรับ/จ่ายเงิน", wrongTable.Message);
        // ผังบัญชีที่ปิดใช้งาน = บอกรหัส/ชื่อบัญชีและทางแก้
        var inactive = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(paymentAccountId: s.InactiveGlId)), "tester"));
        Assert.Contains("11130", inactive.Message);
        Assert.Contains("เปิดใช้งานที่ผังบัญชี", inactive.Message);
    }

    [Fact]
    public async Task บัญชีธนาคารถูกปิดหลังบันทึก_ถึงรอบล้มดังบนแถว_ไม่ลงเงินสดเงียบ_เปิดคืนแล้วรอบถัดไปเดินเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.PaymentVoucher, Template(bankAccountId: s.BankId)), "tester")).Id;

        async Task SetBankActive(bool active)
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            var b = await c.BankAccounts.SingleAsync(x => x.Id == s.BankId && x.CompanyId == s.CompanyId);
            b.IsActive = active;
            await c.SaveChangesAsync();
        }
        await SetBankActive(false);

        // "สร้างทันที": คำตอบผู้เรียกล้มพร้อมเหตุผล + เหตุผลอยู่บนแถว
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).RunNowAsync(s.CompanyId, id, "tester"));
            Assert.Contains("ปิดใช้งาน", ex.Message);
        }
        // cron: ไม่โยน (รายการอื่นต้องได้รันต่อ) · แถวบอกเหตุผล · ไม่มีเอกสาร · ไม่นับรอบ · ยังใช้งาน (ไม่ล็อกตาย — ข้อ 150)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
            Assert.Contains("บัญชีหลัก", row.LastRunError);
            Assert.NotNull(row.LastRunErrorAt);
            Assert.Equal(0, row.TotalRuns);
            Assert.Equal(RecurringStatus.Active, row.Status);
            Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.CompanyId));
            var dto = await Svc(check).GetByIdAsync(s.CompanyId, id);
            Assert.Equal(row.LastRunError, dto.LastRunError);   // echo กลับให้หน้ารายการประจำ
        }

        await SetBankActive(true);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
            Assert.Null(row.LastRunError);
            Assert.Equal(1, row.TotalRuns);
            var doc = Assert.Single(await check.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId).ToListAsync());
            Assert.Equal(s.BankId, doc.BankAccountId);
            Assert.Equal(doc.Id, row.LastDocumentId);
        }
        // cron รอบซ้ำวันเดียวกัน (หลายเครื่อง/15 นาทีถัดไป) ไม่ออกใบงวดเดิมซ้ำ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task งวดค้างหลายงวด_รอบอัตโนมัติออกงวดเดียว_ข้ามงวดเก่าพร้อมบอกบนแถว_ไม่ไล่ออกใบย้อนหลัง()
    {
        // ฝ่ายค้าน 2026-10-14 P0: เอกสารชนิดรายการประจำล้มทุกรอบตั้งแต่ 2026-09-03 ⇒ NextRunDate ค้างงวดแรก — พอแก้ให้รันได้ cron จะออก
        // ใบกำกับภาษีย้อนหลังทีละงวดทุก 15 นาที (ลงวันที่วันนี้ · ซ้ำกับที่ผู้ใช้ออกมือไปแล้ว)
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId)), "tester")).Id;
        var stuck = Today.AddMonths(-3);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await req.RecurringTransactions.Where(r => r.Id == id && r.CompanyId == s.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextRunDate, stuck));

        for (var tick = 0; tick < 4; tick++)   // หลายรอบ cron ติดกัน (เดิม = ใบละงวดต่อรอบ)
            using (var req = DbTestDatabase.TryCreateContext()!)
                await Svc(req).ProcessDueRecurringTransactionsAsync();

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.True(row.NextRunDate > DateTime.UtcNow);
        Assert.Equal(1, row.TotalRuns);
        Assert.Contains("ข้าม", row.LastRunNotice ?? "");
        Assert.Contains("สร้างทันที", row.LastRunNotice ?? "");
        // รอบ 247: ข้อเท็จจริงแบบโครงสร้าง ⇒ หน้าได้ปุ่ม "สร้างงวดที่ข้าม" จากเซิร์ฟเวอร์ (ไม่ต้องแกะข้อความ)
        var facts = System.Text.Json.JsonSerializer.Deserialize<Accounting.Helpers.RecurringRunFacts>(row.LastRunOutcome ?? "{}")!;
        Assert.True(facts.SkippedPeriods > 0);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var view = (await Svc(req).GetByIdAsync(s.CompanyId, id)).LastRunOutcome;
            Assert.NotNull(view);
            Assert.Contains(view!.Actions, a => a.Kind == "runNow");
        }

        // ทิศตรงข้าม: ทางไปต่อของผู้ใช้ — กด "สร้างทันที" ออกเพิ่มได้ทีละใบ (รอบพิเศษ ไม่กินงวด)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).RunNowAsync(s.CompanyId, id, "tester");
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(2, await check2.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task วันสิ้นสุดวันนี้_ยังออกงวดสุดท้าย_แล้วเสร็จสิ้น_ขยายวันสิ้นสุดแล้วกลับมาใช้งาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId,
                Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), start: Today, end: Today), "tester")).Id;

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId));   // เดิม: งวดที่ตรงวันสิ้นสุดถูกตัดทิ้ง
            var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
            Assert.Equal(RecurringStatus.Completed, row.Status);   // เดิมค้าง "ใช้งาน" ตลอดไป
        }

        // ทางไปต่อ: ล้างวันสิ้นสุด (sentinel MinValue) ⇒ กลับมาใช้งาน กำหนดถัดไปไม่ย้อนอดีต
        RecurringTransactionResponse resumed;
        using (var req = DbTestDatabase.TryCreateContext()!)
            resumed = await Svc(req).UpdateAsync(s.CompanyId, id, new UpdateRecurringTransactionRequest(
                null, null, null, DateTime.MinValue, null, null, null, null, null, null, null, null));
        Assert.Equal(RecurringStatus.Active, resumed.Status);
        Assert.Null(resumed.EndDate);
        Assert.True(resumed.NextRunDate >= Today);

        // ทิศตรงข้าม: วันสิ้นสุดก่อนวันเริ่ม = ปฏิเสธพร้อมทางแก้ (ไม่ใช่บันทึกรายการที่ไม่มีวันรัน)
        using var req2 = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req2).CreateAsync(s.CompanyId,
            Req(s, DocumentType.Invoice, Template(), start: Today, end: Today.AddDays(-1)), "tester"));
        Assert.Contains("วันสิ้นสุด", ex.Message);
    }

    [Fact]
    public async Task เทมเพลตว่าง_ไม่มีผู้ติดต่อ_บรรทัดอ่านไม่ได้_ถูกปฏิเสธตอนบันทึก_ไม่ไปล้มตอนเที่ยงคืน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var svc = Svc(db);

        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, ""), "tester"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateAsync(s.CompanyId,
            Req(s, DocumentType.Invoice, Template()) with { ContactId = null }, "tester"));
        var bad = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateAsync(s.CompanyId,
            Req(s, DocumentType.Invoice, """{"lines":[{"description":"x","unitPrice":100,"accountId":"not-a-guid"}]}"""), "tester"));
        Assert.Contains("บรรทัดที่ 1", bad.Message);
        // ทิศตรงข้าม: ตัวเลขเป็นสตริงมีจุลภาค (ข้อมูลเก่า DUP2-23) บันทึกได้ และยอดบนหน้าจอถูก
        var ok = await svc.CreateAsync(s.CompanyId,
            Req(s, DocumentType.Invoice, """{"lines":[{"description":"x","quantity":"1","unitPrice":"1,200"}]}"""), "tester");
        Assert.Equal(1200m, ok.Amount);
    }

    [Fact]
    public async Task ติ๊กส่งอีเมล_ผู้ติดต่อไม่มีอีเมล_บันทึกได้พร้อมคำเตือน_มีอีเมลแล้วไม่เตือน()
    {
        // รอบ 247 (คำร้องเจ้าของ 2026-11-06 "ผู้ติดต่อยังไม่ได้ตั้งค่าอีเมลเลย ทำไมติ๊กส่งเมลได้") — บันทึกได้เสมอ (อาจเพิ่มอีเมลทีหลัง) แต่ห้ามเงียบ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);   // ผู้ติดต่อใน seed ไม่มีอีเมล
        var svc = Svc(db);
        var noEmail = await svc.CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template()) with { AutoSendEmail = true }, "tester");
        Assert.Contains(noEmail.SaveWarnings ?? Array.Empty<string>(), w => w.Contains("ยังไม่มีอีเมล"));
        Assert.Equal("/pages/contacts.html?id=" + s.ContactId.ToString("D"), noEmail.AutoSendEmailFixUrl);
        Assert.False((await svc.GetEmailReadinessAsync(s.CompanyId, s.ContactId)).ContactHasEmail);

        // ทิศตรงข้าม: เพิ่มอีเมลให้ผู้ติดต่อ ⇒ ไม่มีคำเตือน · หน้ารายละเอียดไม่ขึ้นป้าย
        await db.Contacts.Where(c => c.Id == s.ContactId && c.CompanyId == s.CompanyId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Email, "billing@example.co.th"));
        using var fresh = DbTestDatabase.TryCreateContext()!;
        var svc2 = Svc(fresh);
        Assert.True((await svc2.GetEmailReadinessAsync(s.CompanyId, s.ContactId)).ContactHasEmail);
        Assert.Null((await svc2.GetByIdAsync(s.CompanyId, noEmail.Id)).AutoSendEmailWarning);
        var withEmail = await svc2.CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template()) with { AutoSendEmail = true }, "tester");
        Assert.Null(withEmail.SaveWarnings);
    }

    [Fact]
    public async Task ขั้นหลังสร้างเอกสารล้ม_แถวไม่ขึ้นล้ม_นับรอบแล้ว_บอกว่าไม่ต้องกดซ้ำ_ไม่ออกใบซ้ำ()
    {
        // ฝ่ายค้านรอบ 1 P2 (3b): เดิมข้อผิดพลาดหลัง MarkRunAsync (บันทึกผลอนุมัติล้ม/ErrorLog) โยนต่อ ⇒ แถวขึ้น "ล้ม" ทั้งที่ออกใบ + นับงวดแล้ว ⇒ ผู้ใช้กดซ้ำ = ใบซ้ำ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            // ใบแจ้งหนี้ + อนุมัติอัตโนมัติ — ผังรายได้ไม่มีใน seed ⇒ อนุมัติล้ม ⇒ เดินเส้นบันทึกผลล้ม + ErrorLog (ที่ล้มอีกชั้น)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template()) with { AutoApprove = true }, "tester")).Id;

        RecurringTransactionResponse after;
        using (var req = DbTestDatabase.TryCreateContext()!)
            after = await Svc(req, new ThrowingErrorLog()).RunNowAsync(s.CompanyId, id, "tester");   // ไม่โยน

        Assert.Null(after.LastRunError);
        Assert.Equal(1, after.TotalRuns);
        Assert.Contains("ไม่ต้องกด", after.LastRunNotice ?? "");
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = Assert.Single(await check.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId).ToListAsync());
        Assert.Equal(doc.Id, after.LastDocumentId);
    }

    [Fact]
    public async Task ธนาคารยังไม่ผูกผัง_บันทึกรายการใหม่ไม่ได้_แต่รายการเดิมของใบแจ้งหนี้ยังออกได้ทุกงวด()
    {
        // ฝ่ายค้านรอบ 1 P1 + P2 (3c): ตอนบันทึกตรวจด้วยตัวตัดสินเดียวกับหน้าเอกสาร (ธนาคารยังไม่ผูกผัง = ปฏิเสธพร้อมทางไปต่อ) ·
        // รายการที่บันทึกไว้ก่อน (ข้อมูลเดิม) ถึงรอบแล้ว CreateDocumentAsync ไม่ตรวจแบบ "เพิ่งเลือก" ⇒ ใบแจ้งหนี้ (ไม่ลงขาเงิน) ออกได้
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var unlinked = new BankAccount { CompanyId = s.CompanyId, AccountName = "บัญชียังไม่ผูก", BankName = "กรุงไทย", AccountNumber = "9876543210" };
        db.BankAccounts.Add(unlinked);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template(bankAccountId: unlinked.Id)), "tester"));
            Assert.Contains("ยังไม่ได้ผูกผังบัญชี", ex.Message);
            Assert.Contains("รายการประจำ", ex.Message);   // ทางไปต่อชี้หน้ารายการประจำ (ไม่ใช่ "เปิดเอกสาร")
        }

        // ข้อมูลเดิม: รายการที่บันทึกไว้ก่อนด่านนี้
        var legacy = new RecurringTransaction
        {
            CompanyId = s.CompanyId, Name = "ค่าเช่ารายเดือน (เดิม)", Frequency = RecurringFrequency.Monthly, Status = RecurringStatus.Active,
            StartDate = Today, NextRunDate = Today, PreferredDay = Today.Day, TemplateType = "Document", DocumentType = DocumentType.Invoice,
            ContactId = s.ContactId, TemplateData = Template(bankAccountId: unlinked.Id), CreatedBy = "tester",
        };
        db.RecurringTransactions.Add(legacy);
        await db.SaveChangesAsync();
        RecurringTransactionResponse after;
        using (var req = DbTestDatabase.TryCreateContext()!)
            after = await Svc(req).RunNowAsync(s.CompanyId, legacy.Id, "tester");
        Assert.Null(after.LastRunError);
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = Assert.Single(await check.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId).ToListAsync());
        Assert.Equal(unlinked.Id, doc.BankAccountId);
        Assert.Equal(DocumentType.Invoice, doc.DocumentType);
    }

    // ═══════════ รอบ 255 WP-12B ข้อ 2: ทุก N เดือน + สร้างล่วงหน้า ═══════════

    [Fact]
    public async Task R255_ทุกNเดือน_สร้างล่วงหน้า_บันทึกและechoกลับ_ค่าผิดช่วงปฏิเสธ_ล้างได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var start = Today.AddDays(40);
        var created = await Svc(db).CreateAsync(s.CompanyId,
            Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), start) with { IntervalMonths = 2, LeadDays = 5 }, "tester");
        Assert.Equal((int?)2, created.IntervalMonths);
        Assert.Equal(5, created.LeadDays);

        // ทิศตรงข้าม: สร้างล่วงหน้าเกินหนึ่งงวด/ทุก 0 เดือน ⇒ ปฏิเสธพร้อมเหตุ (ไม่ clamp เงียบ) · ไม่มีแถวใหม่
        using (var bad = DbTestDatabase.TryCreateContext()!)
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(bad).CreateAsync(s.CompanyId,
                Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), start) with { LeadDays = 30 }, "tester"));
            await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(bad).CreateAsync(s.CompanyId,
                Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), start) with { IntervalMonths = 0 }, "tester"));
            Assert.Equal(1, await bad.RecurringTransactions.CountAsync(r => r.CompanyId == s.CompanyId));
        }

        // แก้ไข: 0 = ล้างกลับไปใช้ความถี่ · null = ไม่แก้ (สร้างล่วงหน้าคงเดิม)
        using var upd = DbTestDatabase.TryCreateContext()!;
        var cleared = await Svc(upd).UpdateAsync(s.CompanyId, created.Id, new UpdateRecurringTransactionRequest(
            null, null, null, null, null, null, null, null, null, null, null, null, IntervalMonths: 0));
        Assert.Null(cleared.IntervalMonths);
        Assert.Equal(5, cleared.LeadDays);
        // ทิศตรงข้าม (เทสต์เดิมไม่ส่งสองช่องนี้) — รายการที่ไม่ตั้ง = 0/null = พฤติกรรมเดิม
        using var plain = DbTestDatabase.TryCreateContext()!;
        var old = await Svc(plain).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), start), "tester");
        Assert.Null(old.IntervalMonths);
        Assert.Equal(0, old.LeadDays);
    }

    // ═══════════ รอบ 259 ชุด D — B-M4 (สมุดรายวันค้างหลายงวด) · B-L2/L3 (กุญแจกันซ้ำรายการ + งวด) ═══════════

    private static string JournalTemplate(Seed s) => JsonSerializer.Serialize(new
    {
        description = "ค่าทำบัญชีค้างจ่าย <<month>>",
        lines = new object[]
        {
            new { accountId = s.ExpenseGlId, debitAmount = 1000m, creditAmount = 0m, description = "ค่าทำบัญชี" },
            new { accountId = s.CashGlId, debitAmount = 0m, creditAmount = 1000m, description = "จ่ายค่าทำบัญชี" },
        },
    });

    private static async Task<(Guid Id, List<DateTime> Periods)> StuckJournalAsync(Seed s, int monthsBack)
    {
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, new CreateRecurringTransactionRequest("ตั้งค้างจ่ายรายเดือน", null, RecurringFrequency.Monthly,
                Today, null, null, "Journal", null, null, JournalTemplate(s), AutoApprove: false), "tester")).Id;
        var stuck = Today.AddMonths(-monthsBack);
        // ฝ่ายค้าน 259-D M1: รอบอัตโนมัติหยุดเมื่อข้ามปีบัญชี ⇒ ตั้งเดือนเริ่มปีบัญชีให้ทุกงวดในเทสต์อยู่ปีเดียวกัน (ไม่ขึ้นกับวันที่รันเทสต์)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await req.Companies.Where(c => c.Id == s.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.FiscalYearStartMonth, stuck.AddMonths(-1).Month));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await req.RecurringTransactions.Where(r => r.Id == id && r.CompanyId == s.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextRunDate, stuck).SetProperty(r => r.StartDate, stuck)
                    .SetProperty(r => r.PreferredDay, (int?)stuck.Day));
        var periods = new List<DateTime>();
        for (var d = stuck; RecurringSchedule.IsDue(d, DateTime.UtcNow); d = RecurringSchedule.Next(RecurringFrequency.Monthly, d, stuck.Day, stuck, null))
            periods.Add(ThaiDate.CalendarDateUtc(d).Date);
        return (id, periods);
    }

    [Fact]
    public async Task R259_BM4_สมุดรายวันค้าง3งวด_รอบเดียวลงครบ_แต่ละใบลงวันที่ของงวดตัวเอง_ไม่ใช่วันนี้ทุกใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (id, periods) = await StuckJournalAsync(s, 3);
        Assert.True(periods.Count >= 4);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        var dates = await check.JournalEntries.AsNoTracking().Where(j => j.CompanyId == s.CompanyId)
            .Select(j => j.EntryDate).ToListAsync();
        Assert.Equal(periods, dates.Select(d => d.Date).OrderBy(d => d).ToList());
        Assert.Equal(periods.Count, dates.Select(d => (d.Year, d.Month)).Distinct().Count());   // ไม่กองเดือนเดียว
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.Equal(periods.Count, row.TotalRuns);
        Assert.False(RecurringSchedule.IsDue(row.NextRunDate, DateTime.UtcNow));
        // รอบซ้ำ (15 นาทีถัดไป) ไม่ลงเพิ่ม
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(periods.Count, await check2.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task R259_BM4_ทิศตรงข้าม_งวดบัญชีปิด_ข้ามงวดนั้นพร้อมเหตุบนแถว_ไม่ลงงวดอื่นแทน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (id, periods) = await StuckJournalAsync(s, 3);
        var locked = periods[1];
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            CompanyId = s.CompanyId, Name = $"{locked.Year + 543}/{locked.Month:00}", Year = locked.Year, Month = locked.Month,
            StartDate = new DateTime(locked.Year, locked.Month, 1), EndDate = new DateTime(locked.Year, locked.Month, 1).AddMonths(1).AddDays(-1),
            Status = FiscalPeriodStatus.Closed,
        });
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        var dates = (await check.JournalEntries.AsNoTracking().Where(j => j.CompanyId == s.CompanyId).Select(j => j.EntryDate).ToListAsync())
            .Select(d => d.Date).OrderBy(d => d).ToList();
        Assert.Equal(periods.Where(p => p != locked).ToList(), dates);
        Assert.DoesNotContain(dates, d => d.Year == locked.Year && d.Month == locked.Month);
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.Contains("ข้ามงวด", row.LastRunNotice ?? "");
        Assert.Contains("งวดบัญชีปิด", row.LastRunNotice ?? "");
        Assert.Equal(periods.Count - 1, row.TotalRuns);   // งวดที่ข้ามไม่นับครั้ง
        Assert.Null(row.LastRunError);
    }

    [Fact]
    public async Task R259_BL2_ระบบล้มหลังสร้างใบก่อนประทับรอบ_รอบถัดไปไม่ออกซ้ำ_ประทับด้วยใบเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId)), "tester")).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        Guid docId;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var doc = await c.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId);
            docId = doc.Id;
            Assert.Equal(RecurringRunKey.ForPeriod(id, Today), doc.RecurringRunKey);
            Assert.Equal(id, doc.RecurringTransactionId);
        }
        // จำลอง "ล่มระหว่างสร้างใบกับประทับรอบ": แถวกลับเป็นสภาพก่อนประทับ (ใบอยู่แล้ว)
        using (var c = DbTestDatabase.TryCreateContext()!)
            await c.RecurringTransactions.Where(r => r.Id == id && r.CompanyId == s.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextRunDate, Today).SetProperty(r => r.TotalRuns, 0)
                    .SetProperty(r => r.LastDocumentId, (Guid?)null).SetProperty(r => r.LastRunDate, (DateTime?)null));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.Equal(1, row.TotalRuns);
        Assert.Equal(docId, row.LastDocumentId);
        Assert.True(row.NextRunDate > Today);
        Assert.Contains("ไม่ออกซ้ำ", row.LastRunNotice ?? "");
        Assert.Null(row.LastRunError);
    }

    [Fact]
    public async Task R259_BL3_สร้างทันทีก่อนกำหนดกดซ้ำ_409ถามยืนยัน_ไม่ลงเหตุล้ม_ยืนยันแล้วออกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Svc(req).CreateAsync(s.CompanyId, Req(s, DocumentType.Invoice, Template(bankAccountId: s.BankId), Today.AddDays(10)), "tester")).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).RunNowAsync(s.CompanyId, id, "tester");   // ใบล่วงหน้าใบแรก — ได้
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).RunNowAsync(s.CompanyId, id, "tester"));
            Assert.Equal(RecurringRunKey.RuleCode, ex.RuleCode);
            Assert.Equal(409, ex.StatusCode);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1, await c.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
            Assert.Null((await c.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId)).LastRunError);
        }
        // ทางไปต่อ: ยืนยันออกเพิ่ม (เช่น งวดที่ระบบข้าม) ⇒ ได้อีกใบ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).RunNowAsync(s.CompanyId, id, "tester", confirmExtra: true);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(2, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
        // ใบล่วงหน้าเป็นรอบพิเศษ ไม่กินงวด (คงคำตัดสินข้อ 150) — กำหนดงวดยังเป็นวันเริ่ม
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.Equal(Today.AddDays(10), row.NextRunDate);
    }

    [Fact]
    public async Task R259D_M2_สมุดรายวันล่มหลังลงใบก่อนประทับรอบ_รอบถัดไปไม่ลงซ้ำ_ประทับด้วยใบเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (id, _) = await StuckJournalAsync(s, 0);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var je = await c.JournalEntries.AsNoTracking().SingleAsync(j => j.CompanyId == s.CompanyId);
            Assert.Equal(RecurringRunKey.ForPeriod(id, Today), je.RecurringRunKey);
        }
        // จำลองล่มระหว่างลงใบสำคัญกับประทับรอบ
        using (var c = DbTestDatabase.TryCreateContext()!)
            await c.RecurringTransactions.Where(r => r.Id == id && r.CompanyId == s.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextRunDate, Today).SetProperty(r => r.TotalRuns, 0).SetProperty(r => r.LastRunDate, (DateTime?)null));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));
        var row = await check.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId);
        Assert.Equal(1, row.TotalRuns);
        Assert.True(row.NextRunDate > Today);
        Assert.Contains("ไม่ออกซ้ำ", row.LastRunNotice ?? "");
    }

    [Fact]
    public async Task R259D_M1_สมุดรายวันลงย้อนหลัง_บอกจำนวนงวด_ข้ามปีบัญชีหยุดรอคน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var (id, periods) = await StuckJournalAsync(s, 2);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains($"ย้อนหลัง {periods.Count - 1} งวด",
                (await c.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id && r.CompanyId == s.CompanyId)).LastRunNotice ?? "");
        // ทิศตรงข้าม/หยุดรอคน: ปีบัญชีเริ่มเดือนนี้ ⇒ งวดที่ค้างอยู่ปีก่อน ⇒ รอบอัตโนมัติไม่ลง + เหตุบนแถว · ผู้ใช้กดสร้างทันที = ยืนยัน ลงได้
        var (id2, _) = await StuckJournalAsync(s, 1);
        using (var c = DbTestDatabase.TryCreateContext()!)
            await c.Companies.Where(x => x.Id == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.FiscalYearStartMonth, Today.Month));
        var before = 0;
        using (var c = DbTestDatabase.TryCreateContext()!) before = await c.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).ProcessDueRecurringTransactionsAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(before, await c.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));
            Assert.Contains("ปีบัญชีก่อนหน้า", (await c.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == id2 && r.CompanyId == s.CompanyId)).LastRunError ?? "");
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).RunNowAsync(s.CompanyId, id2, "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(before + 1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));
    }
}
