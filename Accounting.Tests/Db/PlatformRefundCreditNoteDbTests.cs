using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 228 ฝ่ายค้าน B1 (PLATFORM_OMISE_AUDIT §5.5 · §86/10) — คืนเงินค่าบริการ NextAcc (ใต้บริษัทผู้ให้บริการ) บน PostgreSQL จริง + DocumentService จริง:
/// GL หลังคืนเงิน = บัญชีพัก 11340 ลดเท่าเงินที่คืน · เงินสด 111 ไม่ขยับ · ลูกหนี้ 0 · รายได้/ภาษีขายลดตามใบลดหนี้ ·
/// <b>เดิม</b> ใบสำคัญคืนเงิน Dr ลูกหนี้/Cr บัญชีพัก + ใบลดหนี้ของใบที่ชำระครบ Cr เงินสด ⇒ เงินออกสองขา (บัญชีพัก + เงินสด) และลูกหนี้ค้างถาวร
/// <para>อยู่ collection เดียวกับเทสต์ที่ตั้ง <c>SiteSettings.PlatformCompanyId</c> (แถวเดียวของระบบ) — ไม่รันขนานกัน</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PlatformRefundCreditNoteDbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformRefundCreditNoteDbTests(ITestOutputHelper output) => _out = output;

    private static async Task WithPlatformAsync(Accounting.Data.AccountingDbContext db, Guid platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var (pid, vat, tax, rev) = (s.PlatformCompanyId, s.PlatformIsVatRegistered, s.PlatformSellerTaxId, s.PlatformRevenueAccountCode);
        s.PlatformCompanyId = platformId; s.PlatformIsVatRegistered = true; s.PlatformSellerTaxId = "0105556000009"; s.PlatformRevenueAccountCode = "41000";
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else { again.PlatformCompanyId = pid; again.PlatformIsVatRegistered = vat; again.PlatformSellerTaxId = tax; again.PlatformRevenueAccountCode = rev; }
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Dictionary<string, decimal>> BalancesAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join e in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                  join a in db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                  where e.CompanyId == companyId && e.Status == JournalEntryStatus.Posted
                  group l by a.AccountCode into g
                  select new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToDictionaryAsync(x => x.Code, x => x.Net);

    [Fact]
    public async Task B1_คืนเงินค่าส่วนเสริม_บันทึกยอดคืน_ลงบัญชีด้วยใบลดหนี้ใบเดียว_บัญชีพักลด_เงินสดไม่ขยับ_ลูกหนี้ศูนย์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var acc = new Dictionary<string, ChartOfAccount>();
        foreach (var (code, name, type) in new[]
                 {
                     ("111", "เงินสด", AccountType.Asset), ("11310", "ลูกหนี้การค้า", AccountType.Asset),
                     (GatewayAccountResolver.StandardClearingCode, "ลูกหนี้ผู้ให้บริการรับชำระเงิน", AccountType.Asset),
                     ("41000", "รายได้ค่าบริการ", AccountType.Revenue), ("21911", "ภาษีขาย", AccountType.Liability),
                 })
        {
            var a = new ChartOfAccount { CompanyId = f.PlatformId, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            acc[code] = a;
        }
        var featureRowId = Guid.NewGuid();
        var intent = new PaymentIntent
        {
            CompanyId = f.PlatformId, PayerCompanyId = f.BuyerId, SourceKind = PaymentSourceKind.AddOnPurchase, SourceId = featureRowId,
            ProviderCode = OmisePaymentProvider.Code, Amount = 535m, Status = PaymentIntentStatus.PartiallyRefunded, IsTestMode = false,
            ConfirmedAt = DateTime.UtcNow.AddDays(-3), ProviderRef = "chrg_test_b1", IdempotencyKey = "t228-" + Guid.NewGuid().ToString("N"),
        };
        db.PaymentIntents.Add(intent);
        // ใบเดิม: ใบกำกับภาษีขายสดค่าส่วนเสริม ชำระครบ (Dr 11340 535 / Cr รายได้ 500 / Cr ภาษีขาย 35) — ตามรอบ 227 C-2/C-3
        var original = new Document
        {
            CompanyId = f.PlatformId, ContactId = f.ContactId, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Paid,
            DocumentNumber = "TIV-B1-" + Guid.NewGuid().ToString("N")[..6], Reference = AddOnPurchaseService.ReceiptReference(featureRowId, intent.Id, null),
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3), SubTotal = 500m, VatAmount = 35m, TotalAmount = 535m, BalanceDue = 0m, PaidAmount = 535m,
            Currency = "THB", ExchangeRate = 1m, PaymentAccountId = acc[GatewayAccountResolver.StandardClearingCode].Id,
            Lines = new List<DocumentLine> { new() { Description = "ค่าส่วนเสริม", Quantity = 1m, UnitPrice = 500m, Amount = 500m, VatRate = 7m, VatAmount = 35m, AccountId = acc["41000"].Id } },
        };
        db.Documents.Add(original);
        db.JournalEntries.Add(new JournalEntry
        {
            CompanyId = f.PlatformId, EntryNumber = "RV-B1-" + Guid.NewGuid().ToString("N")[..6], EntryDate = original.DocumentDate, Status = JournalEntryStatus.Posted,
            SourceDocumentId = original.Id, TotalDebit = 535m, TotalCredit = 535m,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = acc[GatewayAccountResolver.StandardClearingCode].Id, DebitAmount = 535m, LineOrder = 1 },
                new() { AccountId = acc["41000"].Id, CreditAmount = 500m, LineOrder = 2 },
                new() { AccountId = acc["21911"].Id, CreditAmount = 35m, LineOrder = 3 },
            },
        });
        await db.SaveChangesAsync();

        await WithPlatformAsync(db, f.PlatformId, async () =>
        {
            var docs = new DocumentService(db, new AccountingService(db), AllowAll<ISubscriptionService>.Create(), null!, null!,
                NullLogger<DocumentService>.Instance, null!, null!, null!);
            var providers = new IPaymentProvider[] { new OmisePaymentProvider(null!, null!, NullLogger<OmisePaymentProvider>.Instance) };
            var resolver = new GatewayAccountResolver(db, providers, NullLogger<GatewayAccountResolver>.Instance);
            var issuer = new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add<IDocumentService>(docs),
                NullLogger<PlatformBillingDocumentIssuer>.Instance, resolver);
            var refunds = new GatewayRefundService(db, providers, F4Proxy<IPaymentIntentService>.Create((_, _) => null), resolver,
                NullLogger<GatewayRefundService>.Instance, issuer);

            // คืนบางส่วน 107 (ทางเข้า "บันทึกยอดคืนย้อนหลัง · ให้ระบบลงบัญชี" — ฝ่ายค้าน S1 เส้นเดียวกับคืนเงินปกติ หลังเงินออก)
            var r = await refunds.RecordLegacyRefundAsync(f.PlatformId, intent.Id, 107m, DateTime.UtcNow.AddHours(-1), "rfnd_test_b1",
                "แดชบอร์ดผู้ให้บริการแสดงคืน 107", GatewayLegacyRefundJournal.BookNow, "tester", null, null);
            Assert.True(r.Ok, r.Message);
            _out.WriteLine(r.Message);

            using var check = DbTestDatabase.TryCreateContext()!;
            var cn = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == f.PlatformId && d.DocumentType == DocumentType.CreditNote
                && d.RelatedDocumentId == original.Id);
            Assert.Equal(107m, cn.TotalAmount);
            Assert.Equal(7m, cn.VatAmount);
            Assert.NotEqual(DocumentStatus.Draft, cn.Status);
            var gl = await BalancesAsync(check, f.PlatformId);
            Assert.Equal(535m - 107m, gl.GetValueOrDefault(GatewayAccountResolver.StandardClearingCode));   // บัญชีพัก −107
            Assert.Equal(0m, gl.GetValueOrDefault("111"));                                                 // เงินสดไม่ขยับ (เดิม Cr 107)
            Assert.Equal(0m, gl.GetValueOrDefault("11310"));                                               // ลูกหนี้ศูนย์ (เดิม Dr 107 ค้าง)
            Assert.Equal(-400m, gl.GetValueOrDefault("41000"));                                            // รายได้ 500 − 100
            Assert.Equal(-28m, gl.GetValueOrDefault("21911"));                                             // ภาษีขาย 35 − 7
            // ไม่มีใบสำคัญคืนเงินของรายการนี้ (ใบลดหนี้คือรายการบัญชีเดียว) · ไม่มีเรื่องค้าง
            var after = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id);
            Assert.Null(after.LastRefundJournalEntryId);
            Assert.Null(after.RefundCreditNoteProblem);
            Assert.Equal(107m, after.RefundedAmount);
        });
    }
}
