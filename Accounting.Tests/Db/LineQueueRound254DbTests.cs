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
/// รอบ 254 WP-11 L2 บน PostgreSQL จริง (AUDIT_249 WP-11 T± · LINE_MULTICHANNEL_DESIGN §3.e · คำตอบเจ้าของ Q1(b)/Q6/Q7 · คำตัดสิน N8):
/// ติ๊ก LINE ⇒ แถวคิว LINE ผ่าน OA ของบริษัท (retry key คงที่) · กฎอีเมลอย่างเดียวไม่มีแถว LINE (ทิศตรงข้าม) · ส่งสำเร็จ = หลักฐานการส่ง ·
/// ลองใหม่ใช้ key เดิม · ล้มถาวร ⇒ ถอยไปอีเมลครั้งเดียว (ทิศตรงข้าม: ปิดถอย ⇒ ล้มพร้อมเหตุ ไม่มีอีเมล) · กลุ่มกลางของแอดมินไม่ถูกส่งเลย ·
/// ไม่มี OA บริษัท ⇒ บันทึกกฎติ๊ก LINE ไม่ได้ · ลิงก์ผูก LINE: ยินยอม → ผูก (เข้ารหัส + ความยินยอม) → "หยุด" ปลด + ถอนยินยอม · รหัสใช้ครั้งเดียว
/// </summary>
[Trait("Category", "Db")]
public class LineQueueRound254DbTests
{
    private readonly ITestOutputHelper _out;
    public LineQueueRound254DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-254-wp11");
    private const string AdminGroup = "Cadmin254000000000000000000000001";
    private const string Bot = "Ubot254000000000000000000000000001";

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings("T", "S", "@nextacc", null, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());

