using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
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
/// รอบ 267 แพ็ก P3 — "📤 ส่งให้ลูกค้า" บน PostgreSQL จริง (ดีไซน์ §17 S22 · คำตัดสิน 267-1-61…67 · DOCUMENT_FLOW §2.12n-P3):
/// ส่งใบเสนอราคาให้ยอมรับทาง LINE 1:1 พร้อมอีเมลที่ล้ม ⇒ อีเมลบอกล้ม · LINE เข้าคิว (ไม่ถูกลาก) · เปิดช่วงตอบรับ ·
/// การ์ดมีปุ่ม "ดู/ยอมรับ" + s ชี้แถวคิว · ลูกค้ายอมรับจากลิงก์นั้น ⇒ งาน outbox ส่งการ์ด "ฉบับเซ็น" กลับไป LINE เดิม ·
/// ทิศตรงข้าม: s ปลอม = ไม่มีผล (ไม่มีการ์ดฉบับเซ็น) · ไทม์ไลน์เห็นทุกขั้นจากแถวจริง
/// </summary>
[Trait("Category", "Db")]
// ผู้ส่งคิว LINE จองแถว Pending ของทุกบริษัทในฐานร่วม — อยู่ collection เดียวกับคลาสที่เรียกผู้ส่งคิว (ไม่รันขนาน · ดู LineRound259DbTests)
[Collection("SubscriptionExpirySweep")]
public class CustomerShareRound267DbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerShareRound267DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private const string Bot = "Ubot267p3";
    private static readonly SecretProtector Secrets = new("customer-share-267-db");
    private static readonly Svc.Caller Who = new("203.0.113.10", "Mozilla/5.0 (test)");
    private static readonly byte[] PdfBytes = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 9, 8, 7 };

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "customer-share-267-db-test-secret-0123456789",
        }).Build();

    private static IPaymentProvider Provider() => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => "gw267p3",
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay }, false, false, true),
        _ => null,
    });

    private static IPdfGenerationService Pdf() => F4Proxy<IPdfGenerationService>.Create((name, args) =>
        name != nameof(IPdfGenerationService.GenerateDocumentPdfAsync) ? null
            : Task.FromResult(new GeneratePdfResponse(((GeneratePdfRequest)args[1]!).DocumentId, "QT", "QT.pdf", "application/pdf", PdfBytes.Length, PdfBytes, DateTime.UtcNow)));

    /// <summary>อีเมลที่ล่ม (จำลอง SMTP ล้ม) — ทางเลือกช่องทาง = อีเมลธรรมดา · ส่ง = โยน</summary>
    private static IDocumentEmailService BrokenEmail(List<string> calls) => F4Proxy<IDocumentEmailService>.Create((m, a) => m switch
    {
        nameof(IDocumentEmailService.RouteAsync) => new EtaxEmailRoute(EtaxEmailChannel.PlainEmail, "อีเมลธรรมดา"),
        nameof(IDocumentEmailService.SendDocumentRoutedAsync) => Fail(calls, (Accounting.Models.DTOs.Email.SendDocumentEmailRequest)a[2]!),
        _ => null,
    });

    private static object Fail(List<string> calls, Accounting.Models.DTOs.Email.SendDocumentEmailRequest req)
    {
        calls.Add($"{req.To}|page={req.IncludePageLink}");
        return Task.FromException<DocumentEmailLog>(new BusinessRuleException("SMTP ของบริษัทไม่ตอบ (จำลอง)", "EMAIL-TEST", 502));
    }

    private static IContactRecipientResolver Recipients(Guid lineEndpoint)
        => F4Proxy<IContactRecipientResolver>.Create((m, a) => m == nameof(IContactRecipientResolver.ResolveAsync)
            ? new ContactRecipientRule.RecipientSet((NotifyTopic)a[2]!, new[]
            {
                new ContactRecipientRule.RecipientOption(lineEndpoint, NotifyEndpointKind.LineUser, null, "LINE คุณสมหญิง", true, null, null, true, false),
            })
            : null);

    private sealed class PushProbe { public readonly List<(string To, string Alt, string Json)> Pushes = new(); }

    private static ILineNotifyService FakeLine(PushProbe p) => F4Proxy<ILineNotifyService>.Create((m, a) =>
    {
        if (m != nameof(ILineNotifyService.TryPushContactAsync)) return null;
        p.Pushes.Add(((string)a[3]!, (string)a[4]!, System.Text.Json.JsonSerializer.Serialize(a[5],
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        return new LinePushResult(true, null, HttpStatus: 200, LineRequestId: "req-267p3");
    });

    private static IEmailScheduleService NoEmail() => F4Proxy<IEmailScheduleService>.Create((_, _) => null);

    [Fact]
    public async Task CDP267Db_LineSignRoundTrip_อีเมลล้มไม่ลาก_LINE_การ์ดดูยอมรับ_ฉบับเซ็นกลับ_LINE_เดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var tag = Guid.NewGuid().ToString("N")[..6];
        var company = new Company { Name = "บริษัท ผู้ขาย 267P3 " + tag, TaxId = "0105556000007", Email = "office@seller267.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 267P3 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer267.co.th",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var uid = "U267p3" + Guid.NewGuid().ToString("N");
        var ep = new ContactNotifyEndpoint
        {
            CompanyId = company.Id, ContactId = contact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(uid),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, uid), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            TopicsJson = NotifyTopics.Serialize(new[] { NotifyTopic.Document, NotifyTopic.Sign }), ShowAmounts = true, Verified = true,
            Source = ContactLineBindRule.SourceLineInvite, LinkedAt = DateTime.UtcNow,
        };
        db.ContactNotifyEndpoints.Add(ep);
        var qt = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT267P3-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-1), DueDate = DateTime.UtcNow.Date.AddDays(20),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        qt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าออกแบบ", Quantity = 1, Unit = "งาน", UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(qt);
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "cs267p3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        var recipients = Recipients(ep.Id);
        var line = new DocumentLineSendService(db, recipients, links);
        var sign = new SignRequestService(db, F4Proxy<IEmailSenderFactory>.Create((_, _) => null), Secrets, NullLogger<SignRequestService>.Instance,
            Config(), recipients: null, notifyQuota: null, links: links);
        var emailCalls = new List<string>();
        var share = new CustomerShareService(db, links, BrokenEmail(emailCalls), line, NullLogger<CustomerShareService>.Instance, sign);

        // หน้าต่าง: ปลายทาง 1:1 ได้ลิงก์หน้าเอกสาร · มีตัวเลือกเปิดให้ยอมรับ
        var opts = await share.OptionsAsync(company.Id, qt.Id);
        Assert.True(opts.CanShare, opts.Problem);
        Assert.True(opts.Respond.Applies && opts.Respond.Offered);
        Assert.True(Assert.Single(opts.Line.Targets).PageLink);

        // ส่ง: อีเมล (ล้ม) + LINE + เปิดช่วงตอบรับ
        var r = await share.ShareAsync(company.Id, qt.Id, new CustomerShareRequest(
            new List<string> { CustomerShareRule.ChannelEmail, CustomerShareRule.ChannelLine }, OpenRespond: true,
            EmailTo: "ap@buyer267.co.th", LineEndpointIds: new List<Guid> { ep.Id }), null, "staff@seller267.co.th", "https://app.nextacc.test");
        Assert.False(r.Success);                                                     // ล้มดังในคำตอบ
        Assert.Equal(CustomerShareRule.StatusFailed, r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelEmail).Status);
        Assert.Contains("SMTP", r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelEmail).Message);
        Assert.True(r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelLine).Status == CustomerShareRule.StatusQueued, r.Message);   // ไม่ถูกลาก
        Assert.Equal(new[] { "ap@buyer267.co.th|page=True" }, emailCalls);          // อีเมลเดินทางเข้าเดียวพร้อมบล็อกลิงก์หน้าเอกสาร
        // ฝ่ายค้าน LOW-4: แถว audit chain ของคำสั่ง "ส่งให้ลูกค้า" (ช่องทาง + ผล + มีลิงก์หน้าเอกสาร)
        var shareAudit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.CompanyId == company.Id && a.EntityId == qt.Id.ToString()
            && a.NewValues != null && a.NewValues.Contains(CustomerShareRule.AuditAction));
        Assert.Contains("\"pageLink\":true", shareAudit.NewValues);
        Assert.NotNull(shareAudit.RowHash);
        Assert.NotNull(r.Url);
        db.ChangeTracker.Clear();
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.SignLinkIssuedAt).SingleAsync());
        var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == company.Id && q.Trigger == ContactLineChannel.ManualTrigger);
        Assert.Equal((true, ep.Id), (row.IncludePageLink == true, row.EndpointId!.Value));

        // ผู้ส่งคิว: การ์ด "ดู/ยอมรับ" ชี้หน้าเอกสาร + s = แถวคิวนี้
        await db.LineQueues.Where(q => q.Id == row.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddYears(-5)));
        var probe = new PushProbe();
        var sender = new ContactLineQueueService(db, FakeLine(probe), recipients, Secrets, NoEmail(), NullLogger<ContactLineQueueService>.Instance,
            payLinks: links, config: Config());
        await sender.ProcessPendingAsync();
        Assert.Equal(ContactLineChannel.StatusSent, (await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == row.Id)).Status);
        var push = Assert.Single(probe.Pushes, p => p.Alt.Contains(qt.DocumentNumber));
        Assert.Contains("ดู/ยอมรับ", push.Json);
        var source = CustomerShareRule.LineSource(row.Id);
        Assert.Contains("s=" + source, push.Json);
        var token = Regex.Match(push.Json, @"[?&]t=([A-Za-z0-9_\-]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        // ลูกค้ายอมรับจากลิงก์บนการ์ด (s ของแถวนี้) ⇒ outbox จำแหล่งที่มา
        var responses = new Svc(db, NullLogger<Svc>.Instance, leads: null, pdf: Pdf(), storage: storage, rate: null, email: null);
        var page = (await links.PublicViewAsync(company.Id, token))!.Page!;
        Assert.NotNull(page.Respond);
        var accept = await responses.RespondAsync(company.Id, token, new Svc.RespondRequest(CustomerDocPage.ActionAccept,
            new CustomerSignRule.SignerInput("สมหญิง", "ใจดี", "ผู้จัดการฝ่ายจัดซื้อ", true, CustomerSignText.Version, null),
            QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature()), null, null, null,
            page.Respond!.RevisionNumber, page.Respond.ContentHash, null, Source: source), Who);
        Assert.True(accept.Success, accept.Message);
        db.ChangeTracker.Clear();
        var outbox = await db.SignEventOutbox.SingleAsync(e => e.CompanyId == company.Id && e.DocumentId == qt.Id);
        Assert.Equal(row.Id, outbox.SourceLineQueueId);

        // งาน outbox: ฉบับที่ลงนาม (มีแล้ว) ⇒ การ์ด "ฉบับเซ็น" เข้าคิวไป LINE เดิม (ครั้งเดียว)
        var dispatcher = new SignEventDispatcher(db, F4Proxy<IHttpClientFactory>.Create((_, _) => null), Secrets, NullLogger<SignEventDispatcher>.Instance,
            responses: responses);
        await dispatcher.ProcessOneAsync(outbox, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.NotNull(outbox.SignedCopyLineQueuedAt);
        var copyRow = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == company.Id && q.Trigger == ContactLineChannel.SignedCopyTrigger);
        Assert.Equal((ep.Id, true, true), (copyRow.EndpointId!.Value, copyRow.IncludePageLink == true, copyRow.CustomerInitiated));
        await dispatcher.ProcessOneAsync(outbox, DateTime.UtcNow);   // รอบซ้ำ ⇒ ไม่มีแถวที่สอง
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == company.Id && q.Trigger == ContactLineChannel.SignedCopyTrigger));

        await db.LineQueues.Where(q => q.Id == copyRow.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddYears(-5)));
        await sender.ProcessPendingAsync();
        Assert.Equal(ContactLineChannel.StatusSent, (await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == copyRow.Id)).Status);
        var copyPush = Assert.Single(probe.Pushes.Skip(1), p => p.Alt.Contains(qt.DocumentNumber));
        Assert.Equal(push.To, copyPush.To);                                           // LINE เดิม
        Assert.Contains("ฉบับเซ็น", copyPush.Json);

        // ไทม์ไลน์: ทุกขั้นจากแถวจริง — อีเมลล้ม (ทางไปต่อ ส่งใหม่) · การ์ดเอกสาร + การ์ดฉบับเซ็น LINE รับแล้ว · ลูกค้ายอมรับ
        var tl = await new CustomerTimelineService(db).TimelineAsync(company.Id, qt.Id);
        Assert.Contains(tl.Items, i => i.Kind == CustomerShareRule.KindRespondOpened);
        Assert.Contains(tl.Items, i => i.Kind == CustomerShareRule.KindCustomerAction && i.Label.Contains("ยอมรับ"));
        Assert.Equal(2, tl.Items.Count(i => i.Kind == CustomerShareRule.KindLine && i.Tone == CustomerShareRule.ToneOk));
        // แยกบริษัท: บริษัทอื่นไม่เห็นใบนี้
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new CustomerTimelineService(db).TimelineAsync(Guid.NewGuid(), qt.Id));
    }

    [Fact]
    public async Task CDP267Db_ForgedSource_ไม่มีผล_ไม่มีการ์ดฉบับเซ็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var company = new Company { Name = "บริษัท ผู้ขาย 267P3f " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า 267P3f จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson, TaxId = "0107537000017" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var qt = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT267P3F-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-1), DueDate = DateTime.UtcNow.Date.AddDays(20),
            SubTotal = 500m, VatAmount = 35m, TotalAmount = 535m, BalanceDue = 535m, Currency = "THB",
        };
        qt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าตรวจ", Quantity = 1, UnitPrice = 500m, Amount = 500m, VatRate = 7m, VatAmount = 35m });
        db.Documents.Add(qt);
        await db.SaveChangesAsync();
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance);
        var link = await links.EnsureCustomerLinkAsync(company.Id, qt.Id, "test");
        await db.Documents.Where(d => d.Id == qt.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.SignLinkIssuedAt, (DateTime?)DateTime.UtcNow));
        db.ChangeTracker.Clear();
        var root = Path.Combine(Path.GetTempPath(), "cs267p3f-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var responses = new Svc(db, NullLogger<Svc>.Instance, leads: null, pdf: Pdf(), storage: storage, rate: null, email: null);
        var page = (await links.PublicViewAsync(company.Id, link.Token!))!.Page!;
        var r = await responses.RespondAsync(company.Id, link.Token!, new Svc.RespondRequest(CustomerDocPage.ActionAccept,
            new CustomerSignRule.SignerInput("สมชาย", "ใจดี", "กรรมการ", true, CustomerSignText.Version, null),
            QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature()), null, null, null,
            page.Respond!.RevisionNumber, page.Respond.ContentHash, null, Source: CustomerShareRule.LineSource(Guid.NewGuid())), Who);
        Assert.True(r.Success, r.Message);                                            // s ปลอม ไม่กระทบการยอมรับ
        var outbox = await db.SignEventOutbox.AsNoTracking().SingleAsync(e => e.CompanyId == company.Id && e.DocumentId == qt.Id);
        Assert.Null(outbox.SourceLineQueueId);
    }

    // ═══════════════ ฝ่ายค้าน 267-P3 ═══════════════

    private sealed record Arranged(Company Company, ContactNotifyEndpoint Endpoint, Document Doc, DocumentPayLinkService Links, CustomerShareService Share,
        ContactLineQueueService Sender, PushProbe Probe);

    private static async Task<Arranged> ArrangeAsync(Accounting.Data.AccountingDbContext db, DocumentType type, string prefix,
        Func<Accounting.Data.AccountingDbContext, IDocumentEmailService> email)
    {
        var company = new Company { Name = "บริษัท ผู้ขาย 267P3x " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007", Email = "office@seller267.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 267P3x จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer267.co.th",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var uid = "U267p3x" + Guid.NewGuid().ToString("N");
        var ep = new ContactNotifyEndpoint
        {
            CompanyId = company.Id, ContactId = contact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(uid),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, uid), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            TopicsJson = NotifyTopics.Serialize(new[] { NotifyTopic.Document, NotifyTopic.Sign }), ShowAmounts = true, Verified = true,
            Source = ContactLineBindRule.SourceLineInvite, LinkedAt = DateTime.UtcNow,
        };
        db.ContactNotifyEndpoints.Add(ep);
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = type,
            DocumentNumber = prefix + "267P3x-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-1), DueDate = DateTime.UtcNow.Date.AddDays(20),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, Unit = "งาน", UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "cs267p3x-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        var recipients = Recipients(ep.Id);
        var line = new DocumentLineSendService(db, recipients, links);
        var sign = new SignRequestService(db, F4Proxy<IEmailSenderFactory>.Create((_, _) => null), Secrets, NullLogger<SignRequestService>.Instance,
            Config(), recipients: null, notifyQuota: null, links: links);
        var share = new CustomerShareService(db, links, email(db), line, NullLogger<CustomerShareService>.Instance, sign);
        var probe = new PushProbe();
        var sender = new ContactLineQueueService(db, FakeLine(probe), recipients, Secrets, NoEmail(), NullLogger<ContactLineQueueService>.Instance,
            payLinks: links, config: Config());
        return new Arranged(company, ep, doc, links, share, sender, probe);
    }

    private static async Task SendQueuedAsync(Accounting.Data.AccountingDbContext db, Arranged a)
    {
        await db.LineQueues.Where(q => q.CompanyId == a.Company.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddYears(-5)));
        db.ChangeTracker.Clear();
        await a.Sender.ProcessPendingAsync();
    }

    /// <summary>ฝ่ายค้าน MED-1: ใบเสนอราคาส่งทาง LINE 1:1 โดยไม่เปิดให้ตอบรับ ⇒ ปุ่ม "ดูเอกสาร" (ไม่ใช่ "ดู/ยอมรับ") ·
    /// ฝ่ายค้าน MED-2: ติ๊กปุ่มชำระ ⇒ การ์ดมีปุ่มชำระคู่ปุ่มหน้าเอกสาร</summary>
    [Fact]
    public async Task CDP267Db_MED1_MED2_ไม่เปิดตอบรับ_ปุ่มดูเอกสาร_ปุ่มชำระที่ติ๊กอยู่บนการ์ด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var calls = new List<string>();
        var a = await ArrangeAsync(db, DocumentType.Quotation, "QTv", _ => BrokenEmail(calls));
        var r = await a.Share.ShareAsync(a.Company.Id, a.Doc.Id, new CustomerShareRequest(new List<string> { CustomerShareRule.ChannelLine }, OpenRespond: false,
            LineEndpointIds: new List<Guid> { a.Endpoint.Id }), null, "staff@seller267.co.th", "https://app.nextacc.test");
        Assert.True(r.Success, r.Message);
        await SendQueuedAsync(db, a);
        var push = Assert.Single(a.Probe.Pushes, p => p.Alt.Contains(a.Doc.DocumentNumber));
        Assert.Contains("ดูเอกสาร", push.Json);
        Assert.DoesNotContain("ดู/ยอมรับ", push.Json);
        Assert.Contains("s=" + CustomerShareRule.LineSource((await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == a.Company.Id)).Id), push.Json);
        Assert.DoesNotContain(DocumentLabels.For("th").LineCardPay, push.Json);   // ไม่ได้ติ๊ก = ไม่มีปุ่มชำระ

        // MED-2: ใบแจ้งหนี้ + ติ๊กปุ่มชำระ ⇒ ปุ่มชำระคู่ปุ่มหน้าเอกสาร (ป้ายหน้า = "ดูเอกสาร" — ไม่ซ้ำกับปุ่มชำระ)
        var inv = await ArrangeAsync(db, DocumentType.Invoice, "INVp", _ => BrokenEmail(calls));
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = inv.Company.Id, ProviderCode = "gw267p3", IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        var ri = await inv.Share.ShareAsync(inv.Company.Id, inv.Doc.Id, new CustomerShareRequest(new List<string> { CustomerShareRule.ChannelLine },
            LineEndpointIds: new List<Guid> { inv.Endpoint.Id }, LinePayLink: true), null, "staff@seller267.co.th", "https://app.nextacc.test");
        Assert.True(ri.Success, ri.Message + " | " + string.Join(" ; ", ri.Outcomes.Select(o => o.Message)));
        Assert.True((await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == inv.Company.Id)).IncludePayLink, string.Join(" ; ", ri.Outcomes.Select(o => o.Message)));
        await SendQueuedAsync(db, inv);
        var ipush = Assert.Single(inv.Probe.Pushes, p => p.Alt.Contains(inv.Doc.DocumentNumber));
        Assert.Contains(DocumentLabels.For("th").LineCardPay, ipush.Json);
        Assert.Contains("ดูเอกสาร", ipush.Json);
    }

    /// <summary>ฝ่ายค้าน MED-3: ลิงก์หน้าเอกสารถูกยกเลิกไว้ (ไม่ติ๊กออกใหม่) ⇒ ไม่ปฏิเสธทั้งคำขอ — LINE ส่งการ์ดแบบเดิม (ไม่มีลิงก์หน้าเอกสาร) +
    /// เหตุในผล · คัดลอกลิงก์ = ล้มพร้อมเหตุ · ปุ่มชำระที่ติ๊ก (ลิงก์ถูกเพิกถอน) = การ์ดไม่มีปุ่มชำระ + เหตุ ไม่ล้มทั้งช่อง</summary>
    [Fact]
    public async Task CDP267Db_MED3_ลิงก์ถูกยกเลิก_ส่งแบบเดิมพร้อมเหตุ_ไม่ปฏิเสธทั้งคำขอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var calls = new List<string>();
        var a = await ArrangeAsync(db, DocumentType.Invoice, "INVr", _ => BrokenEmail(calls));
        Assert.True((await a.Links.EnsureCustomerLinkAsync(a.Company.Id, a.Doc.Id, "test")).Ok);
        await db.Documents.Where(d => d.Id == a.Doc.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PayLinkRevokedAt, (DateTime?)DateTime.UtcNow));
        db.ChangeTracker.Clear();

        var opts = await a.Share.OptionsAsync(a.Company.Id, a.Doc.Id);
        Assert.True(opts.CanShare, opts.Problem);

        var r = await a.Share.ShareAsync(a.Company.Id, a.Doc.Id, new CustomerShareRequest(
            new List<string> { CustomerShareRule.ChannelCopy, CustomerShareRule.ChannelLine },
            LineEndpointIds: new List<Guid> { a.Endpoint.Id }, LinePayLink: true), null, "staff@seller267.co.th", "https://app.nextacc.test");
        Assert.False(r.Success);                                                     // คัดลอกลิงก์ล้ม — บอกดัง
        Assert.Null(r.Url);
        Assert.Contains("ครั้งนี้ไม่มีลิงก์หน้าเอกสาร", r.NoPageLinkNote);
        Assert.Contains("ครั้งนี้ไม่มีลิงก์หน้าเอกสาร", r.Message);
        Assert.Equal(CustomerShareRule.StatusFailed, r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelCopy).Status);
        var lineOut = r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelLine);
        Assert.Equal(CustomerShareRule.StatusQueued, lineOut.Status);
        Assert.Contains("ไม่ใส่ปุ่มชำระ", lineOut.Message);
        var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == a.Company.Id);
        Assert.NotEqual(true, row.IncludePageLink);
        Assert.NotEqual(true, row.IncludePayLink);
        // ทิศตรงข้าม: ติ๊ก "ออกลิงก์ใหม่แทน" ⇒ มีลิงก์หน้าเอกสาร
        var again = await a.Share.ShareAsync(a.Company.Id, a.Doc.Id, new CustomerShareRequest(new List<string> { CustomerShareRule.ChannelCopy }, ReissueRevoked: true),
            null, "staff@seller267.co.th", "https://app.nextacc.test");
        Assert.True(again.Success, again.Message);
        Assert.NotNull(again.Url);
        Assert.Null(again.NoPageLinkNote);
        await ParkPendingAsync(db, a.Company.Id);
    }

    /// <summary>แถวคิวที่เทสต์นี้ไม่ได้ส่ง ⇒ ปิดไว้ (ผู้ส่งคิวจองแถว Pending ของทุกบริษัท — ไม่ให้ไปโผล่ในเทสต์คลาสอื่นใน collection เดียวกัน)</summary>
    private static Task ParkPendingAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => db.LineQueues.Where(q => q.CompanyId == companyId && q.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, ContactLineChannel.StatusSkipped));

    /// <summary>ฝ่ายค้าน LOW-3: เส้นอีเมลบันทึกแถวไม่สำเร็จ (แถวค้างใน context เดียวกัน) ⇒ LINE ไม่ถูกลาก และแถวค้างไม่ถูกบันทึกเงียบ ๆ ตอน LINE บันทึก</summary>
    [Fact]
    public async Task CDP267Db_LOW3_แถวอีเมลที่ค้าง_ไม่ลาก_LINE_ไม่ถูกบันทึกซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await ArrangeAsync(db, DocumentType.Invoice, "INVd", ctx => F4Proxy<IDocumentEmailService>.Create((m, args) => m switch
        {
            nameof(IDocumentEmailService.RouteAsync) => new EtaxEmailRoute(EtaxEmailChannel.PlainEmail, "อีเมลธรรมดา"),
            nameof(IDocumentEmailService.SendDocumentRoutedAsync) => StuckLog(ctx, (Guid)args[0]!, (Guid)args[1]!),
            _ => null,
        }));
        var r = await a.Share.ShareAsync(a.Company.Id, a.Doc.Id, new CustomerShareRequest(
            new List<string> { CustomerShareRule.ChannelEmail, CustomerShareRule.ChannelLine }, EmailTo: "ap@buyer267.co.th",
            LineEndpointIds: new List<Guid> { a.Endpoint.Id }), null, "staff@seller267.co.th", "https://app.nextacc.test");
        var mail = r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelEmail);
        Assert.Equal(CustomerShareRule.StatusFailed, mail.Status);
        Assert.Contains("อาจออกไปแล้ว", mail.Message);                             // ไม่บอก "ส่งไม่สำเร็จ" เฉย ๆ
        Assert.Equal(CustomerShareRule.StatusQueued, r.Outcomes.Single(o => o.Channel == CustomerShareRule.ChannelLine).Status);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == a.Company.Id));
        Assert.False(await db.DocumentEmailLogs.AnyAsync(l => l.CompanyId == a.Company.Id && l.Subject == "ค้าง-267P3"));
        await ParkPendingAsync(db, a.Company.Id);
    }

    /// <summary>จำลองเส้นอีเมลที่ผู้ให้บริการรับแล้ว แต่บันทึกบันทึกอีเมลล้ม — แถวยังค้าง Added ใน context</summary>
    private static object StuckLog(Accounting.Data.AccountingDbContext ctx, Guid companyId, Guid documentId)
    {
        ctx.DocumentEmailLogs.Add(new DocumentEmailLog { CompanyId = companyId, DocumentId = documentId, ToEmail = "ap@buyer267.co.th", Subject = "ค้าง-267P3", Body = "x",
            Status = EmailLogStatus.Sent, SentAt = DateTime.UtcNow });
        return Task.FromException<DocumentEmailLog>(new DbUpdateException("บันทึกบันทึกอีเมลไม่สำเร็จ (จำลอง)"));
    }
}
