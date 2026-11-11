using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Portal;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Accounting.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 259-3 (ทีม E UX) — เส้นจริงบน PostgreSQL:
/// E-H2 พอร์ทัลผู้ขาย: ออกลิงก์ลูกค้าใหม่ไม่ได้ (ลิงก์ลูกค้าเดิมยังใช้ได้ + อยู่ในรายการ + เพิกถอนได้) · รายการ/เพิกถอน/เพิกถอนซ้ำ · ส่งอีเมลจริงเฉพาะเมื่อระบบตั้งอีเมล ·
/// E-M1 พอร์ทัลลูกค้า: ปุ่มชำระ = ลิงก์หน้าชำระของเอกสาร (มีช่องทางออนไลน์) · ไม่มีช่องทางออนไลน์ = โอน/สลิป · ใบของลูกค้าอื่น = ไม่พบ ·
/// สลิปที่ไม่ส่งยอด = ยอดสุทธิจากตัวคิดเดียวกับหน้าชำระ
/// </summary>
[Trait("Category", "Db")]
public class UxConsolidationRound259DbTests
{
    private readonly ITestOutputHelper _out;
    public UxConsolidationRound259DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    // ═════════════ E-H2 พอร์ทัลผู้ขาย ═════════════

    private static async Task<(Guid CompanyId, Guid ContactId)> VendorSeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัท 259 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000009" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ผู้ขาย 259 จำกัด", IsSupplier = true, ContactType = ContactType.JuristicPerson };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return (company.Id, contact.Id);
    }

    [Fact]
    public async Task พอร์ทัลผู้ขาย_ออกลิงก์ลูกค้าไม่ได้_ลิงก์ลูกค้าเดิมยังใช้ได้และเพิกถอนได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, contactId) = await VendorSeedAsync(db);
        var svc = new VendorPortalService(db, NullLogger<VendorPortalService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IssueAsync(cid, contactId, "Customer", 30, null, null));
        Assert.Contains("พอร์ทัลลูกค้า", ex.Message);
        Assert.False(await db.VendorPortalTokens.AnyAsync(t => t.CompanyId == cid));

        // ทิศตรงข้าม: ผู้ขายออกได้ · บทบาทที่เก็บ = Vendor
        var (raw, stored) = await svc.IssueAsync(cid, contactId, "Vendor", 30, " ap@vendor259.test ", null);
        Assert.Equal(VendorPortalTokenRules.RoleVendor, stored.Role);
        Assert.Equal("ap@vendor259.test", stored.RecipientEmail);
        Assert.NotNull(await svc.ResolveAsync(raw));

        // ลิงก์ลูกค้าที่ออกก่อนรอบนี้ (ข้อมูลเดิม) — ยังเปิดได้ (ไม่ทำลายข้อมูล) และอยู่ในรายการ
        var legacyRaw = "legacy-" + Guid.NewGuid().ToString("N");
        var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyRaw))).ToLowerInvariant();
        var legacy = new VendorPortalToken { CompanyId = cid, ContactId = contactId, Role = "Customer", TokenHash = legacyHash, ExpiresAt = DateTime.UtcNow.AddDays(10) };
        db.VendorPortalTokens.Add(legacy);
        await db.SaveChangesAsync();
        Assert.NotNull(await svc.ResolveAsync(legacyRaw));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc2 = new VendorPortalService(c2, NullLogger<VendorPortalService>.Instance);
        var list = await svc2.ListAsync(cid);
        Assert.Equal(2, list.Count);
        var legacyRow = Assert.Single(list, r => r.Id == legacy.Id);
        Assert.Contains("พอร์ทัลลูกค้า", legacyRow.RoleLabel);
        Assert.True(legacyRow.CanRevoke);
        Assert.Equal("ผู้ขาย 259 จำกัด", legacyRow.ContactName);

        await svc2.RevokeAsync(cid, legacy.Id, "ย้ายไปพอร์ทัลลูกค้า");
        Assert.Null(await new VendorPortalService(DbTestDatabase.TryCreateContext()!, NullLogger<VendorPortalService>.Instance).ResolveAsync(legacyRaw));
        // เพิกถอนซ้ำ = บอกตรง ๆ ไม่ประทับทับ
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc2.RevokeAsync(cid, legacy.Id, "อีกครั้ง"));
        var after = await new VendorPortalService(DbTestDatabase.TryCreateContext()!, NullLogger<VendorPortalService>.Instance).ListAsync(cid);
        var revoked = Assert.Single(after, r => r.Id == legacy.Id);
        Assert.Equal(VendorPortalTokenRules.StatusRevoked, revoked.Status);
        Assert.False(revoked.CanRevoke);
        Assert.Equal("ย้ายไปพอร์ทัลลูกค้า", revoked.RevokedReason);
        // ลิงก์ผู้ขายยังใช้ได้ (เพิกถอนเฉพาะตัวที่เลือก)
        Assert.Equal(VendorPortalTokenRules.StatusActive, Assert.Single(after, r => r.Id == stored.Id).Status);
    }

    [Fact]
    public async Task พอร์ทัลผู้ขาย_บริษัทอื่นไม่เห็นรายการและเพิกถอนไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (a, contactA) = await VendorSeedAsync(db);
        var (b, _) = await VendorSeedAsync(db);
        var svc = new VendorPortalService(db, NullLogger<VendorPortalService>.Instance);
        var (_, stored) = await svc.IssueAsync(a, contactA, "Vendor", 30, null, null);
        Assert.Empty(await svc.ListAsync(b));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RevokeAsync(b, stored.Id, "x"));
        Assert.Equal(VendorPortalTokenRules.StatusActive, Assert.Single(await svc.ListAsync(a)).Status);
    }

    [Fact]
    public async Task พอร์ทัลผู้ขาย_อีเมลผู้รับ_เข้าคิวไม่เก็บtokenดิบ_ลิงก์ของผู้รับออกตอนส่ง_อีเมลผิดรูปปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, contactId) = await VendorSeedAsync(db);
        var svc = new VendorPortalService(db, NullLogger<VendorPortalService>.Instance);

        // ฝ่ายค้าน M3: อีเมลหลายรายการ/ผิดรูป ⇒ ปฏิเสธก่อนออก token (ไม่มีแถว)
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IssueAsync(cid, contactId, "Vendor", 30, "a@x.test, b@x.test", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.IssueAsync(cid, contactId, "Vendor", 30, "not-an-email", null));
        Assert.False(await db.VendorPortalTokens.AnyAsync(t => t.CompanyId == cid));

        var (raw, withEmail) = await svc.IssueAsync(cid, contactId, "Vendor", 30, "ap@vendor259.test", null);
        var r = await svc.QueueInviteAsync(cid, withEmail);
        Assert.True(r.Requested);
        Assert.True(r.Queued);
        var q = await db.EmailQueues.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.EntityType == VendorPortalTokenRules.QueueEntityType);
        Assert.Equal("ap@vendor259.test", q.ToEmail);
        Assert.Equal(withEmail.Id, q.EntityId);
        Assert.Contains(VendorPortalTokenRules.LinkPlaceholder, q.Body);
        Assert.DoesNotContain(raw, q.Body);   // คิวไม่เก็บ token ดิบ

        // ตอนส่ง: ลิงก์ของผู้รับ = token ใหม่ (ผู้ติดต่อ/วันหมดอายุเดียวกัน) ใช้ได้จริง · ต้นทางยังใช้ได้ ⇒ ไม่ข้าม
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();
        Assert.Null(await VendorPortalService.InviteSkipReasonAsync(db, q, default));
        var (url, problem) = await VendorPortalService.IssueInviteUrlAsync(db, cfg, q, default);
        Assert.Null(problem);
        var inviteRaw = System.Web.HttpUtility.ParseQueryString(new Uri(url!).Query)["t"]!;
        Assert.NotEqual(raw, inviteRaw);
        var resolved = await new VendorPortalService(DbTestDatabase.TryCreateContext()!, NullLogger<VendorPortalService>.Instance).ResolveAsync(inviteRaw);
        Assert.NotNull(resolved);
        Assert.Equal(contactId, resolved!.ContactId);
        Assert.Equal(withEmail.ExpiresAt, resolved.ExpiresAt, TimeSpan.FromSeconds(1));

        // ทิศตรงข้าม: ต้นทางถูกเพิกถอนก่อนส่ง ⇒ ข้ามพร้อมเหตุ ไม่ออกลิงก์
        await svc.RevokeAsync(cid, withEmail.Id, "ส่งผิดคน");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.NotNull(await VendorPortalService.InviteSkipReasonAsync(c2, q, default));
        var before = await c2.VendorPortalTokens.CountAsync(t => t.CompanyId == cid);
        var (url2, problem2) = await VendorPortalService.IssueInviteUrlAsync(c2, cfg, q, default);
        Assert.Null(url2);
        Assert.NotNull(problem2);
        Assert.Equal(before, await c2.VendorPortalTokens.CountAsync(t => t.CompanyId == cid));

        // ไม่ระบุอีเมล ⇒ ไม่เข้าคิว
        var (_, noEmail) = await svc.IssueAsync(cid, contactId, "Vendor", 30, "  ", null);
        Assert.False((await svc.QueueInviteAsync(cid, noEmail)).Requested);
        Assert.Equal(1, await c2.EmailQueues.CountAsync(x => x.CompanyId == cid && x.EntityType == VendorPortalTokenRules.QueueEntityType));
    }

    [Fact]
    public async Task พอร์ทัลผู้ขาย_เพิกถอนลิงก์ลูกค้าเดิมทั้งหมด_ลิงก์ผู้ขายไม่ถูกแตะ_บริษัทอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, contactId) = await VendorSeedAsync(db);
        var (other, otherContact) = await VendorSeedAsync(db);
        VendorPortalToken Legacy(Guid c, Guid ct) => new() { CompanyId = c, ContactId = ct, Role = "Customer", TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(30) };
        db.VendorPortalTokens.AddRange(Legacy(cid, contactId), Legacy(cid, contactId), Legacy(other, otherContact));
        await db.SaveChangesAsync();
        var svc = new VendorPortalService(db, NullLogger<VendorPortalService>.Instance);
        var (_, vendor) = await svc.IssueAsync(cid, contactId, "Vendor", 30, null, null);

        Assert.Equal(2, await svc.RevokeLegacyCustomerAsync(cid, ""));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0, await c2.VendorPortalTokens.CountAsync(t => t.CompanyId == cid && t.Role == "Customer" && t.RevokedAt == null));
        Assert.Null((await c2.VendorPortalTokens.SingleAsync(t => t.Id == vendor.Id)).RevokedAt);
        Assert.Equal(1, await c2.VendorPortalTokens.CountAsync(t => t.CompanyId == other && t.RevokedAt == null));
        Assert.Equal(0, await new VendorPortalService(c2, NullLogger<VendorPortalService>.Instance).RevokeLegacyCustomerAsync(cid, ""));
    }

    // ═════════════ M2 เช็กลิสต์ปิดงวดชุดเดียว: พร้อมปิด ⇔ ปิดได้ ═════════════

    [Fact]
    public async Task ปิดงวด_เช็กลิสต์ไม่พร้อม_ปิดไม่ได้_แก้แล้วพร้อม_ปิดได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var start = new DateTime(2019, 3, 1);
        var period = new FiscalPeriod { CompanyId = s.CompanyId, Name = "2019/03", Year = 2019, Month = 3, StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open };
        db.FiscalPeriods.Add(period);
        var draft = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Quotation, Status = DocumentStatus.Draft,
            DocumentNumber = "DRAFT-" + Guid.NewGuid().ToString("N"), DocumentDate = start.AddDays(5), TotalAmount = 100m, Currency = "THB",
        };
        db.Documents.Add(draft);
        await db.SaveChangesAsync();

        var check = await new PreCloseChecklistService(db).RunAsync(s.CompanyId, 2019, 3);
        Assert.False(check.ReadyToClose);
        Assert.Contains(check.Items, i => i.Code == "DRAFT_DOCS" && !i.Passed && i.Severity == "Error");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AccountingService(db).CloseFiscalPeriodAsync(s.CompanyId, period.Id));

        // ทิศตรงข้าม: ลบร่างออก ⇒ พร้อม ⇒ ปิดได้ (ผลเดียวกัน)
        await db.Documents.Where(d => d.Id == draft.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.IsDeleted, true));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var ready = await new PreCloseChecklistService(c2).RunAsync(s.CompanyId, 2019, 3);
        Assert.True(ready.ReadyToClose);
        await new AccountingService(c2).CloseFiscalPeriodAsync(s.CompanyId, period.Id);
        Assert.Equal(FiscalPeriodStatus.Closed, (await DbTestDatabase.TryCreateContext()!.FiscalPeriods.AsNoTracking().SingleAsync(p => p.Id == period.Id)).Status);
    }

    [Fact]
    public async Task ปิดงวด_รายการธนาคารค้างรายการเดียว_เช็กลิสต์และปุ่มปิดบล็อกเหมือนกัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var start = new DateTime(2019, 4, 1);
        var period = new FiscalPeriod { CompanyId = s.CompanyId, Name = "2019/04", Year = 2019, Month = 4, StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open };
        db.FiscalPeriods.Add(period);
        var bankId = await db.Set<BankAccount>().Where(b => b.CompanyId == s.CompanyId).Select(b => b.Id).FirstAsync();
        db.BankTransactions.Add(new BankTransaction { CompanyId = s.CompanyId, BankAccountId = bankId, TransactionDate = start.AddDays(3), Amount = 50m,
            Description = "ค้าง", ReconciliationStatus = ReconciliationStatus.Unmatched });
        await db.SaveChangesAsync();
        var check = await new PreCloseChecklistService(db).RunAsync(s.CompanyId, 2019, 4);
        Assert.False(check.ReadyToClose);   // เดิม 1–4 รายการ = คำเตือน (เขียว) แต่ปุ่มปิดบล็อก
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AccountingService(db).CloseFiscalPeriodAsync(s.CompanyId, period.Id));
    }

    // ═════════════ E-M1 พอร์ทัลลูกค้า → หน้าชำระของเอกสาร ═════════════

    private static readonly SecretProtector Secrets = new("ux-259-db");
    private const string Code = "gw259";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://app.nextacc.test",
        ["Portal:SigningKey"] = "ux-259-portal-signing-key-for-db-tests-0123456789",
    }).Build();

    private static IPaymentProvider Gateway() => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => Code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private sealed record PaySeed(Guid CompanyId, Guid ContactId, Document Invoice, DocumentPayLinkService Links, LocalDiskFileStorage Storage);

    private static async Task<PaySeed> PaySeedAsync(AccountingDbContext db, bool online)
    {
        var company = new Company { Name = "บริษัท 259 จ่าย " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000009", Email = "office@seller259.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 259 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer259.co.th", Address = "1 ถนนสุขุมวิท กรุงเทพฯ",
        };
        db.Contacts.Add(contact);
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "บริษัท 259 จำกัด", BankName = "กสิกรไทย", AccountNumber = "1234567890", PromptPayId = "0812345678" };
        db.Set<BankAccount>().Add(bank);
        if (online)
            db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = Code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV259-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date.AddDays(-1),
            SubTotal = 1_000m, VatAmount = 70m, WithholdingTaxAmount = 30m, TotalAmount = 1_040m, BalanceDue = 1_040m, Currency = "THB",
            BankAccountId = bank.Id,
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "ux259-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var links = new DocumentPayLinkService(db, new[] { Gateway() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        return new PaySeed(company.Id, contact.Id, inv, links, storage);
    }

    private static PortalService Portal(AccountingDbContext db, PaySeed s)
    {
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance);
        return new PortalService(db, Config(), storage: s.Storage, intents: intents, payLinks: s.Links);
    }

    [Fact]
    public async Task พอร์ทัล_ปุ่มชำระ_มีช่องทางออนไลน์_ได้ลิงก์หน้าชำระของเอกสาร_ใบลูกค้าอื่นไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: true);
        var r = await Portal(db, s).GetDocumentPayLinkAsync(s.CompanyId, s.ContactId, s.Invoice.Id);
        Assert.NotNull(r.Url);
        Assert.Contains("pay-document", r.Url!);
        Assert.Null(r.Refusal);
        Assert.False(r.SlipFallback);
        // ลิงก์เดียวกับที่พนักงาน/อีเมลออก (หนึ่งลิงก์ต่อใบ)
        var again = await s.Links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, null);
        Assert.Equal(r.Url, again.Url);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Portal(db, s).GetDocumentPayLinkAsync(s.CompanyId, Guid.NewGuid(), s.Invoice.Id));
    }

    [Fact]
    public async Task ทิศตรงข้าม_พอร์ทัล_ไม่มีช่องทางออนไลน์_ไม่มีลิงก์_ใช้โอนสลิปของพอร์ทัล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var r = await Portal(db, s).GetDocumentPayLinkAsync(s.CompanyId, s.ContactId, s.Invoice.Id);
        Assert.Null(r.Url);
        Assert.True(r.SlipFallback);

        // ใบชำระครบแล้ว ⇒ ไม่มีลิงก์ ไม่มีกล่องโอน (เหตุจากตัวตัดสินเดียวกับหน้าชำระ)
        await db.Documents.Where(d => d.Id == s.Invoice.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.BalanceDue, 0m));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var paid = await Portal(c2, s with { Links = new DocumentPayLinkService(c2, new[] { Gateway() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, s.Storage) })
            .GetDocumentPayLinkAsync(s.CompanyId, s.ContactId, s.Invoice.Id);
        Assert.Null(paid.Url);
        Assert.False(paid.SlipFallback);
        Assert.Equal("เอกสารนี้ชำระครบแล้ว", paid.Refusal);   // ฝ่ายค้าน L1: ข้อความถึงลูกค้า ไม่ใช่เหตุภายในของร้าน
    }

    [Fact]
    public async Task พอร์ทัล_สลิปไม่ส่งยอด_ใช้ยอดสุทธิจากตัวคิดเดียวกับหน้าชำระ_ส่งยอดงวดยังได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var view = (await s.Links.TransferForDocumentAsync(s.CompanyId, s.Invoice.Id))!;
        Assert.Null(view.Refusal);
        var res = await Portal(db, s).UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.Invoice.Id, Slip("net"), null);
        Assert.Equal(view.Net, res.Amount);

        // ทิศตรงข้าม: ยอดงวดที่ส่งมาเอง (ไม่เกินยอดค้างที่เหลือ) ยังผ่านด่านเดิม — ใบใหม่ ไม่ปน
        var s2 = await PaySeedAsync(db, online: false);
        var part = await Portal(db, s2).UploadDocumentSlipAsync(s2.CompanyId, s2.ContactId, s2.Invoice.Id, Slip("part"), 300m);
        Assert.Equal(300m, part.Amount);
    }

    // ═════════════ E-M4 คิวสลิปรอยืนยัน: Autopilot ชี้ไปคิวเจ้าของเดียว ═════════════

    [Fact]
    public async Task Autopilot_สลิปรอยืนยัน_ขึ้นเรื่องค้างพร้อมลิงก์คิว_ยืนยัน_ปฏิเสธแล้วหาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var res = await Portal(db, s).UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.Invoice.Id, Slip("ap"), null);

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var ap = new AutopilotService(c2, F4Proxy<IBankReceiptSettleService>.Create((_, _) => null));
        var issues = await ap.IssuesAsync(s.CompanyId, DateTime.UtcNow);
        var slip = Assert.Single(issues, i => i.Kind == "SlipPending");
        Assert.Equal(AutopilotService.SlipQueueHref, slip.ActionHref);
        Assert.Equal(s.Invoice.Id, slip.DocumentId);
        Assert.Null(slip.ActionPath);   // ไม่มีปุ่มยืนยันซ้ำที่ Autopilot — ยืนยันที่คิวเจ้าของเดียว
        var st = await ap.SettingsAsync(s.CompanyId);
        Assert.Contains(st.Hub!, h => h.Key == "slipQueue" && h.StatusLabel == "รอยืนยัน 1 รายการ");

        // ทิศตรงข้าม: รายการจบแล้ว (ปฏิเสธ/หมดอายุ) ⇒ ไม่ขึ้น
        await c2.PaymentIntents.Where(i => i.Id == res.PaymentIntentId).ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, PaymentIntentStatus.Failed));
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var ap3 = new AutopilotService(c3, F4Proxy<IBankReceiptSettleService>.Create((_, _) => null));
        Assert.DoesNotContain(await ap3.IssuesAsync(s.CompanyId, DateTime.UtcNow), i => i.Kind == "SlipPending");
        // บริษัทอื่นไม่เห็นสลิปของบริษัทนี้
        var other = await PaySeedAsync(c3, online: false);
        Assert.DoesNotContain(await ap3.IssuesAsync(other.CompanyId, DateTime.UtcNow), i => i.Kind == "SlipPending");
    }

    // ═════════════ 3ก-1 เช็กลิสต์เริ่มต้นใช้งาน: นับจากฐานจริง ═════════════

    [Fact]
    public async Task เริ่มต้นใช้งาน_ข้อเท็จจริงจากฐาน_บริษัทใหม่ไม่ครบ_เพิ่มแล้วเสร็จ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await PaySeedAsync(db, online: false);
        var f = await Accounting.Controllers.GettingStartedController.FactsAsync(db, s.Links, s.CompanyId);
        Assert.False(f.CompanyInfoComplete);   // seed ไม่มีที่อยู่บริษัท
        Assert.Equal(1, f.BankAccounts);
        Assert.Equal(1, f.Contacts);
        Assert.Equal(1, f.Documents);
        Assert.False(f.OnlinePayReady);
        Assert.True(f.TransferReady);          // บัญชีธนาคารมีเลขบัญชี/พร้อมเพย์ = รับโอน+สลิปได้

        await db.Companies.Where(c => c.Id == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Address, "1 ถนนสีลม กรุงเทพฯ"));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.True((await Accounting.Controllers.GettingStartedController.FactsAsync(c2, null, s.CompanyId)).CompanyInfoComplete);
        // บริษัทอื่นไม่ปน
        var other = await Accounting.Controllers.GettingStartedController.FactsAsync(c2, null, Guid.NewGuid());
        Assert.Equal(0, other.Documents);
        Assert.False(other.CompanyInfoComplete);
    }

    private static IFormFile Slip(string tag)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 r259 " + tag + Guid.NewGuid());
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }
}
