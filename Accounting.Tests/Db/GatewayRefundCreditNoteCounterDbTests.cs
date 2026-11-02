using Accounting.Models.DTOs.Document;
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
/// รอบ 228 (PLATFORM_OMISE_AUDIT §7.2 · B1 เส้นร้าน) — ใบลดหนี้ที่ผู้ใช้ออกเองหลังคืนเงินผ่านผู้ให้บริการรับชำระ บน PostgreSQL + DocumentService จริง:
/// ใบสำคัญคืนเงิน Dr ลูกหนี้ / Cr บัญชีพัก ⇒ ใบลดหนี้ต้องล้างลูกหนี้ (ไม่ใช่ Cr เงินสด) · ทิศตรงข้าม: ใบที่ไม่มีคืนเงิน gateway ⇒ ขาเงินสดตามเดิม
/// </summary>
[Trait("Category", "Db")]
public class GatewayRefundCreditNoteCounterDbTests
{
    private readonly ITestOutputHelper _out;
    public GatewayRefundCreditNoteCounterDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid ContactId, Dictionary<string, ChartOfAccount> Acc);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var co = new Company { Name = "ร้านทดสอบคืนเงิน gateway", TaxId = "0105556000002" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = co.Id, Name = "ลูกค้าบัตร", IsCustomer = true };
        db.Contacts.Add(contact);
        var acc = new Dictionary<string, ChartOfAccount>();
        foreach (var (code, name, type) in new[]
                 {
                     ("111", "เงินสด", AccountType.Asset), ("11310", "ลูกหนี้การค้า", AccountType.Asset),
                     ("11340", "ลูกหนี้ผู้ให้บริการรับชำระเงิน", AccountType.Asset),
                     ("41000", "รายได้จากการขาย", AccountType.Revenue), ("21911", "ภาษีขาย", AccountType.Liability),
                 })
        {
            var a = new ChartOfAccount { CompanyId = co.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        await db.SaveChangesAsync();
        return new Seed(co.Id, contact.Id, acc);
    }

    /// <summary>ใบกำกับที่ชำระครบผ่าน gateway (Dr บัญชีพัก 535 / Cr รายได้ 500 / Cr ภาษีขาย 35)</summary>
    private static async Task<Document> PaidInvoiceAsync(Accounting.Data.AccountingDbContext db, Seed s, string no)
    {
        var inv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Paid,
            DocumentNumber = no, DocumentDate = DateTime.UtcNow.Date.AddDays(-2), SubTotal = 500m, VatAmount = 35m, TotalAmount = 535m,
            BalanceDue = 0m, PaidAmount = 535m, Currency = "THB", ExchangeRate = 1m,
            Lines = new List<DocumentLine> { new() { Description = "สินค้า", Quantity = 1m, UnitPrice = 500m, Amount = 500m, VatRate = 7m, VatAmount = 35m, AccountId = s.Acc["41000"].Id } },
        };
        db.Documents.Add(inv);
        db.JournalEntries.Add(new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "RV-" + no, EntryDate = inv.DocumentDate, Status = JournalEntryStatus.Posted,
            SourceDocumentId = inv.Id, TotalDebit = 535m, TotalCredit = 535m,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = s.Acc["11340"].Id, DebitAmount = 535m, LineOrder = 1 },
                new() { AccountId = s.Acc["41000"].Id, CreditAmount = 500m, LineOrder = 2 },
                new() { AccountId = s.Acc["21911"].Id, CreditAmount = 35m, LineOrder = 3 },
            },
        });
        await db.SaveChangesAsync();
        return inv;
    }

    private static async Task<Dictionary<string, decimal>> BalancesAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join e in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                  join a in db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                  where e.CompanyId == companyId && e.Status == JournalEntryStatus.Posted
                  group l by a.AccountCode into g
                  select new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.Code, x => x.Net);

    private static async Task CreditNoteAsync(Accounting.Data.AccountingDbContext db, Seed s, Document inv)
    {
        var docs = new DocumentService(db, new AccountingService(db), AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);
        var cn = await docs.CreateDocumentAsync(s.CompanyId, new CreateDocumentRequest(
            DocumentType: DocumentType.CreditNote, DocumentDate: DateTime.UtcNow.Date, DueDate: null, ContactId: s.ContactId,
            Reference: inv.DocumentNumber, Notes: "คืนเงินลูกค้า",
            Lines: new List<DocumentLineRequest>
            {
                new(Description: "คืนเงิน", Quantity: 1m, Unit: "รายการ", UnitPrice: 100m, DiscountPercent: 0m, VatRate: 7m,
                    WithholdingTaxRate: 0m, AccountId: s.Acc["41000"].Id),
            },
            RelatedDocumentId: inv.Id, CnDnPurchaseSideOverride: false, CreditNoteReason: CreditNoteReason.Adjustment), "tester");
        await docs.ApproveDocumentAsync(s.CompanyId, cn.Id, "tester");
    }

    [Fact]
    public async Task B1ร้าน_ใบลดหนี้หลังคืนเงินผ่านgateway_ล้างลูกหนี้_เงินสดไม่ขยับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await PaidInvoiceAsync(db, s, "TIV-GWR-" + Guid.NewGuid().ToString("N")[..6]);
        // คืนเงิน 107 ผ่านผู้ให้บริการ (เส้น GatewayRefundService.BookRefundAsync): PV Dr ลูกหนี้ / Cr บัญชีพัก
        var pv = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "PV-GWR-" + Guid.NewGuid().ToString("N")[..6], EntryDate = DateTime.UtcNow.Date, Status = JournalEntryStatus.Posted,
            TotalDebit = 107m, TotalCredit = 107m,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = s.Acc["11310"].Id, DebitAmount = 107m, LineOrder = 1 },
                new() { AccountId = s.Acc["11340"].Id, CreditAmount = 107m, LineOrder = 2 },
            },
        };
        db.JournalEntries.Add(pv);
        db.PaymentIntents.Add(new PaymentIntent
        {
            CompanyId = s.CompanyId, SourceKind = PaymentSourceKind.Document, SourceId = inv.Id, ProviderCode = "omise", Amount = 535m,
            Status = PaymentIntentStatus.PartiallyRefunded, RefundedAmount = 107m, LastRefundedAt = DateTime.UtcNow, LastRefundJournalEntryId = pv.Id,
            ConfirmedAt = DateTime.UtcNow.AddDays(-2), IdempotencyKey = "t228-gwr-" + Guid.NewGuid().ToString("N"),
        });
        await db.SaveChangesAsync();

        await CreditNoteAsync(db, s, inv);

        using var check = DbTestDatabase.TryCreateContext()!;
        var gl = await BalancesAsync(check, s.CompanyId);
        Assert.Equal(0m, gl.GetValueOrDefault("11310"));          // ลูกหนี้ล้าง (เดิม Dr 107 ค้าง)
        Assert.Equal(0m, gl.GetValueOrDefault("111"));            // เงินสดไม่ขยับ (เดิม Cr 107)
        Assert.Equal(535m - 107m, gl.GetValueOrDefault("11340"));  // บัญชีพักลดเท่าเงินที่คืน (ครั้งเดียว)
        Assert.Equal(-400m, gl.GetValueOrDefault("41000"));
        Assert.Equal(-28m, gl.GetValueOrDefault("21911"));
    }

    [Fact]
    public async Task B1ร้าน_ทิศตรงข้าม_ใบลดหนี้ของใบที่ไม่มีคืนเงินgateway_ขาเงินสดตามเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await PaidInvoiceAsync(db, s, "TIV-CASH-" + Guid.NewGuid().ToString("N")[..6]);

        await CreditNoteAsync(db, s, inv);

        using var check = DbTestDatabase.TryCreateContext()!;
        var gl = await BalancesAsync(check, s.CompanyId);
        Assert.Equal(-107m, gl.GetValueOrDefault("111"));         // คืนเงินสดจากใบลดหนี้ (พฤติกรรมเดิม)
        Assert.Equal(0m, gl.GetValueOrDefault("11310"));
        Assert.Equal(535m, gl.GetValueOrDefault("11340"));
    }
}
