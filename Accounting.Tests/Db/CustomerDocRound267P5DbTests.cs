using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Handlers;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Svc = Accounting.Services.Implementations.CustomerDocumentResponseService;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 267 แพ็ก P5 บน PostgreSQL จริง (DOCUMENT_FLOW §2.12n-P5 · คำตัดสิน 267-1-9 · 267-1-13 · 267-1-43 · 267-1-70…267-1-77):
/// ผู้ขายรับทราบ/ไม่รับใบสั่งซื้อ (S31 · หนึ่งคำตอบต่อ Rev) · แถวหลักฐานของเส้น API (append-only) · LINE Login จำข้อมูลผู้ลงนาม
/// (S34 ไม่ติ๊ก = ไม่มีโปรไฟล์/ความยินยอม · เข้ารหัส · เติมฟอร์ม · S35 ถอน = ลบโปรไฟล์อย่างเดียว · purge 24 เดือน · DSR) · ใบวางบิลจ่ายทั้งใบผ่านเส้นเงินเดิม
/// </summary>
// รอบ 267 P5: เทสต์ของ P2 รัน CustomerSignMigration ซ้ำ (DROP/CREATE TRIGGER) — ช่วงนั้น trigger append-only หายชั่วขณะ ⇒ คลาสที่ตรวจ trigger ต้องไม่รันขนานกัน
[Collection(CustomerDocumentActionsTriggerCollection.Name)]
[Trait("Category", "Db")]
public class CustomerDocRound267P5DbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerDocRound267P5DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("customer-doc-267p5-db");
    private static readonly Svc.Caller Who = new("203.0.113.15", "Mozilla/5.0 (p5)");
    private static readonly DateTime Today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static IPaymentProvider Provider() => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => "gw267p5",
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay }, false, false, true),
        _ => null,
    });

    /// <summary>LINE ปลอม: ช่องเปิดใช้ · code "good-*" ⇒ userId ที่ตามมาหลังขีด · อื่น ๆ = LINE ปฏิเสธ</summary>
    private static ILineSignerLoginVerifier Line(bool enabled = true) => F4Proxy<ILineSignerLoginVerifier>.Create((m, a) => m switch
    {
        nameof(ILineSignerLoginVerifier.SignerLineChannelIdAsync) => enabled ? "1650000267" : null,
        nameof(ILineSignerLoginVerifier.ResolveSignerLineUserAsync) => a[0] is string code && code.StartsWith("good-") ? "U" + code[5..] : null,
        _ => null,
    });

    private sealed record Seed(Guid CompanyId, Guid ContactId, DocumentPayLinkService Links, Svc Responses, CustomerSignerProfileService Profiles, LocalDiskFileStorage Storage);

    private static Seed SeedServices(Accounting.Data.AccountingDbContext db, Guid companyId, Guid contactId, bool lineEnabled = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "cdp267p5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var profiles = new CustomerSignerProfileService(db, NullLogger<CustomerSignerProfileService>.Instance, Line(lineEnabled), storage, Config());
        var responses = new Svc(db, NullLogger<Svc>.Instance, storage: storage, profiles: profiles);
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        return new Seed(companyId, contactId, links, responses, profiles, storage);
    }

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, bool supplier = false, bool lineEnabled = true)
    {
        var company = new Company { Name = "บริษัท ผู้ส่ง 267P5 " + tag, TaxId = "0105556000007", Email = "office@sender267p5.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = supplier ? "บริษัท ผู้ขาย 267P5 จำกัด" : "บริษัท ลูกค้า 267P5 จำกัด", ContactType = ContactType.JuristicPerson,
            IsCustomer = !supplier, IsSupplier = supplier, TaxId = "0107537000017", Email = "office@party267p5.co.th",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return SeedServices(db, company.Id, contact.Id, lineEnabled);
    }

    private static async Task<Document> AddDocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string prefix, DateTime? docDate = null)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type,
            DocumentNumber = prefix + "-267P5-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = docDate ?? Today.AddDays(-2), DueDate = type == DocumentType.Quotation ? Today.AddDays(10) : null,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "สินค้า ก", Quantity = 10, Unit = "ชิ้น", UnitPrice = 100m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static async Task<string> OpenAsync(Accounting.Data.AccountingDbContext db, Seed s, Document doc)
    {
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, doc.Id, "test");
        Assert.True(link.Ok, link.Refusal);
        await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.SignLinkIssuedAt, (DateTime?)DateTime.UtcNow));
        db.ChangeTracker.Clear();
        return link.Token!;
    }

    private static CustomerSignRule.SignerInput Sg(string first = "สมศักดิ์", string last = "ส่งไว", string? email = "somsak@party267p5.co.th", bool tick = true)
        => new(first, last, "ผู้จัดการฝ่ายขาย", tick, CustomerSignText.Version, email);

    private static async Task<CustomerDocPageView> PageAsync(Seed s, string token) => (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;

    private static Svc.RespondRequest Req(CustomerDocPageView page, string action, CustomerSignRule.SignerInput? signer = null, string? message = null,
        DateTime? promised = null, string? ticket = null, bool remember = false)
        => new(action, signer ?? Sg(), null, null, null, message, page.Respond?.RevisionNumber ?? page.RevisionNumber, page.Respond?.ContentHash, null,
            promised, ticket, remember,
            // ฝ่ายค้าน 267-P5 MED-2: รุ่น/ภาษาของข้อความยินยอมที่หน้าแสดง (ค่าจากเซิร์ฟเวอร์ — หน้าส่งกลับ)
            SignerConsentVersion: page.Respond?.Labels.GetValueOrDefault("lineConsentVersion"));

    // ───────── ใบสั่งซื้อ: ผู้ขายรับทราบ / ไม่รับ ─────────

    [Fact]
    public async Task CDP267P5Db_PoAcknowledge_S31_แถวหลักฐาน_ภาพฉาย_outbox_audit_หนึ่งคำตอบต่อRev()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "po-ack " + Guid.NewGuid().ToString("N")[..6], supplier: true);
        var po = await AddDocAsync(db, s, DocumentType.PurchaseOrder, "PO");
        var token = await OpenAsync(db, s, po);
        var page = await PageAsync(s, token);
        Assert.Contains(page.Actions, a => a.Id == CustomerDocPage.ActionPoAcknowledge && a.Enabled);
        Assert.Contains(page.Actions, a => a.Id == CustomerDocPage.ActionPoDecline && a.Enabled);
        var ackSpec = page.Respond!.Actions.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge);
        Assert.True(ackSpec.NeedsPromisedDate);
        Assert.Equal("none", ackSpec.Signature);
        Assert.Contains("เป็นบุคคลของ บริษัท ผู้ขาย 267P5 จำกัด", ackSpec.Attestation);

        // S31: วันส่งก่อนวันที่ใบสั่งซื้อ = 400 · ไม่มีแถว · ใบไม่ถูกแตะ
        var early = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoAcknowledge, promised: po.DocumentDate.AddDays(-1)), Who);
        Assert.Equal(400, early.Status);
        Assert.Contains("ไม่ก่อนวันที่ใบสั่งซื้อ", early.Message);
        Assert.False(await db.CustomerDocumentActions.AnyAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == po.Id));
        // ไม่ติ๊กรับรอง = 400 (ตัวตนครบเหมือนทุกเส้น)
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoAcknowledge, Sg(tick: false)), Who)).Status);

        var promised = Today.AddDays(7);
        var ok = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoAcknowledge, promised: promised), Who);
        Assert.True(ok.Success, ok.Message);
        db.ChangeTracker.Clear();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == po.Id && d.CompanyId == s.CompanyId);
        Assert.Equal(nameof(CustomerSignRule.Kind.PoAcknowledged), doc.CustomerResponse);
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == po.Id);
        Assert.Equal(nameof(CustomerSignRule.Kind.PoAcknowledged), ev.Action);
        Assert.Equal(CustomerSignRule.IdentityFull, ev.IdentityLevel);
        Assert.Equal(promised, ev.PromisedDate);
        Assert.Equal("บริษัท ผู้ขาย 267P5 จำกัด", ev.SignerOrgName);
        Assert.Equal(CustomerSignText.TextHash(ackSpec.Attestation), ev.AttestationTextHash);
        var outbox = await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.DocumentId == po.Id);
        Assert.Equal("purchase_order.acknowledged", outbox.EventType);
        Assert.Equal(ev.Id, outbox.CustomerActionId);
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == po.Id.ToString()).ToListAsync();
        var row = Assert.Single(audit, a => a.NewValues != null && a.NewValues.Contains("PurchaseOrderAcknowledgedOnline"));
        Assert.DoesNotContain("สมศักดิ์", row.NewValues);                         // 267-1-51: audit ไม่คัดลอกข้อมูลผู้ลงนาม
        Assert.DoesNotContain("203.0.113.15", row.NewValues);
        Assert.Equal("signed", SignRequestKinds.Status(doc, DateTime.UtcNow)!.Code);

        // หนึ่งคำตอบต่อ Rev: รับทราบซ้ำ/ไม่รับทีหลัง = ปิด (409) · แถวเดียว
        var again = await PageAsync(s, token);
        Assert.False(again.Actions.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge).Enabled);
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoDecline, message: "ของหมด"), Who)).Status);
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == po.Id));

        // ฝ่ายค้าน 267-P5 MED-7: แก้ใบสั่งซื้อที่ผู้ขายรับทราบแล้ว (ออก Rev) ⇒ ต้องยืนยันก่อน · ยืนยันแล้วยังต้องมีฉบับที่ลงนามของ Rev เดิมก่อน
        var none = new Accounting.Models.DTOs.Document.UpdateDocumentRequest(null, null, null, null, null, null) with { Notes = "แก้ราคา" };
        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var noAck = await Assert.ThrowsAsync<InvalidOperationException>(() => Docs(r1).UpdateDocumentAsync(s.CompanyId, po.Id, none));
            Assert.Contains("ผู้ขายรับทราบใบสั่งซื้อออนไลน์แล้ว", noAck.Message);
        }
        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            var snap = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(r2).UpdateDocumentAsync(s.CompanyId, po.Id,
                none with { AcknowledgeRevisionResetsAcceptance = true }));
            Assert.Equal("DOCPAGE-267-SNAPSHOT", snap.RuleCode);   // ฉบับที่ลงนามยังไม่ถูกสร้าง (ไม่มีตัวสร้าง PDF ในเทสต์) ⇒ ไม่ออก Rev
        }
        db.ChangeTracker.Clear();
        Assert.Equal(0, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == po.Id)).RevisionNumber);
    }

    [Fact]
    public async Task CDP267P5Db_PoDecline_ต้องมีเหตุ_ผู้ซื้อส่งใหม่_เปิดตอบอีกครั้ง_ใบเสนอราคาของบริษัทอื่นเข้าเส้นนี้ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "po-dec " + Guid.NewGuid().ToString("N")[..6], supplier: true);
        var po = await AddDocAsync(db, s, DocumentType.PurchaseOrder, "PO");
        var token = await OpenAsync(db, s, po);
        var page = await PageAsync(s, token);
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoDecline), Who)).Status);
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionPoDecline, message: "สินค้าหมดสต็อก"), Who);
        Assert.True(r.Success, r.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(nameof(CustomerSignRule.Kind.PoDeclined), (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == po.Id)).CustomerResponse);
        Assert.Equal("purchase_order.declined", (await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.DocumentId == po.Id)).EventType);
        var closed = await PageAsync(s, token);
        Assert.Contains("ไม่รับใบสั่งซื้อนี้แล้ว", closed.Actions.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge).DisabledReason);

        // ผู้ซื้อส่งให้ตอบใหม่ (SignRequestKinds.MarkIssued) ⇒ เปิดอีกครั้ง
        var tracked = await db.Documents.SingleAsync(d => d.Id == po.Id && d.CompanyId == s.CompanyId);
        SignRequestKinds.MarkIssued(tracked, DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True((await PageAsync(s, token)).Actions.Single(a => a.Id == CustomerDocPage.ActionPoAcknowledge).Enabled);

        // tenant: token ของใบนี้กับ companyId อื่น = 404
        Assert.Equal(404, (await s.Responses.RespondAsync(Guid.NewGuid(), token, Req(page, CustomerDocPage.ActionPoAcknowledge), Who)).Status);
    }

    // ───────── เส้น API ลงแถวหลักฐานชุดเดียวกัน (267-1-43 ข้อ 2) ─────────

    [Fact]
    public async Task CDP267P5Db_ApiEvidence_ผู้เขียนเดียว_RoPA_appendOnly()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "api " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var ev = Svc.ApiEvidence(qt, "บริษัท ลูกค้า 267P5 จำกัด", DocumentSignedContent.Hash(qt, qt.Lines), "สมใจ ระบบภายนอก", "ผู้อำนวยการ", "somjai@party267p5.co.th",
            "อนุมัติผ่าน ERP", "abc", "198.51.100.9", Guid.NewGuid().ToString(), DateTime.UtcNow);
        await Svc.AppendEvidenceAsync(db, ev);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var row = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == ev.Id && a.CompanyId == s.CompanyId);
        Assert.Equal(CustomerSignRule.ChannelApi, row.Channel);
        Assert.Equal(CustomerSignRule.IdentityApiReported, row.IdentityLevel);
        Assert.Equal(1, await db.PdpaProcessingActivities.CountAsync(r => r.CompanyId == s.CompanyId && r.Purpose == CustomerSignText.RopaPurpose));
        // ครั้งที่สอง: ไม่เพิ่ม RoPA ซ้ำ
        await Svc.AppendEvidenceAsync(db, Svc.ApiEvidence(qt, null, "v2:x", "คนที่สอง", null, null, null, null, null, "u", DateTime.UtcNow));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.PdpaProcessingActivities.CountAsync(r => r.CompanyId == s.CompanyId && r.Purpose == CustomerSignText.RopaPurpose));
        // append-only: แก้ชนิดช่องทาง/วันส่ง = ฐานปฏิเสธ (trigger) — ฐานเทสต์สร้างด้วย EnsureCreated (ไม่มี trigger) ⇒ รันมิเกรชันของตารางนี้ก่อน (แบบ CSR267Db · รันซ้ำได้)
        foreach (var sql in CustomerSignMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.CustomerDocumentActions.Where(a => a.Id == ev.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Channel, CustomerSignRule.ChannelLink)));
        Assert.Contains("append-only", ex.ToString());
        await Assert.ThrowsAnyAsync<Exception>(() => db.CustomerDocumentActions.Where(a => a.Id == ev.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.PromisedDate, (DateTime?)DateTime.UtcNow)));
        // ทิศตรงข้าม: การเติมฉบับที่ลงนามครั้งแรก (ทางที่อนุญาต) ยังทำได้
        Assert.Equal(1, await db.CustomerDocumentActions.Where(a => a.Id == ev.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.SignedPdfSha256, "f00d")));
    }

    // ───────── LINE Login จำข้อมูลผู้ลงนาม (267-1-13) ─────────

    private static string StateOf(string authorizeUrl)
    {
        var q = authorizeUrl[(authorizeUrl.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        return q["state"];
    }

    private static async Task<CustomerSignerProfileService.LoginResult> LoginResultAsync(Seed s, string token, string user = "good-signer1")
    {
        var start = await s.Profiles.LoginUrlAsync(s.CompanyId, token);
        Assert.NotNull(start);
        Assert.Contains(Uri.EscapeDataString("https://app.nextacc.test/pages/pay-document.html"), start!.Url);
        return await s.Profiles.CompleteLoginAsync(s.CompanyId, token, user, StateOf(start.Url), start.BrowserNonce);
    }

    private static async Task<string> LoginAsync(Seed s, string token, string user = "good-signer1")
    {
        var r = await LoginResultAsync(s, token, user);
        Assert.NotNull(r.Ticket);
        return r.Ticket!;
    }

    /// <summary>S34 — เข้าด้วย LINE แล้วไม่ติ๊กจำ ⇒ ไม่มีโปรไฟล์ ไม่มีแถวความยินยอม · การลงนามสำเร็จตามปกติ</summary>
    [Fact]
    public async Task CDP267Db_NoConsentNoProfile()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "s34 " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);
        var ticket = await LoginAsync(s, token);
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, ticket: ticket, remember: false), Who);
        Assert.True(r.Success, r.Message);
        Assert.False(await db.CustomerSignerProfiles.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == s.CompanyId));
        Assert.False(await db.PdpaConsentRecords.AnyAsync(c => c.CompanyId == s.CompanyId && c.Purpose == CustomerSignerProfileRule.ConsentPurpose));
        Assert.Null((await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id)).SignerProfileId);
        // ทิศตรงข้าม: ติ๊กจำแต่ไม่มีตั๋ว LINE ⇒ ไม่มีอะไรถูกเก็บ (ไม่มีกุญแจเจ้าของข้อมูล)
        var qt2 = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token2 = await OpenAsync(db, s, qt2);
        var r2 = await s.Responses.RespondAsync(s.CompanyId, token2, Req(await PageAsync(s, token2), CustomerDocPage.ActionAccept, remember: true), Who);
        Assert.True(r2.Success, r2.Message);
        Assert.False(await db.CustomerSignerProfiles.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == s.CompanyId));
        // ตั๋วของใบอื่นใช้กับใบนี้ไม่ได้
        Assert.Null(await s.Profiles.ActiveProfileIdAsync(s.CompanyId, token2, ticket, Sg()));
    }

    [Fact]
    public async Task CDP267P5Db_RememberWithLine_ความยินยอม_เข้ารหัส_เติมฟอร์มครั้งหน้า_ยังต้องติ๊กเอง_RoPA()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "remember " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token = await OpenAsync(db, s, qt);
        var ticket = await LoginAsync(s, token);
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept, ticket: ticket, remember: true), Who);
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain("ไม่สำเร็จ", r.Message);
        db.ChangeTracker.Clear();

        var profile = await db.CustomerSignerProfiles.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId);
        Assert.Equal("สมศักดิ์", profile.FirstName);
        Assert.Equal("somsak@party267p5.co.th", profile.Email);
        Assert.Equal(s.ContactId, profile.ContactId);
        Assert.Equal(CustomerSignerProfileRule.SubjectKey(s.CompanyId, "1650000267", "Usigner1"), profile.SubjectKey);
        // เข้ารหัสขณะจัดเก็บ (PDPA ม.37) — ค่าดิบในฐานไม่ใช่ชื่อ
        var raw = await db.Database.SqlQuery<string>($"SELECT \"FirstName\" AS \"Value\" FROM \"CustomerSignerProfiles\" WHERE \"Id\" = {profile.Id}").SingleAsync();
        Assert.NotEqual("สมศักดิ์", raw);
        Assert.DoesNotContain("สมศักดิ์", raw);
        var consent = await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == profile.ConsentRecordId && c.CompanyId == s.CompanyId);
        Assert.Equal(CustomerSignerProfileRule.ConsentPurpose, consent.Purpose);
        Assert.Equal(CustomerSignerProfileRule.StoredVersion(false), consent.PolicyVersion);   // MED-2: รุ่น + ภาษาของข้อความที่หน้าแสดง
        Assert.Equal(CustomerSignerProfileRule.ConsentEvidenceHash(s.CompanyId, profile.SubjectKey,
            CustomerSignerProfileRule.ConsentText(false, await db.Companies.AsNoTracking().Where(c => c.Id == s.CompanyId).Select(c => c.Name).SingleAsync()),
            consent.PolicyVersion, consent.GrantedAt), consent.EvidenceHash);
        Assert.Equal("system", consent.EvidenceSource);
        Assert.Null(consent.WithdrawnAt);
        Assert.Equal(1, await db.PdpaProcessingActivities.CountAsync(x => x.CompanyId == s.CompanyId && x.Purpose == CustomerSignerProfileRule.RopaPurpose));
        // audit (append-only) ทั้งแถวที่ระบบบันทึกเอง + แถวเหตุการณ์ — ไม่มีชื่อ/อีเมล/กุญแจเจ้าของข้อมูล (AuditRedaction)
        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == profile.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.NewValues != null && a.NewValues.Contains("CustomerSignerProfileSaved"));
        Assert.All(audits, a => Assert.DoesNotContain("สมศักดิ์", a.NewValues ?? ""));
        Assert.All(audits, a => Assert.DoesNotContain("somsak@", a.NewValues ?? ""));
        Assert.All(audits, a => Assert.DoesNotContain(profile.SubjectKey, a.NewValues ?? ""));

        // ครั้งหน้า (ใบใหม่): เข้าด้วย LINE ⇒ ค่าที่เติม = ที่จำไว้ (ไม่มีช่องรับรอง) · ส่งโดยไม่ติ๊ก = ยังปฏิเสธ
        var qt2 = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token2 = await OpenAsync(db, s, qt2);
        var login = await LoginResultAsync(s, token2);
        Assert.Equal(new CustomerSignerProfileRule.Prefill("สมศักดิ์", "ส่งไว", "ผู้จัดการฝ่ายขาย", "somsak@party267p5.co.th"), login.Prefill);
        var page2 = await PageAsync(s, token2);
        var noTick = await s.Responses.RespondAsync(s.CompanyId, token2, Req(page2, CustomerDocPage.ActionAccept, Sg(tick: false), ticket: login.Ticket), Who);
        Assert.Equal(400, noTick.Status);
        var ok = await s.Responses.RespondAsync(s.CompanyId, token2, Req(page2, CustomerDocPage.ActionAccept, ticket: login.Ticket), Who);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(profile.Id, (await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt2.Id)).SignerProfileId);

        // LINE คนอื่น ⇒ ไม่มีค่าที่เติม · state ปลอม/code ที่ LINE ปฏิเสธ ⇒ ไม่มีตั๋ว
        var other = await LoginResultAsync(s, token2, "good-someoneelse");
        Assert.Null(other.Prefill);
        var st = (await s.Profiles.LoginUrlAsync(s.CompanyId, token2))!;
        Assert.Null((await s.Profiles.CompleteLoginAsync(s.CompanyId, token2, "good-x", "bad.state.value", st.BrowserNonce)).Ticket);
        Assert.Null((await s.Profiles.CompleteLoginAsync(s.CompanyId, token2, "bad-code", StateOf(st.Url), st.BrowserNonce)).Ticket);
        // LOW-1: state ถูกต้องแต่มาจากเบราว์เซอร์อื่น (ไม่มี cookie ของ nonce) ⇒ ไม่มีตั๋ว
        Assert.Null((await s.Profiles.CompleteLoginAsync(s.CompanyId, token2, "good-signer1", StateOf(st.Url), null)).Ticket);

        // MED-1: คนอื่นบนตั๋วเดิม (เครื่องใช้ร่วม) ลงนามด้วยชื่อตัวเอง ⇒ แถวหลักฐานไม่อ้างโปรไฟล์ของเจ้าของตั๋ว
        var qt3 = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token3 = await OpenAsync(db, s, qt3);
        var t3 = await LoginAsync(s, token3);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token3, Req(await PageAsync(s, token3), CustomerDocPage.ActionAccept, Sg("สมหญิง", "คนอื่น"), ticket: t3), Who)).Success);
        Assert.Null((await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt3.Id)).SignerProfileId);

        // MED-2: หน้าค้างจากรุ่นก่อน (ไม่ส่งรุ่น/ส่งรุ่นเก่า) ⇒ ลงนามสำเร็จ แต่ไม่จำ + บอกตรง ๆ
        var qt4 = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token4 = await OpenAsync(db, s, qt4);
        var t4 = await LoginAsync(s, token4, "good-newperson");
        var stale = await s.Responses.RespondAsync(s.CompanyId, token4,
            Req(await PageAsync(s, token4), CustomerDocPage.ActionAccept, Sg("ใหม่", "คนใหม่"), ticket: t4, remember: true) with { SignerConsentVersion = "267-5/th" }, Who);
        Assert.True(stale.Success, stale.Message);
        Assert.Contains("รุ่นเก่า", stale.Message);
        Assert.Equal(1, await db.CustomerSignerProfiles.IgnoreQueryFilters().CountAsync(p => p.CompanyId == s.CompanyId));
    }

    /// <summary>S35 — ถอนความยินยอม ⇒ โปรไฟล์ถูกลบ (ลบจริง) + แถวความยินยอมถูกประทับถอน · แถวหลักฐานการลงนามคงอยู่</summary>
    [Fact]
    public async Task CDP267Db_WithdrawDeletesProfileOnly()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "s35 " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token = await OpenAsync(db, s, qt);
        var ticket = await LoginAsync(s, token);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept, ticket: ticket, remember: true), Who)).Success);
        var profile = await db.CustomerSignerProfiles.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId);

        Assert.NotNull(await s.Profiles.WithdrawAsync(s.CompanyId, token, null));            // ไม่มีตั๋ว = ถอนไม่ได้ (ข้อความ)
        Assert.Null(await s.Profiles.WithdrawAsync(s.CompanyId, token, ticket));
        db.ChangeTracker.Clear();
        Assert.False(await db.CustomerSignerProfiles.IgnoreQueryFilters().AnyAsync(p => p.Id == profile.Id));
        var consent = await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == profile.ConsentRecordId);
        Assert.NotNull(consent.WithdrawnAt);
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id);
        Assert.Equal("สมศักดิ์", ev.SignerFirstName);                                         // หลักฐานการลงนามไม่ถูกแตะ
        Assert.Null(ev.ErasedAt);
        Assert.Null(await s.Profiles.ActiveProfileIdAsync(s.CompanyId, token, ticket, Sg()));
        // MED-4: ถอนแล้ว ⇒ อีเมล/IP/UA บนแถวความยินยอมถูกล้าง · hash + เวลาให้/ถอน คงเป็นหลักฐาน
        Assert.Null(consent.SubjectContact);
        Assert.Null(consent.IpAddress);
        Assert.Null(consent.UserAgent);
        Assert.NotNull(consent.PseudonymisedAt);
        Assert.False(string.IsNullOrEmpty(consent.EvidenceHash));
        Assert.Contains("ล้างอีเมล/IP/UA", consent.WithdrawnReason);
    }

    [Fact]
    public async Task CDP267P5Db_Purge24เดือน_และถอนแล้ว_ลบ_ที่ยังใช้อยู่คงไว้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "purge " + Guid.NewGuid().ToString("N")[..6]);
        async Task<CustomerSignerProfile> AddAsync(string key, DateTime lastUsed, bool withdrawn)
        {
            var c = new PdpaConsentRecord { CompanyId = s.CompanyId, Purpose = CustomerSignerProfileRule.ConsentPurpose, WithdrawnAt = withdrawn ? DateTime.UtcNow : null };
            db.PdpaConsentRecords.Add(c);
            var p = new CustomerSignerProfile { CompanyId = s.CompanyId, SubjectKey = key.PadRight(64, '0'), FirstName = "ก", LastName = "ข", ConsentRecordId = c.Id, LastUsedAt = lastUsed };
            db.CustomerSignerProfiles.Add(p);
            await db.SaveChangesAsync();
            return p;
        }
        var stale = await AddAsync("a", DateTime.UtcNow.AddMonths(-25), false);
        var withdrawn = await AddAsync("b", DateTime.UtcNow, true);
        var fresh = await AddAsync("c", DateTime.UtcNow.AddMonths(-23), false);
        var purged = await CustomerSignerProfileService.PurgeAsync(db, DateTime.UtcNow);
        Assert.True(purged >= 2);
        db.ChangeTracker.Clear();
        var left = await db.CustomerSignerProfiles.IgnoreQueryFilters().Where(p => p.CompanyId == s.CompanyId).Select(p => p.Id).ToListAsync();
        Assert.Equal(new[] { fresh.Id }, left);
        Assert.NotNull((await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == stale.ConsentRecordId)).WithdrawnAt);
        Assert.Null((await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == fresh.ConsentRecordId)).WithdrawnAt);
    }

    /// <summary>DSR ม.30/31/33 ของผู้ลงนาม (บุคคลที่สาม) — ค้นด้วยอีเมล · ลบโปรไฟล์ได้เสมอ · หลักฐานในอายุเก็บคงไว้ (S33) · พ้นแล้วล้าง</summary>
    [Fact]
    public async Task CDP267P5Db_SignerDsr_access_erase_เคารพlegalHold()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "dsr " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token = await OpenAsync(db, s, qt);
        var ticket = await LoginAsync(s, token);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept, ticket: ticket, remember: true), Who)).Success);
        // หลักฐานเก่าพ้นอายุเก็บ (เอกสารลงวันที่ 6 ปีก่อน)
        var old = await AddDocAsync(db, s, DocumentType.Quotation, "QT", docDate: Today.AddYears(-6));
        var oldEv = Svc.ApiEvidence(old, "บริษัท ลูกค้า 267P5 จำกัด", "v2:old", "สมศักดิ์ ส่งไว", null, "SOMSAK@party267p5.co.th", null, null, "198.51.100.1", "u", DateTime.UtcNow);
        await Svc.AppendEvidenceAsync(db, oldEv);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // MED-3: คนชื่อซ้ำ (อีเมลอื่น) บนเอกสารอื่น — ต้องไม่ถูกดึง/ลบ
        var twin = await AddDocAsync(db, s, DocumentType.Quotation, "QT", docDate: Today.AddYears(-6));
        var twinEv = Svc.ApiEvidence(twin, "บริษัท อื่น จำกัด", "v2:twin", "สมศักดิ์", null, "somsak.other@elsewhere.co.th", null, null, "198.51.100.2", "u", DateTime.UtcNow);
        twinEv.SignerLastName = "ส่งไว";
        await Svc.AppendEvidenceAsync(db, twinEv);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var byEmail = new CustomerSignerProfileService.SignerQuery("somsak@party267p5.co.th", null, null, null);
        var report = await s.Profiles.AccessAsync(s.CompanyId, byEmail);
        Assert.Single(report.Profiles);
        Assert.Single(report.Consents);                                                          // MED-4: แถวความยินยอม (อีเมล/IP/UA) อยู่ในรายงาน
        Assert.Equal("somsak@party267p5.co.th", report.Consents[0]["email"]);
        Assert.Equal("203.0.113.15", report.Consents[0]["ip"]);
        Assert.Equal(2, report.Evidence.Count);
        Assert.Equal(4, report.TotalRecords);
        Assert.DoesNotContain(report.Evidence, e => (Guid)e["id"]! == twinEv.Id);
        // ชื่ออย่างเดียว = ปฏิเสธ · ชื่อ + เลขเอกสาร ⇒ เฉพาะคนนั้น (อีเมลบนแถวนั้น)
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Profiles.AccessAsync(s.CompanyId, new(null, "สมศักดิ์", "ส่งไว", null)));
        var byNameDoc = await s.Profiles.AccessAsync(s.CompanyId, new(null, "สมศักดิ์", "ส่งไว", twin.DocumentNumber));
        Assert.Equal("somsak.other@elsewhere.co.th", byNameDoc.Email);
        Assert.Equal(new[] { twinEv.Id }, byNameDoc.Evidence.Select(e => (Guid)e["id"]!));
        Assert.Empty(byNameDoc.Profiles);
        // บริษัทอื่นไม่เห็น (tenant)
        Assert.Equal(0, (await s.Profiles.AccessAsync(Guid.NewGuid(), byEmail)).TotalRecords);

        var erase = await s.Profiles.EraseAsync(s.CompanyId, byEmail, "dpo@test");
        Assert.Equal(1, erase.ProfilesDeleted);
        Assert.Equal(1, erase.ConsentsAnonymized);
        Assert.Equal(1, erase.EvidenceErased);
        Assert.Equal(1, erase.EvidenceHeld);
        Assert.Contains(erase.Notes, n => n.Contains("legal hold"));
        db.ChangeTracker.Clear();
        Assert.False(await db.CustomerSignerProfiles.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == s.CompanyId));
        var oldRow = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == oldEv.Id);
        Assert.NotNull(oldRow.ErasedAt);
        Assert.Equal(QuotationAcceptance.Anonymized, oldRow.SignerFirstName);
        Assert.Null(oldRow.Ip);
        var heldRow = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id);
        Assert.Null(heldRow.ErasedAt);                                                           // ทิศตรงข้าม: ในอายุเก็บ = คงไว้
        Assert.Equal("สมศักดิ์", heldRow.SignerFirstName);
        var twinRow = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == twinEv.Id);
        Assert.Null(twinRow.ErasedAt);                                                           // MED-3 ทิศตรงข้าม: คนชื่อซ้ำไม่ถูกแตะ
        Assert.Equal("somsak.other@elsewhere.co.th", twinRow.SignerEmail);
        var consentAfter = await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.CompanyId == s.CompanyId && c.Purpose == CustomerSignerProfileRule.ConsentPurpose);
        Assert.Null(consentAfter.SubjectContact);                                                // MED-4: erase ล้างแถวความยินยอม
        Assert.Null(consentAfter.IpAddress);
        Assert.NotNull(consentAfter.WithdrawnAt);
    }

    /// <summary>MED-3: ชื่อ + เลขเอกสารพบหลายคน (อีเมลต่างกัน) ⇒ ให้ DPO เลือก — ไม่รวมเอง ไม่ลบอะไร</summary>
    [Fact]
    public async Task CDP267P5Db_SignerDsr_ชื่อซ้ำบนเอกสารเดียว_ให้DPOเลือก_ไม่ลบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "dsr2 " + Guid.NewGuid().ToString("N")[..6]);
        var old = await AddDocAsync(db, s, DocumentType.Quotation, "QT", docDate: Today.AddYears(-6));
        foreach (var e in new[] { "a@one.co.th", "b@two.co.th" })
        {
            var ev = Svc.ApiEvidence(old, "x", "v2:" + e, "สมชาย", null, e, null, null, null, "u", DateTime.UtcNow);
            ev.SignerLastName = "ใจดี";
            await Svc.AppendEvidenceAsync(db, ev);
        }
        await db.SaveChangesAsync();
        var q = new CustomerSignerProfileService.SignerQuery(null, "สมชาย", "ใจดี", old.DocumentNumber);
        var ex = await Assert.ThrowsAsync<CustomerSignerProfileService.SignerSubjectAmbiguousException>(() => s.Profiles.EraseAsync(s.CompanyId, q, "dpo@test"));
        Assert.Equal(2, ex.Candidates.Count);
        Assert.DoesNotContain(ex.Candidates, c => c.Contains("one.co.th") && c.StartsWith("a@"));   // ปิดบังอีเมล
        db.ChangeTracker.Clear();
        Assert.False(await db.CustomerDocumentActions.AnyAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == old.Id && a.ErasedAt != null));
    }

    [Fact]
    public async Task CDP267P5Db_ปิดLINELogin_ไม่มีเส้น_ลงนามได้ตามปกติ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "noline " + Guid.NewGuid().ToString("N")[..6], lineEnabled: false);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var token = await OpenAsync(db, s, qt);
        Assert.False(await s.Profiles.AvailableAsync());
        Assert.Null(await s.Profiles.LoginUrlAsync(s.CompanyId, token));
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept, ticket: "x.y.z", remember: true), Who);
        Assert.True(r.Success, r.Message);                                                       // kill-switch: ลงนามครบ
        Assert.Contains("จำข้อมูลกับ LINE ไม่สำเร็จ", r.Message);                                   // ตั๋วใช้ไม่ได้ — บอกตรง ๆ ไม่เงียบ
        Assert.False(await db.CustomerSignerProfiles.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == s.CompanyId));
    }

    // ───────── ใบวางบิลจ่ายทั้งใบ (267-1-9 v2) — เส้นเงินเดิม ─────────

    private sealed record Books(Guid CompanyId, Guid ContactId, Guid ArId, Guid RevenueId, Guid BankId);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Books> BooksAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบใบวางบิล 267P5", TaxId = "0105556000025", IsVatRegistered = false };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var bankGl = Acc(company.Id, "11122-001", "เงินฝากออมทรัพย์", AccountType.Asset, level: 5);
        var ar = Acc(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var group216 = Acc(company.Id, "216", "เงินมัดจำและเงินรับล่วงหน้าอื่น", AccountType.Liability, level: 3);
        var revenue = Acc(company.Id, "41100", "รายได้จากการให้บริการ", AccountType.Revenue);
        var rounding = Acc(company.Id, DocumentRounding.AccountCode, "ผลต่างจากการปัดเศษ", AccountType.Expense);
        db.ChartOfAccounts.AddRange(bankGl, ar, group216, revenue, rounding, Acc(company.Id, "111", "เงินสด", AccountType.Asset));
        await db.SaveChangesAsync();
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์", BankName = "กสิกร", AccountNumber = "1234567890", LinkedAccountId = bankGl.Id, IsActive = true };
        db.BankAccounts.Add(bank);
        // บุคคลธรรมดา ⇒ ไม่มีหน้าที่หัก ณ ที่จ่าย (ชุดใบจ่ายรวมได้)
        var customer = new Contact { CompanyId = company.Id, Name = "คุณลูกค้า ใบวางบิล", ContactType = ContactType.Individual, IsCustomer = true,
            Address = "1 ถนนทดสอบ กรุงเทพมหานคร 10110" };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        return new Books(company.Id, customer.Id, ar.Id, revenue.Id, bank.Id);
    }

    private static async Task<Document> InvoiceAsync(Accounting.Data.AccountingDbContext db, Books b, decimal amount, decimal wht = 0m)
    {
        var d = new Document
        {
            CompanyId = b.CompanyId, ContactId = b.ContactId, DocumentType = DocumentType.Invoice, DocumentNumber = "INV-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = Today, SubTotal = amount, TotalAmount = amount, BalanceDue = amount, WithholdingTaxAmount = wht,
            Currency = "THB", WhtCertAutoIssue = false, BankAccountId = b.BankId,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = amount, Amount = amount });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        var je = new JournalEntry
        {
            CompanyId = b.CompanyId, EntryNumber = "SV-" + d.DocumentNumber, EntryDate = d.DocumentDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.Sales, Description = "ออกใบ " + d.DocumentNumber, Reference = d.DocumentNumber,
            TotalDebit = amount, TotalCredit = amount, SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = b.ArId, DebitAmount = amount, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = b.RevenueId, CreditAmount = amount, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return d;
    }

    private static async Task<Document> BillingNoteAsync(Accounting.Data.AccountingDbContext db, Books b, params Document[] invoices)
    {
        var bn = new Document
        {
            CompanyId = b.CompanyId, ContactId = b.ContactId, DocumentType = DocumentType.BillingNote, DocumentNumber = "BN-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = Today, TotalAmount = invoices.Sum(i => i.TotalAmount), BalanceDue = invoices.Sum(i => i.TotalAmount), Currency = "THB",
        };
        var n = 1;
        foreach (var i in invoices)
            bn.Lines.Add(new DocumentLine { LineOrder = n++, Description = i.DocumentNumber, Quantity = 1, UnitPrice = i.TotalAmount, Amount = i.TotalAmount, SourceDocumentId = i.Id });
        db.Documents.Add(bn);
        await db.SaveChangesAsync();
        return bn;
    }

    private static DocumentService Docs(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static DocumentPaymentHandler Handler(Accounting.Data.AccountingDbContext db)
        => new(db, Docs(db), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null), NullLogger<DocumentPaymentHandler>.Instance, null,
            new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance));

    private static PaymentIntent Intent(Books b, Document bn, decimal amount) => new()
    {
        CompanyId = b.CompanyId, ProviderCode = "gw267p5", SourceKind = PaymentSourceKind.Document, SourceId = bn.Id, Amount = amount, Currency = "THB",
        Status = PaymentIntentStatus.Succeeded, MethodKind = PaymentMethodKind.PromptPay, IsTestMode = false, PaidAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task CDP267P5Db_BillingNotePayAll_การรับชำระเดียว_จัดสรรรายใบ_ทำซ้ำไม่ลงซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var b = await BooksAsync(db);
        var i1 = await InvoiceAsync(db, b, 1_000m);
        var i2 = await InvoiceAsync(db, b, 500m);
        var bn = await BillingNoteAsync(db, b, i1, i2);
        await db.CompanySettings.Where(x => x.CompanyId == b.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.PostPayEmailReceipt, true));
        Assert.Equal(new[] { i1.Id, i2.Id }, (await BillingNoteChildren.MapAsync(db, b.CompanyId, new[] { bn.Id, i1.Id }, default))[bn.Id]);
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var plan = await links.BillingNotePlanAsync(b.CompanyId, bn.Id);
        Assert.NotNull(plan);
        Assert.True(plan!.Payable, plan.Refusal);
        Assert.Equal(1_500m, plan.Total);

        var intent = Intent(b, bn, 1_500m);
        using (var db2 = DbTestDatabase.TryCreateContext()!) await Handler(db2).HandleSucceededAsync(intent);
        db.ChangeTracker.Clear();
        Assert.Equal(0m, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == i1.Id)).BalanceDue);
        Assert.Equal(0m, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == i2.Id)).BalanceDue);
        var reference = DocumentPaymentHandler.PaymentReference(intent.Id);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == b.CompanyId && p.Reference == reference && !p.IsDeleted);
        Assert.Equal(1_500m, payment.Amount);
        var allocations = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == payment.Id).ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(1_000m, allocations.Single(a => a.DocumentId == i1.Id).AllocatedAmount);

        // ฝ่ายค้าน 267-P5 MED-5: งานหลังรับชำระเข้าคิวด้วยทางเข้าจริง (ผู้ให้บริการรับชำระ) — ทุกทางเข้า 1:1
        var run = await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == b.CompanyId && r.PaymentId == payment.Id);
        Assert.Equal(nameof(PaymentEntrySource.Gateway), run.Source);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);

        // webhook ซ้ำ ⇒ ไม่มีการรับชำระที่สอง
        using (var db3 = DbTestDatabase.TryCreateContext()!) await Handler(db3).HandleSucceededAsync(intent);
        Assert.Equal(1, await db.Payments.AsNoTracking().CountAsync(p => p.CompanyId == b.CompanyId && p.Reference == reference && !p.IsDeleted));
        // ชำระครบแล้ว ⇒ จ่ายรวมไม่ได้อีก (ยอด 0)
        Assert.False((await links.BillingNotePlanAsync(b.CompanyId, bn.Id))!.Payable);
    }

    [Fact]
    public async Task CDP267P5Db_BillingNotePayAll_ยอดไม่ตรง_หรือมีภาษีหัก_ล้มดัง_ไม่แตะใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var b = await BooksAsync(db);
        var i1 = await InvoiceAsync(db, b, 1_000m);
        var i2 = await InvoiceAsync(db, b, 500m);
        var bn = await BillingNoteAsync(db, b, i1, i2);
        var intent = Intent(b, bn, 1_499m);   // ระหว่างลูกค้าจ่าย ยอดเปลี่ยน/จ่ายไม่ครบ
        using (var db2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(db2).HandleSucceededAsync(intent));
            Assert.Equal(BillingNoteCombinedPay.RuleCode, ex.RuleCode);
            Assert.Contains("บันทึกรับชำระด้วยมือ", ex.Message);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(1_000m, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == i1.Id)).BalanceDue);
        Assert.False(await db.Payments.AsNoTracking().AnyAsync(p => p.CompanyId == b.CompanyId && !p.IsDeleted));

        // ผู้ซื้อนิติบุคคลที่ต้องหัก ณ ที่จ่าย ⇒ ชุดใบจ่ายรวมไม่ได้ (ชำระรายใบ)
        var withholder = new Contact { CompanyId = b.CompanyId, Name = "บริษัท หักภาษี จำกัด", ContactType = ContactType.JuristicPerson, IsCustomer = true, TaxId = "0107537000017" };
        db.Contacts.Add(withholder);
        await db.SaveChangesAsync();
        var bw = b with { ContactId = withholder.Id };
        var w1 = await InvoiceAsync(db, bw, 1_000m, wht: 30m);
        var bnW = await BillingNoteAsync(db, bw, w1);
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var plan = await links.BillingNotePlanAsync(b.CompanyId, bnW.Id);
        Assert.False(plan!.Payable);
        Assert.Contains("ภาษีหัก ณ ที่จ่าย", plan.Refusal);
        // ไม่ใช่ใบวางบิล ⇒ null
        Assert.Null(await links.BillingNotePlanAsync(b.CompanyId, w1.Id));
    }
}

/// <summary>รอบ 267 P5: คลาสที่รันมิเกรชันของตารางหลักฐาน (DROP/CREATE TRIGGER) หรือพึ่ง trigger นั้น — รันทีละคลาส (ไม่ขนาน)</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CustomerDocumentActionsTriggerCollection
{
    public const string Name = "CustomerDocumentActions triggers";
}
