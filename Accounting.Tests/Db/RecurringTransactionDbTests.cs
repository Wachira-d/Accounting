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

    private static RecurringTransactionService Svc(AccountingDbContext db)
    {
        var acc = new AccountingService(db);
        var docs = new DocumentService(db, acc, AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);
        return new RecurringTransactionService(db, docs, acc, NullLogger<RecurringTransactionService>.Instance,
            new ErrorLogService(db, NullLogger<ErrorLogService>.Instance));
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
}
