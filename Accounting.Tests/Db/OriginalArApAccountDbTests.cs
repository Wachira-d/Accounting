using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 206 (ฝ่ายค้านรอบ 2 P2(b) ส่วนที่ค้าง · PAYMENT_CHANNEL_AUDIT ADV2-P2-b) — ทุกเส้นที่ปิดหนี้ของใบที่ออกแล้วตัดผังที่ "JE ตอนออกใบ" ตั้งไว้
/// (<see cref="OriginalArApAccount"/>) ไม่ใช่ผังที่ผู้ติดต่อปักอยู่ตอนนี้ — ผ่านเมธอดจริงบน PostgreSQL:
/// <list type="bullet">
/// <item>ใบขายลงลูกหนี้ 11310-X (ผังที่ปักไว้) แล้วผังนั้นถูกปิดใช้ ⇒ รับชำระยังตัด 11310-X และสำเร็จ (เดิมโยน "เปิดใช้บัญชีเดิม")</item>
/// <item>ตัดหนี้สูญใบเดียวกัน ⇒ ตัด 11310-X</item>
/// <item>ใบซื้อลงเจ้าหนี้ 21210-X แล้วผู้ติดต่อเปลี่ยนผังที่ปักเป็นผังอื่น ⇒ จ่ายชำระตัด 21210-X (ไม่ใช่ผังใหม่)</item>
/// <item>ทิศตรงข้าม: ใบที่ไม่มี JE ⇒ พฤติกรรมเดิม (ผังที่ปักใช้ได้ = ตัดผังที่ปัก · ปิดใช้ = ล้มดังพร้อมทางไปต่อ)</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class OriginalArApAccountDbTests
{
    private readonly ITestOutputHelper _out;
    public OriginalArApAccountDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid CustomerId, Guid SupplierId, Guid PinnedArId, Guid DefaultArId,
        Guid PinnedApId, Guid NewApId, Guid RevenueId, Guid ExpenseId);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบผังลูกหนี้เดิม", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cash = Acc(company.Id, "11111", "เงินสด", AccountType.Asset);
        var defaultAr = Acc(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var pinnedAr = Acc(company.Id, "11310-X", "ลูกหนี้การค้า - ลูกค้ารายใหญ่", AccountType.Asset, level: 5);
        var defaultAp = Acc(company.Id, "21210", "เจ้าหนี้การค้า", AccountType.Liability);
        var pinnedAp = Acc(company.Id, "21210-X", "เจ้าหนี้การค้า - ผู้ขายหลัก", AccountType.Liability, level: 5);
        var newAp = Acc(company.Id, "21210-Y", "เจ้าหนี้การค้า - ผังใหม่", AccountType.Liability, level: 5);
        var revenue = Acc(company.Id, "41100", "รายได้จากการขาย", AccountType.Revenue);
        var expense = Acc(company.Id, "51110", "ซื้อสินค้า", AccountType.Expense);
        var badDebt = Acc(company.Id, "64000", "หนี้สูญ", AccountType.Expense);
        db.ChartOfAccounts.AddRange(cash, defaultAr, pinnedAr, defaultAp, pinnedAp, newAp, revenue, expense, badDebt);
        var customer = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, DefaultArAccountId = pinnedAr.Id,
        };
        var supplier = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0105537000014", ContactType = ContactType.JuristicPerson,
            IsSupplier = true, DefaultApAccountId = pinnedAp.Id,
        };
        db.Contacts.AddRange(customer, supplier);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id, supplier.Id, pinnedAr.Id, defaultAr.Id, pinnedAp.Id, newAp.Id, revenue.Id, expense.Id);
    }

    /// <summary>ใบที่อนุมัติแล้ว 1,000 (ไม่มี VAT/WHT) + JE ตอนออกใบ (Dr <paramref name="debitId"/> / Cr <paramref name="creditId"/>) ถ้า <paramref name="withJe"/></summary>
    private static async Task<Document> IssuedAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, Guid contactId,
        bool withJe, Guid debitId, Guid creditId)
    {
        var no = (type == DocumentType.PurchaseInvoice ? "PI-" : "INV-") + Guid.NewGuid().ToString("N")[..6];
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId, DocumentType = type, DocumentNumber = no,
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m, Currency = "THB", WhtCertAutoIssue = false,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "รายการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        if (withJe)
        {
            var je = new JournalEntry
            {
                CompanyId = s.CompanyId, EntryNumber = "JV-" + no, EntryDate = DateTime.UtcNow.Date, Status = JournalEntryStatus.Posted,
                JournalType = type == DocumentType.PurchaseInvoice ? JournalType.Purchase : JournalType.Sales,
                Description = "ออกใบ " + no, Reference = no, TotalDebit = 1_000m, TotalCredit = 1_000m,
                SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
            };
            je.Lines.Add(new JournalEntryLine { AccountId = debitId, DebitAmount = 1_000m, LineOrder = 1 });
            je.Lines.Add(new JournalEntryLine { AccountId = creditId, CreditAmount = 1_000m, LineOrder = 2 });
            db.JournalEntries.Add(je);
            await db.SaveChangesAsync();
        }
        return d;
    }

    private static DocumentService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static CreatePaymentRequest Pay(Guid docId) => new(docId, DateTime.UtcNow.Date, 1_000m, PaymentMethod.Cash, null, null, null);

    private static async Task SetActiveAsync(Accounting.Data.AccountingDbContext db, Seed s, Guid accountId, bool active)
        => await db.ChartOfAccounts.Where(a => a.Id == accountId && a.CompanyId == s.CompanyId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.IsActive, active));

    /// <summary>ผังที่ JE (ไม่ใช่ JE ตอนออกใบ) ของใบนี้ลงไว้ฝั่งที่กำหนด</summary>
    private static async Task<List<Guid>> SettleSideAccountsAsync(Accounting.Data.AccountingDbContext db, Seed s, Guid docId, bool credit)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == s.CompanyId && j.SourceDocumentId == docId && !j.EntryNumber.StartsWith("JV-INV-")
                        && !j.EntryNumber.StartsWith("JV-PI-") && (credit ? l.CreditAmount > 0 : l.DebitAmount > 0)
                  select l.AccountId).ToListAsync();

    [Fact]
    public async Task รับชำระ_ผังลูกหนี้ที่ใบตั้งไว้ถูกปิดใช้ทีหลัง_ยังตัดผังเดิมและสำเร็จ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var inv = await IssuedAsync(db, s, DocumentType.Invoice, s.CustomerId, withJe: true, s.PinnedArId, s.RevenueId);
        await SetActiveAsync(db, s, s.PinnedArId, active: false);

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(inv.Id), "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var credited = await SettleSideAccountsAsync(check, s, inv.Id, credit: true);
        Assert.Contains(s.PinnedArId, credited);          // ตัด 11310-X ที่ใบตั้งไว้
        Assert.DoesNotContain(s.DefaultArId, credited);   // ไม่ตัดผังมาตรฐาน 11310 แทน
        Assert.Equal(0m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id && d.CompanyId == s.CompanyId)).BalanceDue);
    }

    [Fact]
    public async Task ตัดหนี้สูญ_ตัดผังลูกหนี้ที่ใบตั้งไว้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await IssuedAsync(db, s, DocumentType.Invoice, s.CustomerId, withJe: true, s.PinnedArId, s.RevenueId);
        await SetActiveAsync(db, s, s.PinnedArId, active: false);

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).WriteOffBadDebtAsync(s.CompanyId, inv.Id, "tester", "ลูกค้าปิดกิจการ");

        using var check = DbTestDatabase.TryCreateContext()!;
        var credited = await SettleSideAccountsAsync(check, s, inv.Id, credit: true);
        Assert.Equal(new[] { s.PinnedArId }, credited);
    }

    [Fact]
    public async Task จ่ายชำระ_ผู้ติดต่อเปลี่ยนผังเจ้าหนี้ที่ปักหลังออกใบ_ตัดผังที่ใบตั้งไว้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pi = await IssuedAsync(db, s, DocumentType.PurchaseInvoice, s.SupplierId, withJe: true, s.ExpenseId, s.PinnedApId);
        // ผู้ติดต่อย้ายไปปักผังใหม่ + ผังเดิมปิดใช้ (เดิม: เส้นจ่ายชำระตัด 21210-Y ⇒ 21210-X ค้างเครดิตถาวร · 21210-Y ติดเดบิต)
        await db.Contacts.Where(c => c.Id == s.SupplierId && c.CompanyId == s.CompanyId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.DefaultApAccountId, (Guid?)s.NewApId));
        await SetActiveAsync(db, s, s.PinnedApId, active: false);

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id), "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var debited = await SettleSideAccountsAsync(check, s, pi.Id, credit: false);
        Assert.Contains(s.PinnedApId, debited);
        Assert.DoesNotContain(s.NewApId, debited);
    }

    [Fact]
    public async Task ทิศตรงข้าม_ใบไม่มีJE_ใช้ตัวหาเดิม_ผังที่ปักใช้ได้ตัดผังที่ปัก_ปิดใช้ล้มดังพร้อมทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        // (1) ไม่มี JE + ผังที่ปักใช้ได้ ⇒ ตัดผังที่ปัก (เดิม)
        var ok = await IssuedAsync(db, s, DocumentType.Invoice, s.CustomerId, withJe: false, s.PinnedArId, s.RevenueId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(ok.Id), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(s.PinnedArId, await SettleSideAccountsAsync(check, s, ok.Id, credit: true));

        // (2) ไม่มี JE + ผังที่ปักปิดใช้ ⇒ ล้มดังพร้อมทางไปต่อ (เดิม — ไม่มีหลักฐานว่าใบตั้งไว้ผังไหน ระบบไม่เดา) · ไม่มีอะไรถูกบันทึก
        var blocked = await IssuedAsync(db, s, DocumentType.Invoice, s.CustomerId, withJe: false, s.PinnedArId, s.RevenueId);
        await SetActiveAsync(db, s, s.PinnedArId, active: false);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).CreatePaymentAsync(s.CompanyId, Pay(blocked.Id), "tester"));
            Assert.Contains("ถูกปิดใช้งาน", ex.Message);
            Assert.Contains("เปิดใช้บัญชีเดิม", ex.Message);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Empty(await SettleSideAccountsAsync(check, s, blocked.Id, credit: true));
            Assert.Equal(1_000m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == blocked.Id && d.CompanyId == s.CompanyId)).BalanceDue);
        }
    }
}
