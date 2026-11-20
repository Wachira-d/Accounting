using System.Security.Claims;
using System.Text.Json;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 280 บน PostgreSQL จริง — เจ้าของ: "คำขอส่งไปทางไหน ไลน์ที่ผูกกันไว้ไม่เด้ง เมล์ไม่มี ปรับให้เลือกช่องทางการส่งแจ้งได้ด้วย" ·
/// เลือก LINE ⇒ ข้อความเข้ากลุ่มที่ผูกกับผู้ติดต่อ (OA ที่กลุ่มผูก) · อีเมล ⇒ ส่งผ่านผู้ส่งเดียว · ไม่มีช่อง ⇒ ปิดพร้อมเหตุ ไม่ยิง · ส่งซ้ำถูกจำกัด ·
/// คำตอบ API ไม่บอกว่าเลขใช้ NextAcc · tenant · คู่ค้าที่ยังไม่ใช้ NextAcc สมัครทีหลังแล้วเห็นคำขอ
/// </summary>
[Trait("Category", "Db")]
[Collection("PartnerLinkRequestSweep")]   // สร้างคำเชิญ (outbox) — ร่วม collection กับคลาสที่กวาดคิวแจ้ง (รอบ 269)
public class PartnerLinkNotifyRound280DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerLinkNotifyRound280DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม";
    private static readonly SecretProtector Secrets = new("db-test-key-280");
    private const string AdminGroup = "Cadmin280000000000000000000000001";
    private const string Bot = "Ubot280000000000000000000000000001";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "08" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings("T", "S", "@nextacc", null, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private sealed record Push(Guid CompanyId, string OaScope, LineTargetKind Target, string To, string Text);

    private sealed class Probe
    {
        public List<Push> Pushes { get; } = new();
        public List<EmailMessage> Emails { get; } = new();
        public Func<LinePushResult> LineResult { get; set; } = () => LinePushResult.Success;
    }

    private sealed class FakeSender : IEmailSender
    {
        private readonly Probe _p;
        public FakeSender(Probe p) => _p = p;
        public EmailProvider Provider => EmailProvider.Smtp;
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        { _p.Emails.Add(message); return Task.FromResult(EmailSendResult.Ok("mid-280")); }
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static PartnerLinkRequestNotifier Notifier(AccountingDbContext db, Probe p)
    {
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushContactTextAsync)) return null;
            p.Pushes.Add(new Push((Guid)a[0]!, (string)a[1]!, (LineTargetKind)a[2]!, (string)a[3]!, (string)a[4]!));
            return Task.FromResult(p.LineResult());
        });
        var senders = F4Proxy<IEmailSenderFactory>.Create((m, _) => m == nameof(IEmailSenderFactory.PlatformFromAddress) ? "no-reply@nextacc.net" : new FakeSender(p));
        return new PartnerLinkRequestNotifier(db, new ContactRecipientResolver(db, Secrets, Central()), line, Secrets, senders,
            NullLogger<PartnerLinkRequestNotifier>.Instance, Config());
    }

    private static CrossTenantWorkflowController Controller(AccountingDbContext db, Guid userId, Probe p)
    {
        var ctl = new CrossTenantWorkflowController(new CrossTenantWorkflowService(db, NullLogger<CrossTenantWorkflowService>.Instance), db,
            new PermissionService(db), new ChatRateLimiter(db, NullLogger<ChatRateLimiter>.Instance), Notifier(db, p));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            },
        };
        return ctl;
    }

    private static async Task<(Company Co, Guid Owner)> CompanyAsync(AccountingDbContext db, string name, string? taxId = null, bool lineOa = false)
    {
        var c = new Company { Name = name + " " + Guid.NewGuid().ToString("N")[..6], TaxId = taxId ?? RandTax(), BranchCode = "00000" };
        db.Companies.Add(c);
        var u = new User { Email = $"r280-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 280" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = c.Id, Role = UserRole.Owner });
        if (lineOa)
            db.CompanySettings.Add(new CompanySettings
            {
                CompanyId = c.Id, LineEnabled = true, LineChannelAccessToken = Secrets.Protect("company-token-280"),
                LineChannelSecret = Secrets.Protect("company-secret-280"), LineOaDestination = Bot,
            });
        await db.SaveChangesAsync();
        foreach (var sql in DatabaseMigrationHelper.ChatRateBucketStatements())
            await db.Database.ExecuteSqlRawAsync(sql);
        return (c, u.Id);
    }

    /// <summary>ผู้ติดต่อของ <paramref name="companyId"/> (เลขภาษีคู่ค้า) + กลุ่ม LINE ผ่าน OA บริษัท (เรื่องที่รับ = <paramref name="topics"/>) + อีเมล</summary>
    private static async Task<(Guid ContactId, string GroupId)> ContactAsync(AccountingDbContext db, Guid companyId, string? taxId, string? email,
        bool group, NotifyTopic[]? topics = null, string groupName = "กลุ่ม แอม แฮปปี้เนส")
    {
        var contact = new Contact { CompanyId = companyId, Name = "หจก. แอม แฮปปี้เนส " + Guid.NewGuid().ToString("N")[..4], IsSupplier = true, TaxId = taxId, BranchCode = "00000", Email = email };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var gid = "C" + Guid.NewGuid().ToString("N");
        if (group)
        {
            var consent = new PdpaConsentRecord { CompanyId = companyId, Purpose = ContactLineBindRule.ConsentPurpose, SubjectContactId = contact.Id };
            db.PdpaConsentRecords.Add(consent);
            await db.SaveChangesAsync();
            db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
            {
                CompanyId = companyId, ContactId = contact.Id, Kind = NotifyEndpointKind.LineGroup, LineGroupId = Secrets.Protect(gid),
                LineIdHash = NotifyEndpointKind.LineIdHash(Bot, gid), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
                MemberCountSnapshot = 5, Verified = true, Source = "test", ConsentRecordId = consent.Id, GroupNameSnapshot = groupName, Label = groupName,
                TopicsJson = NotifyTopics.Serialize(topics ?? NotifyTopics.DefaultsFor(NotifyEndpointKind.LineGroup)),
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return (contact.Id, gid);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Body<T>(ActionResult<T> r)
        => JsonSerializer.Serialize((r.Result as ObjectResult)?.Value, Json);

    private static async Task<List<PartnerLinkRequestNotice>> NoticesAsync(Guid companyId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.PartnerLinkRequestNotices.AsNoTracking().Where(n => n.CompanyId == companyId).OrderBy(n => n.AttemptedAt).ToListAsync();
    }

    private static async Task<Guid> LastRequestAsync(Guid companyId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.PartnerLinkRequests.AsNoTracking().Where(r => r.CompanyId == companyId).OrderByDescending(r => r.CreatedAt).Select(r => r.Id).FirstAsync();
    }

    // ═══════════ (1) LINE กลุ่มของผู้ติดต่อ + คำตอบไม่รั่วสมาชิกภาพ ═══════════

    [Fact]
    public async Task เลือกLINE_ส่งเข้ากลุ่มที่ผูกกับผู้ติดต่อ_ผลส่งจริงบนแถว_คำตอบเท่ากันไม่ว่าเลขใช้NextAccหรือไม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280", lineOa: true);
        var b = await CompanyAsync(db, "คู่ค้าใช้ NextAcc 280");
        var unknownTax = RandTax();
        var cb = await ContactAsync(db, a.Co.Id, b.Co.TaxId, null, group: true);
        var cu = await ContactAsync(db, a.Co.Id, unknownTax, null, group: true);
        var probe = new Probe();

        var known = await Controller(db, a.Owner, probe).Invite(a.Co.Id,
            new CrossTenantWorkflowController.InviteRequest(b.Co.TaxId!, null, "00000", cb.ContactId, new List<string> { "LineGroup" }));
        var unknown = await Controller(db, a.Owner, probe).Invite(a.Co.Id,
            new CrossTenantWorkflowController.InviteRequest(unknownTax, null, "00000", cu.ContactId, new List<string> { "LineGroup" }));
        Assert.Equal(Body(known), Body(unknown));                                   // ไม่บอกว่าเลขใช้ NextAcc (ผลเป็นของช่องของเราเอง)
        Assert.Contains("\"status\":\"Sent\"", Body(known));

        Assert.Equal(2, probe.Pushes.Count);
        var push = probe.Pushes[0];
        Assert.Equal(a.Co.Id, push.CompanyId);
        Assert.Equal(NotifyEndpointKind.ScopeCompany, push.OaScope);                // OA ที่กลุ่มผูก
        Assert.Equal(LineTargetKind.ContactGroup, push.Target);
        Assert.Equal(cb.GroupId, push.To);                                           // กลุ่มของผู้ติดต่อรายนั้น
        Assert.Contains("https://app.nextacc.test" + PartnerLinkRequestChannels.LandingPath, push.Text);
        Assert.DoesNotContain(b.Co.TaxId!, push.Text);                               // เลขเต็มไม่อยู่ในกลุ่ม

        // แถวคำขอบอกแจ้งไปช่องไหน + เวลา + ผล · ช่องในระบบ = เข้าคิว (เหมือนกันทั้งสองคำขอ)
        var outgoing = JsonSerializer.Serialize(await new CrossTenantWorkflowService(db, NullLogger<CrossTenantWorkflowService>.Instance)
            .ListOutgoingLinkRequestsAsync(a.Co.Id, DateTime.UtcNow), Json);
        using var doc = JsonDocument.Parse(outgoing);
        Assert.All(doc.RootElement.EnumerateArray(), row =>
        {
            var n = row.GetProperty("notices").EnumerateArray().ToList();
            Assert.Equal(new[] { "InApp", "LineGroup" }, n.Select(x => x.GetProperty("channel").GetString()));
            Assert.Equal("Queued", n[0].GetProperty("status").GetString());
            Assert.Equal("Sent", n[1].GetProperty("status").GetString());
            Assert.True(row.GetProperty("canNotify").GetBoolean());
        });
        var audit = await db.AuditLogs.AsNoTracking().CountAsync(x => x.CompanyId == a.Co.Id && x.EntityType == PartnerLinkRequestNotifier.AuditEntityType);
        Assert.Equal(2, audit);
    }

    [Fact]
    public async Task LINEล้ม_ประทับล้มพร้อมเหตุ_ไม่ใช่ส่งสำเร็จ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 ล้ม", lineOa: true);
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: true);
        var probe = new Probe { LineResult = () => new LinePushResult(false, "LINE ตอบ 400: Invalid reply token", HttpStatus: 400) };
        await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup" }));
        var line = Assert.Single(await NoticesAsync(a.Co.Id), n => n.Channel == PartnerLinkRequestChannels.LineGroup);
        Assert.Equal(PartnerLinkRequestChannels.StatusFailed, line.Status);
        Assert.Contains("400", line.Reason);
    }

    // ═══════════ (2) อีเมล ═══════════

    [Fact]
    public async Task เลือกอีเมล_ส่งถึงอีเมลผู้ติดต่อผ่านผู้ส่งเดียว_บันทึกนับเพดาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 อีเมล");
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, "ap@amhappy280.co.th", group: false);
        var probe = new Probe();
        var r = await Controller(db, a.Owner, probe).Invite(a.Co.Id,
            new CrossTenantWorkflowController.InviteRequest(tax, "ข้อความลับ", null, c.ContactId, new List<string> { "Email" }));
        var mail = Assert.Single(probe.Emails);
        Assert.Equal(new[] { "ap@amhappy280.co.th" }, mail.To);
        Assert.Equal("no-reply@nextacc.net", mail.FromAddress);                       // ไม่ได้ตั้งผู้ส่งของบริษัท ⇒ ส่งผ่าน NextAcc
        Assert.Contains(PartnerLinkRequestChannels.LandingPath, mail.HtmlBody);
        Assert.DoesNotContain("ข้อความลับ", mail.HtmlBody);                           // ข้อความอิสระอยู่ในระบบเท่านั้น
        var n = Assert.Single(await NoticesAsync(a.Co.Id), x => x.Channel == PartnerLinkRequestChannels.Email);
        Assert.Equal(PartnerLinkRequestChannels.StatusSent, n.Status);
        Assert.Equal("a***@amhappy280.co.th", n.RecipientLabel);                      // ผู้รับปิดบังบนแถว
        Assert.True(await db.DocumentEmailLogs.AnyAsync(l => l.CompanyId == a.Co.Id && l.Purpose == PartnerLinkRequestNotifier.EmailPurpose
            && l.Status == EmailLogStatus.Sent && l.SenderMode == OnBehalfEmailSender.ModePlatform));
        Assert.Contains("\"status\":\"Sent\"", Body(r));
    }

    // ═══════════ (3) ไม่มีช่อง / ไม่ได้ติ๊กเรื่อง ⇒ ปิดพร้อมเหตุ ═══════════

    [Fact]
    public async Task ไม่มีอีเมลไม่มีกลุ่ม_ช่องปิดพร้อมเหตุและลิงก์_กดส่งก็ไม่ยิง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 ไม่มีช่อง", lineOa: true);
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: false);
        var probe = new Probe();
        var opts = JsonSerializer.Serialize(await Notifier(db, probe).ChannelOptionsAsync(a.Co.Id, tax, null), Json);
        using (var d = JsonDocument.Parse(opts))
        {
            Assert.Equal(c.ContactId, d.RootElement.GetProperty("selectedContactId").GetGuid());   // ผู้ติดต่อเลขนี้คนเดียว ⇒ เลือกให้
            var ch = d.RootElement.GetProperty("channels").EnumerateArray().ToList();
            Assert.All(ch, x => Assert.False(x.GetProperty("available").GetBoolean()));
            Assert.Equal(PartnerLinkRequestChannels.NoLineGroupReason, ch[0].GetProperty("reason").GetString());
            Assert.Equal(PartnerLinkRequestChannels.NoEmailReason, ch[1].GetProperty("reason").GetString());
            Assert.Equal(PartnerLinkRequestChannels.ContactNotifyUrl(c.ContactId), ch[0].GetProperty("fixUrl").GetString());
        }
        await Controller(db, a.Owner, probe).Invite(a.Co.Id,
            new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup", "Email" }));
        Assert.Empty(probe.Pushes);
        Assert.Empty(probe.Emails);
        var rows = await NoticesAsync(a.Co.Id);
        Assert.All(rows.Where(n => n.Channel != PartnerLinkRequestChannels.InApp), n => Assert.Equal(PartnerLinkRequestChannels.StatusRefused, n.Status));
    }

    [Fact]
    public async Task กลุ่มเดิมที่ไม่ได้ติ๊กเรื่องคำขอเชื่อมคู่ค้า_ไม่เปิดให้เอง_ปิดพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 เรื่อง", lineOa: true);
        var tax = RandTax();
        // กลุ่มของเจ้าของในภาพ: เอกสาร · ทวงชำระ/ใบแจ้งยอด · ใบเสร็จ · 50 ทวิ
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: true,
            topics: new[] { NotifyTopic.Document, NotifyTopic.Dunning, NotifyTopic.Receipt, NotifyTopic.Wht });
        var probe = new Probe();
        var res = await Notifier(db, probe).SendAsync(a.Co.Id, await InviteOnlyAsync(db, a, tax), c.ContactId, new[] { "LineGroup" }, a.Owner,
            PartnerLinkRequestNotifier.InAppAction.Record);
        var line = Assert.Single(res, x => x.Channel == PartnerLinkRequestChannels.LineGroup);
        Assert.Equal(PartnerLinkRequestChannels.StatusRefused, line.Status);
        Assert.Contains("คำขอเชื่อมคู่ค้า", line.Reason);
        Assert.Empty(probe.Pushes);
    }

    private static async Task<Guid> InviteOnlyAsync(AccountingDbContext db, (Company Co, Guid Owner) a, string tax)
        => await new CrossTenantWorkflowService(db, NullLogger<CrossTenantWorkflowService>.Instance).InvitePartnerAsync(a.Co.Id, tax, null, a.Owner, null);

    // ═══════════ (4) ส่งซ้ำถูกจำกัด + tenant ═══════════

    [Fact]
    public async Task ส่งแจ้งอีกครั้งทันที_ถูกจำกัด_พ้น12ชมส่งได้_บริษัทอื่นแตะไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 ถี่", lineOa: true);
        var other = await CompanyAsync(db, "บริษัทอื่น 280");
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: true);
        var probe = new Probe();
        await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup" }));
        var reqId = await LastRequestAsync(a.Co.Id);
        Assert.Single(probe.Pushes);

        var again = await Controller(db, a.Owner, probe).Renotify(a.Co.Id, reqId, new CrossTenantWorkflowController.NotifyRequest(null, new List<string> { "LineGroup" }));
        Assert.Single(probe.Pushes);                                                 // ไม่ยิงซ้ำ
        Assert.Contains("12 ชั่วโมง", Body(again));
        // ผู้ติดต่อจำไว้บนคำขอ (ไม่ต้องเลือกซ้ำ) · พ้นช่วงพัก ⇒ ส่งได้
        await db.Database.ExecuteSqlRawAsync("UPDATE \"PartnerLinkRequestNotices\" SET \"AttemptedAt\" = \"AttemptedAt\" - interval '13 hours' WHERE \"CompanyId\" = {0}", a.Co.Id);
        await Controller(db, a.Owner, probe).Renotify(a.Co.Id, reqId, new CrossTenantWorkflowController.NotifyRequest(null, new List<string> { "LineGroup" }));
        Assert.Equal(2, probe.Pushes.Count);

        // tenant: บริษัทอื่นส่งแจ้งคำขอของ A ไม่ได้ (404) · ใช้ผู้ติดต่อของ A ในคำขอของตัวเองไม่ได้
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Notifier(db, probe).SendAsync(other.Co.Id, reqId, null, new[] { "LineGroup" }, other.Owner,
            PartnerLinkRequestNotifier.InAppAction.Requeue));
        Assert.Equal(404, ex.StatusCode);
        var otherReq = await InviteOnlyAsync(db, other, tax);
        var res = await Notifier(db, probe).SendAsync(other.Co.Id, otherReq, c.ContactId, new[] { "LineGroup" }, other.Owner, PartnerLinkRequestNotifier.InAppAction.Record);
        Assert.Equal(PartnerLinkRequestChannels.ContactNotFoundReason, res.Single(x => x.Channel == PartnerLinkRequestChannels.LineGroup).Reason);
        Assert.Equal(2, probe.Pushes.Count);
    }

    // ═══════════ (5) คู่ค้ายังไม่ใช้ NextAcc ⇒ สมัครทีหลัง (แก้ตามฝ่ายค้าน MED-1 · 280-10) ═══════════

    private static async Task<bool> PairAsync(Guid requester, Guid target)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.TradingPartnerships.AsNoTracking().AnyAsync(p => p.InvitedByCompanyId == requester && (p.CompanyAId == target || p.CompanyBId == target));
    }

    [Fact]
    public async Task ฝ่ายค้านMED1_สมัครด้วยเลขของคนอื่นแล้วเปิดหน้า_ไม่เห็นคำขอเก่า_ผู้ขอส่งแจ้งอีกครั้งจึงเสนอใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 รอสมัคร");
        var tax = RandTax();
        var reqId = await InviteOnlyAsync(db, a, tax);
        Assert.False(await db.TradingPartnerships.AnyAsync(p => p.InvitedByCompanyId == a.Co.Id));   // ยังไม่มีบริษัทเลขนี้

        // ผู้ที่สมัคร/ตั้งเลขนี้ทีหลัง (ระบบยืนยันเลขภาษีไม่ได้) เปิดหน้า + กดรับ ⇒ ไม่เห็นคำขอที่ส่งก่อนตั้งเลข
        var late = await CompanyAsync(db, "ตั้งเลขทีหลัง 280", taxId: tax);
        var probe = new Probe();
        await Controller(db, late.Owner, probe).RefreshIncomingRequests(late.Co.Id);
        Assert.False(await PairAsync(a.Co.Id, late.Co.Id));
        Assert.DoesNotContain("\"canRespond\":true", Body(await Controller(db, late.Owner, probe).List(late.Co.Id)));
        // GET รายการคู่ค้าไม่สร้างแถวอีกต่อไป (เดิมไม่มีด่านสิทธิ์)
        Assert.False(await PairAsync(a.Co.Id, late.Co.Id));

        // ผู้ขอกด "ส่งแจ้งอีกครั้ง" = เสนอคำขอนี้ใหม่ถึงบริษัทที่ตรงเลขตอนนี้ (เหมือนส่งคำขอใหม่) · คำตอบไม่บอกจำนวน
        var re = await Controller(db, a.Owner, probe).Renotify(a.Co.Id, reqId, new CrossTenantWorkflowController.NotifyRequest(null, null));
        Assert.DoesNotContain("\"created\"", Body(re));
        Assert.True(await PairAsync(a.Co.Id, late.Co.Id));
        Assert.Contains("\"canRespond\":true", Body(await Controller(db, late.Owner, probe).List(late.Co.Id)));
        var row = await db.TradingPartnerships.AsNoTracking().SingleAsync(p => p.InvitedByCompanyId == a.Co.Id);
        using (var acc = DbTestDatabase.TryCreateContext()!)
            await new CrossTenantWorkflowService(acc, NullLogger<CrossTenantWorkflowService>.Instance).AcceptPartnershipAsync(late.Co.Id, row.Id, late.Owner);
        var state = JsonSerializer.Serialize(await new CrossTenantWorkflowService(db, NullLogger<CrossTenantWorkflowService>.Instance)
            .ListOutgoingLinkRequestsAsync(a.Co.Id, DateTime.UtcNow), Json);
        Assert.Contains("\"state\":\"Accepted\"", state);
    }

    [Fact]
    public async Task ฝ่ายค้านMED1_คำขอที่ส่งหลังตั้งเลข_เปิดให้ได้_บริษัทเลขอื่นไม่ได้แถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 หลังตั้งเลข");
        var b = await CompanyAsync(db, "ปลายทาง 280 หลังตั้งเลข");
        var stranger = await CompanyAsync(db, "เลขอื่น 280");
        // คำขอที่ส่งหลังปลายทางตั้งเลข แต่ยังไม่มีแถวคู่ (เช่น ส่งพร้อมกับตอนตั้งเลข) ⇒ เปิดให้เมื่อปลายทางกดรับ
        db.PartnerLinkRequests.Add(new PartnerLinkRequest
        {
            CompanyId = a.Co.Id, PartnerTaxId = b.Co.TaxId!, PartnerBranchCode = "00000", RequestedByUserId = a.Owner,
            ExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow.AddMinutes(1),
        });
        await db.SaveChangesAsync();
        var probe = new Probe();
        await Controller(db, b.Owner, probe).RefreshIncomingRequests(b.Co.Id);
        await Controller(db, b.Owner, probe).RefreshIncomingRequests(b.Co.Id);   // ซ้ำไม่สร้างแถวซ้ำ
        Assert.Equal(1, await db.TradingPartnerships.CountAsync(p => p.InvitedByCompanyId == a.Co.Id));
        await Controller(db, stranger.Owner, probe).RefreshIncomingRequests(stranger.Co.Id);
        Assert.False(await PairAsync(a.Co.Id, stranger.Co.Id));
    }

    [Fact]
    public async Task ฝ่ายค้านMED1ค_เปลี่ยนเลขภาษี_ยกเลิกคำขอรอยอมรับของเลขเดิม_คู่ที่เชื่อมแล้วไม่ถูกแตะ_ประทับเวลาตั้งเลขใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 เปลี่ยนเลข");
        var b = await CompanyAsync(db, "ปลายทาง 280 เปลี่ยนเลข");
        await InviteOnlyAsync(db, a, b.Co.TaxId!);
        Assert.True(await PairAsync(a.Co.Id, b.Co.Id));
        var setBefore = (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == b.Co.Id)).TaxIdSetAt;
        Assert.NotNull(setBefore);

        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.SingleAsync(c => c.Id == b.Co.Id);
            var oldTax = co.TaxId;
            co.TaxId = RandTax();
            Assert.Equal(1, await CrossTenantWorkflowService.VoidIncomingRequestsForOldTaxIdAsync(u, b.Co.Id, oldTax, co.TaxId, b.Owner, DateTime.UtcNow));
            await u.SaveChangesAsync();
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.TradingPartnerships.AsNoTracking().SingleAsync(p => p.InvitedByCompanyId == a.Co.Id);
        Assert.Equal(TradingPartnershipStatus.Rejected, row.Status);
        Assert.Equal(CrossTenantWorkflowService.TaxIdChangedReason, row.RejectionReason);
        Assert.Null(row.InviteNoticeDueAt);
        Assert.True((await check.Companies.AsNoTracking().SingleAsync(c => c.Id == b.Co.Id)).TaxIdSetAt > setBefore);
        Assert.True(await check.AuditLogs.AnyAsync(x => x.CompanyId == b.Co.Id && x.EntityType == "TradingPartnerRequestsVoidedByTaxIdChange"));
        // ผู้ขอยังเห็น "รอตอบ" (ไม่บอกการยกเลิก — 264-1-2)
        Assert.Contains("\"state\":\"Awaiting\"", JsonSerializer.Serialize(await new CrossTenantWorkflowService(check, NullLogger<CrossTenantWorkflowService>.Instance)
            .ListOutgoingLinkRequestsAsync(a.Co.Id, DateTime.UtcNow), Json));
        // ทิศตรงข้าม: เลขเดิม (ไม่เปลี่ยน) ⇒ ไม่แตะอะไร
        Assert.Equal(0, await CrossTenantWorkflowService.VoidIncomingRequestsForOldTaxIdAsync(check, b.Co.Id, "0105556000245", "0105556000245", b.Owner, DateTime.UtcNow));
    }

    // ═══════════ (6) ฝ่ายค้าน MED-2 + LOW-1 ═══════════

    [Fact]
    public async Task ฝ่ายค้านMED2_สร้างคำขอใหม่เลี่ยงเพดานไม่ได้_3ครั้งต่อผู้ติดต่อใน7วัน_ผู้ติดต่อไม่มีเลขภาษีไม่ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 เพดานผู้ติดต่อ", lineOa: true);
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: true);
        var probe = new Probe();
        for (var i = 0; i < 4; i++)
            await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup" }));
        Assert.Equal(3, probe.Pushes.Count);                                          // คำขอที่ 4 ถูกกัน (ทุกคำขอนับรวม)
        var last = (await NoticesAsync(a.Co.Id)).Where(n => n.Channel == PartnerLinkRequestChannels.LineGroup).Last();
        Assert.Equal(PartnerLinkRequestChannels.StatusRefused, last.Status);
        Assert.Contains("7 วัน", last.Reason);
        // ทิศตรงข้าม: พ้นหน้าต่าง 7 วัน ⇒ ส่งได้
        await db.Database.ExecuteSqlRawAsync("UPDATE \"PartnerLinkRequestNotices\" SET \"AttemptedAt\" = \"AttemptedAt\" - interval '8 days' WHERE \"CompanyId\" = {0}", a.Co.Id);
        await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup" }));
        Assert.Equal(4, probe.Pushes.Count);

        // ผู้ติดต่อไม่มีเลขภาษี ⇒ ไม่ส่ง (เดิมส่งได้ ⇒ ช่องส่งข้อความถึงใครก็ได้)
        var noTax = await ContactAsync(db, a.Co.Id, null, null, group: true);
        await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(RandTax(), null, null, noTax.ContactId, new List<string> { "LineGroup" }));
        Assert.Equal(4, probe.Pushes.Count);
        Assert.Equal(PartnerLinkRequestChannels.ContactNoTaxIdReason,
            (await NoticesAsync(a.Co.Id)).Where(n => n.Channel == PartnerLinkRequestChannels.LineGroup).Last().Reason);
    }

    [Fact]
    public async Task ฝ่ายค้านLOW1_ส่งได้บางกลุ่ม_สถานะส่งได้บางส่วนพร้อมรายการที่ล้ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 280 บางส่วน", lineOa: true);
        var tax = RandTax();
        var c = await ContactAsync(db, a.Co.Id, tax, null, group: true, groupName: "กลุ่มหนึ่ง");
        // กลุ่มที่สองของผู้ติดต่อเดียวกัน
        var consentId = await db.PdpaConsentRecords.Where(x => x.SubjectContactId == c.ContactId).Select(x => x.Id).FirstAsync();
        var gid2 = "C" + Guid.NewGuid().ToString("N");
        db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
        {
            CompanyId = a.Co.Id, ContactId = c.ContactId, Kind = NotifyEndpointKind.LineGroup, LineGroupId = Secrets.Protect(gid2),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, gid2), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            MemberCountSnapshot = 4, Verified = true, Source = "test", ConsentRecordId = consentId, GroupNameSnapshot = "กลุ่มสอง", Label = "กลุ่มสอง",
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineGroup)),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var probe = new Probe();
        var n = 0;
        probe.LineResult = () => ++n == 1 ? LinePushResult.Success : new LinePushResult(false, "LINE ตอบ 400: bad group", HttpStatus: 400);
        await Controller(db, a.Owner, probe).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(tax, null, null, c.ContactId, new List<string> { "LineGroup" }));
        Assert.Equal(2, probe.Pushes.Count);
        var row = Assert.Single(await NoticesAsync(a.Co.Id), x => x.Channel == PartnerLinkRequestChannels.LineGroup);
        Assert.Equal(PartnerLinkRequestChannels.StatusPartial, row.Status);
        Assert.Contains("bad group", row.Reason);
    }
}
