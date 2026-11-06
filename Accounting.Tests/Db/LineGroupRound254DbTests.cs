using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 254 WP-11 L3 บน PostgreSQL จริง (LINE_MULTICHANNEL_DESIGN §3.c · คำตอบเจ้าของ Q1(b)/Q2/Q5/Q8): #HeyNextAcc ออกรหัส → ผู้ดูแลตรวจ/ยืนยัน (รับรองสิทธิ์) →
/// ปลายทางกลุ่ม (เข้ารหัส · หลักฐาน · แสดงยอดค่าตั้งต้น) + ประกาศในกลุ่ม · รหัสใช้ครั้งเดียว · #ByeNextAcc ปลด + บอทออก · ยกเลิกที่หน้าผู้ติดต่อ ⇒ บอทออก ·
/// กลุ่มกลางของแอดมิน/เกิน 200 คน ⇒ ไม่ออกรหัส · ข้อความอื่นในกลุ่มไม่เก็บ · webhook ซ้ำข้าม
/// </summary>
[Trait("Category", "Db")]
public class LineGroupRound254DbTests
{
    private readonly ITestOutputHelper _out;
    public LineGroupRound254DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-254-l3");
    private const string AdminGroup = "Cadmin254l30000000000000000000001";
    private const string Bot = "Ubot254l3000000000000000000000001";

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings("T", "S", "@nextacc", null, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());

    private static ILineNotifyService FakeLine(List<string> log, int members)
        => F4Proxy<ILineNotifyService>.Create((m, a) => m switch
        {
            nameof(ILineNotifyService.TryGetCompanyGroupSummaryAsync) => (object?)new LineGroupSummary("ABC × ร้านเรา", members),
            nameof(ILineNotifyService.TryLeaveCompanyGroupAsync) => Log(log, "leave:" + a[1]),
            nameof(ILineNotifyService.TryPushContactTextAsync) => LogPush(log, "text:" + a[3]),
            _ => null,
        });

    private static object Log(List<string> log, string s) { log.Add(s); return true; }
    private static object LogPush(List<string> log, string s) { log.Add(s); return LinePushResult.Success; }

    private static ContactLineGroupService Groups(AccountingDbContext db, List<string> log, int members = 5)
        => new(db, Secrets, FakeLine(log, members), Resolver(db), Central(),
            F4Proxy<IChatRateLimiter>.Create((m, _) => m == nameof(IChatRateLimiter.TryConsumeAsync) ? (object)true : 0),
            NullLogger<ContactLineGroupService>.Instance);

