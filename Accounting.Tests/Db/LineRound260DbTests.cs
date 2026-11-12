using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 260-4 (คำตัดสิน 260-4-1…260-4-4) — เส้นจริงบน PostgreSQL: ชั่วโมงเงียบเลื่อนการ์ดตามกฎแล้วส่งเมื่อพ้นช่วง (ใบเสร็จ/ส่งเองไม่ถูกเลื่อน) ·
/// LINE ล้มเพราะ Token ใช้ไม่ได้ ⇒ กระดิ่งเจ้าของครั้งเดียว (ล้มอีกแถวไม่แจ้งซ้ำ · ล้มครั้งเดียวแบบอื่นไม่แจ้ง) ·
/// ปลายทางทดสอบ LINE ที่ระบบรู้จัก = ของบริษัทนี้ที่ผูกกับ OA ของบริษัทเท่านั้น
/// </summary>
[Trait("Category", "Db")]
public class LineRound260DbTests
{
    private readonly ITestOutputHelper _out;
    public LineRound260DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private const string Bot = "Ubot260";
    private static readonly SecretProtector Secrets = new("line-260-db");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "line-260-db-test-secret-0123456789abcdef",
        }).Build();

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid DocumentId, Guid EndpointId, string LineUserId, Guid OwnerId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool quietNow)
    {
        var company = new Company { Name = "บริษัท 260 " + tag, TaxId = "0105556123453", FiscalYearStartMonth = 1 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        // ช่วงเงียบที่ครอบ "ตอนนี้" เสมอ (ชั่วโมงไทยปัจจุบัน → +2) — เทสต์ไม่ขึ้นกับเวลาที่ CI รัน
        var bkkHour = DateTime.UtcNow.AddHours(7).Hour;
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineQuietHoursEnabled = quietNow, LineQuietStartHour = bkkHour, LineQuietEndHour = (bkkHour + 2) % 24,
            LineOaDestination = Bot, LineDefaultGroupId = "Cteam260",
        });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 260 " + tag, IsCustomer = true, ContactType = ContactType.JuristicPerson };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var doc = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice,
            DocumentNumber = "INV260-" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        db.Documents.Add(doc);
        var uid = "U260" + Guid.NewGuid().ToString("N");
        var ep = new ContactNotifyEndpoint
        {
            CompanyId = company.Id, ContactId = contact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(uid),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, uid), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            TopicsJson = NotifyTopics.Serialize(new[] { NotifyTopic.Document }), ShowAmounts = true, Verified = true,
            Source = ContactLineBindRule.SourceLineInvite, LinkedAt = DateTime.UtcNow, Label = "ฝ่ายบัญชี",
        };
        db.ContactNotifyEndpoints.Add(ep);
        var owner = new User { Email = $"line260-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 260" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = company.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, doc.Id, ep.Id, uid, owner.Id);
    }

    private static LineQueue Row(Seed s, string trigger) => new()
    {
        CompanyId = s.CompanyId, EntityType = "Document", EntityId = s.DocumentId, ContactId = s.ContactId, EndpointId = s.EndpointId,
        EndpointKind = NotifyEndpointKind.LineUser, Topic = NotifyTopic.Document.ToString(), Trigger = trigger,
        ScheduledFor = DateTime.UtcNow.AddYears(-5), Status = ContactLineChannel.StatusPending, RetryKey = Guid.NewGuid(),
        IdempotencyKey = "t260:" + Guid.NewGuid().ToString("N"), BaseKey = "t260:" + trigger,
        IncludePayLink = trigger == ContactLineChannel.ManualTrigger ? false : null,
    };

    private static IContactRecipientResolver Recipients(Guid endpointId)
        => F4Proxy<IContactRecipientResolver>.Create((m, a) => m == nameof(IContactRecipientResolver.ResolveAsync)
            ? new ContactRecipientRule.RecipientSet((NotifyTopic)a[2]!, new[]
            {
                new ContactRecipientRule.RecipientOption(endpointId, NotifyEndpointKind.LineUser, null, "LINE ของลูกค้า", true, null, null, true, false),
            })
            : null);

    private static ILineNotifyService FakeLine(List<string> pushedTo, LinePushResult result)
        => F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushContactAsync)) return null;
            pushedTo.Add((string)a[3]!);
            return result;
        });

    private static IEmailScheduleService NoEmail() => F4Proxy<IEmailScheduleService>.Create((_, _) => null);

    private static IServiceScopeFactory Scopes()
        => new ServiceCollection()
            .AddScoped(_ => DbTestDatabase.TryCreateContext()!)
            .AddScoped<INotificationService>(sp => new NotificationService(sp.GetRequiredService<AccountingDbContext>(), NullLogger<NotificationService>.Instance))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static ContactLineQueueService Sender(AccountingDbContext db, Seed s, ILineNotifyService line, IServiceScopeFactory? scopes = null)
        => new(db, line, Recipients(s.EndpointId), Secrets, NoEmail(), NullLogger<ContactLineQueueService>.Instance, config: Config(), scopes: scopes);

    [Fact]
    public async Task ชั่วโมงเงียบเลื่อนการ์ดตามกฎไม่ทิ้ง_พ้นช่วงแล้วส่ง_ส่งเองไม่ถูกเลื่อน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "q " + Guid.NewGuid().ToString("N")[..6], quietNow: true);
        var ruled = Row(s, "DocumentApproved");
        var manual = Row(s, ContactLineChannel.ManualTrigger);
        db.LineQueues.AddRange(ruled, manual);
        await db.SaveChangesAsync();
        var pushed = new List<string>();
        await Sender(db, s, FakeLine(pushed, new LinePushResult(true, null, HttpStatus: 200, LineRequestId: "req-260"))).ProcessPendingAsync();

        var r = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == ruled.Id);
        Assert.Equal((ContactLineChannel.StatusPending, LineQuietHours.RuleCode, 0), (r.Status, r.RuleCode, r.RetryCount));
        Assert.True(r.ScheduledFor > DateTime.UtcNow);   // เลื่อนไปสิ้นช่วงเงียบ (ไม่ใช่ทิ้ง)
        Assert.Contains("ชั่วโมงเงียบ", r.ErrorMessage);
        // ทิศตรงข้าม: ส่งเองจากหน้าเอกสาร (ยกเว้น) ส่งทันที
        Assert.Equal(ContactLineChannel.StatusSent, (await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == manual.Id)).Status);
        Assert.Equal(1, pushed.Count(t => t == s.LineUserId));

        // พ้นช่วงเงียบ (จำลอง: ถึงเวลาที่เลื่อนไว้ + ช่วงไม่ครอบแล้ว) ⇒ แถวเดิมส่งจริง · หมายเหตุถูกล้าง
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.LineQuietHoursEnabled, false));
        await db.LineQueues.Where(q => q.Id == ruled.Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, DateTime.UtcNow.AddYears(-5)));
        db.ChangeTracker.Clear();
        await Sender(db, s, FakeLine(pushed, new LinePushResult(true, null, HttpStatus: 200, LineRequestId: "req-260b"))).ProcessPendingAsync();
        var after = await db.LineQueues.AsNoTracking().SingleAsync(q => q.Id == ruled.Id);
        Assert.Equal((ContactLineChannel.StatusSent, (string?)null), (after.Status, after.ErrorMessage));
        Assert.Equal(2, pushed.Count(t => t == s.LineUserId));
    }

    [Fact]
    public async Task Token_ใช้ไม่ได้_กระดิ่งเจ้าของครั้งเดียว_ล้มอีกไม่แจ้งซ้ำ_ล้มครั้งเดียวแบบอื่นไม่แจ้ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "b " + Guid.NewGuid().ToString("N")[..6], quietNow: false);
        db.LineQueues.AddRange(Row(s, ContactLineChannel.ManualTrigger), Row(s, ContactLineChannel.ManualTrigger));
        await db.SaveChangesAsync();
        var pushed = new List<string>();
        var auth = new LinePushResult(false, "Authentication failed", HttpStatus: 401);
        await Sender(db, s, FakeLine(pushed, auth), Scopes()).ProcessPendingAsync();
        Assert.Equal(2, await db.LineQueues.CountAsync(q => q.CompanyId == s.CompanyId && q.Status == ContactLineChannel.StatusFailed));
        var bells = await db.Notifications.AsNoTracking()
            .Where(n => n.CompanyId == s.CompanyId && n.EntityType == LineFailureAlert.EntityType).ToListAsync();
        var bell = Assert.Single(bells);   // สองแถวล้ม ⇒ กระดิ่งเดียว (กันซ้ำ 24 ชม.)
        Assert.Equal((s.OwnerId, LineFailureAlert.Title(channelBroken: true)), (bell.UserId, bell.Title));

        db.LineQueues.Add(Row(s, ContactLineChannel.ManualTrigger));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Sender(db, s, FakeLine(pushed, auth), Scopes()).ProcessPendingAsync();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.CompanyId == s.CompanyId && n.EntityType == LineFailureAlert.EntityType));

        // ทิศตรงข้าม: ล้มครั้งเดียว (ไม่ใช่ช่องเสีย · ต่ำกว่าเกณฑ์) ⇒ ไม่มีกระดิ่ง
        var o = await SeedAsync(db, "o " + Guid.NewGuid().ToString("N")[..6], quietNow: false);
        db.LineQueues.Add(Row(o, ContactLineChannel.ManualTrigger));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Sender(db, o, FakeLine(pushed, new LinePushResult(false, "The request body has 1 error(s)", HttpStatus: 400)), Scopes()).ProcessPendingAsync();
        Assert.Equal(1, await db.LineQueues.CountAsync(q => q.CompanyId == o.CompanyId && q.Status == ContactLineChannel.StatusFailed));
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.CompanyId == o.CompanyId && n.EntityType == LineFailureAlert.EntityType));
    }

    [Fact]
    public async Task ปลายทางทดสอบ_เฉพาะของบริษัทนี้ที่ผูกกับ_OA_ของบริษัท_หาใหม่ตอนส่ง_ข้ามบริษัทไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await SeedAsync(db, "pa " + Guid.NewGuid().ToString("N")[..6], quietNow: false);
        var b = await SeedAsync(db, "pb " + Guid.NewGuid().ToString("N")[..6], quietNow: false);
        var gid = "C260" + Guid.NewGuid().ToString("N");
        var group = new ContactNotifyEndpoint
        {
            CompanyId = a.CompanyId, ContactId = a.ContactId, Kind = NotifyEndpointKind.LineGroup, LineGroupId = Secrets.Protect(gid),
            OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot, GroupNameSnapshot = "บัญชีลูกค้า", Verified = true, LinkedAt = DateTime.UtcNow,
        };
        ContactNotifyEndpoint Hidden(Action<ContactNotifyEndpoint> tweak)
        {
            var e = new ContactNotifyEndpoint
            {
                CompanyId = a.CompanyId, ContactId = a.ContactId, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect("Uhidden"),
                OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot, Verified = true, LinkedAt = DateTime.UtcNow,
            };
            tweak(e);
            return e;
        }
        var central = Hidden(e => e.OaScope = NotifyEndpointKind.ScopeNextAcc);
        var unlinked = Hidden(e => e.UnlinkedAt = DateTime.UtcNow);
        var blocked = Hidden(e => e.BlockedAt = DateTime.UtcNow);
        var oldOa = Hidden(e => e.OaDestination = "Uold-bot");
        db.ContactNotifyEndpoints.AddRange(group, central, unlinked, blocked, oldOa);
        await db.SaveChangesAsync();

        var list = await LineTestTargets.ListAsync(db, a.CompanyId, default);
        Assert.Equal(LineOaHealth.TeamGroupTargetKey, list[0].Key);
        Assert.False(list[0].ReachesContact);
        var keys = list.Skip(1).Select(t => t.Key).ToHashSet();
        Assert.Equal(new[] { LineOaHealth.EndpointTargetKey(a.EndpointId), LineOaHealth.EndpointTargetKey(group.Id) }.ToHashSet(), keys);
        Assert.All(list.Skip(1), t => Assert.True(t.ReachesContact));
        Assert.Contains(list, t => t.Label.Contains("บัญชีลูกค้า") && t.Label.Contains("ลูกค้า 260"));
        Assert.DoesNotContain(list, t => t.Key == LineOaHealth.EndpointTargetKey(b.EndpointId));   // ของบริษัทอื่นไม่อยู่ในรายการ
        Assert.DoesNotContain(list, t => t.Label.Contains(gid) || t.Label.Contains(a.LineUserId));   // ไม่มี LINE id ในป้าย

        Assert.Equal((gid, (string?)null), await LineTestTargets.ResolveAsync(db, Secrets, a.CompanyId, group.Id, default));
        Assert.Equal((a.LineUserId, (string?)null), await LineTestTargets.ResolveAsync(db, Secrets, a.CompanyId, a.EndpointId, default));
        // ข้ามบริษัท / ปลายทางที่ไม่อยู่ในรายการ ⇒ ไม่พบพร้อมเหตุ
        foreach (var id in new[] { b.EndpointId, central.Id, unlinked.Id, blocked.Id, oldOa.Id })
        {
            var (to, problem) = await LineTestTargets.ResolveAsync(db, Secrets, a.CompanyId, id, default);
            Assert.Null(to);
            Assert.NotNull(problem);
        }
    }
}
