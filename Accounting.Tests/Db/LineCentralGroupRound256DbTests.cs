using System.Net;
using System.Text.Json;
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
/// รอบ 256 (คำตัดสินเจ้าของ 256-2 · 2026-11-09) บน PostgreSQL จริง — บริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) ผูก/ส่งกลุ่ม LINE ของผู้ติดต่อผ่าน
/// LINE OA กลาง: #HeyNextAcc บน webhook กลาง ⇒ รหัส (scope nextacc · ผูกกับบริษัทผู้ให้บริการ) → ยืนยัน → กฎ "กลุ่ม LINE" บันทึกได้โดยไม่มี OA บริษัท →
/// คิวส่งด้วย <b>token ของ OA กลาง</b> + นับต้นทุน OA กลาง · ทิศตรงข้าม: บริษัทอื่นยืนยันรหัสกลาง ⇒ ปฏิเสธด้วยประโยคเดิม · กลุ่มกลางของแอดมิน ⇒ ไม่ออกรหัส ·
/// ไม่ได้ตั้งบริษัทผู้ให้บริการ ⇒ ตอบ CentralOaNotSupported · #ByeNextAcc ปลดเฉพาะปลายทาง nextacc ของบริษัทผู้ให้บริการ ·
/// บริษัทผู้ให้บริการเปลี่ยนหลังผูก ⇒ คิวข้ามพร้อมเหตุ ไม่ยิง LINE
/// <para>อยู่ collection เดียวกับเทสต์ที่ตั้ง <c>SiteSettings.PlatformCompanyId</c> (แถวเดียวของระบบ) — ไม่รันขนานกัน · คืนค่าเดิมเสมอ</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class LineCentralGroupRound256DbTests
{
    private readonly ITestOutputHelper _out;
    public LineCentralGroupRound256DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-256-central");
    private const string AdminGroup = "Cadmin256c0000000000000000000001";
    private const string CentralBot = "Ucentral256c000000000000000000001";
    private const string CentralToken = "CENTRAL-TOKEN-256";

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings(CentralToken, "S", "@810pamnc", CentralBot, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());

    /// <summary>ตัวส่ง LINE ปลอมของบริการกลุ่ม — จดว่าอ่านสรุปกลุ่มด้วย OA ไหน + ประกาศ/ออกจากกลุ่ม</summary>
    private static ILineNotifyService FakeLine(List<string> log, int members = 7)
        => F4Proxy<ILineNotifyService>.Create((m, a) => m switch
        {
            nameof(ILineNotifyService.TryGetNextAccGroupSummaryAsync) => Note(log, "summary:central", new LineGroupSummary("ทีม NextAcc × ลูกค้า", members)),
            nameof(ILineNotifyService.TryGetCompanyGroupSummaryAsync) => Note(log, "summary:company", new LineGroupSummary("x", members)),
            nameof(ILineNotifyService.TryLeaveNextAccGroupAsync) => Note(log, "leave:central:" + a[0], true),
            nameof(ILineNotifyService.TryLeaveCompanyGroupAsync) => Note(log, "leave:company:" + a[1], true),
            nameof(ILineNotifyService.TryPushContactTextAsync) => Note(log, "text:" + a[1] + ":" + a[3], LinePushResult.Success),
            _ => null,
        });

    private static object Note(List<string> log, string s, object result) { log.Add(s); return result; }

    private static ContactLineGroupService Groups(AccountingDbContext db, List<string> log, int members = 7)
        => new(db, Secrets, FakeLine(log, members), Resolver(db), Central(),
            F4Proxy<IChatRateLimiter>.Create((m, _) => m == nameof(IChatRateLimiter.TryConsumeAsync) ? (object)true : 0),
            NullLogger<ContactLineGroupService>.Instance);

    private static readonly ContactNotifyActor Actor = new(null, "owner@platform256.local", "127.0.0.1");

    private static JsonElement Ev(string type, string groupId, string? text = null)
    {
        var json = text == null
            ? JsonSerializer.Serialize(new { type, source = new { type = "group", groupId } })
            : JsonSerializer.Serialize(new { type, source = new { type = "group", groupId }, message = new { type = "text", text } });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string CodeFrom(string? reply) => Regex.Match(reply ?? "", @"GB-[A-Z0-9]{8}").Value;

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    /// <summary>บริษัท + ผู้ติดต่อ · <paramref name="companyOa"/> false = ไม่มี LINE OA ของบริษัท (บริษัทผู้ให้บริการใช้ OA กลาง)</summary>
    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool companyOa = false)
    {
        var company = new Company { Name = "บริษัท 256c " + tag, TaxId = "", Email = "office@p256.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineEnabled = companyOa,
            LineChannelAccessToken = companyOa ? Secrets.Protect("company-token-256") : null,
            LineChannelSecret = companyOa ? Secrets.Protect("company-secret-256") : null,
        });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 256c " + tag, IsCustomer = true, TaxId = "", Email = "ap@b256.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(company.Id, contact.Id);
    }

    /// <summary>ตั้งบริษัทผู้ให้บริการชั่วคราว (null = ไม่ได้ตั้ง) แล้วคืนค่าเดิมเสมอ</summary>
    private static async Task WithPlatformAsync(AccountingDbContext db, Guid? platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var previous = s.PlatformCompanyId;
        s.PlatformCompanyId = platformId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        try { await body(); }
        finally
        {
            db.ChangeTracker.Clear();
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else again.PlatformCompanyId = previous;
            await db.SaveChangesAsync();
        }
    }

    private static async Task SetPlatformAsync(AccountingDbContext db, Guid? platformId)
    {
        db.ChangeTracker.Clear();
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstAsync();
        s.PlatformCompanyId = platformId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ───────── ตัวส่งจริง (LineNotifyService) + จด Authorization ─────────

    private sealed class AuthCapture : HttpMessageHandler
    {
        public List<string?> Bearers { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearers.Add(request.Headers.Authorization?.Parameter);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            resp.Headers.Add("x-line-request-id", "req-256c");
            return Task.FromResult(resp);
        }
    }

    private static LineNotifyService RealLine(AccountingDbContext db, AuthCapture h)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, _) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, db, Secrets, Central());
    }

    private sealed class FakeSender : IEmailSender
    {
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default) => Task.FromResult(EmailSendResult.Ok("mid-256c"));
    }

    private static EmailScheduleService Sched(AccountingDbContext db)
        => new(db, F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : new FakeSender()),
            F4Proxy<IPdfGenerationService>.Create((_, _) => null), NullLogger<EmailScheduleService>.Instance,
            payLinks: null, config: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build(),
            recipients: Resolver(db));

    private static DateTime TodayBkk() => DateTime.UtcNow.AddHours(7).Date;

    /// <summary>ใบแจ้งหนี้ครบกำหนดอีก 3 วัน + กฎ "ใกล้ครบกำหนด −3 วัน" ติ๊กกลุ่ม LINE อย่างเดียว (บันทึกผ่านด่านบันทึกกฎจริง)</summary>
    private static async Task<Guid> InvoiceWithGroupRuleAsync(AccountingDbContext db, Seed s)
    {
        var inv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV256C-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = TodayBkk().AddDays(-27), DueDate = TodayBkk().AddDays(3),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Sched(db).UpsertRuleAsync(s.CompanyId, new EmailScheduleRule
        {
            CompanyId = s.CompanyId, Trigger = "DocumentDueSoon", OffsetDays = -3, SendAtHour = 0, IsActive = true,
            DocumentType = DocumentType.Invoice.ToString(),
            ChannelEmail = false, ChannelLineUser = false, ChannelLineGroup = true, LineFallbackToEmail = false,
        }, "test-256c");
        db.ChangeTracker.Clear();
        return inv.Id;
    }

    private static async Task DueNowAsync(AccountingDbContext db, Guid companyId)
    {
        await db.LineQueues.Where(q => q.CompanyId != companyId && q.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddDays(30)));
        await db.LineQueues.Where(q => q.CompanyId == companyId).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddMinutes(-1)));
    }

    private static Task<long> GroupPushCountAsync(AccountingDbContext db)
        => db.NextAccOaUsageCounters.AsNoTracking()
            .Where(c => c.Period == NextAccOaQuotaRule.PeriodOf(DateTime.UtcNow) && c.Kind == NextAccOaQuotaRule.KindGroupPush)
            .Select(c => (long?)c.Count).FirstOrDefaultAsync().ContinueWith(t => t.Result ?? 0);

    [Fact]
    public async Task บริษัทผู้ให้บริการ_HeyNextAcc_บน_OA_กลาง_ได้รหัส_ยืนยันผูก_คิวส่งด้วย_token_กลาง_และนับต้นทุน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "flow " + Guid.NewGuid().ToString("N")[..6]);
        var group = "C256c" + Guid.NewGuid().ToString("N")[..27];
        var log = new List<string>();
        await WithPlatformAsync(db, s.CompanyId, async () =>
        {
            var hey = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "#HeyNextAcc"));
            var code = CodeFrom(hey.Reply);
            Assert.False(string.IsNullOrEmpty(code), hey.Reply);
            Assert.Contains("summary:central", log);   // จำนวนสมาชิกอ่านด้วย OA กลาง
            Assert.DoesNotContain("summary:company", log);
            var codeRow = await db.LineGroupBindCodes.AsNoTracking().SingleAsync(r => r.CodeHash == LineGroupBindRule.Hash(code[3..]));
            Assert.Equal(NotifyEndpointKind.ScopeNextAcc, codeRow.OaScope);
            Assert.Equal(s.CompanyId, codeRow.CompanyIdHint);

            // หน้าผู้ติดต่อ: กล่องผูกกลุ่มเปิด + บอกให้เชิญ OA กลาง (Basic ID จากค่าตั้ง) · ลิงก์ 1:1 ยังปิด (ไม่มี OA บริษัท)
            var overview = await Resolver(db).GetOverviewAsync(s.CompanyId, s.ContactId, canEdit: true);
            Assert.True(overview!.LineGroupBind!.Available);
            Assert.True(overview.LineGroupBind.ViaCentralOa);
            Assert.Contains("@810pamnc", overview.LineGroupBind.Instruction);
            Assert.False(overview.LineBind!.Available);

            var preview = await Groups(db, log).PreviewAsync(s.CompanyId, s.ContactId, code, null);
            Assert.Null(preview.Problem);
            await Groups(db, log).ConfirmAsync(s.CompanyId, s.ContactId, new ConfirmContactLineGroupRequest(code, null, null, true), Actor);
            db.ChangeTracker.Clear();
            var ep = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.Kind == NotifyEndpointKind.LineGroup);
            Assert.Equal(NotifyEndpointKind.ScopeNextAcc, ep.OaScope);
            Assert.Equal(CentralBot, ep.OaDestination);
            Assert.Equal(group, Secrets.Unprotect(ep.LineGroupId));
            Assert.Contains($"text:{NotifyEndpointKind.ScopeNextAcc}:{group}", log);   // ประกาศผ่าน OA กลาง

            // กฎติ๊กกลุ่ม LINE อย่างเดียว บันทึกได้ทั้งที่ไม่มี OA ของบริษัท → เข้าคิว → ส่งด้วย token ของ OA กลาง
            var invoiceId = await InvoiceWithGroupRuleAsync(db, s);
            await Sched(db).ScanDueSoonAndOverdueAsync();
            var queued = await db.LineQueues.AsNoTracking().SingleAsync(q => q.CompanyId == s.CompanyId && q.EntityId == invoiceId);
            Assert.Equal(ContactLineChannel.StatusPending, queued.Status);
            Assert.Equal(ep.Id, queued.EndpointId);
            await DueNowAsync(db, s.CompanyId);
            var before = await GroupPushCountAsync(db);
            var h = new AuthCapture();
            var sent = await new ContactLineQueueService(db, RealLine(db, h), Resolver(db), Secrets, Sched(db), NullLogger<ContactLineQueueService>.Instance)
                .ProcessPendingAsync();
            Assert.Equal(1, sent);
            Assert.Equal(CentralToken, Assert.Single(h.Bearers));
            var row = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == queued.Id);
            Assert.Equal(ContactLineChannel.StatusSent, row.Status);
            Assert.Equal(NotifyEndpointKind.ScopeNextAcc, row.OaScope);
            Assert.True(await GroupPushCountAsync(db) >= before + 1);

            // ทิศตรงข้าม (ตรวจใหม่ตอนส่ง): บริษัทผู้ให้บริการเปลี่ยน ⇒ แถวใหม่ข้ามพร้อมเหตุ ไม่ยิง LINE
            await db.LineQueues.Where(q => q.Id == queued.Id).ExecuteUpdateAsync(x => x
                .SetProperty(q => q.Status, ContactLineChannel.StatusPending).SetProperty(q => q.SentAt, (DateTime?)null)
                .SetProperty(q => q.RetryKey, Guid.NewGuid()));
            await SetPlatformAsync(db, Guid.NewGuid());
            await DueNowAsync(db, s.CompanyId);
            var h2 = new AuthCapture();
            await new ContactLineQueueService(db, RealLine(db, h2), Resolver(db), Secrets, Sched(db), NullLogger<ContactLineQueueService>.Instance)
                .ProcessPendingAsync();
            Assert.Empty(h2.Bearers);
            var skipped = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == queued.Id);
            Assert.Equal(ContactLineChannel.StatusSkipped, skipped.Status);
            Assert.Contains("บริษัทผู้ให้บริการ", skipped.ErrorMessage);
            Assert.Equal(ContactRecipientRule.RuleCodeNextAccNotOpen, skipped.RuleCode);
        });
    }

    [Fact]
    public async Task ทิศตรงข้าม_บริษัทอื่นยืนยันรหัสจาก_OA_กลาง_ถูกปฏิเสธด้วยประโยคเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var platform = await SeedAsync(db, "plat " + Guid.NewGuid().ToString("N")[..6]);
        var other = await SeedAsync(db, "other " + Guid.NewGuid().ToString("N")[..6], companyOa: true);
        var group = "C256c" + Guid.NewGuid().ToString("N")[..27];
        var log = new List<string>();
        await WithPlatformAsync(db, platform.CompanyId, async () =>
        {
            var code = CodeFrom((await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "#HeyNextAcc"))).Reply);
            Assert.False(string.IsNullOrEmpty(code));

            var preview = await Groups(db, log).PreviewAsync(other.CompanyId, other.ContactId, code, null);
            Assert.Equal(LineGroupBindRule.Replies.CentralCodeRefused, preview.Problem);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Groups(db, log).ConfirmAsync(other.CompanyId, other.ContactId,
                new ConfirmContactLineGroupRequest(code, null, null, true), Actor));
            Assert.Equal(LineGroupBindRule.Replies.CentralCodeRefused, ex.Message);
            Assert.False(await db.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == other.CompanyId && e.Kind == NotifyEndpointKind.LineGroup));
            // รหัสยังไม่ถูกใช้ — บริษัทผู้ให้บริการยังยืนยันได้
            Assert.Null((await db.LineGroupBindCodes.AsNoTracking().SingleAsync(r => r.CodeHash == LineGroupBindRule.Hash(code[3..]))).UsedAt);
            // หน้าผู้ติดต่อของบริษัทอื่น: กล่องผูกกลุ่มยังเป็นเส้น OA ของบริษัท (N8)
            var overview = await Resolver(db).GetOverviewAsync(other.CompanyId, other.ContactId, canEdit: true);
            Assert.False(overview!.LineGroupBind!.ViaCentralOa);
        });
    }

    [Theory]
    [InlineData(AdminGroup, 5)]
    [InlineData("C256cbig000000000000000000000001", 201)]
    public async Task ทิศตรงข้าม_กลุ่มกลางของแอดมิน_หรือเกิน200คน_ไม่ออกรหัสบน_OA_กลาง(string group, int members)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "refuse " + Guid.NewGuid().ToString("N")[..6]);
        await WithPlatformAsync(db, s.CompanyId, async () =>
        {
            var before = await db.LineGroupBindCodes.CountAsync();
            var r = await Groups(db, new List<string>(), members).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "#HeyNextAcc"));
            Assert.StartsWith("❌", r.Reply);
            Assert.Equal(before, await db.LineGroupBindCodes.CountAsync());
        });
    }

    [Fact]
    public async Task ไม่ได้ตั้งบริษัทผู้ให้บริการ_ตอบ_CentralOaNotSupported_ไม่ออกรหัส()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        await WithPlatformAsync(db, null, async () =>
        {
            var before = await db.LineGroupBindCodes.CountAsync();
            var group = "C256c" + Guid.NewGuid().ToString("N")[..27];
            foreach (var cmd in new[] { "#HeyNextAcc", "#ByeNextAcc" })
            {
                var r = await Groups(db, new List<string>()).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, cmd));
                Assert.Equal(LineGroupBindRule.Replies.CentralOaNotSupported, r.Reply);
                Assert.False(r.LeaveAfterReply);
            }
            Assert.Null((await Groups(db, new List<string>()).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "สวัสดี"))).Reply);
            Assert.Null((await Groups(db, new List<string>()).HandleCentralGroupEventAsync(CentralBot, Ev("join", group))).Reply);
            Assert.Equal(before, await db.LineGroupBindCodes.CountAsync());
        });
    }

    [Fact]
    public async Task ByeNextAcc_บน_OA_กลาง_ปลดเฉพาะปลายทาง_nextacc_ของบริษัทผู้ให้บริการ_แล้วบอทกลางออก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var platform = await SeedAsync(db, "bye " + Guid.NewGuid().ToString("N")[..6]);
        var other = await SeedAsync(db, "bye-other " + Guid.NewGuid().ToString("N")[..6], companyOa: true);
        var group = "C256c" + Guid.NewGuid().ToString("N")[..27];
        var hash = NotifyEndpointKind.LineIdHash(CentralBot, group);
        ContactNotifyEndpoint Ep(Seed s, string scope) => new()
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, Kind = NotifyEndpointKind.LineGroup, OaScope = scope, OaDestination = CentralBot,
            LineGroupId = Secrets.Protect(group), LineIdHash = hash, MemberCountSnapshot = 7, Verified = true, Source = LineGroupBindRule.SourceGroupCode,
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.All), LinkedAt = DateTime.UtcNow,
        };
        var central = Ep(platform, NotifyEndpointKind.ScopeNextAcc);
        var sameCompanyOtherScope = Ep(platform, NotifyEndpointKind.ScopeCompany);   // แถวผิดรูปที่ hash ชนกัน — ห้ามถูกแตะ
        var otherCompany = Ep(other, NotifyEndpointKind.ScopeNextAcc);              // บริษัทอื่น — ห้ามถูกแตะ
        db.ContactNotifyEndpoints.AddRange(central, sameCompanyOtherScope, otherCompany);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var log = new List<string>();
        await WithPlatformAsync(db, platform.CompanyId, async () =>
        {
            var bye = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "#ByeNextAcc"));
            Assert.Equal(LineGroupBindRule.Replies.Bye, bye.Reply);
            Assert.True(bye.LeaveAfterReply);
            await Groups(db, log).LeaveCentralGroupIfUnboundAsync(CentralBot, group);
            Assert.Contains("leave:central:" + group, log);
            Assert.DoesNotContain(log, l => l.StartsWith("leave:company:"));
            db.ChangeTracker.Clear();
            Assert.NotNull((await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == central.Id)).UnlinkedAt);
            Assert.Null((await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == sameCompanyOtherScope.Id)).UnlinkedAt);
            Assert.Null((await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == otherCompany.Id)).UnlinkedAt);
            // ไม่เหลือการผูก nextacc ⇒ #ByeNextAcc ซ้ำตอบว่ายังไม่ได้ผูก
            Assert.Equal(LineGroupBindRule.Replies.ByeNothing,
                (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev("message", group, "#ByeNextAcc"))).Reply);
        });
    }
}
