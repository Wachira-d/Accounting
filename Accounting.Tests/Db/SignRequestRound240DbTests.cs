using System.Net;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 240 — เส้นจริงบน PostgreSQL: ยอมรับ/เซ็นรับ ⇒ outbox 1 แถวในธุรกรรมเดียว (ซ้ำ = ไม่เพิ่ม) · ผู้ส่ง callback ตรวจ SSRF ตอนส่ง (ไม่ยิง HTTP เลย)
/// แล้วถอยเวลา · การยอมรับไม่ขึ้นกับปลายทาง · อีเมลขอเซ็นผ่านแพลตฟอร์ม (From ของแพลตฟอร์ม · Reply-To บริษัท · บันทึกการส่ง · ผู้รับล่าสุดของผู้ติดต่อ) ·
/// เพดานรายวันกันจริง (ไม่เรียกผู้ให้บริการ · บันทึก Failed) · ผู้ให้บริการล้ม = ล้มดังบนบันทึก · ออกลิงก์พร้อม callback ถูกเก็บ+echo · ข้ามบริษัทไม่ได้
/// </summary>
[Trait("Category", "Db")]
public class SignRequestRound240DbTests
{
    private readonly ITestOutputHelper _out;
    public SignRequestRound240DbTests(ITestOutputHelper output) => _out = output;

    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("sign-request-240-db");

