using System.Net;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
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
/// รอบ 262 (คำตัดสิน 262-1-1…262-1-8) บน PostgreSQL จริง — บริษัทผู้ให้บริการเรียกเก็บค่าบริการผ่าน LINE: คำเชิญ scope nextacc → ยินยอม (PDPA ม.19) →
/// รหัสผ่าน webhook กลาง → ปลายทาง LINE ผู้ใช้ของผู้ติดต่อ (ผู้เช่า) → ใบแจ้งหนี้ค่าบริการเข้าคิว LINE (เจ้าของที่ LINE เดียวกันไม่ได้ข้อความตรงซ้ำ) →
/// ส่งด้วย <b>token ของ OA กลาง</b> + ปุ่มชำระไปศูนย์บิล → ใบเสร็จหลังชำระเข้าคิว · ทิศตรงข้าม: บริษัทอื่นสร้างคำเชิญผ่าน OA กลางไม่ได้ · ปลายทาง nextacc ของบริษัทอื่นไม่ส่ง ·
/// รหัสของบริษัทอื่นไถ่ผ่าน webhook กลางไม่ได้ · คำเชิญ OA บริษัทไถ่ผ่าน OA กลางไม่ได้ · ใบของผู้เช่า A ไม่ถึงผู้ติดต่อของผู้เช่า B
/// <para>collection เดียวกับเทสต์ที่ตั้ง <c>SiteSettings.PlatformCompanyId</c> (แถวเดียวของระบบ) — ไม่รันขนานกัน · คืนค่าเดิมเสมอ</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class ProviderCentralOaRound262DbTests
{
    private readonly ITestOutputHelper _out;
    public ProviderCentralOaRound262DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-262-provider");
    private const string AdminGroup = "Cadmin262p0000000000000000000001";
    private const string CentralBot = "Ucentral262p00000000000000000001";
    private const string CentralToken = "CENTRAL-TOKEN-262";

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings(CentralToken, "S", "@810pamnc", CentralBot, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private static IConfiguration Cfg() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());

    private static ContactLineBindingService Binding(AccountingDbContext db) => new(db, Secrets, Resolver(db),
        F4Proxy<IChatRateLimiter>.Create((m, _) => m == nameof(IChatRateLimiter.TryConsumeAsync) ? (object)true : 0),
        Cfg(), NullLogger<ContactLineBindingService>.Instance, line: null, nextAcc: Central());

    /// <summary>ตัวส่งข้อความตรงของแพลตฟอร์ม (OA กลาง) — จดผู้รับ</summary>
    private static ILineNotifyService DirectLine(List<string> log) => F4Proxy<ILineNotifyService>.Create((m, a) =>
    {
        if (m == nameof(ILineNotifyService.TryPushViaNextAccAsync)) { log.Add("direct:" + a[0]); return LinePushResult.Success; }
        return null;
    });

    private sealed class AuthCapture : HttpMessageHandler
    {
        public List<string?> Bearers { get; } = new();
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearers.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            resp.Headers.Add("x-line-request-id", "req-262");
            return resp;
        }
    }

    private static LineNotifyService RealLine(AccountingDbContext db, AuthCapture h)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, _) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        return new LineNotifyService(factory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<LineNotifyService>.Instance, db, Secrets, Central());
    }

    private sealed class FakeSender : IEmailSender
    {
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default) => Task.FromResult(EmailSendResult.Ok("mid-262"));
    }

    private static EmailScheduleService Sched(AccountingDbContext db)
        => new(db, F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : new FakeSender()),
            F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: Cfg(), recipients: Resolver(db));

    private static ContactLineQueueService Queue(AccountingDbContext db, AuthCapture h)
        => new(db, RealLine(db, h), Resolver(db), Secrets, Sched(db), NullLogger<ContactLineQueueService>.Instance, config: Cfg());

    private static async Task DueNowAsync(AccountingDbContext db, Guid companyId)
    {
        await db.LineQueues.Where(q => q.CompanyId != companyId && q.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddDays(30)));
        await db.LineQueues.Where(q => q.CompanyId == companyId).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddMinutes(-1)));
    }

    /// <summary>เปิดช่อง LINE ของการแจ้งค่าบริการชั่วคราว (แถวเดียวของระบบ) แล้วคืนค่าเดิม — ไม่มีแถว = ค่าตั้งต้นเปิดอยู่แล้ว</summary>
    private static async Task WithBillingLineAsync(AccountingDbContext db, Func<Task> body)
    {
        var row = await db.PlatformBillingSettings.FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId);
        var before = row?.ReminderLineEnabled;
        if (row != null) { row.ReminderLineEnabled = true; await db.SaveChangesAsync(); }
        db.ChangeTracker.Clear();
        try { await body(); }
        finally
        {
            db.ChangeTracker.Clear();
            if (before is bool b)
            {
                var again = await db.PlatformBillingSettings.FirstAsync(x => x.Id == PlatformBillingSettings.SingletonId);
                again.ReminderLineEnabled = b;
                await db.SaveChangesAsync();
            }
        }
    }

    private static string TokenOf(string path) => Uri.UnescapeDataString(Regex.Match(path, @"[?&]t=([^&]+)").Groups[1].Value);

    /// <summary>ผู้ติดต่อของผู้เช่าในบริษัทผู้ให้บริการผูก LINE ผ่าน OA กลาง (เส้นจริงทั้งหมด) — คืนปลายทาง</summary>
    private static async Task<ContactNotifyEndpoint> BindViaCentralAsync(AccountingDbContext db, Guid providerId, Guid contactId, string uid)
    {
        var invite = await Binding(db).CreateInviteAsync(providerId, contactId, null, "staff@provider262.local");
        Assert.Contains("LINE OA กลาง", invite.Note);
        var row = await db.ContactLineInvites.AsNoTracking().Where(i => i.CompanyId == providerId && i.ContactId == contactId)
            .OrderByDescending(i => i.CreatedAt).FirstAsync();
        Assert.Equal(NotifyEndpointKind.ScopeNextAcc, row.OaScope);
        Assert.Equal(CentralBot, row.OaDestination);
        var consent = await Binding(db).ConsentAsync(providerId, TokenOf(invite.Path), "127.0.0.1", "xunit-262");
        Assert.Contains("810pamnc", consent.AddFriendUrl);   // ปุ่มเพิ่มเพื่อน = OA กลาง (ไม่ใช่ Basic ID ของบริษัท)
        // ทิศตรงข้าม: รหัสของคำเชิญ OA กลาง ส่งผ่าน webhook OA บริษัท ⇒ ไม่ผูก
        Assert.Equal(ContactLineBindRule.Replies.WrongChannel, await Binding(db).TryRedeemAsync(providerId, CentralBot, uid, consent.MessageText));
        var reply = await Binding(db).TryRedeemCentralAsync(CentralBot, uid, consent.MessageText);
        Assert.StartsWith("✅", reply);
        db.ChangeTracker.Clear();
        var ep = await db.ContactNotifyEndpoints.AsNoTracking()
            .SingleAsync(e => e.CompanyId == providerId && e.ContactId == contactId && e.Kind == NotifyEndpointKind.LineUser && e.UnlinkedAt == null);
        Assert.Equal(NotifyEndpointKind.ScopeNextAcc, ep.OaScope);
        Assert.Equal(CentralBot, ep.OaDestination);
        Assert.Equal(uid, Secrets.Unprotect(ep.LineUserId));
        Assert.NotNull(ep.ConsentRecordId);
        return ep;
    }

    [Fact]
    public async Task บริษัทผู้ให้บริการ_ผูก_LINE_ลูกค้าผ่าน_OA_กลาง_ใบแจ้งหนี้เข้าคิว_ส่งด้วย_token_กลาง_ปุ่มไปศูนย์บิล_ใบเสร็จตามมา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var uid = "U262" + Guid.NewGuid().ToString("N")[..28];
        await PlatformBillingPhase2DbTests.WithPlatformTenantAsync(db, f.PlatformId, async () => await WithBillingLineAsync(db, async () =>
        {
            db.ChangeTracker.Clear();
            var ep = await BindViaCentralAsync(db, f.PlatformId, f.ContactId, uid);

            // หน้าผู้ติดต่อ: ปุ่มลิงก์ผูก LINE เปิดผ่าน OA กลาง (รอบ 262 — เดิมปิดเพราะไม่มี OA บริษัท)
            var overview = await Resolver(db).GetOverviewAsync(f.PlatformId, f.ContactId, canEdit: true);
            Assert.True(overview!.LineBind!.Available);
            Assert.True(overview.LineBind.ViaCentralOa);
            var set = await Resolver(db).ResolveAsync(f.PlatformId, f.ContactId, NotifyTopic.Document);
            Assert.True(set.Options.Single(o => o.EndpointId == ep.Id).Available);

            // เจ้าของผู้เช่าผูก LINE เดียวกันกับบอทกลาง (บัญชี ERP) — ได้การ์ดแล้ว ⇒ ไม่ได้ข้อความตรงซ้ำ
            var owner = new User { Email = $"owner262-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้เช่า 262", LineUserId = uid };
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = f.BuyerId, Role = UserRole.Owner });
            await db.SaveChangesAsync();
            var inv = await PlatformBillingPhase2DbTests.InvoiceAsync(db, f, "R262-" + Guid.NewGuid().ToString("N")[..6], 1_070m, DateTime.UtcNow.AddDays(10));
            db.ChangeTracker.Clear();

            var direct = new List<string>();
            var notifier = new PlatformBillingNotifier(db, NullLogger<PlatformBillingNotifier>.Instance, email: null, line: DirectLine(direct),
                recipients: Resolver(db), secrets: Secrets);
            await notifier.InvoiceIssuedAsync(f.BuyerId, inv.Id);
            await notifier.InvoiceIssuedAsync(f.BuyerId, inv.Id);   // ซ้ำ ⇒ ไม่เข้าคิวซ้ำ
            db.ChangeTracker.Clear();
            var queued = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == f.PlatformId && q.EntityId == inv.Id);
            Assert.Equal("DocumentApproved", queued.Trigger);
            Assert.Equal(ep.Id, queued.EndpointId);
            Assert.Equal(ContactLineChannel.StatusPending, queued.Status);
            Assert.Empty(direct);
            var key = PlatformDunningPolicy.Key(inv.Id, PlatformDunningPolicy.KindIssued, 0);
            var notices = await db.PlatformBillingNotices.AsNoTracking().Where(n => n.MessageKey == key).ToListAsync();
            Assert.Equal(PlatformBillingNotifier.StatusQueued, notices.Single(n => n.Channel == PlatformBillingNotifier.ChannelLineContact).Status);
            var lineNotice = notices.Single(n => n.Channel == PlatformBillingNotifier.ChannelLine);
            Assert.Equal(PlatformBillingNotifier.StatusSkipped, lineNotice.Status);   // ไม่ประทับ "ส่งแล้ว" ให้ช่องที่ไม่ได้ยิง
            Assert.Contains("การ์ด", lineNotice.Detail);

            // ผู้ส่งคิวตัวเดิม: token ของ OA กลาง · ปุ่มชำระไปศูนย์บิล (ไม่ใช่ลิงก์ชำระเอกสารทั่วไป)
            await DueNowAsync(db, f.PlatformId);
            var h = new AuthCapture();
            Assert.Equal(1, await Queue(db, h).ProcessPendingAsync());
            Assert.Equal(CentralToken, Assert.Single(h.Bearers));
            Assert.Contains("/pages/billing.html", Assert.Single(h.Bodies));
            var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == queued.Id);
            Assert.Equal(ContactLineChannel.StatusSent, row.Status);
            Assert.Equal(NotifyEndpointKind.ScopeNextAcc, row.OaScope);

            // ใบเสร็จหลังชำระ ⇒ การ์ดใบเสร็จ (ไม่รอชั่วโมงเงียบ) ครั้งเดียวต่อใบ
            var receipt = new Document
            {
                CompanyId = f.PlatformId, ContactId = f.ContactId, DocumentType = DocumentType.Receipt,
                DocumentNumber = "RC-T" + Guid.NewGuid().ToString("N")[..8], Status = DocumentStatus.Approved,
                DocumentDate = DateTime.UtcNow, SubTotal = 1_070m, TotalAmount = 1_070m, BalanceDue = 0m, Currency = "THB",
            };
            receipt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "รับชำระค่าบริการ", Quantity = 1, UnitPrice = 1_070m, Amount = 1_070m });
            db.Documents.Add(receipt);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await notifier.ReceiptIssuedAsync(f.BuyerId, receipt.Id);
            await notifier.ReceiptIssuedAsync(f.BuyerId, receipt.Id);
            var rq = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == f.PlatformId && q.EntityId == receipt.Id);
            Assert.Equal(ContactLineChannel.ReceiptTrigger, rq.Trigger);
            Assert.True(rq.CustomerInitiated);

            // ทิศตรงข้าม (isolation): ใบของผู้เช่ารายนี้ ส่งในนามผู้เช่ารายอื่น ⇒ ไม่มีการ์ดถึงผู้ติดต่อใดเลย
            var inv2 = await PlatformBillingPhase2DbTests.InvoiceAsync(db, f, "R262X-" + Guid.NewGuid().ToString("N")[..6], 500m, DateTime.UtcNow.AddDays(5));
            db.ChangeTracker.Clear();
            await notifier.SendAsync(Guid.NewGuid(), inv2.Id, PlatformDunningPolicy.Key(inv2.Id, PlatformDunningPolicy.KindIssued, 0), "หัวข้อ", "เนื้อความ");
            Assert.False(await db.LineQueues.AnyAsync(q => q.EntityId == inv2.Id));

            // ทิศตรงข้าม: บริษัทผู้ให้บริการเปลี่ยน ⇒ ปลายทาง nextacc ของบริษัทเดิมไม่ส่ง (ตรวจใหม่ตอนหาผู้รับ)
            db.ChangeTracker.Clear();
            var site = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstAsync();
            site.PlatformCompanyId = Guid.NewGuid();
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var after = await Resolver(db).ResolveAsync(f.PlatformId, f.ContactId, NotifyTopic.Document);
            var opt = after.Options.Single(o => o.EndpointId == ep.Id);
            Assert.False(opt.Available);
            Assert.Equal(ContactRecipientRule.NextAccNotOpenReason, opt.Reason);
        }));
    }

    [Fact]
    public async Task ทิศตรงข้าม_บริษัททั่วไป_ใช้_OA_กลางกับผู้ติดต่อไม่ได้_และรหัสของบริษัทอื่นไถ่ผ่าน_webhook_กลางไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var other = new Company { Name = "บริษัททั่วไป 262", TaxId = "" };
        db.Companies.Add(other);
        await db.SaveChangesAsync();
        var otherContact = new Contact { CompanyId = other.Id, Name = "ลูกค้าของบริษัททั่วไป 262", IsCustomer = true, TaxId = "" };
        db.Contacts.Add(otherContact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var uid = "U262o" + Guid.NewGuid().ToString("N")[..27];

        await PlatformBillingPhase2DbTests.WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            db.ChangeTracker.Clear();
            // บริษัททั่วไปไม่มี OA ของตัวเอง ⇒ สร้างลิงก์ผูก LINE ไม่ได้ (ไม่ถอยไป OA กลาง)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Binding(db).CreateInviteAsync(other.Id, otherContact.Id, null, "staff@other262.local"));
            var overview = await Resolver(db).GetOverviewAsync(other.Id, otherContact.Id, canEdit: true);
            Assert.False(overview!.LineBind!.Available);
            Assert.False(overview.LineBind.ViaCentralOa);

            // ปลายทาง nextacc ที่หลุดเข้ามาในบริษัททั่วไป (แก้ฐานตรง) ⇒ ตัวหาผู้รับไม่ใช้ส่ง
            var consent = new PdpaConsentRecord { CompanyId = other.Id, Purpose = "contact-line-notify", SubjectContactId = otherContact.Id };
            db.PdpaConsentRecords.Add(consent);
            await db.SaveChangesAsync();
            db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
            {
                CompanyId = other.Id, ContactId = otherContact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(uid),
                LineIdHash = NotifyEndpointKind.LineIdHash(CentralBot, uid), OaScope = NotifyEndpointKind.ScopeNextAcc, OaDestination = CentralBot,
                TopicsJson = NotifyTopics.Serialize(NotifyTopics.All), Verified = true, Source = ContactLineBindRule.SourceLineInvite,
                ConsentRecordId = consent.Id, LinkedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var set = await Resolver(db).ResolveAsync(other.Id, otherContact.Id, NotifyTopic.Document);
            var opt = set.Options.Single(o => o.Kind == NotifyEndpointKind.LineUser);
            Assert.False(opt.Available);
            Assert.Equal(ContactRecipientRule.NextAccNotOpenReason, opt.Reason);

            // คำเชิญของบริษัททั่วไป (แม้ประทับ scope nextacc) — รหัสส่งเข้า webhook กลาง ⇒ ไม่พบ (ค้นเฉพาะบริษัทผู้ให้บริการ) ⇒ ไม่ผูก
            var code = CustomerLineLinkRule.NewCode();
            db.ContactLineInvites.Add(new ContactLineInvite
            {
                CompanyId = other.Id, ContactId = otherContact.Id, TokenHash = ContactLineBindRule.TokenHash(ContactLineBindRule.NewToken()),
                ExpiresAt = DateTime.UtcNow.AddDays(1), OaScope = NotifyEndpointKind.ScopeNextAcc, OaDestination = CentralBot,
                CodeHash = CustomerLineLinkRule.Hash(code), CodeExpiresAt = DateTime.UtcNow.AddMinutes(20), ConsentRecordId = consent.Id,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.ContactNotifyEndpoints.CountAsync(e => e.CompanyId == other.Id);
            Assert.Null(await Binding(db).TryRedeemCentralAsync(CentralBot, "U262x" + Guid.NewGuid().ToString("N")[..27], CustomerLineLinkRule.MessageText(code)));
            Assert.Equal(before, await db.ContactNotifyEndpoints.CountAsync(e => e.CompanyId == other.Id));

            // คำเชิญ OA บริษัทของบริษัทผู้ให้บริการ (ค้างจากก่อนรอบนี้) — รหัสส่งเข้า webhook กลาง ⇒ ช่องผิด ไม่ผูก
            var code2 = CustomerLineLinkRule.NewCode();
            var pConsent = new PdpaConsentRecord { CompanyId = f.PlatformId, Purpose = "contact-line-notify", SubjectContactId = f.ContactId };
            db.PdpaConsentRecords.Add(pConsent);
            await db.SaveChangesAsync();
            db.ContactLineInvites.Add(new ContactLineInvite
            {
                CompanyId = f.PlatformId, ContactId = f.ContactId, TokenHash = ContactLineBindRule.TokenHash(ContactLineBindRule.NewToken()),
                ExpiresAt = DateTime.UtcNow.AddDays(1), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = null,
                CodeHash = CustomerLineLinkRule.Hash(code2), CodeExpiresAt = DateTime.UtcNow.AddMinutes(20), ConsentRecordId = pConsent.Id,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(ContactLineBindRule.Replies.WrongChannel,
                await Binding(db).TryRedeemCentralAsync(CentralBot, "U262y" + Guid.NewGuid().ToString("N")[..27], CustomerLineLinkRule.MessageText(code2)));
            Assert.False(await db.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == f.PlatformId && e.ContactId == f.ContactId && e.Kind == NotifyEndpointKind.LineUser));

            // ศูนย์บิลของผู้เช่า: กล่อง "รับใบแจ้งหนี้ทาง LINE" กดได้ (ผู้ติดต่อมีแล้ว · OA กลางพร้อม) · บริษัทที่ไม่มีผู้ติดต่อในบริษัทผู้ให้บริการ ⇒ เหตุ
            Assert.True((await Binding(db).BillingLineViewAsync(f.BuyerId, canManage: true)).Available);
            var none = await Binding(db).BillingLineViewAsync(other.Id, canManage: true);
            Assert.False(none.Available);
            await Assert.ThrowsAsync<BusinessRuleException>(() => Binding(db).CreateBillingInviteAsync(other.Id, null, "x@other262.local"));
            var billingInvite = await Binding(db).CreateBillingInviteAsync(f.BuyerId, null, "owner@buyer262.local");
            Assert.Contains($"c={f.PlatformId:D}", billingInvite.Path);   // คำเชิญอยู่ในบริษัทผู้ให้บริการ (ผู้ติดต่อของผู้เช่า)
        });
    }
}