    private static readonly ContactNotifyActor Actor = new(null, "owner@l3.local", "127.0.0.1");

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag)
    {
        var company = new Company { Name = "บริษัท L3 " + tag, TaxId = "", Email = "office@l3.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineEnabled = true, LineChannelAccessToken = Secrets.Protect("tok-l3"),
            LineChannelSecret = Secrets.Protect("sec-l3"), LineOaDestination = Bot,
        });
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า L3 " + tag, IsCustomer = true, TaxId = "", Email = "ap@l3buyer.co.th" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(company.Id, contact.Id);
    }

    private static JsonElement Ev(string type, string groupId, string? text = null)
    {
        var json = text == null
            ? JsonSerializer.Serialize(new { type, source = new { type = "group", groupId } })
            : JsonSerializer.Serialize(new { type, source = new { type = "group", groupId }, message = new { type = "text", text } });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string CodeFrom(string? reply) => Regex.Match(reply ?? "", @"GB-[A-Z0-9]{8}").Value;

    [Fact]
    public async Task HeyNextAcc_ออกรหัส_ยืนยัน_ผูกกลุ่ม_รหัสใช้ครั้งเดียว_ByeNextAcc_ปลดและบอทออก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "flow " + Guid.NewGuid().ToString("N")[..6]);
        var group = "C254l3" + Guid.NewGuid().ToString("N")[..26];
        var log = new List<string>();

        var join = await Groups(db, log).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("join", group));
        Assert.Contains("#HeyNextAcc", join.Reply);
        var hey = await Groups(db, log).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("message", group, "＃heynextacc"));
        var code = CodeFrom(hey.Reply);
        Assert.False(string.IsNullOrEmpty(code));

        var preview = await Groups(db, log).PreviewAsync(s.CompanyId, s.ContactId, code, null);
        Assert.Null(preview.Problem);
        Assert.Equal(5, preview.MemberCount);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Groups(db, log).ConfirmAsync(s.CompanyId, s.ContactId,
            new ConfirmContactLineGroupRequest(code, null, null, AcceptResponsibility: false), Actor));   // ไม่รับรองสิทธิ์ ⇒ ไม่ผูก

        await Groups(db, log).ConfirmAsync(s.CompanyId, s.ContactId, new ConfirmContactLineGroupRequest(code, null, null, true), Actor);
        db.ChangeTracker.Clear();
        var ep = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.Kind == NotifyEndpointKind.LineGroup);
        Assert.True(ep.Verified);
        Assert.True(ep.ShowAmounts);   // Q5 ค่าตั้งต้น
        Assert.NotEqual(group, ep.LineGroupId);
        Assert.Equal(group, Secrets.Unprotect(ep.LineGroupId));
        Assert.NotNull(ep.ConsentRecordId);
        Assert.Contains(log, l => l == "text:" + group);   // ประกาศในกลุ่ม
        // ส่งได้จริงตามตัวหาผู้รับ (OA บริษัทพร้อม · กลุ่มไม่ใช่กลุ่มกลาง)
        var set = await Resolver(db).ResolveAsync(s.CompanyId, s.ContactId, NotifyTopic.Document);
        Assert.Single(set.LineGroups);

        await Assert.ThrowsAsync<BusinessRuleException>(() => Groups(db, log).ConfirmAsync(s.CompanyId, s.ContactId,
            new ConfirmContactLineGroupRequest(code, null, null, true), Actor));   // ใช้ซ้ำไม่ได้

        var bye = await Groups(db, log).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("message", group, "#ByeNextAcc"));
        Assert.True(bye.LeaveAfterReply);
        await Groups(db, log).LeaveGroupIfUnboundAsync(s.CompanyId, Bot, group);
        Assert.Contains("leave:" + group, log);
        db.ChangeTracker.Clear();
        var after = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.Id == ep.Id);
        Assert.NotNull(after.UnlinkedAt);
        Assert.Null(after.LineGroupId);
    }

    [Fact]
    public async Task ยกเลิกกลุ่มที่หน้าผู้ติดต่อ_ไม่เหลือการผูก_บอทออก_Q8()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "unlink " + Guid.NewGuid().ToString("N")[..6]);
        var group = "C254l3" + Guid.NewGuid().ToString("N")[..26];
        var log = new List<string>();
        var code = CodeFrom((await Groups(db, log).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("message", group, "#HeyNextAcc"))).Reply);
        await Groups(db, log).ConfirmAsync(s.CompanyId, s.ContactId, new ConfirmContactLineGroupRequest(code, null, false, true), Actor);
        db.ChangeTracker.Clear();
        var ep = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.CompanyId == s.CompanyId && e.Kind == NotifyEndpointKind.LineGroup);
        Assert.False(ep.ShowAmounts);   // ปิดแสดงยอดรายกลุ่มได้ (Q5)

        await new ContactNotifyEndpointService(db, Resolver(db), Groups(db, log)).UnlinkAsync(s.CompanyId, s.ContactId, ep.Id, Actor);

        Assert.Contains("leave:" + group, log);
    }

    [Theory]
    [InlineData(AdminGroup, 5)]
    [InlineData("C254l3big0000000000000000000000001", 201)]
    public async Task กลุ่มกลางของแอดมิน_หรือเกิน200คน_ไม่ออกรหัส(string group, int members)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "refuse " + Guid.NewGuid().ToString("N")[..6]);
        var before = await db.LineGroupBindCodes.CountAsync();
        var r = await Groups(db, new List<string>(), members).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("message", group, "#HeyNextAcc"));
        Assert.StartsWith("❌", r.Reply);
        Assert.Equal(before, await db.LineGroupBindCodes.CountAsync());
    }

    [Fact]
    public async Task ข้อความอื่นในกลุ่มไม่เก็บ_webhook_ซ้ำข้าม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "other " + Guid.NewGuid().ToString("N")[..6]);
        var before = await db.LineGroupBindCodes.CountAsync();
        var r = await Groups(db, new List<string>()).HandleCompanyGroupEventAsync(s.CompanyId, Bot, Ev("message", "C254l3other00000000000000000000001", "ส่งใบเสนอราคาให้หน่อย #HeyNextAcc"));
        Assert.Null(r.Reply);
        Assert.Equal(before, await db.LineGroupBindCodes.CountAsync());
        var id = "evt-" + Guid.NewGuid().ToString("N");
        Assert.True(await Groups(db, new List<string>()).MarkEventSeenAsync(id));
        Assert.False(await Groups(db, new List<string>()).MarkEventSeenAsync(id));
    }
}