    private sealed class FakeSender : IEmailSender
    {
        public bool Succeed { get; set; } = true;
        public List<EmailMessage> Sent { get; } = new();
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(Succeed ? EmailSendResult.Ok("mid-1") : EmailSendResult.Fail("SMTP 550 mailbox unavailable"));
        }
    }

    private static IEmailSenderFactory Factory(FakeSender sender)
        => F4Proxy<IEmailSenderFactory>.Create((m, _) => m switch
        {
            nameof(IEmailSenderFactory.PlatformFromAddress) => "no-reply@nextacc.net",
            _ => sender,
        });

    private static IConfiguration Config(int? cap = null)
    {
        var d = new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" };
        if (cap != null) d[SignEmailThrottle.ConfigKey] = cap.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static SignRequestService Service(Accounting.Data.AccountingDbContext db, FakeSender sender, int? cap = null)
        => new(db, Factory(sender), Secrets, NullLogger<SignRequestService>.Instance, Config(cap));

    private static DocumentEmailService DocEmail(Accounting.Data.AccountingDbContext db, FakeSender sender, int? cap = null)
        => new(db, Factory(sender), F4Proxy<IEtaxInvoiceService>.Create((_, _) => null), NullLogger<DocumentEmailService>.Instance,
            pdf: null, storage: null, config: Config(cap));

    private static async Task<(Guid CompanyId, Document Doc)> SeedAsync(Accounting.Data.AccountingDbContext db, string name,
        DocumentType type = DocumentType.Quotation, bool withSecret = false, string? contactEmail = "buyer@customer.co.th")
    {
        var company = new Company { Name = name, TaxId = "0105556000007", Email = "office@seller.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id,
            SignCallbackSecret = withSecret ? Secrets.Protect("whsec_db240") : null,
        });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า " + name, IsCustomer = true, Email = contactEmail };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = type,
            DocumentNumber = (type == DocumentType.Quotation ? "QT-" : "DN-") + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
        };
        SignRequestKinds.SetNewToken(doc, PublicQuotationController.NewToken(), DateTime.UtcNow);
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (company.Id, doc);
    }

    private static PublicQuotationController PublicController(Accounting.Data.AccountingDbContext db)
    {
        var root = Path.Combine(Path.GetTempPath(), "sr240-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new PublicQuotationController(db, NullLogger<PublicQuotationController>.Instance, leads: null, pdf: null, storage: storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.9") },
                    Request = { Headers = { UserAgent = "Mozilla/5.0 (test)" } },
                },
            },
        };
    }

    [Fact]
    public async Task ยอมรับ_เขียน_outbox_หนึ่งแถว_ซ้ำไม่เพิ่ม_และผู้ส่งกัน_SSRF_ตอนส่งโดยไม่ยิง_HTTP()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 accept " + Guid.NewGuid().ToString("N")[..6], withSecret: true);
        var tracked = await db.Documents.FirstAsync(d => d.Id == doc.Id);
        SignRequestService.ApplyCallback(tracked, await db.CompanySettings.FirstAsync(s => s.CompanyId == cid), "https://erp.example.com/hook", "ERP-77", false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ctl = PublicController(db);
        var r1 = await ctl.Accept(doc.QuotationAcceptToken!, new PublicQuotationController.AcceptRequest("คุณสมชาย"));
        Assert.IsType<OkObjectResult>(r1);
        var r2 = await ctl.Accept(doc.QuotationAcceptToken!, new PublicQuotationController.AcceptRequest("คุณสมชาย"));
        Assert.IsType<OkObjectResult>(r2);

        var rows = await db.SignEventOutbox.Where(e => e.CompanyId == cid).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("quotation.accepted", row.EventType);
        Assert.Equal("https://erp.example.com/hook", row.CallbackUrl);
        Assert.Equal("ERP-77", row.CallbackRef);
        Assert.Equal(SignEventStatus.Pending, row.Status);
        Assert.Contains("\"acceptedBy\":", row.PayloadJson);
        Assert.NotEqual(Guid.Empty, row.EventId);
        Assert.Contains(row.EventId.ToString(), row.PayloadJson);   // eventId ใน payload = ของแถว (ผู้รับกันซ้ำได้)
        Assert.DoesNotContain("203.0.113.9", row.PayloadJson);   // ไม่มี IP ใน payload

        // DNS rebinding จำลอง: ชื่อโฮสต์ที่ผ่านกติการูปแบบ แต่ resolve ตอนส่งได้ IP ภายใน ⇒ ผู้ส่งต้องไม่ยิง HTTP เลย + นับครั้ง + ถอยเวลา
        row.CallbackUrl = "https://localhost/hook";
        var calls = 0;
        var http = F4Proxy<IHttpClientFactory>.Create((_, _) => { calls++; return new HttpClient(); });
        var dispatcher = new SignEventDispatcher(db, http, Secrets, NullLogger<SignEventDispatcher>.Instance);
        var now = DateTime.UtcNow;
        await dispatcher.ProcessOneAsync(row, now);
        await db.SaveChangesAsync();
        Assert.Equal(0, calls);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(SignEventStatus.Pending, row.Status);
        Assert.True(row.NextAttemptAt > now);
        Assert.Contains("ภายใน", row.LastError);
        Assert.NotNull(row.SellerNotifiedAt);
        Assert.NotNull(row.RegistrationsDispatchedAt);

        // การยอมรับบันทึกแล้วแม้ปลายทางล้ม (ไม่ผูกกัน)
        Assert.NotNull((await db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id)).QuotationAcceptedAt);
    }

    [Fact]
    public async Task ไม่มี_callback_จบเป็น_NoCallback_และเซ็นรับสินค้าเขียน_delivery_signed_ครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 dn " + Guid.NewGuid().ToString("N")[..6], DocumentType.DeliveryNote);
        var ctl = PublicController(db);
        var sig = QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());
        Assert.IsType<OkObjectResult>(await ctl.SignDelivery(doc.DeliverySignToken!, new PublicQuotationController.DeliverySignRequest("ผู้รับ", sig)));
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await PublicController(db).SignDelivery(doc.DeliverySignToken!, new PublicQuotationController.DeliverySignRequest("ผู้รับ", sig)));

        var row = Assert.Single(await db.SignEventOutbox.Where(e => e.CompanyId == cid).ToListAsync());
        Assert.Equal("delivery.signed", row.EventType);
        Assert.Null(row.CallbackUrl);
        var dispatcher = new SignEventDispatcher(db, F4Proxy<IHttpClientFactory>.Create((_, _) => throw new InvalidOperationException("ต้องไม่ยิง")),
            Secrets, NullLogger<SignEventDispatcher>.Instance);
        await dispatcher.ProcessOneAsync(row, DateTime.UtcNow);
        Assert.Equal(SignEventStatus.NoCallback, row.Status);
    }

    [Fact]
    public async Task อีเมลผ่านแพลตฟอร์ม_From_ของ_NextAcc_ReplyTo_บริษัท_บันทึกการส่ง_และจำผู้รับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 mail " + Guid.NewGuid().ToString("N")[..6]);
        var sender = new FakeSender();
        var r = await Service(db, sender).SendEmailAsync(cid, doc.Id, new SendSignEmailOptions(null, "ขอบคุณครับ"), "https://app.nextacc.net", "tester");

        Assert.True(r.AllSent);
        Assert.Equal(OnBehalfEmailSender.ModePlatform, r.SenderMode);
        var msg = Assert.Single(sender.Sent);
        Assert.Equal("no-reply@nextacc.net", msg.FromAddress);
        Assert.EndsWith("ผ่าน NextAcc", msg.FromName);
        Assert.Equal("office@seller.co.th", msg.ReplyTo);
        Assert.Equal(new[] { "buyer@customer.co.th" }, msg.To);
        Assert.Contains("อีเมลนี้ส่งในนาม", msg.HtmlBody);
        Assert.Contains("quotation-accept.html?token=", msg.HtmlBody);

        var log = Assert.Single(await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == cid).ToListAsync());
        Assert.Equal(EmailLogStatus.Sent, log.Status);
        Assert.Equal(SignRequestPurposes.Manual, log.Purpose);
        Assert.Equal(OnBehalfEmailSender.ModePlatform, log.SenderMode);
        var fresh = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        Assert.NotNull(fresh.SignLinkEmailedAt);
        Assert.Equal("buyer@customer.co.th", fresh.SignLinkEmailedTo);
        Assert.Equal("buyer@customer.co.th", (await db.Contacts.AsNoTracking().FirstAsync(c => c.Id == doc.ContactId)).SignLinkLastRecipients);
    }

    [Fact]
    public async Task เพดานรายวันกันจริง_และผู้ให้บริการล้มต้องล้มดังบนบันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 cap " + Guid.NewGuid().ToString("N")[..6]);
        var sender = new FakeSender();
        var svc = Service(db, sender, cap: 1);
        Assert.True((await svc.SendEmailAsync(cid, doc.Id, new SendSignEmailOptions(new[] { "a@customer.co.th" }, null), "https://app.nextacc.net", "t")).AllSent);
        var blocked = await svc.SendEmailAsync(cid, doc.Id, new SendSignEmailOptions(new[] { "b@customer.co.th" }, null), "https://app.nextacc.net", "t");
        Assert.True(blocked.Throttled);
        Assert.False(blocked.AnySent);
        Assert.Single(sender.Sent);   // ฉบับที่ถูกกันไม่เรียกผู้ให้บริการ
        Assert.Contains(await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == cid).ToListAsync(),
            l => l.ToEmail == "b@customer.co.th" && l.Status == EmailLogStatus.Failed && l.ErrorMessage!.Contains("เพดาน"));

        // ผู้ให้บริการล้ม (บริษัทอื่น — ไม่ติดเพดาน) ⇒ ผลไม่สำเร็จ + บันทึก Failed พร้อมเหตุ · ไม่ประทับ "ส่งแล้ว"
        var (cid2, doc2) = await SeedAsync(db, "SR240 fail " + Guid.NewGuid().ToString("N")[..6]);
        var failing = new FakeSender { Succeed = false };
        var r = await Service(db, failing).SendEmailAsync(cid2, doc2.Id, new SendSignEmailOptions(null, null), "https://app.nextacc.net", "t");
        Assert.False(r.AnySent);
        Assert.False(r.Throttled);
        Assert.Contains("550", Assert.Single(r.Recipients).Error);
        Assert.Null((await db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc2.Id)).SignLinkEmailedAt);
    }

    [Fact]
    public async Task ออกลิงก์พร้อม_callback_ถูกเก็บและ_echo_และข้ามบริษัทไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cidA, docA) = await SeedAsync(db, "SR240 A " + Guid.NewGuid().ToString("N")[..6], withSecret: true);
        var (cidB, _) = await SeedAsync(db, "SR240 B " + Guid.NewGuid().ToString("N")[..6], withSecret: true);
        var svc = Service(db, new FakeSender());

        var link = await svc.IssueLinkAsync(cidA, docA.Id, new IssueSignLinkOptions(false, "https://erp.example.com/cb", "PO-1"), "https://app.nextacc.net");
        Assert.Equal("https://erp.example.com/cb", link.CallbackUrl);
        Assert.Equal("PO-1", link.CallbackRef);
        Assert.Equal(docA.QuotationAcceptToken, link.Token);   // ไม่ขอออกใหม่ + ลิงก์เดิมยังใช้ได้ ⇒ ใช้ token เดิม
        Assert.StartsWith("https://app.nextacc.net/pages/quotation-accept.html?token=", link.Url);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.IssueLinkAsync(cidB, docA.Id, new IssueSignLinkOptions(true), "https://app.nextacc.net"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.SendEmailAsync(cidB, docA.Id, new SendSignEmailOptions(null, null), "https://app.nextacc.net", "t"));
        var statuses = await svc.GetStatusesAsync(cidB, new[] { docA.Id });
        Assert.Empty(statuses);
    }

    // ───────────── ฝ่ายค้าน B1 (240-9): อีเมลเอกสารบนเส้นแพลตฟอร์มไม่ใช่ open relay · เส้น SMTP บริษัทไม่ถูกแตะ ─────────────

    [Fact]
    public async Task อีเมลเอกสารผ่านแพลตฟอร์ม_HTMLดิบถูกหนี_ห่อเทมเพลต_เพดานกัน_และนับสำเนา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 docmail " + Guid.NewGuid().ToString("N")[..6]);
        var sender = new FakeSender();
        var svc = DocEmail(db, sender, cap: 1);

        var log = await svc.SendDocumentEmailAsync(cid, doc.Id,
            new SendDocumentEmailRequest("buyer@customer.co.th", null, null, "หัว\r\nBcc: x@evil.com", "<a href='https://phish'>จ่ายที่นี่</a>\nขอบคุณ", AttachPdf: false));
        Assert.Equal(EmailLogStatus.Sent, log.Status);
        var msg = Assert.Single(sender.Sent);
        Assert.Equal("no-reply@nextacc.net", msg.FromAddress);
        Assert.DoesNotContain("<a href='https://phish'>", msg.HtmlBody);
        Assert.Contains("&lt;a href=", msg.HtmlBody);
        Assert.Contains("อีเมลนี้ส่งในนาม", msg.HtmlBody);
        Assert.Contains("ไม่ใช่โฆษณา", msg.HtmlBody);
        Assert.DoesNotContain("\n", msg.Subject);
        Assert.Equal(OnBehalfEmailSender.ModePlatform, log.SenderMode);

        // บริษัทยังไม่ยืนยันตัวตน (ไม่เคยชำระ) + เพดาน 1 ⇒ ฉบับที่สองถูกกัน 429 · ไม่เรียกผู้ให้บริการ · แถว Failed
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SendDocumentEmailAsync(cid, doc.Id,
            new SendDocumentEmailRequest("other@customer.co.th", null, null, null, null, AttachPdf: false)));
        Assert.Equal(429, ex.StatusCode);
        Assert.Single(sender.Sent);
        Assert.Contains(await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == cid).ToListAsync(),
            l => l.Status == EmailLogStatus.Failed && l.ErrorMessage!.Contains("เพดาน"));

        // To + Cc + Bcc รวม 6 คน ⇒ ปฏิเสธ (บริษัทใหม่ ยังไม่ติดเพดานรายวัน)
        var (cid2, doc2) = await SeedAsync(db, "SR240 docmail2 " + Guid.NewGuid().ToString("N")[..6]);
        var sender2 = new FakeSender();
        await Assert.ThrowsAsync<BusinessRuleException>(() => DocEmail(db, sender2).SendDocumentEmailAsync(cid2, doc2.Id,
            new SendDocumentEmailRequest("a@x.co.th,b@x.co.th", "c@x.co.th;d@x.co.th", "e@x.co.th,f@x.co.th", null, null, AttachPdf: false)));
        Assert.Empty(sender2.Sent);
    }

    [Fact]
    public async Task อีเมลเอกสารผ่าน_SMTP_ของบริษัท_พฤติกรรมเดิม_ไม่ห่อ_ไม่ติดเพดานแพลตฟอร์ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 own smtp " + Guid.NewGuid().ToString("N")[..6]);
        var s = await db.CompanySettings.FirstAsync(x => x.CompanyId == cid);
        s.EmailProvider = EmailProvider.Smtp;
        s.EmailSmtpHost = "smtp.seller.co.th";
        s.EmailFromAddress = "billing@seller.co.th";
        s.EmailConfigured = true;
        await db.SaveChangesAsync();

        var sender = new FakeSender();
        var svc = DocEmail(db, sender, cap: 1);
        const string body = "<p>ข้อความ <b>HTML</b> ของบริษัทเอง</p>";
        for (var i = 0; i < 2; i++)
            Assert.Equal(EmailLogStatus.Sent, (await svc.SendDocumentEmailAsync(cid, doc.Id,
                new SendDocumentEmailRequest($"buyer{i}@customer.co.th", null, null, null, body, AttachPdf: false))).Status);
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, m =>
        {
            Assert.Equal("billing@seller.co.th", m.FromAddress);
            Assert.Equal(body, m.HtmlBody);                     // ไม่ห่อ/ไม่หนี — เส้นของบริษัทเอง
            Assert.DoesNotContain("อีเมลนี้ส่งในนาม", m.HtmlBody);
        });
    }

    // ───────────── ฝ่ายค้าน S2/S3: ผู้ส่ง callback ─────────────

    [Fact]
    public async Task โฮสต์ที่ถูกถอดจากรายชื่อตอนส่ง_ปิดเป็น_Dead_พร้อมเหตุ_ไม่ยิง_และส่งซ้ำมือถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 allow " + Guid.NewGuid().ToString("N")[..6], withSecret: true);
        var row = SignEventOutbox.Enqueue(db, doc, SignRequestKinds.QuotationAccept, DateTime.UtcNow, "x", false, null, "tok-allow");
        row.CallbackUrl = "https://erp.example.com/h";
        await db.SaveChangesAsync();
        var settings = await db.CompanySettings.FirstAsync(s => s.CompanyId == cid);
        settings.SignCallbackAllowedHosts = "other.example.com";
        await db.SaveChangesAsync();

        var calls = 0;
        var dispatcher = new SignEventDispatcher(db, F4Proxy<IHttpClientFactory>.Create((_, _) => { calls++; return new HttpClient(); }),
            Secrets, NullLogger<SignEventDispatcher>.Instance);
        await dispatcher.ProcessOneAsync(row, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.Equal(0, calls);
        Assert.Equal(SignEventStatus.Dead, row.Status);
        Assert.Contains("other.example.com", settings.SignCallbackAllowedHosts);
        Assert.Contains("ไม่อยู่ในรายชื่อ", row.LastError);

        await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db, new FakeSender()).RetryEventAsync(cid, row.Id));

        // payload ถูกล้างแล้ว ⇒ ส่งซ้ำไม่ได้ (N3)
        settings.SignCallbackAllowedHosts = null;
        row.PayloadJson = "{}";
        await db.SaveChangesAsync();
        var purged = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(db, new FakeSender()).RetryEventAsync(cid, row.Id));
        Assert.Equal("SIGN-EVENT-PURGED", purged.RuleCode);
    }

    [Fact]
    public async Task ล้มกลางแถว_แถวนั้นถอยเวลา_แถวอื่นยังได้ประมวลผล_และไม่ยิง_webhook_ที่ลงทะเบียนซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, doc) = await SeedAsync(db, "SR240 isolate " + Guid.NewGuid().ToString("N")[..6], withSecret: true);
        var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bad = SignEventOutbox.Enqueue(db, doc, SignRequestKinds.QuotationAccept, past, "x", false, null, "tok-bad");
        bad.CallbackUrl = "https://93.184.216.34/h";   // IP สาธารณะตรง — ผ่านทุกด่าน แล้ว client ล้มด้วยข้อผิดพลาดที่ไม่ใช่ HTTP
        var ok = SignEventOutbox.Enqueue(db, doc, SignRequestKinds.QuotationAccept, past, "x", false, null, "tok-ok");
        await db.SaveChangesAsync();

        var triggers = 0;
        var hooks = F4Proxy<IWebhookService>.Create((m, _) => { if (m == nameof(IWebhookService.TriggerAsync)) triggers++; return null; });
        var http = F4Proxy<IHttpClientFactory>.Create((_, _) => throw new InvalidOperationException("client พัง"));
        var dispatcher = new SignEventDispatcher(db, http, Secrets, NullLogger<SignEventDispatcher>.Instance, hooks);
        await dispatcher.ProcessDueAsync(DateTime.UtcNow);

        Assert.Equal(SignEventStatus.Pending, bad.Status);
        Assert.Equal(1, bad.Attempts);
        Assert.Contains("ข้อผิดพลาดของระบบ", bad.LastError);
        Assert.True(bad.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(SignEventStatus.NoCallback, ok.Status);
        var savedBad = await db.SignEventOutbox.AsNoTracking().FirstAsync(e => e.Id == bad.Id);
        Assert.NotNull(savedBad.RegistrationsDispatchedAt);   // ขั้น 2 บันทึกแล้วก่อนขั้น callback ล้ม
        var before = triggers;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.ProcessOneAsync(bad, DateTime.UtcNow));
        Assert.Equal(before, triggers);                       // ลองใหม่ไม่ยิง webhook ที่ลงทะเบียนซ้ำ
    }

    // ───────────── ฝ่ายค้าน S7: งานอัตโนมัติ (ครั้งเดียว · watermark · เตือนครั้งเดียว) ─────────────

    [Fact]
    public async Task ส่งอัตโนมัติครั้งเดียว_ตาม_watermark_และเตือนครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, fresh) = await SeedAsync(db, "SR240 auto " + Guid.NewGuid().ToString("N")[..6]);
        var since = DateTime.UtcNow.AddHours(-1);
        var settings = await db.CompanySettings.FirstAsync(s => s.CompanyId == cid);
        settings.SignLinkAutoEmailOnApprove = true;
        settings.SignLinkAutoEmailSince = since;
        settings.SignLinkReminderDays = 1;
        // ใบเก่า (สร้างก่อนเปิดค่าตั้ง) ⇒ ไม่ส่งย้อนหลัง
        var old = await db.Documents.FirstAsync(d => d.Id == fresh.Id);
        var oldDoc = new Document
        {
            CompanyId = cid, ContactId = old.ContactId, DocumentType = DocumentType.Quotation, DocumentNumber = "QT-OLD-" + Random.Shared.Next(1000, 9999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, TotalAmount = 10m, CreatedAt = since.AddDays(-2),
        };
        oldDoc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "x", Quantity = 1, UnitPrice = 10m, Amount = 10m });
        db.Documents.Add(oldDoc);
        // ใบที่ส่งไปแล้ว 2 วันก่อน ⇒ ถึงเวลาเตือน
        var sentDoc = new Document
        {
            CompanyId = cid, ContactId = old.ContactId, DocumentType = DocumentType.DeliveryNote, DocumentNumber = "DN-REM-" + Random.Shared.Next(1000, 9999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, TotalAmount = 10m,
        };
        SignRequestKinds.SetNewToken(sentDoc, PublicQuotationController.NewToken(), DateTime.UtcNow);
        sentDoc.SignLinkEmailedAt = DateTime.UtcNow.AddDays(-2);
        sentDoc.SignLinkEmailedTo = "dn@customer.co.th";
        sentDoc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "x", Quantity = 1, UnitPrice = 10m, Amount = 10m });
        db.Documents.Add(sentDoc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sender = new FakeSender();
        var svc = Service(db, sender);
        var r1 = await svc.RunAutomationAsync(DateTime.UtcNow);
        Assert.Null(r1.Error);
        db.ChangeTracker.Clear();
        var r2 = await svc.RunAutomationAsync(DateTime.UtcNow);
        Assert.Null(r2.Error);

        var logs = await db.DocumentEmailLogs.AsNoTracking().Where(l => l.CompanyId == cid).ToListAsync();
        Assert.Single(logs, l => l.DocumentId == fresh.Id && l.Purpose == SignRequestPurposes.Auto);   // ครั้งเดียวแม้รันสองรอบ
        Assert.DoesNotContain(logs, l => l.DocumentId == oldDoc.Id);                                    // watermark
        Assert.Single(logs, l => l.DocumentId == sentDoc.Id && l.Purpose == SignRequestPurposes.Reminder && l.ToEmail == "dn@customer.co.th");
        Assert.NotNull((await db.Documents.AsNoTracking().FirstAsync(d => d.Id == sentDoc.Id)).SignReminderSentAt);
    }
}
