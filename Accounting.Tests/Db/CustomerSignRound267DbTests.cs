using System.Text.RegularExpressions;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdpa;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Svc = Accounting.Services.Implementations.CustomerDocumentResponseService;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 267 แพ็ก P2 — ผู้ลงนาม + บัญชีหลักฐาน + ฉบับที่ลูกค้าเซ็น บน PostgreSQL จริง (DOCUMENT_FLOW §2.12n-P2 · คำตัดสิน 267-1-4…6 · 267-1-11 · 267-1-33…):
/// ยอมรับด้วยตัวตนครบ = ธุรกรรมเดียว (ภาพฉายบนใบ + แถวหลักฐาน + audit chain + outbox + RoPA) · ขาดติ๊ก/ตำแหน่ง = ปฏิเสธ ไม่มีแถว ·
/// เอกสารถูกแก้ = 409 · ไม่รับ/ขอแก้ + หนึ่งคำตอบต่อ Rev · แถวหลักฐานแก้/ลบไม่ได้ (trigger) · ฉบับที่ลงนาม SHA-256 คงที่ + ถูกแก้ไม่ส่งออก ·
/// แยกบริษัท · ลิงก์รุ่นก่อนลงบัญชีหลักฐานด้วย · รหัสยืนยันอีเมล · ลบตามคำขอเคารพ legal hold
/// </summary>
[Trait("Category", "Db")]
public class CustomerSignRound267DbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerSignRound267DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("customer-sign-267-db");
    private static readonly Svc.Caller Who = new("203.0.113.9", "Mozilla/5.0 (test)");
    private static readonly byte[] PdfBytes = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 1, 2, 3, 4, 5 };

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private sealed class Mail
    {
        public readonly List<(string To, string Subject, string Body)> Plain = new();
        public readonly List<(string To, string Subject, string File, byte[] Bytes)> Attached = new();
        public bool Ok = true;
        public IEmailService Proxy() => F4Proxy<IEmailService>.Create((m, a) => m switch
        {
            nameof(IEmailService.IsSystemEmailConfiguredAsync) => true,
            nameof(IEmailService.TrySendAsync) => Send(() => Plain.Add(((string)a[0]!, (string)a[1]!, (string)a[2]!))),
            nameof(IEmailService.TrySendWithAttachmentAsync) => Send(() => Attached.Add(((string)a[0]!, (string)a[1]!, (string)a[3]!, (byte[])a[4]!))),
            _ => null,
        });
        private object Send(Action record) { if (!Ok) return false; record(); return true; }
    }

    private sealed class PdfFake
    {
        public int Calls;
        public bool Fail;
        public IPdfGenerationService Proxy() => F4Proxy<IPdfGenerationService>.Create((name, args) =>
        {
            if (name != nameof(IPdfGenerationService.GenerateDocumentPdfAsync)) return null;
            Calls++;
            if (Fail) throw new InvalidOperationException("ตัวสร้าง PDF ล่ม (จำลอง)");
            var req = (GeneratePdfRequest)args[1]!;
            return Task.FromResult(new GeneratePdfResponse(req.DocumentId, "QT", "QT.pdf", "application/pdf", PdfBytes.Length, PdfBytes, DateTime.UtcNow));
        });
    }

    private static IPaymentProvider Provider(string code) => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private sealed record Seed(Guid CompanyId, Guid ContactId, DocumentPayLinkService Links, Svc Responses, LocalDiskFileStorage Storage, PdfFake Pdf, Mail Mail, string Root);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, ContactType contactType = ContactType.JuristicPerson)
    {
        var company = new Company { Name = "บริษัท ผู้ขาย 267P2 " + tag, TaxId = "0105556000007", Email = "office@seller267.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 267 จำกัด", IsCustomer = true, ContactType = contactType,
            TaxId = "0107537000017", Email = "ap@buyer267.co.th",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "csr267-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var pdf = new PdfFake();
        var mail = new Mail();
        var responses = new Svc(db, NullLogger<Svc>.Instance, leads: null, pdf: pdf.Proxy(), storage: storage, rate: null, email: mail.Proxy());
        var links = new DocumentPayLinkService(db, new[] { Provider("gw267p2") }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        return new Seed(company.Id, contact.Id, links, responses, storage, pdf, mail, root);
    }

    private static async Task<Document> AddDocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string prefix,
        DateTime? due = null, Action<Document>? tweak = null)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type,
            DocumentNumber = prefix + "267P2-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3), DueDate = due,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการติดตั้ง", Quantity = 2, Unit = "งาน", UnitPrice = 500m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        tweak?.Invoke(doc);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>ออกลิงก์หน้าเอกสาร + ผู้ขายส่งให้ตอบรับ (สิ่งที่ <c>SignRequestService</c> ทำ) → token ที่ลูกค้าถือ</summary>
    private static async Task<string> OpenAsync(Accounting.Data.AccountingDbContext db, Seed s, Document doc)
    {
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, doc.Id, "test");
        Assert.True(link.Ok, link.Refusal);
        await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.SignLinkIssuedAt, (DateTime?)DateTime.UtcNow));
        db.ChangeTracker.Clear();
        return link.Token!;
    }

    private static CustomerSignRule.SignerInput Sg(bool tick = true, string? title = "ผู้จัดการฝ่ายจัดซื้อ", string? email = null)
        => new("สมหญิง", "ใจดี", title, tick, CustomerSignText.Version, email);

    private static async Task<CustomerDocPageView> PageAsync(Seed s, string token) => (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;

    private static Svc.RespondRequest Req(CustomerDocPageView page, string action, CustomerSignRule.SignerInput? signer = null,
        string? reason = null, string? message = null, string? signature = null, string? otp = null)
        => new(action, signer ?? Sg(), signature, null, reason, message, page.Respond?.RevisionNumber ?? page.RevisionNumber, page.Respond?.ContentHash, otp);

    private static string Sig() => QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());

    // ───────── ยอมรับด้วยตัวตนครบ = ธุรกรรมเดียว ─────────

    [Fact]
    public async Task CSR267Db_AcceptFullIdentity_ภาพฉาย_แถวหลักฐาน_audit_outbox_RoPA_ฉบับที่ลงนาม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "accept " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);

        // หน้าเอกสาร: ฟอร์มผู้ลงนามมาจากเซิร์ฟเวอร์ (ข้อความรับรองอ้างชื่อผู้ซื้อ · เลข Rev + hash เนื้อหาที่แสดง · ไม่ต้องใช้รหัสอีเมลเป็นค่าตั้งต้น)
        var page = await PageAsync(s, token);
        var form = page.Respond!;
        Assert.Equal(CustomerSignText.Version, form.AttestationVersion);
        Assert.True(form.TitleRequired);
        Assert.Equal(new[] { CustomerDocPage.ActionAccept, CustomerDocPage.ActionDecline, CustomerDocPage.ActionRequestChange }, form.Actions.Select(a => a.Id));
        var acceptSpec = form.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept);
        Assert.Contains("เป็นบุคคลของ บริษัท ลูกค้า 267 จำกัด", acceptSpec.Attestation);
        Assert.False(acceptSpec.OtpRequired);
        Assert.Equal("optional", acceptSpec.Signature);
        Assert.Contains("มาตรา 9", acceptSpec.Consent);
        Assert.Empty(page.Responses);

        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(email: "somying@buyer267.co.th"), signature: Sig()), Who);
        Assert.True(r.Success, r.Message);
        db.ChangeTracker.Clear();

        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id && d.CompanyId == s.CompanyId);
        Assert.NotNull(doc.QuotationAcceptedAt);
        Assert.Equal("สมหญิง ใจดี", doc.QuotationAcceptedBy);
        Assert.Equal(nameof(CustomerSignRule.Kind.Accepted), doc.CustomerResponse);
        Assert.Equal(doc.QuotationAcceptedAt, doc.CustomerResponseAt);
        Assert.Equal(form.ContentHash, doc.QuotationAcceptContentHash);

        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id);
        Assert.Equal(nameof(CustomerSignRule.Kind.Accepted), ev.Action);
        Assert.Equal(CustomerSignRule.ChannelLink, ev.Channel);
        Assert.Equal(CustomerSignRule.IdentityFull, ev.IdentityLevel);
        Assert.Equal("สมหญิง", ev.SignerFirstName);
        Assert.Equal("ใจดี", ev.SignerLastName);
        Assert.Equal("ผู้จัดการฝ่ายจัดซื้อ", ev.SignerTitle);
        Assert.Equal("บริษัท ลูกค้า 267 จำกัด", ev.SignerOrgName);
        Assert.Equal("somying@buyer267.co.th", ev.SignerEmail);
        Assert.False(ev.EmailVerified);                                                   // ไม่ได้ใช้รหัส = ไม่อ้างว่ายืนยันแล้ว
        Assert.Equal(CustomerSignText.Version, ev.AttestationVersion);
        Assert.Equal(CustomerSignText.TextHash(acceptSpec.Attestation), ev.AttestationTextHash);   // hash ของข้อความที่หน้าแสดงจริง
        Assert.Equal(form.ContentHash, ev.ContentHash);
        Assert.Equal(form.RevisionNumber, ev.RevisionNumber);
        Assert.Equal("203.0.113.9", ev.Ip);
        Assert.Equal(QuotationAcceptance.Sha256Hex(Who.UserAgent), ev.UserAgentHash);
        Assert.Equal(QuotationAcceptance.Sha256Hex(token.ToLowerInvariant()), ev.TokenHash);
        Assert.NotNull(ev.SignatureSha256);
        Assert.Equal(doc.QuotationAcceptSignaturePath, ev.SignatureFilePath);

        // audit chain + outbox + RoPA ในธุรกรรมเดียวกัน
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == qt.Id.ToString() && a.Action == AuditAction.Approve).ToListAsync();
        var accepted = Assert.Single(audit);
        Assert.Contains(ev.Id.ToString(), accepted.NewValues);
        Assert.Contains("ETA-9", accepted.NewValues);
        var outbox = await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.DocumentId == qt.Id);
        Assert.Equal("quotation.accepted", outbox.EventType);
        Assert.Equal(ev.Id, outbox.CustomerActionId);
        Assert.DoesNotContain("203.0.113.9", outbox.PayloadJson);                         // payload ไม่มี IP
        Assert.Equal(1, await db.PdpaProcessingActivities.CountAsync(x => x.CompanyId == s.CompanyId && x.Purpose == CustomerSignText.RopaPurpose));

        // ฉบับที่ลูกค้าเซ็น: สร้างทันที · SHA-256 ของไฟล์ที่เก็บ · อยู่ใต้โฟลเดอร์ของบริษัท · ดาวน์โหลดได้ด้วย token ของใบนี้
        Assert.Equal(CustomerSignRule.FileSha256(PdfBytes), ev.SignedPdfSha256);
        Assert.True(CustomerSignRule.IsOwnSignedCopyLocation(ev.SignedPdfPath, s.Storage.Locator.AttachmentsRoot, s.CompanyId, qt.Id, ev.Id));
        Assert.Equal(PdfBytes, await s.Storage.ReadAllBytesAsync(ev.SignedPdfPath));
        var file = await s.Responses.SignedCopyForTokenAsync(s.CompanyId, token, ev.Id);
        Assert.Equal(PdfBytes, file!.Value.Bytes);
        Assert.Equal(qt.Id, file.Value.DocumentId);
        // audit ของแถวหลักฐาน: แถวสร้าง (ตัวเก็บ audit อัตโนมัติของ context) + การเก็บฉบับที่ลงนามครั้งเดียว
        var evAudit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(CustomerDocumentAction) && a.EntityId == ev.Id.ToString()).ToListAsync();
        Assert.Single(evAudit, a => a.NewValues != null && a.NewValues.Contains("CustomerSignedCopyStored"));
        Assert.Contains(evAudit, a => a.Action == AuditAction.Create && (a.NewValues == null || !a.NewValues.Contains("CustomerSignedCopyStored")));
        // ฝ่ายค้าน 267-P2 H1: audit (ลบตามคำขอไม่ได้) ไม่มีข้อมูลผู้ลงนาม — ทั้งแถว Create อัตโนมัติของแถวหลักฐาน และ audit ที่เขียนเองบนใบ
        var allAudit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId
            && (a.EntityId == ev.Id.ToString() || a.EntityId == qt.Id.ToString())).ToListAsync();
        Assert.NotEmpty(allAudit);
        foreach (var row in allAudit)
        {
            var text = (row.OldValues ?? "") + (row.NewValues ?? "") + (row.IpAddress ?? "");
            foreach (var pii in new[] { "สมหญิง", "ใจดี", "ผู้จัดการฝ่ายจัดซื้อ", "somying@buyer267.co.th", "203.0.113.9", ev.UserAgentHash! })
                Assert.DoesNotContain(pii, text);
        }
        // ตอนแสดง: แถว audit เก่า (ก่อนการแก้) ที่มีค่าเหล่านี้ถูกปิดด้วยรายชื่อเดียวกัน
        Assert.Equal(AuditRedaction.PiiMask, System.Text.Json.Nodes.JsonNode.Parse(
            AuditRedaction.RedactJson("{\"SignerFirstName\":\"สมหญิง\",\"Ip\":\"203.0.113.9\"}", nameof(CustomerDocumentAction))!)!["SignerFirstName"]!.GetValue<string>());

        // หน้าเอกสารหลังยอมรับ: ปุ่มปิด · ไม่มีฟอร์ม · ประวัติ + ฉบับที่ลงนามพร้อมดาวน์โหลด (ไม่มี IP ใน view model)
        var after = await PageAsync(s, token);
        Assert.Null(after.Respond);
        Assert.False(after.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept).Enabled);
        Assert.DoesNotContain(after.Actions, a => a.Id is CustomerDocPage.ActionDecline or CustomerDocPage.ActionRequestChange);
        var item = Assert.Single(after.Responses);
        Assert.Equal(ev.Id, item.ActionId);
        Assert.Equal("สมหญิง ใจดี", item.SignerName);
        Assert.True(item.SignedCopyReady);
        Assert.DoesNotContain("203.0.113.9", System.Text.Json.JsonSerializer.Serialize(after));

        // กดซ้ำ = สถานะเดิม (ไม่มีแถวหลักฐานที่สอง · ไม่มีเหตุการณ์ที่สอง)
        var again = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who);
        Assert.True(again.Success);
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id));
        Assert.Equal(1, await db.SignEventOutbox.CountAsync(e => e.CompanyId == s.CompanyId && e.DocumentId == qt.Id));

        // งาน outbox: ส่งสำเนาทางอีเมลที่ผู้ลงนามให้ไว้ (ไฟล์เดียวกับที่เก็บ) · ไม่สร้างไฟล์ซ้ำ
        var calls = s.Pdf.Calls;
        var copy = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev.Id);
        Assert.True(copy.Done && copy.HasFile, copy.Note);
        Assert.Equal(calls, s.Pdf.Calls);
        var sent = Assert.Single(s.Mail.Attached);
        Assert.Equal("somying@buyer267.co.th", sent.To);
        Assert.Equal(PdfBytes, sent.Bytes);

        // ฝั่งพนักงาน: เห็นหลักฐานครบ + คำแนะนำแปลงเอกสาร + คำเตือนอากรแสตมป์ (บรรทัดพิมพ์เองไม่ผูกสินค้า = อาจเป็นงานบริการ)
        var staff = await s.Responses.EvidenceForStaffAsync(s.CompanyId, qt.Id);
        Assert.Equal("203.0.113.9", Assert.Single(staff.Items).Ip);
        Assert.Equal(CustomerSignText.ConvertSuggestionText, staff.ConvertSuggestion);
        Assert.Equal(new[] { "DeliveryNote", "Invoice", "TaxInvoice" }, staff.ConvertTo);
        Assert.Equal(CustomerSignText.StampDutyWarning, staff.StampDutyWarning);
    }

    // ───────── (S3) สองคนกดยอมรับพร้อมกัน = แถวหลักฐานเดียว · คนที่สองเห็นสถานะเดิม (compare-and-set) ─────────

    [Fact]
    public async Task CSR267Db_ConcurrentAcceptOneRow_สองเครื่องกดพร้อมกัน_ยอมรับครั้งเดียว_แถวเดียว_เหตุการณ์เดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var s = await SeedAsync(db, "race " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);
        db.ChangeTracker.Clear();
        var other = new Svc(db2, NullLogger<Svc>.Instance, leads: null, pdf: s.Pdf.Proxy(), storage: s.Storage, rate: null, email: s.Mail.Proxy());

        var results = await Task.WhenAll(
            s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who),
            other.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept,
                new CustomerSignRule.SignerInput("สมชาย", "รักงาน", "ผู้อำนวยการ", true, CustomerSignText.Version, null)), Who));
        // ทั้งสองได้ 200 (คนที่สองได้ "ถูกยอมรับไปแล้ว") — ไม่มีใครได้ 500 จากการชนกัน
        Assert.All(results, r => Assert.True(r.Success, r.Message));
        db.ChangeTracker.Clear();
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id);
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id && d.CompanyId == s.CompanyId);
        // ภาพฉายบนใบเป็นของผู้ชนะคนเดียวกับแถวหลักฐาน (ไม่ผสมชื่อของสองคน)
        Assert.Equal(ev.SignerFirstName + " " + ev.SignerLastName, doc.QuotationAcceptedBy);
        Assert.Equal(1, await db.SignEventOutbox.CountAsync(e => e.CompanyId == s.CompanyId && e.DocumentId == qt.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == qt.Id.ToString() && a.Action == AuditAction.Approve));
    }

    // ───────── ฝ่ายค้าน 267-P2 M2: ลิงก์รุ่นก่อนเคารพหนึ่งคำตอบต่อ Rev + เกณฑ์รหัสยืนยัน ─────────

    [Fact]
    public async Task CSR267Db_LegacyLinkObeysOneAnswerAndOtp_ไม่รับทางหน้าแล้วยอมรับทางลิงก์เก่า409_ถึงเกณฑ์รหัส409_ทิศตรงข้ามผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "legacy-m2 " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5), tweak: d => LegacySignLinkSeed.Seed(d));
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);
        var declined = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionDecline, reason: "price"), Who);
        Assert.True(declined.Success, declined.Message);
        db.ChangeTracker.Clear();
        var legacy = await s.Responses.Accept(LegacySignLinkSeed.RawOf(qt), new PublicQuotationController.AcceptRequest("คุณอีกคน"), Who);
        Assert.Equal(409, legacy.Status);
        Assert.Contains("ติดต่อผู้ขาย", legacy.Message);                                  // ทางไปต่อ
        db.ChangeTracker.Clear();
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id));

        // ยอดถึงเกณฑ์รหัสของบริษัท ⇒ ลิงก์รุ่นก่อน (ไม่มีฟอร์มรหัส) ยอมรับไม่ได้
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.CustomerSignOtpMinAmount, (decimal?)1_000m));
        var big = await AddDocAsync(db, s, DocumentType.Quotation, "QL", tweak: d => LegacySignLinkSeed.Seed(d));
        db.ChangeTracker.Clear();
        var bigR = await s.Responses.Accept(LegacySignLinkSeed.RawOf(big), new PublicQuotationController.AcceptRequest("คุณเดิม"), Who);
        Assert.Equal(409, bigR.Status);
        Assert.Contains("ลิงก์", bigR.Message);
        // ทิศตรงข้าม: ยอดต่ำกว่าเกณฑ์ ⇒ ลิงก์รุ่นก่อนยอมรับได้ตามเดิม
        var small = await AddDocAsync(db, s, DocumentType.Quotation, "QM", tweak: d => { LegacySignLinkSeed.Seed(d); d.TotalAmount = 500m; });
        db.ChangeTracker.Clear();
        Assert.True((await s.Responses.Accept(LegacySignLinkSeed.RawOf(small), new PublicQuotationController.AcceptRequest("คุณเดิม"), Who)).Success);
    }

    // ───────── ฝ่ายค้าน 267-P2 M3: ฉบับที่ลงนามต้องสร้างก่อนออก Rev · เลิกสร้างแล้วหน้าเลิกบอก "กำลังเตรียม" ─────────

    [Fact]
    public async Task CSR267Db_SnapshotBeforeRevision_ค้างอยู่สร้างให้เสร็จก่อน_ล้มคืนจำนวนค้าง_เลิกสร้างแล้วไม่ค้างกำลังเตรียม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "rev-snap " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        s.Pdf.Fail = true;
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept), Who);
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain("ดาวน์โหลด", r.Message);                                    // ยังไม่มีไฟล์ = ไม่บอกว่าดาวน์โหลดได้
        db.ChangeTracker.Clear();
        Assert.Equal(1, await s.Responses.EnsureSnapshotsBeforeRevisionAsync(s.CompanyId, qt.Id, 0));   // ยังล้ม ⇒ ผู้เรียกไม่ออก Rev
        s.Pdf.Fail = false;
        Assert.Equal(0, await s.Responses.EnsureSnapshotsBeforeRevisionAsync(s.CompanyId, qt.Id, 0));
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id)).SignedPdfSha256);

        // ใบที่เนื้อหาเปลี่ยนก่อนสร้างทัน ⇒ เลิกสร้าง (Abandoned) · ขั้นของ outbox จบ ⇒ หน้าไม่แสดง "กำลังเตรียม" ค้าง
        var qt2 = await AddDocAsync(db, s, DocumentType.Quotation, "QB", due: DateTime.UtcNow.Date.AddDays(5));
        var token2 = await OpenAsync(db, s, qt2);
        s.Pdf.Fail = true;
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token2, Req(await PageAsync(s, token2), CustomerDocPage.ActionAccept), Who)).Success);
        s.Pdf.Fail = false;
        await db.Documents.Where(d => d.Id == qt2.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.TotalAmount, 9_000m));
        db.ChangeTracker.Clear();
        var ev2 = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt2.Id);
        var gone = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev2.Id);
        Assert.True(gone.Done && gone.Abandoned && !gone.HasFile);
        Assert.True(Assert.Single(await s.Responses.HistoryAsync(s.CompanyId, qt2.Id, false)).SignedCopyExpected);   // ขั้นของ outbox ยังไม่ประทับ
        await db.SignEventOutbox.Where(o => o.CompanyId == s.CompanyId && o.CustomerActionId == ev2.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.SignedCopyDoneAt, (DateTime?)DateTime.UtcNow));            // สิ่งที่ SignEventDispatcher ประทับเมื่อ Done
        var item = Assert.Single(await s.Responses.HistoryAsync(s.CompanyId, qt2.Id, false));
        Assert.False(item.SignedCopyExpected);
        Assert.False(item.SignedCopyReady);
    }

    // ───────── ขาดติ๊กรับรอง / ขาดตำแหน่ง = ปฏิเสธ (ทิศตรงข้าม: ครบ = ผ่าน) ─────────

    [Fact]
    public async Task CSR267Db_MissingTickRefused_ไม่ติ๊กหรือไม่กรอกตำแหน่ง_400_ไม่มีแถว_ครบแล้วผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "tick " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);

        var noTick = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(tick: false)), Who);
        Assert.Equal(400, noTick.Status);
        Assert.Contains("ติ๊กรับรอง", noTick.Message);
        var noTitle = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(title: " ")), Who);
        Assert.Equal(400, noTitle.Status);
        var noSigner = await s.Responses.RespondAsync(s.CompanyId, token,
            new Svc.RespondRequest(CustomerDocPage.ActionAccept, null, RevisionNumber: page.Respond!.RevisionNumber, ContentHash: page.Respond.ContentHash), Who);
        Assert.Equal(400, noSigner.Status);
        var oldText = await s.Responses.RespondAsync(s.CompanyId, token,
            Req(page, CustomerDocPage.ActionAccept, new CustomerSignRule.SignerInput("สมหญิง", "ใจดี", "ผจก.", true, "รุ่นที่ไม่มี")), Who);
        Assert.Equal(400, oldText.Status);
        // เส้นของลิงก์รุ่นก่อนใช้ token ของหน้าเอกสารแล้วพิมพ์ชื่ออย่างเดียว = ไม่ผ่านเหมือนกัน (ไม่มีทางอ้อมข้ามด่านผู้ลงนาม)
        var bypass = await s.Responses.Accept(token, new PublicQuotationController.AcceptRequest("ชื่ออย่างเดียว", RevisionNumber: page.Respond.RevisionNumber,
            ContentHash: page.Respond.ContentHash), Who);   // ส่ง hash ที่เห็นด้วย (L5) — ด่านที่ทดสอบคือผู้ลงนาม
        Assert.Equal(400, bypass.Status);
        db.ChangeTracker.Clear();
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());
        Assert.Equal(0, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));
        Assert.Equal(0, await db.SignEventOutbox.CountAsync(e => e.CompanyId == s.CompanyId));

        // ทิศตรงข้าม: ครบ ⇒ ผ่าน
        var ok = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who);
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));
    }

    // ───────── เอกสารถูกแก้ระหว่างที่หน้าค้างอยู่ ⇒ 409 ─────────

    [Fact]
    public async Task CSR267Db_RevisionChanged409_Revหรือเนื้อหาไม่ตรงกับที่เห็น_ไม่บันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "rev " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);

        // ผู้ขายแก้ราคา (เนื้อหาเปลี่ยน · เลข Rev เดิม) ⇒ hash ที่หน้าถืออยู่ไม่ตรง
        await db.Documents.Where(d => d.Id == qt.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.TotalAmount, 2_140m));
        db.ChangeTracker.Clear();
        foreach (var action in new[] { CustomerDocPage.ActionAccept, CustomerDocPage.ActionDecline, CustomerDocPage.ActionRequestChange })
        {
            var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, action, reason: "price", message: "ขอแก้"), Who);
            Assert.Equal(409, r.Status);
            Assert.Equal(CustomerDocPage.RespondStaleMessage, r.Message);
        }
        // เลข Rev เปลี่ยน ⇒ 409 เช่นกัน (แม้ส่ง hash ของฉบับใหม่มาไม่ได้)
        await db.Documents.Where(d => d.Id == qt.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.RevisionNumber, 1).SetProperty(d => d.TotalAmount, 1_070m));
        db.ChangeTracker.Clear();
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who)).Status);
        // ไม่ส่งเลข Rev เลย ⇒ 409
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, new Svc.RespondRequest(CustomerDocPage.ActionAccept, Sg()), Who)).Status);
        Assert.Equal(0, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.CustomerResponse).SingleAsync());

        // ทิศตรงข้าม: โหลดหน้าใหม่ (เห็น Rev 1) ⇒ ยอมรับได้ · แถวหลักฐานจำ Rev ที่เห็น
        var fresh = await PageAsync(s, token);
        Assert.Equal(1, fresh.Respond!.RevisionNumber);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(fresh, CustomerDocPage.ActionAccept), Who)).Success);
        Assert.Equal(1, (await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId)).RevisionNumber);
    }

    // ───────── ไม่รับ / ขอแก้ + หนึ่งคำตอบต่อ Rev (267-1-11) ─────────

    [Fact]
    public async Task CSR267Db_DeclineAndReopen_ไม่รับพร้อมเหตุผล_ยอมรับทับไม่ได้_ผู้ขายเปิดใหม่แล้วยอมรับได้_OutRevล้างคำตอบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "decline " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);

        // เหตุผลไม่อยู่ในรายการ / "อื่น ๆ" ไม่มีข้อความ ⇒ 400
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionDecline, reason: "ไม่มี"), Who)).Status);
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionDecline, reason: CustomerSignText.OtherReasonCode), Who)).Status);

        var declined = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionDecline, reason: "price", message: "เกินงบ 10%"), Who);
        Assert.True(declined.Success, declined.Message);
        db.ChangeTracker.Clear();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id);
        Assert.Equal(nameof(CustomerSignRule.Kind.Declined), doc.CustomerResponse);
        Assert.Null(doc.QuotationAcceptedAt);                                             // ไม่ใช่การยอมรับ
        Assert.Equal(DocumentStatus.Approved, doc.Status);                                // ไม่เปลี่ยนสถานะของใบ (ไม่ใช่ยกเลิก)
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt.Id);
        Assert.Equal(nameof(CustomerSignRule.Kind.Declined), ev.Action);
        Assert.Equal("price", ev.ReasonCode);
        Assert.Equal("เกินงบ 10%", ev.Message);
        Assert.Null(ev.SignedPdfSha256);                                                  // ไม่มีฉบับที่ลงนามสำหรับการไม่รับ
        Assert.Equal("quotation.declined", (await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId)).EventType);
        Assert.Equal("declined", SignRequestKinds.Status(doc, DateTime.UtcNow)!.Code);    // ป้ายบนรายการเอกสารของพนักงาน
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == qt.Id.ToString() && a.NewValues!.Contains("QuotationDeclinedOnline")));

        // หนึ่งคำตอบต่อ Rev: ยอมรับ/ไม่รับซ้ำ/ขอแก้ ทับไม่ได้ (หน้า = ปุ่มปิดพร้อมเหตุ + ทางขอให้ผู้ขายเปิดใหม่ · เซิร์ฟเวอร์ = 409)
        var blocked = await PageAsync(s, token);
        Assert.Null(blocked.Respond);
        Assert.False(blocked.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept).Enabled);
        Assert.True(blocked.Actions.Single(a => a.Id == CustomerDocPage.ActionRequestLink).Enabled);
        foreach (var action in new[] { CustomerDocPage.ActionAccept, CustomerDocPage.ActionDecline, CustomerDocPage.ActionRequestChange })
            Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, action, reason: "price", message: "x"), Who)).Status);
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));

        // ผู้ขายส่งให้ตอบรับใหม่ (MarkIssued) ⇒ เปิดอีกครั้ง → ลูกค้ายอมรับได้ · แถว "ไม่รับ" เดิมคงอยู่เป็นประวัติ
        var tracked = await db.Documents.FirstAsync(d => d.Id == qt.Id);
        SignRequestKinds.MarkIssued(tracked, DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reopened = await PageAsync(s, token);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(reopened, CustomerDocPage.ActionAccept), Who)).Success);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { nameof(CustomerSignRule.Kind.Declined), nameof(CustomerSignRule.Kind.Accepted) },
            await db.CustomerDocumentActions.AsNoTracking().Where(a => a.CompanyId == s.CompanyId).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync());
        Assert.Equal(2, await db.SignEventOutbox.CountAsync(e => e.CompanyId == s.CompanyId));   // เหตุการณ์ต่อแถวหลักฐาน (token เดิม)

        // ออก Rev ใหม่ (ตัวตั้งเดียวของเส้น revision) ⇒ คำตอบของ Rev เดิมไม่ผูก Rev ใหม่ · แถวหลักฐานเดิมยังอยู่
        var rev = await db.Documents.FirstAsync(d => d.Id == qt.Id);
        rev.RevisionNumber++;
        QuotationAcceptance.ClearOn(rev);
        SignRequestKinds.CloseRespondOnRevision(rev);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var afterRev = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id);
        Assert.Null(afterRev.CustomerResponse);
        Assert.Null(afterRev.QuotationAcceptedAt);
        Assert.Equal(2, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));
        var history = (await PageAsync(s, token)).Responses;
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(0, h.RevisionNumber));                      // ประวัติบอกว่าเป็นของ Rev เดิม
    }

    [Fact]
    public async Task CSR267Db_ChangeRequest_ต้องมีข้อความ_ทำได้แม้พ้นวันยืนราคา_ลิงก์รุ่นก่อนทำไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "change " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(-2));   // พ้นวันยืนราคา
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);
        Assert.False(page.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept).Enabled);
        Assert.DoesNotContain(page.Actions, a => a.Id == CustomerDocPage.ActionDecline);
        Assert.Equal(new[] { CustomerDocPage.ActionRequestChange }, page.Respond!.Actions.Select(a => a.Id));
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who)).Status);      // ยอมรับหลังวันยืนราคาไม่ได้
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionDecline, reason: "price"), Who)).Status);
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionRequestChange), Who)).Status);  // ไม่มีข้อความ
        var ok = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionRequestChange, message: "ขอขยายวันยืนราคา + ลดจำนวนเหลือ 1 งาน"), Who);
        Assert.True(ok.Success, ok.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(nameof(CustomerSignRule.Kind.ChangeRequested), await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.CustomerResponse).SingleAsync());
        Assert.Equal("quotation.change_requested", (await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId)).EventType);
        Assert.Equal(409, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionRequestChange, message: "อีกครั้ง"), Who)).Status);

        // ลิงก์ยอมรับที่ออกก่อนรอบ 267: ไม่รับ/ขอแก้ทำไม่ได้ (ไม่มีด่านผู้ลงนามของหน้าเอกสาร) — 409 พร้อมทางไปต่อ
        var legacy = await AddDocAsync(db, s, DocumentType.Quotation, "QL", tweak: d => LegacySignLinkSeed.Seed(d));
        db.ChangeTracker.Clear();
        var viaLegacy = await s.Responses.DeclineOrRequestChange(LegacySignLinkSeed.RawOf(legacy), CustomerSignRule.Kind.Declined,
            new Svc.RespondRequest(CustomerDocPage.ActionDecline, Sg(), ReasonCode: "price", RevisionNumber: 0), Who);
        Assert.Equal(409, viaLegacy.Status);
        Assert.Contains("ลิงก์หน้าเอกสาร", viaLegacy.Message);
    }

    // ───────── แถวหลักฐาน append-only (trigger ในฐาน) ─────────

    [Fact]
    public async Task CSR267Db_EvidenceRowImmutable_แก้หรือลบแถวหลักฐานไม่ได้_เติมไฟล์ครั้งเดียว_ลบตามคำขอได้ครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var sql in CustomerSignMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);   // รันซ้ำได้ + trigger append-only
        foreach (var sql in CustomerSignMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        var s = await SeedAsync(db, "immutable " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept), Who)).Success);
        db.ChangeTracker.Clear();
        var id = (await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId)).Id;
        var mine = db.CustomerDocumentActions.Where(a => a.Id == id && a.CompanyId == s.CompanyId);

        static async Task Refused(Func<Task> act)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(act);
            var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
            Assert.Equal(CustomerSignMigration.TriggerSqlState, pg?.SqlState);
        }

        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignerFirstName, "คนอื่น")));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignerTitle, "กรรมการผู้จัดการ")));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.ContentHash, "v2:ปลอม")));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.Action, nameof(CustomerSignRule.Kind.Declined))));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.RevisionNumber, 9)));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAt, DateTime.UtcNow.AddDays(-30))));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.Ip, (string?)null)));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignedPdfSha256, new string('0', 64))));   // ฉบับที่ลงนามถูกบันทึกแล้ว เขียนทับไม่ได้
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignedPdfPath, "/tmp/other.pdf")));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.IsDeleted, true)));
        await Refused(() => mine.ExecuteDeleteAsync());
        db.ChangeTracker.Clear();
        var row = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal("สมหญิง", row.SignerFirstName);
        Assert.Equal(CustomerSignRule.FileSha256(PdfBytes), row.SignedPdfSha256);

        // ฝ่ายค้าน 267-P2 L2: "ลบ" ที่เขียนค่าใหม่ (ชื่อปลอม) / แตะ hash ฉบับเซ็นในคำสั่งเดียวกัน / แก้ CreatedBy / TRUNCATE = ฐานปฏิเสธ
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignerFirstName, "ชื่อปลอม").SetProperty(a => a.ErasedAt, (DateTime?)DateTime.UtcNow)));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignerFirstName, QuotationAcceptance.Anonymized)
            .SetProperty(a => a.SignerLastName, (string?)null).SetProperty(a => a.SignerTitle, (string?)null).SetProperty(a => a.SignerOrgName, (string?)null)
            .SetProperty(a => a.SignerEmail, (string?)null).SetProperty(a => a.SignatureFilePath, (string?)null).SetProperty(a => a.Message, (string?)null)
            .SetProperty(a => a.Ip, (string?)null).SetProperty(a => a.UserAgentHash, (string?)null).SetProperty(a => a.SignedPdfPath, (string?)null)
            .SetProperty(a => a.SignedPdfSha256, new string('1', 64)).SetProperty(a => a.ErasedAt, (DateTime?)DateTime.UtcNow)));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedBy, "ใครก็ได้")));
        await Refused(() => db.Database.ExecuteSqlRawAsync("TRUNCATE \"CustomerDocumentActions\""));
        db.ChangeTracker.Clear();

        // ทางเดียวที่แก้ได้: ลบข้อมูลส่วนบุคคลตามคำขอ (ประทับ ErasedAt พร้อมกัน) — และทำได้ครั้งเดียว
        var tracked = await db.CustomerDocumentActions.SingleAsync(a => a.Id == id && a.CompanyId == s.CompanyId);
        QuotationAcceptance.EraseEvidencePii(tracked, DateTime.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var erased = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(QuotationAcceptance.Anonymized, erased.SignerFirstName);
        Assert.Null(erased.Ip);
        Assert.Equal(row.ContentHash, erased.ContentHash);
        Assert.Equal(row.SignedPdfSha256, erased.SignedPdfSha256);
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.SignerFirstName, "กลับมา")));
        await Refused(() => mine.ExecuteUpdateAsync(u => u.SetProperty(a => a.ErasedAt, (DateTime?)null)));
    }

    // ───────── ฉบับที่ลงนาม: SHA-256 คงที่ · ไฟล์ถูกแก้ไม่ส่งออก ─────────

    [Fact]
    public async Task CSR267Db_SignedPdfHashStable_ไม่สร้างซ้ำ_ไฟล์ถูกแก้ไม่ส่งออก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "hash " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept), Who)).Success);
        db.ChangeTracker.Clear();
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId);
        Assert.Equal(1, s.Pdf.Calls);

        // เรียกซ้ำ (หน้า/งาน outbox) ⇒ ไม่เรนเดอร์ใหม่ · hash เดิม · ไฟล์เดิม
        Assert.True((await s.Responses.EnsureSnapshotAsync(s.CompanyId, ev.Id)).HasFile);
        Assert.True((await s.Responses.EnsureSnapshotAsync(s.CompanyId, ev.Id)).HasFile);
        Assert.Equal(1, s.Pdf.Calls);
        db.ChangeTracker.Clear();
        var again = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == ev.Id);
        Assert.Equal(ev.SignedPdfSha256, again.SignedPdfSha256);
        Assert.Equal(ev.SignedPdfAt, again.SignedPdfAt);
        Assert.Equal(CustomerSignRule.FileSha256((await s.Storage.ReadAllBytesAsync(ev.SignedPdfPath))!), again.SignedPdfSha256);

        // ผู้ขายแก้เอกสารหลังลูกค้าลงนาม ⇒ ฉบับที่ลงนามยังเป็นไฟล์เดิม (ไม่ถูกสร้างใหม่จากเนื้อหาใหม่)
        await db.Documents.Where(d => d.Id == qt.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.TotalAmount, 9_999m));
        Assert.Equal(PdfBytes, (await s.Responses.ReadSignedCopyAsync(s.CompanyId, qt.Id, ev.Id))!.Value.Bytes);
        Assert.Equal(1, s.Pdf.Calls);

        // ไฟล์บนที่เก็บถูกแก้ ⇒ ไม่ส่งออกทุกทาง (ลูกค้า · พนักงาน · อีเมล)
        var tampered = (byte[])PdfBytes.Clone();
        tampered[^1] ^= 0xFF;
        await s.Storage.WriteAsync(ev.SignedPdfPath!, tampered, "application/pdf");
        Assert.Null(await s.Responses.ReadSignedCopyAsync(s.CompanyId, qt.Id, ev.Id));
        Assert.Null(await s.Responses.SignedCopyForTokenAsync(s.CompanyId, token, ev.Id));
    }

    // ───────── ฉบับที่ลงนามสร้างไม่ได้ตอนกด ⇒ คำตอบยังมีผล · งาน outbox ลองใหม่ (S12) ─────────

    [Fact]
    public async Task CSR267Db_SnapshotFailureLoud_ยอมรับยังมีผล_หน้าแสดงกำลังเตรียม_งานลองใหม่สำเร็จ_เนื้อหาเปลี่ยนไม่สร้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "snap " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var token = await OpenAsync(db, s, qt);
        s.Pdf.Fail = true;
        var r = await s.Responses.RespondAsync(s.CompanyId, token, Req(await PageAsync(s, token), CustomerDocPage.ActionAccept, Sg(email: "a@buyer267.co.th")), Who);
        Assert.True(r.Success, r.Message);                                                // การลงนามเกิดแล้ว
        db.ChangeTracker.Clear();
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId);
        Assert.Null(ev.SignedPdfSha256);
        var pending = Assert.Single((await PageAsync(s, token)).Responses);
        Assert.False(pending.SignedCopyReady);
        Assert.True(pending.SignedCopyExpected);                                          // หน้า: "กำลังเตรียมฉบับที่ลงนาม"
        Assert.Null(await s.Responses.SignedCopyForTokenAsync(s.CompanyId, token, ev.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev.Id));   // งาน outbox เห็นความล้ม (ถอยเวลา/ลองใหม่)
        Assert.Empty(s.Mail.Attached);

        s.Pdf.Fail = false;                                                               // รอบถัดไปของงาน
        var ok = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev.Id);
        Assert.True(ok.Done && ok.HasFile, ok.Note);
        Assert.Equal("a@buyer267.co.th", Assert.Single(s.Mail.Attached).To);
        Assert.True(Assert.Single((await PageAsync(s, token)).Responses).SignedCopyReady);

        // อีเมลส่งไม่สำเร็จ ⇒ ขั้นนี้ยังไม่จบ (ลองใหม่) — ไม่ประทับว่าส่งแล้ว
        s.Mail.Ok = false;
        var mailFail = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev.Id);
        Assert.False(mailFail.Done);
        Assert.Contains("อีเมล", mailFail.Note);

        // ใบอีกใบ: ตัวสร้าง PDF ล่มตอนกด แล้วผู้ขายแก้เนื้อหาก่อนงานลองใหม่ ⇒ ไม่สร้างไฟล์จากเนื้อหาที่ลูกค้าไม่ได้เห็น (จบขั้นพร้อมเหตุ)
        var qt2 = await AddDocAsync(db, s, DocumentType.Quotation, "QB", due: DateTime.UtcNow.Date.AddDays(5));
        var token2 = await OpenAsync(db, s, qt2);
        s.Pdf.Fail = true;
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token2, Req(await PageAsync(s, token2), CustomerDocPage.ActionAccept), Who)).Success);
        s.Pdf.Fail = false;
        await db.Documents.Where(d => d.Id == qt2.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.TotalAmount, 5_000m));
        db.ChangeTracker.Clear();
        var ev2 = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == qt2.Id);
        var calls = s.Pdf.Calls;
        var changed = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev2.Id);
        Assert.True(changed.Done);
        Assert.False(changed.HasFile);
        Assert.Contains("ถูกแก้หลังลูกค้าลงนาม", changed.Note);
        Assert.Equal(calls, s.Pdf.Calls);
    }

    // ───────── เซ็นรับสินค้าบนหน้าเอกสาร ─────────

    [Fact]
    public async Task CSR267Db_DeliverySignFullIdentity_ต้องเซ็น_ไม่แตะเนื้อหาใบ_มีฉบับที่ลงนาม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "dn " + Guid.NewGuid().ToString("N")[..6]);
        var dn = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DN", tweak: d => d.Notes = "ส่งหน้างาน");
        var token = await OpenAsync(db, s, dn);
        var page = await PageAsync(s, token);
        var spec = Assert.Single(page.Respond!.Actions);
        Assert.Equal(CustomerDocPage.ActionSignDelivery, spec.Id);
        Assert.Equal("required", spec.Signature);
        Assert.Contains("รับสินค้า", spec.Attestation);

        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionSignDelivery), Who)).Status);                 // ไม่ได้เซ็น
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionSignDelivery, Sg(tick: false), signature: Sig()), Who)).Status);
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, signature: Sig()), Who)).Status);     // ปุ่มของชนิดอื่น
        var ok = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionSignDelivery, signature: Sig()), Who);
        Assert.True(ok.Success, ok.Message);
        db.ChangeTracker.Clear();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == dn.Id);
        Assert.NotNull(doc.DeliverySignedAt);
        Assert.Equal("สมหญิง ใจดี", doc.DeliverySignedBy);
        Assert.StartsWith("data:image/png;base64,", doc.DeliverySignatureBase64);
        Assert.Equal("ส่งหน้างาน", doc.Notes);                                             // ไม่ต่อข้อความลงเนื้อหาใบหลังเซ็น
        Assert.Null(doc.CustomerResponse);                                                // ภาพฉายคำตอบใช้กับใบเสนอราคา
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId);
        Assert.Equal(nameof(CustomerSignRule.Kind.DeliverySigned), ev.Action);
        Assert.Equal("ผู้จัดการฝ่ายจัดซื้อ", ev.SignerTitle);
        Assert.NotNull(ev.SignatureSha256);
        Assert.Equal(CustomerSignRule.FileSha256(PdfBytes), ev.SignedPdfSha256);          // เนื้อหาไม่เปลี่ยน ⇒ สร้างฉบับที่ลงนามได้
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == dn.Id.ToString() && a.NewValues!.Contains("DeliverySignedOnline")));
        Assert.Equal("delivery.signed", (await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId)).EventType);
        // เซ็นซ้ำ = สถานะเดิม
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionSignDelivery, signature: Sig()), Who)).Success);
        Assert.Equal(1, await db.CustomerDocumentActions.CountAsync(a => a.CompanyId == s.CompanyId));
    }

    // ───────── ลิงก์ยอมรับที่ออกก่อนรอบ 267 ลงบัญชีหลักฐานด้วย (S2) ─────────

    [Fact]
    public async Task CSR267Db_LegacyAcceptWritesLedger_ลิงก์รุ่นก่อนพิมพ์ชื่อได้_ลงแถวหลักฐานระดับชื่ออย่างเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "legacy " + Guid.NewGuid().ToString("N")[..6]);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", tweak: d => LegacySignLinkSeed.Seed(d));
        db.ChangeTracker.Clear();
        var r = await s.Responses.Accept(LegacySignLinkSeed.RawOf(qt), new PublicQuotationController.AcceptRequest("คุณเดิม ลูกค้าเก่า"), Who);
        Assert.True(r.Success, r.Message);
        db.ChangeTracker.Clear();
        Assert.Equal("คุณเดิม ลูกค้าเก่า", await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedBy).SingleAsync());
        var ev = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId);
        Assert.Equal(CustomerSignRule.ChannelLegacyLink, ev.Channel);
        Assert.Equal(CustomerSignRule.IdentityNameOnly, ev.IdentityLevel);
        Assert.Equal("คุณเดิม ลูกค้าเก่า", ev.SignerFirstName);
        Assert.Null(ev.SignerLastName);                                                   // ไม่แต่งช่องที่ไม่ได้เก็บ
        Assert.Null(ev.SignerTitle);
        Assert.Null(ev.AttestationTextHash);
        Assert.Equal(CustomerSignText.LegacyVersion, ev.AttestationVersion);
        Assert.Null(ev.SignedPdfSha256);
        var copy = await s.Responses.ProcessSignedCopyAsync(s.CompanyId, ev.Id);
        Assert.True(copy.Done);
        Assert.False(copy.HasFile);                                                       // ลิงก์รุ่นก่อนไม่มีฉบับที่ลงนาม — จบขั้นพร้อมเหตุ ไม่ลองวน
        Assert.Equal(0, s.Pdf.Calls);
    }

    // ───────── แยกบริษัท / แยกใบ ─────────

    [Fact]
    public async Task CSR267Db_TenantIsolation_tokenของบริษัทหนึ่งใช้กับอีกบริษัทไม่ได้_ฉบับที่ลงนามอ่านข้ามใบไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await SeedAsync(db, "tenantA " + Guid.NewGuid().ToString("N")[..6]);
        var b = await SeedAsync(db, "tenantB " + Guid.NewGuid().ToString("N")[..6]);
        var qtA = await AddDocAsync(db, a, DocumentType.Quotation, "QA", due: DateTime.UtcNow.Date.AddDays(5));
        var qtA2 = await AddDocAsync(db, a, DocumentType.Quotation, "QA2", due: DateTime.UtcNow.Date.AddDays(5));
        var qtB = await AddDocAsync(db, b, DocumentType.Quotation, "QB", due: DateTime.UtcNow.Date.AddDays(5));
        var tokenA = await OpenAsync(db, a, qtA);
        var tokenA2 = await OpenAsync(db, a, qtA2);
        var tokenB = await OpenAsync(db, b, qtB);
        var pageA = await PageAsync(a, tokenA);

        // URL ของบริษัท B + token ของบริษัท A ⇒ ไม่พบ (ข้อความเดียว) · ไม่มีอะไรถูกบันทึก
        var cross = await a.Responses.RespondAsync(b.CompanyId, tokenA, Req(pageA, CustomerDocPage.ActionAccept), Who);
        Assert.Equal(404, cross.Status);
        Assert.Equal(CustomerDocPage.NotFoundMessage, cross.Message);
        Assert.Equal(404, (await a.Responses.RequestOtpAsync(b.CompanyId, tokenA, "x@y.co", Who)).Status);
        Assert.Equal(0, await db.CustomerDocumentActions.CountAsync(x => x.CompanyId == a.CompanyId || x.CompanyId == b.CompanyId));

        Assert.True((await a.Responses.RespondAsync(a.CompanyId, tokenA, Req(pageA, CustomerDocPage.ActionAccept), Who)).Success);
        db.ChangeTracker.Clear();
        var evA = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(x => x.CompanyId == a.CompanyId);
        Assert.Equal(0, await db.CustomerDocumentActions.CountAsync(x => x.CompanyId == b.CompanyId));
        Assert.Null((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qtB.Id)).QuotationAcceptedAt);

        // ฉบับที่ลงนามของใบ A: อ่านได้ด้วย token ของใบ A เท่านั้น
        Assert.NotNull(await a.Responses.SignedCopyForTokenAsync(a.CompanyId, tokenA, evA.Id));
        Assert.Null(await a.Responses.SignedCopyForTokenAsync(b.CompanyId, tokenA, evA.Id));     // บริษัทอื่นใน URL
        Assert.Null(await a.Responses.SignedCopyForTokenAsync(b.CompanyId, tokenB, evA.Id));     // token ของบริษัทอื่น
        Assert.Null(await a.Responses.SignedCopyForTokenAsync(a.CompanyId, tokenA2, evA.Id));    // token ของอีกใบในบริษัทเดียวกัน
        Assert.Null(await a.Responses.ReadSignedCopyAsync(b.CompanyId, qtA.Id, evA.Id));
        Assert.Null(await a.Responses.ReadSignedCopyAsync(a.CompanyId, qtA2.Id, evA.Id));
        // ฝั่งพนักงาน/ประวัติของบริษัทอื่นไม่เห็นแถวนี้
        Assert.Empty((await a.Responses.EvidenceForStaffAsync(b.CompanyId, qtA.Id)).Items);
        Assert.Empty(await a.Responses.HistoryAsync(b.CompanyId, qtA.Id, false));
        Assert.Empty((await PageAsync(b, tokenB)).Responses);
        // เพิกถอนลิงก์ ⇒ ตอบรับ/ดาวน์โหลดไม่ได้อีก
        await a.Links.RevokeAsync(a.CompanyId, qtA2.Id, "test");
        db.ChangeTracker.Clear();
        Assert.Equal(404, (await a.Responses.RespondAsync(a.CompanyId, tokenA2, Req(pageA, CustomerDocPage.ActionAccept), Who)).Status);
    }

    // ───────── รหัสยืนยันอีเมลเมื่อยอดถึงเกณฑ์ของบริษัท (267-1-6) ─────────

    [Fact]
    public async Task CSR267Db_EmailOtpAboveCompanyAmount_ไม่มีรหัสปฏิเสธ_รหัสผิดนับครั้ง_รหัสถูกยอมรับและใช้ได้ครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "otp " + Guid.NewGuid().ToString("N")[..6]);
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.CustomerSignOtpMinAmount, (decimal?)1_000m));
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));   // ยอดรวม 1,070 ≥ 1,000
        var token = await OpenAsync(db, s, qt);
        var page = await PageAsync(s, token);
        Assert.True(page.Respond!.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept).OtpRequired);
        Assert.False(page.Respond.Actions.Single(a => a.Id == CustomerDocPage.ActionDecline).OtpRequired);      // ไม่รับไม่ต้องใช้รหัส
        const string email = "cfo@buyer267.co.th";

        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept), Who)).Status);                     // ไม่มีอีเมล
        var noCode = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(email: email)), Who);
        Assert.Equal(400, noCode.Status);                                                                                                                  // ยังไม่ได้ขอรหัส
        Assert.Equal(400, (await s.Responses.RequestOtpAsync(s.CompanyId, token, "ไม่ใช่อีเมล", Who)).Status);

        var sentOtp = await s.Responses.RequestOtpAsync(s.CompanyId, token, email, Who);
        Assert.True(sentOtp.Success, sentOtp.Message);
        var mail = Assert.Single(s.Mail.Plain);
        Assert.Equal(email, mail.To);
        var code = Regex.Match(mail.Body, @"<b>(\d{6})</b>").Groups[1].Value;
        Assert.Matches("^[0-9]{6}$", code);
        // ฝ่ายค้าน 267-P2 L7: อีเมลที่ยังไม่ยืนยันไม่เห็นเลขเอกสาร · M1: hash เป็น HMAC (ไม่ใช่ SHA-256 เปล่าที่กู้รหัสได้)
        Assert.DoesNotContain(qt.DocumentNumber, mail.Subject + mail.Body);
        var stored = await db.CustomerSignOtps.AsNoTracking().SingleAsync(o => o.CompanyId == s.CompanyId);
        Assert.NotEqual(QuotationAcceptance.Sha256Hex($"{qt.Id:N}|{email}|{code}"), stored.CodeHash);
        Assert.Equal(CustomerSignRule.OtpHash(qt.Id, email, code), stored.CodeHash);
        // H1/M1: แถว Create ของรหัสใน audit ไม่มี hash ของรหัส/อีเมล
        var otpAudit = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityType == nameof(CustomerSignOtp)).ToListAsync();
        Assert.All(otpAudit, a => { Assert.DoesNotContain(stored.CodeHash, a.NewValues ?? ""); Assert.DoesNotContain(stored.EmailHash, a.NewValues ?? ""); });
        Assert.DoesNotContain(code, stored.CodeHash);                                     // ฐานเก็บ hash
        Assert.DoesNotContain(email, stored.EmailHash);
        Assert.Equal(429, (await s.Responses.RequestOtpAsync(s.CompanyId, token, email, Who)).Status);          // ขอซ้ำถี่เกิน

        var wrong = code == "000000" ? "000001" : "000000";
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(email: email), otp: wrong), Who)).Status);
        Assert.Equal(1, (await db.CustomerSignOtps.AsNoTracking().SingleAsync(o => o.CompanyId == s.CompanyId)).Attempts);
        // รหัสของอีเมลหนึ่งใช้กับอีกอีเมลไม่ได้
        Assert.Equal(400, (await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(email: "other@buyer267.co.th"), otp: code), Who)).Status);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());

        var ok = await s.Responses.RespondAsync(s.CompanyId, token, Req(page, CustomerDocPage.ActionAccept, Sg(email: email), otp: code), Who);
        Assert.True(ok.Success, ok.Message);
        db.ChangeTracker.Clear();
        Assert.True((await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId)).EmailVerified);
        Assert.NotNull((await db.CustomerSignOtps.AsNoTracking().SingleAsync(o => o.CompanyId == s.CompanyId)).ConsumedAt);   // ใช้ได้ครั้งเดียว

        // ทิศตรงข้าม: ใบที่ยอดต่ำกว่าเกณฑ์ ⇒ ไม่ต้องใช้รหัส
        var small = await AddDocAsync(db, s, DocumentType.Quotation, "QS", due: DateTime.UtcNow.Date.AddDays(5), tweak: d => d.TotalAmount = 500m);
        var smallToken = await OpenAsync(db, s, small);
        var smallPage = await PageAsync(s, smallToken);
        Assert.False(smallPage.Respond!.Actions.Single(a => a.Id == CustomerDocPage.ActionAccept).OtpRequired);
        Assert.Equal(400, (await s.Responses.RequestOtpAsync(s.CompanyId, smallToken, email, Who)).Status);   // ยอดต่ำกว่าเกณฑ์ = ไม่ต้องขอรหัส
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, smallToken, Req(smallPage, CustomerDocPage.ActionAccept), Who)).Success);
        // ตอบแล้ว ⇒ ปุ่มยอมรับปิด ⇒ ขอรหัสได้ 409 (ปิดก่อนตรวจเกณฑ์)
        Assert.Equal(409, (await s.Responses.RequestOtpAsync(s.CompanyId, smallToken, email, Who)).Status);
    }

    // ───────── PDPA: ลบตามคำขอเคารพ legal hold (S33) ─────────

    [Fact]
    public async Task CSR267Db_EraseRespectsHold_ใบในอายุเก็บคงหลักฐาน_พ้นแล้วล้างข้อมูลผู้ลงนามและลบไฟล์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "dsr " + Guid.NewGuid().ToString("N")[..6]);
        var recent = await AddDocAsync(db, s, DocumentType.Quotation, "QN", due: DateTime.UtcNow.Date.AddDays(5));
        var old = await AddDocAsync(db, s, DocumentType.Quotation, "QO", due: DateTime.UtcNow.Date.AddDays(5));
        var tRecent = await OpenAsync(db, s, recent);
        var tOld = await OpenAsync(db, s, old);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, tRecent, Req(await PageAsync(s, tRecent), CustomerDocPage.ActionAccept), Who)).Success);
        Assert.True((await s.Responses.RespondAsync(s.CompanyId, tOld, Req(await PageAsync(s, tOld), CustomerDocPage.ActionAccept, signature: Sig()), Who)).Success);
        // ใบเก่า: พ้นอายุเก็บ (วันที่เอกสาร 6 ปีก่อน · ไม่มี RetentionUntil)
        await db.Documents.Where(d => d.Id == old.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.DocumentDate, DateTime.UtcNow.AddYears(-6)));
        db.ChangeTracker.Clear();
        var evOld = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == old.Id);
        Assert.True(await s.Storage.ExistsAsync(evOld.SignedPdfPath));
        Assert.True(await s.Storage.ExistsAsync(evOld.SignatureFilePath));

        // รายงานข้อมูลของเจ้าของข้อมูล (ม.30) มีแถวหลักฐานใต้เอกสาร
        var pdpa = new PdpaService(db, s.Storage);
        var access = await pdpa.GenerateAccessReportAsync(s.CompanyId, null, s.ContactId);
        var docRow = access.Documents.Single(d => (Guid)d["id"]! == recent.Id);
        var responses = Assert.IsAssignableFrom<System.Collections.IEnumerable>(docRow["customerResponses"]).Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("สมหญิง", Assert.Single(responses)["signerFirstName"]);

        Assert.True(await pdpa.ApplyErasureAsync(s.CompanyId, null, s.ContactId, "dpo-test") > 0);
        db.ChangeTracker.Clear();
        // ใบในอายุเก็บ = legal hold: หลักฐานคงครบ
        var kept = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.CompanyId == s.CompanyId && a.DocumentId == recent.Id);
        Assert.Equal("สมหญิง", kept.SignerFirstName);
        Assert.Null(kept.ErasedAt);
        Assert.True(await s.Storage.ExistsAsync(kept.SignedPdfPath));
        // ใบพ้นอายุเก็บ: ล้างข้อมูลผู้ลงนาม + ลบไฟล์ · hash/เวลา/ชนิดคง
        var erased = await db.CustomerDocumentActions.AsNoTracking().SingleAsync(a => a.Id == evOld.Id);
        Assert.Equal(QuotationAcceptance.Anonymized, erased.SignerFirstName);
        Assert.Null(erased.SignerLastName);
        Assert.Null(erased.SignerTitle);
        Assert.Null(erased.Ip);
        Assert.NotNull(erased.ErasedAt);
        Assert.Equal(evOld.ContentHash, erased.ContentHash);
        Assert.Equal(evOld.SignedPdfSha256, erased.SignedPdfSha256);
        Assert.False(await s.Storage.ExistsAsync(evOld.SignedPdfPath));
        Assert.False(await s.Storage.ExistsAsync(evOld.SignatureFilePath));
        Assert.Null(await s.Responses.ReadSignedCopyAsync(s.CompanyId, old.Id, evOld.Id));
    }
}
