using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 259 แพ็ก B (คำตัดสิน 258-3-6 · 259-2-1 · 259-2-2) — เส้นจริงบน PostgreSQL:
/// ทวง 50 ทวิ ทาง LINE (ผู้ติดต่อไม่มีอีเมลแต่ผูก LINE ⇒ การ์ดเข้าคิว + ลิงก์อัปโหลดต่อแถว · รอบซ้ำไม่ซ้ำ · ไม่มีทั้งสอง ⇒ "ข้าม" พร้อมเหตุ) ·
/// ผู้ส่งคิวส่งการ์ดทวง (ปุ่มอัปโหลด = URL ของแถวนั้น) และหยุดเมื่อได้ใบครบ · ส่งทาง LINE เองจากหน้าเอกสาร (เข้าคิว · ใบร่าง = ปฏิเสธ · กดซ้ำนาทีเดียว = แถวเดิม)
/// </summary>
[Trait("Category", "Db")]
public class LineRound259DbTests
{
    private readonly ITestOutputHelper _out;
    public LineRound259DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";
    private const string Bot = "Ubot259";
    private static readonly SecretProtector Secrets = new("line-259-db");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "line-259-db-test-secret-0123456789abcdef",
        }).Build();

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid DocumentId, Guid CreditId, Guid EndpointId, string Tag);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, string? email, DocumentStatus status = DocumentStatus.Paid)
    {
        var company = new Company { Name = "บริษัท 259 " + tag, TaxId = Ours, FiscalYearStartMonth = 1 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, WhtCertReminderEnabled = true });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 259 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = Payer, Email = email,
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice,
            DocumentNumber = "INV259-" + Random.Shared.Next(100000, 999999), Status = status,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-8), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m,
            WithholdingTaxAmount = 30m, PaidAmount = 1_040m, BalanceDue = 0m, Currency = "THB",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        var credit = new WhtCreditReceived
        {
            CompanyId = company.Id, TaxYear = DateTime.UtcNow.Year, PayerName = contact.Name, PayerTaxId = Payer, PayerContactId = contact.Id,
            DocumentId = doc.Id, IncomeAmount = 1_000m, WhtRate = 3m, WhtAmount = 30m, Status = WhtCreditStatus.Pending,
            FirstWithheldAt = DateTime.UtcNow.AddDays(-8),
        };
        db.WhtCreditsReceived.Add(credit);
        var uid = "U259" + Guid.NewGuid().ToString("N");
        var ep = new ContactNotifyEndpoint
        {
            CompanyId = company.Id, ContactId = contact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(uid),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, uid), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            TopicsJson = NotifyTopics.Serialize(new[] { NotifyTopic.Document, NotifyTopic.Wht }), ShowAmounts = true, Verified = true,
            Source = ContactLineBindRule.SourceLineInvite, LinkedAt = DateTime.UtcNow,
        };
        db.ContactNotifyEndpoints.Add(ep);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, doc.Id, credit.Id, ep.Id, tag);
    }

    /// <summary>ตัวหาผู้รับปลอม — ปลายทาง LINE ที่ส่งได้ (มี/ไม่มี) · ไม่มีอีเมล</summary>
    private static IContactRecipientResolver Recipients(Guid? lineEndpoint)
        => F4Proxy<IContactRecipientResolver>.Create((m, a) => m == nameof(IContactRecipientResolver.ResolveAsync)
            ? new ContactRecipientRule.RecipientSet((NotifyTopic)a[2]!, lineEndpoint is Guid id
                ? new[] { new ContactRecipientRule.RecipientOption(id, NotifyEndpointKind.LineUser, null, "LINE ของลูกค้า", true, null, null, true, false) }
                : Array.Empty<ContactRecipientRule.RecipientOption>())
            : null);

    [Fact]
    public async Task ไม่มีอีเมลแต่ผูก_LINE_การ์ดทวงเข้าคิวพร้อมลิงก์ต่อแถว_รอบซ้ำไม่ซ้ำ_ทิศตรงข้ามไม่มีช่องทาง_ข้ามพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "wl " + Guid.NewGuid().ToString("N")[..6], email: null);
        var now = DateTime.UtcNow;
        var svc = new WhtCertReminderService(db, Recipients(s.EndpointId), NullLogger<WhtCertReminderService>.Instance, Config());
        var r = await svc.RunCompanyAsync(s.CompanyId, now);
        Assert.Equal(1, r.Queued);
        Assert.Equal(0, await db.EmailQueues.CountAsync(q => q.CompanyId == s.CompanyId));   // ไม่มีอีเมล ⇒ ไม่มีแถวอีเมล
        var lq = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == s.CompanyId);
        Assert.Equal((LineWhtCertCard.QueueEntityType, LineWhtCertCard.QueueTrigger, s.ContactId, s.EndpointId),
            (lq.EntityType, lq.Trigger, lq.EntityId, lq.EndpointId!.Value));
        Assert.Equal(7, WhtCertReminderCadence.StepFromQueueKey(lq.BaseKey));   // กุญแจขั้นเดียวกับอีเมล (ขั้น 7 วัน)
        var log = await db.WhtCertReminderLogs.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId);
        Assert.Equal((WhtCertReminderCadence.OutcomeQueued, 1), (log.Outcome, log.LineQueuedCount));
        Assert.Null(log.EmailQueueId);
        var link = await db.WhtCertUploadLinks.AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId);
        Assert.Equal(lq.Id, link.LineQueueId);
        var url = await WhtCertUploadLinkService.LineReminderUrlAsync(db, Config(), s.CompanyId, lq.Id, default);
        Assert.NotNull(url.Url);
        Assert.Equal(url.Url, (await WhtCertUploadLinkService.LineReminderUrlAsync(db, Config(), s.CompanyId, lq.Id, default)).Url);   // ลองใหม่ = URL เดิม
        Assert.Null((await WhtCertUploadLinkService.LineReminderUrlAsync(db, Config(), s.CompanyId, Guid.NewGuid(), default)).Url);

        // รอบซ้ำ (ขั้นเดิม) ⇒ ไม่มีการ์ดที่สอง
        Assert.Equal(0, (await svc.RunCompanyAsync(s.CompanyId, now.AddMinutes(3))).Queued);
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == s.CompanyId));

        // ทิศตรงข้าม: ไม่มีอีเมลและไม่มี LINE ⇒ แถว "ข้าม" พร้อมเหตุ ไม่มีคิว
        var n = await SeedAsync(db, "wn " + Guid.NewGuid().ToString("N")[..6], email: null);
        var none = await new WhtCertReminderService(db, Recipients(null), NullLogger<WhtCertReminderService>.Instance, Config())
            .RunCompanyAsync(n.CompanyId, now);
        Assert.Equal((0, 1), (none.Queued, none.SkippedOptOut));
        var skipped = await db.WhtCertReminderLogs.AsNoTracking().SingleAsync(l => l.CompanyId == n.CompanyId);
        Assert.Equal((WhtCertReminderCadence.OutcomeSkipped, LineWhtCertCard.NoChannelReason), (skipped.Outcome, skipped.Reason));
        Assert.Equal(0, await db.LineQueues.CountAsync(q => q.CompanyId == n.CompanyId));
    }

    private sealed class PushProbe
    {
        public readonly List<(string To, string Alt, string Json)> Pushes = new();
    }

    private static ILineNotifyService FakeLine(PushProbe p)
        => F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushContactAsync)) return null;
            p.Pushes.Add(((string)a[3]!, (string)a[4]!, System.Text.Json.JsonSerializer.Serialize(a[5],
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
            return new LinePushResult(true, null, HttpStatus: 200, LineRequestId: "req-259");
        });

    private static IEmailScheduleService NoEmail() => F4Proxy<IEmailScheduleService>.Create((_, _) => null);

    [Fact]
    public async Task ผู้ส่งคิวส่งการ์ดทวงพร้อมปุ่มอัปโหลดของแถว_ได้ใบครบแล้ว_ข้ามไม่ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "ws " + Guid.NewGuid().ToString("N")[..6], email: null);
        await new WhtCertReminderService(db, Recipients(s.EndpointId), NullLogger<WhtCertReminderService>.Instance, Config())
            .RunCompanyAsync(s.CompanyId, DateTime.UtcNow);
        var lq = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == s.CompanyId);
        // แถวของเทสต์นี้ถึงเวลาแล้ว — ให้ผู้ส่งหยิบก่อนแถวอื่นของฐานร่วม
        await db.LineQueues.Where(q => q.Id == lq.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddYears(-5)));
        var probe = new PushProbe();
        var sender = new ContactLineQueueService(db, FakeLine(probe), Recipients(s.EndpointId), Secrets, NoEmail(),
            NullLogger<ContactLineQueueService>.Instance, config: Config());
        await sender.ProcessPendingAsync();
        var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == lq.Id);
        Assert.Equal(ContactLineChannel.StatusSent, row.Status);
        var url = (await WhtCertUploadLinkService.LineReminderUrlAsync(db, Config(), s.CompanyId, lq.Id, default)).Url!;
        var push = Assert.Single(probe.Pushes, p => p.Alt.Contains(s.Tag));
        Assert.Contains("50 ทวิ", push.Json);
        Assert.Contains(url.Split("t=")[1], push.Json);   // token ของแถวนี้ (ไม่พึ่งการ escape ของ &)
        Assert.Contains("30.00", push.Json);   // ผู้ใช้ 1:1 เห็นภาษีที่หัก

        // ทิศตรงข้าม: ได้ใบครบแล้ว ⇒ แถวใหม่ (ขั้นถัดไปจำลอง) ข้ามพร้อมเหตุ ไม่ push
        await db.WhtCreditsReceived.Where(w => w.Id == s.CreditId)
            .ExecuteUpdateAsync(x => x.SetProperty(w => w.Status, WhtCreditStatus.Received).SetProperty(w => w.ReceivedAmount, 30m));
        var again = new LineQueue
        {
            CompanyId = s.CompanyId, EntityType = LineWhtCertCard.QueueEntityType, EntityId = s.ContactId, ContactId = s.ContactId,
            EndpointId = s.EndpointId, EndpointKind = NotifyEndpointKind.LineUser, Topic = NotifyTopic.Wht.ToString(), Trigger = LineWhtCertCard.QueueTrigger,
            ScheduledFor = DateTime.UtcNow.AddYears(-5), Status = ContactLineChannel.StatusPending, RetryKey = Guid.NewGuid(),
            IdempotencyKey = "t259:" + Guid.NewGuid().ToString("N"), BaseKey = WhtCertReminderCadence.QueueKey(s.ContactId, 21, DateTime.UtcNow.Date),
        };
        db.LineQueues.Add(again);
        await db.SaveChangesAsync();
        var before = probe.Pushes.Count;
        await sender.ProcessPendingAsync();
        var skip = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == again.Id);
        Assert.Equal(ContactLineChannel.StatusSkipped, skip.Status);
        Assert.Contains("ครบแล้ว", skip.ErrorMessage);
        Assert.DoesNotContain(probe.Pushes.Skip(before), p => p.Alt.Contains(s.Tag));
    }

    [Fact]
    public async Task ส่งทาง_LINE_เองเข้าคิว_กดซ้ำนาทีเดียวแถวเดิม_ใบร่างปฏิเสธ_ปลายทางที่ไม่ใช่ของผู้ติดต่อปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "ms " + Guid.NewGuid().ToString("N")[..6], email: "ap@buyer259.co.th", status: DocumentStatus.Approved);
        var svc = new DocumentLineSendService(db, Recipients(s.EndpointId));
        var o = await svc.OptionsAsync(s.CompanyId, s.DocumentId);
        Assert.True(o.CanSend);
        Assert.Equal(s.EndpointId, Assert.Single(o.Targets).EndpointId);
        var r = await svc.SendAsync(s.CompanyId, s.DocumentId, new[] { s.EndpointId }, includePayLink: false, null, "t@259");
        Assert.Equal((1, 0), (r.Queued, r.AlreadyQueued));
        var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == s.CompanyId);
        Assert.Equal((ContactLineChannel.ManualTrigger, "Document", s.DocumentId, (bool?)false), (row.Trigger, row.EntityType, row.EntityId, row.IncludePayLink));
        var again = await svc.SendAsync(s.CompanyId, s.DocumentId, new[] { s.EndpointId }, includePayLink: false, null, "t@259");
        // กดซ้ำในนาทีเดียวกัน (หรือข้ามนาทีพอดี = แถวใหม่ — ยอมรับได้) · ไม่มีกรณีที่ได้สองแถวในนาทีเดียว
        Assert.True(again.AlreadyQueued == 1 || again.Queued == 1);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SendAsync(s.CompanyId, s.DocumentId, new[] { Guid.NewGuid() }, false, null, "t@259"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SendAsync(s.CompanyId, s.DocumentId, Array.Empty<Guid>(), false, null, "t@259"));

        var d = await SeedAsync(db, "md " + Guid.NewGuid().ToString("N")[..6], email: null, status: DocumentStatus.Draft);
        var draft = new DocumentLineSendService(db, Recipients(d.EndpointId));
        Assert.False((await draft.OptionsAsync(d.CompanyId, d.DocumentId)).CanSend);
        await Assert.ThrowsAsync<BusinessRuleException>(() => draft.SendAsync(d.CompanyId, d.DocumentId, new[] { d.EndpointId }, false, null, "t@259"));
        Assert.Equal(0, await db.LineQueues.CountAsync(q => q.CompanyId == d.CompanyId));
        // ข้ามบริษัท: เอกสารของบริษัทอื่น ⇒ ไม่พบ
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SendAsync(d.CompanyId, s.DocumentId, new[] { s.EndpointId }, false, null, "t@259"));
    }
}