    private sealed class FakeSender : IEmailSender
    {
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default) => Task.FromResult(EmailSendResult.Ok("mid-254"));
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static EmailScheduleService Sched(AccountingDbContext db)
        => new(db, F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : new FakeSender()),
            F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: Config(), recipients: Resolver(db));

    /// <summary>ตัวส่ง LINE ปลอม — จดทุกการเรียก (OA · ปลายทาง · retry key) แล้วตอบตาม <paramref name="respond"/></summary>
    private sealed record Push(string OaScope, LineTargetKind Target, string To, Guid RetryKey);

    private static ILineNotifyService FakeLine(List<Push> log, Func<int, LinePushResult> respond)
        => F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushContactAsync)) return null;
            log.Add(new Push((string)a[1]!, (LineTargetKind)a[2]!, (string)a[3]!, (Guid)a[6]!));
            return respond(log.Count);
        });

    private static ContactLineQueueService Queue(AccountingDbContext db, ILineNotifyService line)
        => new(db, line, Resolver(db), Secrets, Sched(db), NullLogger<ContactLineQueueService>.Instance);

    private static DateTime TodayBkk() => DateTime.UtcNow.AddHours(7).Date;

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid InvoiceId, Guid EndpointId, Guid RuleId);

    /// <summary>บริษัทที่มี LINE OA พร้อม (token/secret เข้ารหัส · บอทที่จำไว้) + ผู้ติดต่อ (อีเมล) + ปลายทาง LINE 1:1 ผ่าน OA บริษัทพร้อมความยินยอม +
    /// ใบแจ้งหนี้ครบกำหนดอีก 3 วัน + กฎ "ใกล้ครบกำหนด −3 วัน"</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool channelEmail = false, bool lineUser = true,
        bool fallback = true, bool companyOa = true, string? groupId = null)
    {
        var company = new Company { Name = "บริษัท L2 " + tag, TaxId = "", Email = "office@seller254.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineEnabled = companyOa,
            LineChannelAccessToken = companyOa ? Secrets.Protect("company-token-254") : null,
            LineChannelSecret = companyOa ? Secrets.Protect("company-secret-254") : null,
            LineOaDestination = Bot,
        });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า L2 " + tag, IsCustomer = true, TaxId = "", Email = "ap@buyer254.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var consent = new PdpaConsentRecord { CompanyId = company.Id, Purpose = ContactLineBindRule.ConsentPurpose, SubjectContactId = contact.Id };
        db.PdpaConsentRecords.Add(consent);
        var isGroup = groupId != null;
        var lineId = groupId ?? "Ucustomer2540000000000000000000001";
        var ep = new ContactNotifyEndpoint
        {
            CompanyId = company.Id, ContactId = contact.Id, Kind = isGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.LineUser,
            LineUserId = isGroup ? null : Secrets.Protect(lineId), LineGroupId = isGroup ? Secrets.Protect(lineId) : null,
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, lineId), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            MemberCountSnapshot = isGroup ? 5 : null, Verified = true, Source = "test", ConsentRecordId = consent.Id,
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.All),
        };
        db.ContactNotifyEndpoints.Add(ep);
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV254-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = TodayBkk().AddDays(-27), DueDate = TodayBkk().AddDays(3),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        var rule = new EmailScheduleRule
        {
            CompanyId = company.Id, Trigger = "DocumentDueSoon", OffsetDays = -3, SendAtHour = 0, IsActive = true,
            DocumentType = DocumentType.Invoice.ToString(),
            ChannelEmail = channelEmail, ChannelLineUser = lineUser && !isGroup, ChannelLineGroup = isGroup, LineFallbackToEmail = fallback,
        };
        db.EmailScheduleRules.Add(rule);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(company.Id, contact.Id, inv.Id, ep.Id, rule.Id);
    }

    private static Task<List<LineQueue>> LineRowsAsync(AccountingDbContext db, Seed s)
        => db.LineQueues.AsNoTracking().Where(q => q.CompanyId == s.CompanyId && q.EntityId == s.InvoiceId).ToListAsync();

    private static Task<List<EmailQueue>> EmailRowsAsync(AccountingDbContext db, Seed s)
        => db.EmailQueues.AsNoTracking().Where(q => q.CompanyId == s.CompanyId && q.EntityId == s.InvoiceId && !q.IsDeleted).ToListAsync();

    /// <summary>ให้แถวคิวถึงเวลาส่ง (กฎตั้ง 00:00 ไทยของวันนี้อยู่แล้ว — กันเวลาเครื่องเทสต์) · แถวรอส่งของเทสต์อื่น (บริษัทอื่น) เลื่อนไปอนาคต
    /// ⇒ ผู้ส่งคิวของเทสต์นี้เห็นเฉพาะแถวของบริษัทนี้ (ฐานเทสต์ใช้ร่วมกัน)</summary>
    private static async Task DueNowAsync(AccountingDbContext db, params Seed[] seeds)
    {
        var ids = seeds.Select(x => x.CompanyId).ToList();
        await db.LineQueues.Where(q => !ids.Contains(q.CompanyId) && q.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddDays(30)));
        await db.LineQueues.Where(q => ids.Contains(q.CompanyId)).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task ติ๊ก_LINE_อย่างเดียว_เข้าคิว_LINE_ผ่าน_OA_บริษัท_ไม่มีอีเมล_retry_key_คงที่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "line-only " + Guid.NewGuid().ToString("N")[..6]);

        await Sched(db).ScanDueSoonAndOverdueAsync();
        await Sched(db).ScanDueSoonAndOverdueAsync();   // สแกนซ้ำไม่เพิ่มแถว

        var row = Assert.Single(await LineRowsAsync(db, s));
        Assert.Equal(ContactLineChannel.StatusPending, row.Status);
        Assert.Equal(s.EndpointId, row.EndpointId);
        Assert.Equal(ContactLineChannel.RetryKey(s.CompanyId, row.IdempotencyKey), row.RetryKey);
        Assert.True(row.FallbackToEmail);
        Assert.Empty(await EmailRowsAsync(db, s));
    }

    [Fact]
    public async Task ทิศตรงข้าม_กฎอีเมลอย่างเดียว_ไม่มีแถว_LINE_อีเมลเดิมเข้าคิว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "email-only " + Guid.NewGuid().ToString("N")[..6], channelEmail: true, lineUser: false);

        await Sched(db).ScanDueSoonAndOverdueAsync();

        Assert.Empty(await LineRowsAsync(db, s));
        Assert.Equal("Pending", Assert.Single(await EmailRowsAsync(db, s)).Status);
    }

    [Fact]
    public async Task ส่งสำเร็จ_ประทับส่งแล้วจากคำตอบ_LINE_เป็นหลักฐานการส่ง_ใช้_key_ของแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "sent " + Guid.NewGuid().ToString("N")[..6]);
        await Sched(db).ScanDueSoonAndOverdueAsync();
        await DueNowAsync(db, s);
        var log = new List<Push>();

        var sent = await Queue(db, FakeLine(log, _ => new LinePushResult(true, null, HttpStatus: 200, LineRequestId: "req-1"))).ProcessPendingAsync();

        Assert.Equal(1, sent);
        var row = Assert.Single(await LineRowsAsync(db, s));
        Assert.Equal(ContactLineChannel.StatusSent, row.Status);
        Assert.Equal("req-1", row.LineRequestId);
        var push = Assert.Single(log);
        Assert.Equal(NotifyEndpointKind.ScopeCompany, push.OaScope);
        Assert.Equal(LineTargetKind.TenantMember, push.Target);
        Assert.Equal(row.RetryKey, push.RetryKey);
        Assert.Contains(s.InvoiceId, await DocumentDeliveryEvidence.DeliveredAsync(db, s.CompanyId, new[] { s.InvoiceId }));
    }

    [Fact]
    public async Task เครือข่ายล้ม_ลองใหม่ด้วย_key_เดิม_ไม่ส่งสองข้อความ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "retry " + Guid.NewGuid().ToString("N")[..6]);
        await Sched(db).ScanDueSoonAndOverdueAsync();
        await DueNowAsync(db, s);
        var log = new List<Push>();
        // ครั้งแรก timeout (LINE อาจรับไปแล้ว) · ครั้งที่สอง LINE ตอบ 409 = รับ key นี้ไปแล้ว ⇒ ส่งแล้ว ไม่ใช่ส่งซ้ำ
        var line = FakeLine(log, n => n == 1
            ? new LinePushResult(false, "เรียก LINE ไม่สำเร็จ (TaskCanceledException)", TransportError: true)
            : new LinePushResult(false, "LINE ตอบ 409: conflict", HttpStatus: 409));

        await Queue(db, line).ProcessPendingAsync();
        var afterFirst = Assert.Single(await LineRowsAsync(db, s));
        Assert.Equal(ContactLineChannel.StatusPending, afterFirst.Status);
        Assert.Equal(1, afterFirst.RetryCount);
        db.ChangeTracker.Clear();
        await DueNowAsync(db, s);
        await Queue(db, line).ProcessPendingAsync();

        var final = Assert.Single(await LineRowsAsync(db, s));
        Assert.Equal(ContactLineChannel.StatusSent, final.Status);
        Assert.Equal(LinePushOutcome.CodeAlreadyAccepted, final.RuleCode);
        Assert.Equal(2, log.Count);
        Assert.Equal(log[0].RetryKey, log[1].RetryKey);
    }

    [Fact]
    public async Task ผู้รับไม่ได้เป็นเพื่อน_บล็อกปลายทาง_ถอยไปอีเมลครั้งเดียว_ทิศตรงข้ามปิดถอย_ไม่มีอีเมล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        foreach (var fallback in new[] { true, false })
        {
            var s = await SeedAsync(db, "fb-" + fallback + " " + Guid.NewGuid().ToString("N")[..6], fallback: fallback);
            await Sched(db).ScanDueSoonAndOverdueAsync();
            await DueNowAsync(db, s);
            var log = new List<Push>();
            await Queue(db, FakeLine(log, _ => new LinePushResult(false, "LINE ตอบ 400: The user hasn't added the LINE Official Account as a friend", HttpStatus: 400)))
                .ProcessPendingAsync();
            db.ChangeTracker.Clear();

            var row = Assert.Single(await LineRowsAsync(db, s));
            Assert.Equal(ContactLineChannel.StatusFailed, row.Status);
            Assert.Equal(LinePushOutcome.CodeNotFriend, row.RuleCode);
            Assert.NotNull((await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == s.EndpointId)).BlockedAt);
            var emails = await EmailRowsAsync(db, s);
            if (fallback)
            {
                var mail = Assert.Single(emails);
                Assert.EndsWith(":line-fallback", mail.IdempotencyKey);
                Assert.Equal("ap@buyer254.co.th", mail.ToEmail);
                Assert.NotNull(row.FallbackQueuedAt);
            }
            else
            {
                Assert.Empty(emails);
                Assert.Null(row.FallbackQueuedAt);
            }
        }
    }

    [Fact]
    public async Task กลุ่มกลางของแอดมิน_ไม่เข้าคิวส่ง_ไม่มีการเรียก_LINE_ทิศตรงข้ามกลุ่มของผู้ติดต่อส่งได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var central = await SeedAsync(db, "admin-group " + Guid.NewGuid().ToString("N")[..6], groupId: AdminGroup, fallback: false);
        var own = await SeedAsync(db, "own-group " + Guid.NewGuid().ToString("N")[..6], groupId: "Ccustomer254000000000000000000009", fallback: false);

        await Sched(db).ScanDueSoonAndOverdueAsync();
        await DueNowAsync(db, central, own);
        var log = new List<Push>();
        await Queue(db, FakeLine(log, _ => new LinePushResult(true, null, HttpStatus: 200))).ProcessPendingAsync();

        var centralRow = Assert.Single(await LineRowsAsync(db, central));
        Assert.Equal(ContactLineChannel.StatusSkipped, centralRow.Status);
        Assert.Null(centralRow.EndpointId);
        Assert.Contains("กลุ่ม LINE กลาง", centralRow.ErrorMessage);
        Assert.DoesNotContain(log, p => p.To == AdminGroup);
        var ownRow = Assert.Single(await LineRowsAsync(db, own));
        Assert.Equal(ContactLineChannel.StatusSent, ownRow.Status);
        Assert.Contains(log, p => p.Target == LineTargetKind.ContactGroup);
    }

    [Fact]
    public async Task บันทึกกฎติ๊ก_LINE_โดยไม่มี_OA_บริษัท_ปฏิเสธ_ทิศตรงข้ามอีเมลอย่างเดียวบันทึกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "no-oa " + Guid.NewGuid().ToString("N")[..6], companyOa: false);
        var svc = Sched(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UpsertRuleAsync(s.CompanyId,
            new EmailScheduleRule { Trigger = "DocumentApproved", ChannelEmail = false, ChannelLineUser = true }, "tester"));
        Assert.Contains("LINE OA ของบริษัท", ex.Message);
        var saved = await svc.UpsertRuleAsync(s.CompanyId, new EmailScheduleRule { Trigger = "DocumentApproved", ChannelEmail = true }, "tester");
        Assert.True(saved.ChannelEmail);
        Assert.False(saved.ChannelLineUser);
        // ปลายทาง LINE ที่มีอยู่แสดงเหตุ "OA ของบริษัทยังไม่พร้อม" (ไม่หายเงียบ)
        var set = await Resolver(db).ResolveAsync(s.CompanyId, s.ContactId, NotifyTopic.Document);
        var opt = Assert.Single(set.Options, o => o.EndpointId == s.EndpointId);
        Assert.False(opt.Available);
        Assert.Equal(ContactRecipientRule.RuleCodeCompanyOaNotReady, opt.RuleCode);
    }

    // ───── ลิงก์/QR ผูก LINE (Q6 ทางลัด) ─────

    private static ContactLineBindingService Binding(AccountingDbContext db)
        => new(db, Secrets, Resolver(db), F4Proxy<IChatRateLimiter>.Create((m, _) => m == nameof(IChatRateLimiter.TryConsumeAsync) ? (object)true : 0),
            Config(), NullLogger<ContactLineBindingService>.Instance);

    [Fact]
    public async Task ลิงก์ผูก_ยินยอมแล้วผูก_เข้ารหัส_มีความยินยอม_รหัสใช้ครั้งเดียว_หยุดแล้วปลดและถอนยินยอม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "bind " + Guid.NewGuid().ToString("N")[..6]);
        var invite = await Binding(db).CreateInviteAsync(s.CompanyId, s.ContactId, null, "owner@seller254.co.th");
        var token = Uri.UnescapeDataString(invite.Path.Split("t=")[1]);
        Assert.Equal("https", new Uri(invite.Url!).Scheme);
        Assert.StartsWith("data:image/png;base64,", invite.QrDataUri);

        // ยังไม่ยินยอม ⇒ ไม่มีรหัส ⇒ ส่งข้อความผูกแล้วไม่ใช่ของผู้ติดต่อ (null = เส้นลูกค้าเว็บ)
        Assert.Null(await Binding(db).TryRedeemAsync(s.CompanyId, Bot, "Unew2540000000000000000000000001", "ผูก LN-ABCDEFGH"));
        var view = await Binding(db).GetJoinViewAsync(s.CompanyId, token);
        Assert.Null(view.Problem);
        Assert.Contains("LY Corporation", view.ConsentText);

        var consent = await Binding(db).ConsentAsync(s.CompanyId, token, "203.0.113.5", "test-agent");
        var code = consent.MessageText;
        db.ChangeTracker.Clear();
        var reply = await Binding(db).TryRedeemAsync(s.CompanyId, Bot, "Unew2540000000000000000000000001", code);
        Assert.Contains("ผูก LINE สำเร็จ", reply);
        db.ChangeTracker.Clear();
        var ep = await db.ContactNotifyEndpoints.AsNoTracking()
            .SingleAsync(e => e.CompanyId == s.CompanyId && e.Source == ContactLineBindRule.SourceLineInvite);
        Assert.NotEqual("Unew2540000000000000000000000001", ep.LineUserId);   // เข้ารหัส
        Assert.Equal("Unew2540000000000000000000000001", Secrets.Unprotect(ep.LineUserId));
        Assert.Equal(NotifyEndpointKind.ScopeCompany, ep.OaScope);
        var consentRow = await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == ep.ConsentRecordId);
        Assert.Equal(ContactLineBindRule.ConsentPurpose, consentRow.Purpose);
        Assert.False(string.IsNullOrEmpty(consentRow.EvidenceHash));

        // ใช้ซ้ำไม่ได้
        Assert.Equal(ContactLineBindRule.Replies.Invalid, await Binding(db).TryRedeemAsync(s.CompanyId, Bot, "Uother254000000000000000000000001", code));
        // บริษัทอื่นใช้รหัสนี้ไม่ได้ (tenant) ⇒ ไม่ใช่ของผู้ติดต่อของบริษัทนั้น
        Assert.Null(await Binding(db).TryRedeemAsync(Guid.NewGuid(), Bot, "Uother254000000000000000000000001", code));

        // "หยุด" ⇒ ปลด + ถอนความยินยอม · ข้อความอื่นไม่ตอบ (ทิศตรงข้าม)
        Assert.Null(await Binding(db).TryStopAsync(s.CompanyId, Bot, "Unew2540000000000000000000000001", "หยุดก่อนนะ"));
        Assert.NotNull(await Binding(db).TryStopAsync(s.CompanyId, Bot, "Unew2540000000000000000000000001", "หยุด"));
        db.ChangeTracker.Clear();
        var after = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == ep.Id);
        Assert.NotNull(after.UnlinkedAt);
        Assert.Null(after.LineUserId);
        Assert.NotNull((await db.PdpaConsentRecords.AsNoTracking().SingleAsync(c => c.Id == ep.ConsentRecordId)).WithdrawnAt);
    }

    [Fact]
    public async Task ลิงก์ผูก_บริษัทไม่มี_OA_สร้างไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "bind-no-oa " + Guid.NewGuid().ToString("N")[..6], companyOa: false);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Binding(db).CreateInviteAsync(s.CompanyId, s.ContactId, null, null));
        Assert.False(await db.ContactLineInvites.AnyAsync(i => i.CompanyId == s.CompanyId));
    }
}
