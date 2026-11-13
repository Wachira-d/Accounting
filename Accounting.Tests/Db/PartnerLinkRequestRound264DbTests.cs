using System.Security.Claims;
using System.Text.Json;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 264 บน PostgreSQL จริง (trait Db) — ผู้ใช้รายงาน: A ส่งคำขอเชื่อมด้วยเลขภาษี + สาขา แต่ B หาที่ยอมรับไม่เจอ (B ไม่ได้เปิด "ให้คู่ค้าค้นเจอ" ⇒ คำขอไม่ถูกเก็บ) ·
/// (1) B ไม่เปิดค้นเจอ ⇒ ได้คำขอ + แจ้งผ่านเครื่องแจ้งเตือน (ลิงก์การ์ดยอมรับ) ⇒ ยอมรับ ⇒ ผู้ขอเห็น "เชื่อมแล้ว" + ได้แจ้งชื่อผู้ยอมรับ · ค้นเจอทั้งระบบยังปิด ·
/// เลขที่ไม่มีในระบบ ⇒ คำตอบเท่ากันทุกไบต์ ไม่มีแถวคู่ · (2) ปฏิเสธ ⇒ ผู้ขอยังเห็น "รอตอบ" (ไม่รั่ว) จนหมดอายุ · หมดอายุ ⇒ ยอมรับไม่ได้ · ส่งใหม่ ⇒ คำขอสด ·
/// (3) เพดานการแจ้งต่อปลายทาง · ส่งล้ม ⇒ ลองใหม่ ไม่ประทับว่าแจ้งแล้ว · (4) tenant: บริษัทอื่น/ผู้ขอเปิดการ์ดของปลายทางไม่ได้
/// </summary>
[Trait("Category", "Db")]
// รอบ 269 (CI db-test 43376320015 + 43380265759 · "InviteNoticeDueAt does not have a value"): CrossTenantWorkflowService.DispatchPendingLinkRequestNoticesAsync
// กวาดคำขอเชื่อมคู่ค้าที่ถึงกำหนดของทุกบริษัทในฐานร่วม ⇒ คลาสที่รันขนานส่ง/ล้างคิวแจ้งของคำเชิญที่อีกคลาสเพิ่งสร้าง · คลาสที่สร้างคำเชิญหรือกวาดคิวอยู่ collection เดียวกัน
[Collection("PartnerLinkRequestSweep")]
public class PartnerLinkRequestRound264DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerLinkRequestRound264DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "07" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private sealed class FakeNotify : INotificationEngine
    {
        public NotificationDispatchOutcome Outcome { get; set; } = NotificationDispatchOutcome.Delivered;
        public List<(Guid CompanyId, string Key, NotificationContext Ctx)> Calls { get; } = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context)
        { Calls.Add((companyId, eventKey, context)); return Task.CompletedTask; }
        public Task<NotificationDispatchOutcome> DispatchWithOutcomeAsync(Guid companyId, string eventKey, NotificationContext context)
        { Calls.Add((companyId, eventKey, context)); return Task.FromResult(Outcome); }
    }

    private static CrossTenantWorkflowService Svc(AccountingDbContext db, FakeNotify? n = null)
        => new(db, NullLogger<CrossTenantWorkflowService>.Instance, null, n);

    private static CrossTenantWorkflowController Controller(AccountingDbContext db, Guid userId, FakeNotify? n = null)
    {
        var ctl = new CrossTenantWorkflowController(Svc(db, n), db, new PermissionService(db),
            new ChatRateLimiter(db, NullLogger<ChatRateLimiter>.Instance));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            },
        };
        return ctl;
    }

    private static async Task<(Company Co, Guid Owner)> CompanyAsync(AccountingDbContext db, string name, string? taxId = null, bool discoverable = false)
    {
        var c = new Company { Name = name + " " + Guid.NewGuid().ToString("N")[..6], TaxId = taxId ?? RandTax(), BranchCode = "00000", PartnerDiscoverable = discoverable };
        db.Companies.Add(c);
        var u = new User { Email = $"r264-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 264" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = c.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        foreach (var sql in DatabaseMigrationHelper.ChatRateBucketStatements())
            await db.Database.ExecuteSqlRawAsync(sql);
        return (c, u.Id);
    }

    private static int? Status<T>(ActionResult<T> r) => r.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static string Body<T>(ActionResult<T> r)
        => JsonSerializer.Serialize((r.Result as ObjectResult)?.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement Data<T>(ActionResult<T> r) => JsonDocument.Parse(Body(r)).RootElement.GetProperty("data");

    private static async Task<TradingPartnership> RowAsync(Guid a, Guid b)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.TradingPartnerships.AsNoTracking().SingleAsync(p => (p.CompanyAId == a && p.CompanyBId == b) || (p.CompanyAId == b && p.CompanyBId == a));
    }

    // ═══════════ (1) ทางหลัก ═══════════

    [Fact]
    public async Task B_ไม่เปิดค้นเจอ_ได้คำขอ_แจ้งพร้อมลิงก์_ยอมรับแล้วเชื่อม_ผู้ขอเห็นเชื่อมแล้ว_เลขไม่มีในระบบคำตอบเท่ากัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ 264");
        var b = await CompanyAsync(db, "ปลายทาง 264", discoverable: false);
        var notify = new FakeNotify();

        var known = await Controller(db, a.Owner).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(b.Co.TaxId, "ขอเชื่อมครับ", "00000"));
        var unknownTax = RandTax();
        var unknown = await Controller(db, a.Owner).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(unknownTax, "ขอเชื่อมครับ", "00000"));
        Assert.Equal(202, Status(known));
        Assert.Equal(Body(known), Body(unknown));                         // anti-enumeration: ไบต์เท่ากัน
        Assert.Equal(JsonValueKind.Null, Data(known).ValueKind);            // ไม่มี id/สถานะในคำตอบ
        Assert.Equal(PartnerCompanyMatch.InviteAckMessage, JsonDocument.Parse(Body(known)).RootElement.GetProperty("message").GetString());

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            // แถวคู่เกิดเฉพาะ B · เลขที่ไม่มีในระบบไม่มีแถวคู่ (ไม่มีอะไรบอกว่ามี) · "คำขอที่เราส่ง" 2 แถวรูปเดียวกัน
            Assert.Single(await check.TradingPartnerships.AsNoTracking().Where(p => p.CompanyAId == a.Co.Id || p.CompanyBId == a.Co.Id).ToListAsync());
            var reqs = await check.PartnerLinkRequests.AsNoTracking().Where(r => r.CompanyId == a.Co.Id).ToListAsync();
            Assert.Equal(2, reqs.Count);
            Assert.All(reqs, r => { Assert.Null(r.AcceptedAt); Assert.Equal("00000", r.PartnerBranchCode); });
            Assert.False(await check.Notifications.AnyAsync(n => n.CompanyId == b.Co.Id || n.CompanyId == a.Co.Id));   // ไม่มีงานแจ้งในคำขอ
        }
        var row = await RowAsync(a.Co.Id, b.Co.Id);
        Assert.Equal(TradingPartnershipStatus.Pending, row.Status);
        Assert.NotNull(row.InviteNoticeDueAt);

        // งานเบื้องหลัง: แจ้ง B ผ่านเครื่องแจ้งเตือน (คีย์ใหม่ · ลิงก์การ์ดยอมรับ · ไม่มีข้อความอิสระของผู้ขอ)
        using (var job = DbTestDatabase.TryCreateContext()!)
            await Svc(job, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        var call = Assert.Single(notify.Calls, c => c.CompanyId == b.Co.Id);
        Assert.Equal(NotificationEvents.PartnerLinkRequested, call.Key);
        Assert.Equal(PartnerLinkRequestRules.AcceptUrl(b.Co.Id, row.Id), call.Ctx.ActionUrl);
        // ฝ่ายค้าน M2/L6: ไม่มีชื่อ (ข้อความอิสระ) · เลขปิดบัง · ไม่มีข้อความเชิญ
        Assert.DoesNotContain(a.Co.Name, call.Ctx.Message);
        Assert.DoesNotContain(a.Co.TaxId, call.Ctx.Message);
        Assert.Contains(PiiMask.CitizenId(a.Co.TaxId)!, call.Ctx.Message);
        Assert.DoesNotContain("ขอเชื่อมครับ", call.Ctx.Message);
        Assert.DoesNotContain(notify.Calls, c => c.CompanyId == a.Co.Id);   // ผู้ขอไม่ได้สัญญาณ
        var stamped = await RowAsync(a.Co.Id, b.Co.Id);
        Assert.Equal(nameof(NotificationDispatchOutcome.Delivered), stamped.InviteNoticeResult);
        Assert.NotNull(stamped.InviteNotifiedAt);
        // รอบถัดไปไม่แจ้งซ้ำ
        using (var job2 = DbTestDatabase.TryCreateContext()!)
            await Svc(job2, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        Assert.Single(notify.Calls, c => c.CompanyId == b.Co.Id);

        // B เปิดลิงก์ ⇒ การ์ดยอมรับ (กดได้) · ยอมรับ ⇒ เชื่อมต่อเสร็จสิ้น
        using (var cb = DbTestDatabase.TryCreateContext()!)
        {
            var card = await Controller(cb, b.Owner).GetIncomingRequest(b.Co.Id, row.Id);
            Assert.Equal(200, Status(card));
            Assert.True(Data(card).GetProperty("canRespond").GetBoolean());
            Assert.Equal(a.Co.TaxId, Data(card).GetProperty("requester").GetProperty("taxId").GetString());
        }
        using (var cb2 = DbTestDatabase.TryCreateContext()!)
        {
            var acc = await Controller(cb2, b.Owner, notify).Accept(b.Co.Id, row.Id);
            Assert.Equal(200, Status(acc));
            Assert.Contains("เชื่อมต่อเสร็จสิ้น", JsonDocument.Parse(Body(acc)).RootElement.GetProperty("message").GetString());
        }
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(TradingPartnershipStatus.Accepted, (await RowAsync(a.Co.Id, b.Co.Id)).Status);
        Assert.False((await after.Companies.AsNoTracking().SingleAsync(c => c.Id == b.Co.Id)).PartnerDiscoverable);   // ยอมรับรายเดียว ไม่เปิดค้นเจอทั้งระบบ
        Assert.True(await after.AuditLogs.AnyAsync(x => x.CompanyId == b.Co.Id && x.EntityId == row.Id.ToString()
            && x.NewValues != null && x.NewValues.Contains("TradingPartnerLinkAccepted")));   // หลักฐานความยินยอม
        // ผู้ขอ: คำขอถึง B = เชื่อมแล้ว · เลขที่ไม่มีในระบบ = ยังรอ (ไม่บอกว่าไม่มี) · ได้แจ้งชื่อ/เลข/สาขาของผู้ยอมรับ
        var outgoing = Data(await Controller(after, a.Owner).ListOutgoingRequests(a.Co.Id)).EnumerateArray().ToList();
        Assert.Equal("Accepted", outgoing.Single(o => o.GetProperty("partnerTaxId").GetString() == b.Co.TaxId).GetProperty("state").GetString());
        Assert.Equal("Awaiting", outgoing.Single(o => o.GetProperty("partnerTaxId").GetString() == unknownTax).GetProperty("state").GetString());
        var toA = Assert.Single(notify.Calls, c => c.CompanyId == a.Co.Id);
        Assert.Equal(NotificationEvents.PartnerLinkAccepted, toA.Key);   // ฝ่ายค้าน L4: คีย์ของตัวเอง
        Assert.DoesNotContain(b.Co.Name, toA.Ctx.Message);
        Assert.Contains(PiiMask.CitizenId(b.Co.TaxId)!, toA.Ctx.Message);
        // A เห็นคู่ค้าในตารางคู่ค้าหลังยอมรับเท่านั้น
        Assert.Equal(1, Data(await Controller(after, a.Owner).List(a.Co.Id)).GetArrayLength());
    }

    // ═══════════ (2) ปฏิเสธ · หมดอายุ · ส่งใหม่ ═══════════

    [Fact]
    public async Task ปฏิเสธ_ผู้ขอยังเห็นรอตอบ_หมดอายุแล้วยอมรับไม่ได้_ส่งใหม่ได้คำขอสดและแจ้งใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอปฏิเสธ 264");
        var b = await CompanyAsync(db, "ปลายทางปฏิเสธ 264");
        var c = await CompanyAsync(db, "ปลายทางหมดอายุ 264");
        var notify = new FakeNotify();
        await Svc(db).InvitePartnerAsync(a.Co.Id, b.Co.TaxId, null, a.Owner, null);
        await Svc(db).InvitePartnerAsync(a.Co.Id, c.Co.TaxId, null, a.Owner, null);
        var rowB = await RowAsync(a.Co.Id, b.Co.Id);
        var rowC = await RowAsync(a.Co.Id, c.Co.Id);

        using (var cb = DbTestDatabase.TryCreateContext()!)
            await Controller(cb, b.Owner).Reject(b.Co.Id, rowB.Id, new CrossTenantWorkflowController.RejectRequest("ไม่รู้จัก"));
        Assert.Null((await RowAsync(a.Co.Id, b.Co.Id)).InviteNoticeDueAt);   // ตอบแล้ว ⇒ ไม่แจ้ง
        using (var ca = DbTestDatabase.TryCreateContext()!)
        {
            var outgoing = Data(await Controller(ca, a.Owner).ListOutgoingRequests(a.Co.Id)).EnumerateArray().ToList();
            Assert.Equal("Awaiting", outgoing.Single(o => o.GetProperty("partnerTaxId").GetString() == b.Co.TaxId).GetProperty("state").GetString());   // ไม่รั่วการปฏิเสธ
        }
        Assert.DoesNotContain(notify.Calls, x => x.CompanyId == a.Co.Id);

        // หมดอายุ (31 วัน) ⇒ การ์ดกดไม่ได้ + เหตุ · ยอมรับ = 409 · งานแจ้งไม่ส่ง · ผู้ขอเห็น "หมดอายุ"
        using (var old = DbTestDatabase.TryCreateContext()!)
        {
            await old.TradingPartnerships.Where(p => p.Id == rowC.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.InvitedAt, DateTime.UtcNow.AddDays(-31)));
            await old.PartnerLinkRequests.Where(r => r.CompanyId == a.Co.Id && r.PartnerTaxId == c.Co.TaxId)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
        }
        using (var job = DbTestDatabase.TryCreateContext()!)
            await Svc(job, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        Assert.DoesNotContain(notify.Calls, x => x.CompanyId == c.Co.Id);
        Assert.Equal(nameof(PartnerLinkRequestRules.NoticeAction.SkipExpired), (await RowAsync(a.Co.Id, c.Co.Id)).InviteNoticeResult);
        using (var cc = DbTestDatabase.TryCreateContext()!)
        {
            var card = await Controller(cc, c.Owner).GetIncomingRequest(c.Co.Id, rowC.Id);
            Assert.False(Data(card).GetProperty("canRespond").GetBoolean());
            Assert.Equal(PartnerLinkRequestRules.ExpiredMessage, Data(card).GetProperty("problemMessage").GetString());
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(cc).AcceptPartnershipAsync(c.Co.Id, rowC.Id, c.Owner));
            Assert.Equal(409, ex.StatusCode);
        }
        using (var ca2 = DbTestDatabase.TryCreateContext()!)
        {
            var outgoing = Data(await Controller(ca2, a.Owner).ListOutgoingRequests(a.Co.Id)).EnumerateArray().ToList();
            Assert.Equal("Expired", outgoing.Single(o => o.GetProperty("partnerTaxId").GetString() == c.Co.TaxId).GetProperty("state").GetString());
        }

        // ทางไปต่อ: ส่งใหม่ ⇒ คำขอสด (เข้าคิวแจ้งใหม่) ⇒ ยอมรับได้
        using (var re = DbTestDatabase.TryCreateContext()!)
            await Svc(re).InvitePartnerAsync(a.Co.Id, c.Co.TaxId, null, a.Owner, null);
        var fresh = await RowAsync(a.Co.Id, c.Co.Id);
        Assert.Equal(rowC.Id, fresh.Id);
        Assert.NotNull(fresh.InviteNoticeDueAt);
        Assert.Null(fresh.InviteNotifiedAt);
        using (var job2 = DbTestDatabase.TryCreateContext()!)
            await Svc(job2, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        Assert.Single(notify.Calls, x => x.CompanyId == c.Co.Id && x.Key == NotificationEvents.PartnerLinkRequested);
        using var acc = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(TradingPartnershipStatus.Accepted, (await Svc(acc).AcceptPartnershipAsync(c.Co.Id, rowC.Id, c.Owner)).Status);

        // คู่ที่รอตอบและยังไม่หมดอายุ ⇒ ส่งซ้ำไม่แจ้งซ้ำ (กันสแปม) — ทิศตรงข้ามของ "ส่งใหม่หลังหมดอายุ"
        var d = await CompanyAsync(acc, "ปลายทางส่งซ้ำ 264");
        await Svc(acc).InvitePartnerAsync(a.Co.Id, d.Co.TaxId, null, a.Owner, null);
        using (var job3 = DbTestDatabase.TryCreateContext()!)
            await Svc(job3, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        using (var again = DbTestDatabase.TryCreateContext()!)
            await Svc(again).InvitePartnerAsync(a.Co.Id, d.Co.TaxId, null, a.Owner, null);
        Assert.Null((await RowAsync(a.Co.Id, d.Co.Id)).InviteNoticeDueAt);
        using (var job4 = DbTestDatabase.TryCreateContext()!)
            await Svc(job4, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        Assert.Single(notify.Calls, x => x.CompanyId == d.Co.Id);
        // ยอมรับ ⇒ "คำขอที่เราส่ง" ทั้งสองแถว (ฉบับแรก + ฉบับซ้ำระหว่างรอ) เป็นเชื่อมแล้ว — ไม่ค้าง "รอตอบ" จนหมดอายุ
        using (var cd = DbTestDatabase.TryCreateContext()!)
            await Svc(cd).AcceptPartnershipAsync(d.Co.Id, (await RowAsync(a.Co.Id, d.Co.Id)).Id, d.Owner);
        using var fin = DbTestDatabase.TryCreateContext()!;
        var dRows = await fin.PartnerLinkRequests.AsNoTracking().Where(r => r.CompanyId == a.Co.Id && r.PartnerTaxId == d.Co.TaxId).ToListAsync();
        Assert.Equal(2, dRows.Count);
        Assert.All(dRows, r => Assert.NotNull(r.AcceptedAt));
        // ทิศตรงข้าม: คำขอถึงบริษัทอื่น (B ที่ปฏิเสธ) ไม่ถูกแตะ
        Assert.Null((await fin.PartnerLinkRequests.AsNoTracking().SingleAsync(r => r.CompanyId == a.Co.Id && r.PartnerTaxId == b.Co.TaxId)).AcceptedAt);
    }

    // ═══════════ (3) เพดาน · ส่งล้ม ═══════════

    [Fact]
    public async Task เพดานแจ้งต่อปลายทางต่อวัน_ข้ามแต่ยังอยู่ในรายการ_ส่งล้มลองใหม่ไม่ประทับว่าแจ้งแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var target = await CompanyAsync(db, "ปลายทางถูกรุม 264");
        var now = DateTime.UtcNow;
        // ผู้ขอ 10 รายที่ปลายทางได้รับแจ้งไปแล้วใน 24 ชม.
        for (var i = 0; i < PartnerLinkRequestRules.NoticesPerTargetPerDay; i++)
        {
            var r = await CompanyAsync(db, "ผู้ขอรุม 264");
            var (x, y) = r.Co.Id.CompareTo(target.Co.Id) < 0 ? (r.Co.Id, target.Co.Id) : (target.Co.Id, r.Co.Id);
            db.TradingPartnerships.Add(new TradingPartnership
            {
                CompanyAId = x, CompanyBId = y, InvitedByCompanyId = r.Co.Id, Status = TradingPartnershipStatus.Pending,
                InvitedAt = now.AddHours(-1), InviteNotifiedAt = now.AddHours(-1), InviteNoticeResult = nameof(NotificationDispatchOutcome.Delivered),
            });
        }
        await db.SaveChangesAsync();
        var extra = await CompanyAsync(db, "ผู้ขอรายที่ 11 264");
        await Svc(db).InvitePartnerAsync(extra.Co.Id, target.Co.TaxId, null, extra.Owner, null);
        var notify = new FakeNotify();
        using (var job = DbTestDatabase.TryCreateContext()!)
            await Svc(job, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        Assert.DoesNotContain(notify.Calls, c => c.CompanyId == target.Co.Id);
        var capped = await RowAsync(extra.Co.Id, target.Co.Id);
        Assert.Equal(nameof(PartnerLinkRequestRules.NoticeAction.SkipCapped), capped.InviteNoticeResult);
        // ฝ่ายค้าน M3: ไม่เงียบถาวร — ไม่ประทับว่าแจ้งแล้ว · เลื่อนไปเมื่อหน้าต่าง 24 ชม. ว่าง
        Assert.Null(capped.InviteNotifiedAt);
        Assert.True(capped.InviteNoticeDueAt > DateTime.UtcNow.AddHours(22));
        // ยังอยู่ในรายการของปลายทาง (กดยอมรับได้)
        using (var ct = DbTestDatabase.TryCreateContext()!)
        {
            var list = Data(await Controller(ct, target.Owner).List(target.Co.Id)).EnumerateArray().ToList();
            Assert.Contains(list, p => p.GetProperty("partner").GetProperty("id").GetGuid() == extra.Co.Id && p.GetProperty("canRespond").GetBoolean());
        }

        // วันถัดไป (หน้าต่างว่าง) ⇒ ได้แจ้ง
        using (var nextDay = DbTestDatabase.TryCreateContext()!)
            await Svc(nextDay, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow.AddHours(25));
        Assert.Single(notify.Calls, c => c.CompanyId == target.Co.Id);
        Assert.Equal(nameof(NotificationDispatchOutcome.Delivered), (await RowAsync(extra.Co.Id, target.Co.Id)).InviteNoticeResult);

        // ส่งล้ม ⇒ ผล Failed + เลื่อนเวลา · ไม่ประทับว่าแจ้งแล้ว · รอบถัดไปหลังถึงเวลา ⇒ ส่งได้
        var t2 = await CompanyAsync(db, "ปลายทางส่งล้ม 264");
        var r2 = await CompanyAsync(db, "ผู้ขอส่งล้ม 264");
        await Svc(db).InvitePartnerAsync(r2.Co.Id, t2.Co.TaxId, null, r2.Owner, null);
        var failing = new FakeNotify { Outcome = NotificationDispatchOutcome.Failed };
        using (var job2 = DbTestDatabase.TryCreateContext()!)
            await Svc(job2, failing).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow);
        var f = await RowAsync(r2.Co.Id, t2.Co.Id);
        Assert.Equal(nameof(NotificationDispatchOutcome.Failed), f.InviteNoticeResult);
        Assert.Null(f.InviteNotifiedAt);
        Assert.True(f.InviteNoticeDueAt > DateTime.UtcNow.AddMinutes(PartnerLinkRequestRules.NoticeRetryMinutes - 2));
        using (var job3 = DbTestDatabase.TryCreateContext()!)
            await Svc(job3, notify).DispatchPendingLinkRequestNoticesAsync(DateTime.UtcNow.AddMinutes(PartnerLinkRequestRules.NoticeRetryMinutes + 1));
        Assert.Single(notify.Calls, c => c.CompanyId == t2.Co.Id);
        Assert.Equal(nameof(NotificationDispatchOutcome.Delivered), (await RowAsync(r2.Co.Id, t2.Co.Id)).InviteNoticeResult);
    }

    // ═══════════ (4) tenant isolation ═══════════

    [Fact]
    public async Task การ์ดยอมรับ_เฉพาะปลายทาง_ผู้ขอและบริษัทอื่นได้404ข้อความเดียว_คำขอที่ส่งเห็นเฉพาะของตัวเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ tenant 264");
        var b = await CompanyAsync(db, "ปลายทาง tenant 264");
        var other = await CompanyAsync(db, "บริษัทอื่น 264");
        await Svc(db).InvitePartnerAsync(a.Co.Id, b.Co.TaxId, null, a.Owner, null);
        var row = await RowAsync(a.Co.Id, b.Co.Id);

        using var c = DbTestDatabase.TryCreateContext()!;
        var byRequester = await Controller(c, a.Owner).GetIncomingRequest(a.Co.Id, row.Id);
        var byOther = await Controller(c, other.Owner).GetIncomingRequest(other.Co.Id, row.Id);
        var missing = await Controller(c, other.Owner).GetIncomingRequest(other.Co.Id, Guid.NewGuid());
        Assert.Equal(404, Status(byRequester));
        Assert.Equal(404, Status(byOther));
        Assert.Equal(Body(missing), Body(byOther));   // ไม่บอกว่ามีแถวในบริษัทอื่น
        Assert.Equal(200, Status(await Controller(c, b.Owner).GetIncomingRequest(b.Co.Id, row.Id)));
        // บริษัทอื่นยอมรับแทนไม่ได้
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Svc(c).AcceptPartnershipAsync(other.Co.Id, row.Id, other.Owner));
        // "คำขอที่เราส่ง" เห็นเฉพาะแถวของบริษัทตัวเอง
        Assert.Equal(0, Data(await Controller(c, other.Owner).ListOutgoingRequests(other.Co.Id)).GetArrayLength());
        Assert.Equal(1, Data(await Controller(c, a.Owner).ListOutgoingRequests(a.Co.Id)).GetArrayLength());
        // ผู้ขอไม่เห็นแถวรอตอบในตารางคู่ค้า (P-1)
        Assert.Equal(0, Data(await Controller(c, a.Owner).List(a.Co.Id)).GetArrayLength());
    }

    // ═══════════ (5) ฝ่ายค้านรอบ 264 ═══════════

    [Fact]
    public async Task M1_ส่งพร้อมกันสองครั้งถึงบริษัทที่มีอยู่_ทั้งคู่202_แถวคู่เดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอพร้อมกัน 264");
        var b = await CompanyAsync(db, "ปลายทางพร้อมกัน 264");
        async Task<int?> One()
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            return Status(await Controller(c, a.Owner).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(b.Co.TaxId, null)));
        }
        var results = await Task.WhenAll(One(), One(), One());
        Assert.All(results, r => Assert.Equal(202, r));
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Single(await check.TradingPartnerships.AsNoTracking().Where(p => p.CompanyAId == a.Co.Id || p.CompanyBId == a.Co.Id).ToListAsync());
        Assert.Equal(3, await check.PartnerLinkRequests.CountAsync(r => r.CompanyId == a.Co.Id));
    }

    [Fact]
    public async Task M4_ปฏิเสธคู่ที่เชื่อมแล้ว409_ผู้ขอปฏิเสธเองไม่ได้_ปฏิเสธคำขอจริงมีaudit_บล็อกแล้วส่งใหม่ไม่เปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอปฏิเสธ M4 264");
        var b = await CompanyAsync(db, "ปลายทาง M4 264");
        await Svc(db).InvitePartnerAsync(a.Co.Id, b.Co.TaxId, null, a.Owner, null);
        var row = await RowAsync(a.Co.Id, b.Co.Id);

        // ผู้ขอ "ปฏิเสธ" คำขอของตัวเองไม่ได้
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c1).RejectPartnershipAsync(a.Co.Id, row.Id, null, a.Owner))).StatusCode);
        // ปลายทางปฏิเสธ + บล็อก ⇒ audit · ผู้ขอส่งใหม่หลังพ้นช่วงพัก ⇒ ไม่เปิด ไม่แจ้ง (คำตอบเท่าเดิม)
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await Controller(c2, b.Owner).Reject(b.Co.Id, row.Id, new CrossTenantWorkflowController.RejectRequest("สแปม", BlockRequester: true));
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await c3.AuditLogs.AnyAsync(x => x.CompanyId == b.Co.Id && x.EntityId == row.Id.ToString()
                && x.NewValues != null && x.NewValues.Contains("TradingPartnerLinkRejected")));
            Assert.Equal(b.Co.Id, (await RowAsync(a.Co.Id, b.Co.Id)).RequestsBlockedByCompanyId);
            await c3.TradingPartnerships.Where(p => p.Id == row.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.RejectedAt, DateTime.UtcNow.AddDays(-8)));
        }
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(202, Status(await Controller(c4, a.Owner).Invite(a.Co.Id, new CrossTenantWorkflowController.InviteRequest(b.Co.TaxId, null))));
        var blocked = await RowAsync(a.Co.Id, b.Co.Id);
        Assert.Equal(TradingPartnershipStatus.Rejected, blocked.Status);
        Assert.Null(blocked.InviteNoticeDueAt);
        // ทิศตรงข้าม: ผู้บล็อก (B) ส่งคำขอถึง A เอง ⇒ เปิดคำขอสด + ปลดบล็อก ⇒ A ยอมรับ ⇒ เชื่อม
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            await Svc(c5).InvitePartnerAsync(b.Co.Id, a.Co.TaxId, null, b.Owner, null);
        var reopened = await RowAsync(a.Co.Id, b.Co.Id);
        Assert.Equal(TradingPartnershipStatus.Pending, reopened.Status);
        Assert.Null(reopened.RequestsBlockedByCompanyId);
        using (var c6 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(TradingPartnershipStatus.Accepted, (await Svc(c6).AcceptPartnershipAsync(a.Co.Id, row.Id, a.Owner)).Status);
        // คู่ที่เชื่อมแล้ว ⇒ ปฏิเสธ = 409 (ทางไปต่อ: ระงับการเชื่อม) · สถานะไม่เปลี่ยน
        using (var c7 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c7).RejectPartnershipAsync(b.Co.Id, row.Id, null, b.Owner));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains("ระงับการเชื่อม", ex.Message);
        }
        Assert.Equal(TradingPartnershipStatus.Accepted, (await RowAsync(a.Co.Id, b.Co.Id)).Status);
        // L3: คำขอถึงคู่ที่เชื่อมอยู่แล้ว ⇒ คำขอที่เราส่ง = เชื่อมแล้วทันที
        using (var c8 = DbTestDatabase.TryCreateContext()!)
            await Svc(c8).InvitePartnerAsync(a.Co.Id, b.Co.TaxId, null, a.Owner, null);
        using var c9 = DbTestDatabase.TryCreateContext()!;
        var latest = await c9.PartnerLinkRequests.AsNoTracking().Where(r => r.CompanyId == a.Co.Id).OrderByDescending(r => r.CreatedAt).FirstAsync();
        Assert.NotNull(latest.AcceptedAt);
    }

    [Fact]
    public async Task L1_L2_ส่งซ้ำระหว่างรอต่ออายุคู่_ยอมรับไม่ประทับคำขอไม่ระบุสาขาที่ไม่ได้ส่งถึงผู้ยอมรับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "ผู้ขอ L2 264");
        var tax = RandTax();
        var hq = await CompanyAsync(db, "สำนักงานใหญ่ L2 264", taxId: tax);
        var br = new Company { Name = "สาขา L2 264 " + Guid.NewGuid().ToString("N")[..6], TaxId = tax, BranchCode = "00001" };
        db.Companies.Add(br);
        var brOwner = new User { Email = $"r264-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของสาขา" };
        db.Users.Add(brOwner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = brOwner.Id, CompanyId = br.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();

        await Svc(db).InvitePartnerAsync(a.Co.Id, tax, null, a.Owner, null);      // ไม่ระบุสาขา ⇒ สำนักงานใหญ่
        await Svc(db).InvitePartnerAsync(a.Co.Id, tax, "00001", a.Owner, null);   // สาขา 00001
        // L1: ส่งซ้ำถึงสาขาระหว่างรอ ⇒ ต่ออายุ InvitedAt (ไม่แจ้งซ้ำ)
        var before = await RowAsync(a.Co.Id, br.Id);
        using (var old = DbTestDatabase.TryCreateContext()!)
            await old.TradingPartnerships.Where(p => p.Id == before.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.InvitedAt, DateTime.UtcNow.AddDays(-25)));
        using (var again = DbTestDatabase.TryCreateContext()!)
            await Svc(again).InvitePartnerAsync(a.Co.Id, tax, "00001", a.Owner, null);
        var refreshed = await RowAsync(a.Co.Id, br.Id);
        Assert.True(refreshed.InvitedAt > DateTime.UtcNow.AddDays(-1));

        // L2: สาขายอมรับ ⇒ คำขอ "ไม่ระบุสาขา" (ถึงสำนักงานใหญ่) ไม่ถูกประทับ · คำขอสาขาทั้งสองฉบับ = เชื่อมแล้ว
        using (var acc = DbTestDatabase.TryCreateContext()!)
            await Svc(acc).AcceptPartnershipAsync(br.Id, refreshed.Id, brOwner.Id);
        using var check = DbTestDatabase.TryCreateContext()!;
        var reqs = await check.PartnerLinkRequests.AsNoTracking().Where(r => r.CompanyId == a.Co.Id).ToListAsync();
        Assert.Null(reqs.Single(r => r.PartnerBranchCode == null).AcceptedAt);
        Assert.All(reqs.Where(r => r.PartnerBranchCode == "00001"), r => Assert.NotNull(r.AcceptedAt));
        _ = hq;
    }
}
