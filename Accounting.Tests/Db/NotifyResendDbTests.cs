using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Cms;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 213 (คำตัดสินข้อ 172) — ข้อความค้างเพราะโควตาเต็มแล้วส่งซ้ำหลังเติมโควตา บน PostgreSQL จริง ผ่าน<b>ตัวส่งจริง</b> (<see cref="BookingNotifier"/> +
/// <see cref="NotifyQuotaService"/>): แถวที่ระงับเพราะโควตาได้รหัส quota-full · ส่งซ้ำตอนยังเต็ม = หยุด ไม่ส่ง (ปล่อย lease) · เติมโบนัสแล้วส่งได้ตามจำนวน
/// แล้วหยุดเมื่อหมดอีก · แถวที่ส่งแล้วกดซ้ำไม่ส่งซ้ำ · บริษัทอื่นแตะแถวไม่ได้ · ร่องรอยลง audit · ซ่อมแถวเก่าจากหมายเหตุ
/// </summary>
[Trait("Category", "Db")]
public class NotifyResendDbTests
{
    private readonly ITestOutputHelper _out;
    public NotifyResendDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> SeedAsync(AccountingDbContext db, int limit)
    {
        var company = new Company { Name = "ร้านทดสอบส่งซ้ำ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = company.Id, Plan = SubscriptionPlan.Basic, Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30), UsageResetDate = DateTime.UtcNow.AddDays(20),
            MaxSharedMessagesPerMonth = limit,
        });
        await db.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>ตัวส่งจริง (อีเมลระบบ = ช่องกลาง นับโควตา) บน context ของตัวเอง</summary>
    private static BookingNotifier Notifier(AccountingDbContext db, List<string> delivered, bool emailOk = true)
    {
        var email = F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m != nameof(IEmailService.TrySendAsync)) return null;
            if (!emailOk) return false;
            lock (delivered) delivered.Add((string)a[0]!);
            return true;
        });
        return new BookingNotifier(NullLogger<BookingNotifier>.Instance, email: email,
            quota: new NotifyQuotaService(db, NullLogger<NotifyQuotaService>.Instance));
    }

    private static Task<BookingSendResult> SendConfirmedAsync(BookingNotifier n, Guid cid, Guid bookingId)
        => SendAsync(n, cid, bookingId, CustomerNotifyEvents.BookingConfirmed, CustomerNotifyKeys.Booking(bookingId, CustomerNotifyEvents.BookingConfirmed));

    private static Task<BookingSendResult> SendAsync(BookingNotifier n, Guid cid, Guid bookingId, string eventKey, string messageKey)
        => n.SendAsync(new CustomerNotifyRequest(cid, null, eventKey, messageKey, new BookingContact("guest@example.test", null), "s", "t")
        { TargetKind = CustomerLineLinkRule.KindBooking, TargetId = bookingId });

    /// <summary>บริการส่งซ้ำที่รายการปลายทางมาจากเทสต์ (การจองยืนยันแล้ว · นัดพรุ่งนี้) · เส้นประกอบใหม่ = ตัวส่งจริงด้วยคีย์เดิม</summary>
    private sealed class TestResend : CustomerNotifyResendService
    {
        public TestResend(AccountingDbContext db, ICmsBookingService bookings)
            : base(db, NullLogger<CustomerNotifyResendService>.Instance, bookings, null, null) { }

        protected override Task<IReadOnlyDictionary<Guid, CustomerNotifyResendTarget>> LoadTargetsAsync(Guid companyId,
            IReadOnlyCollection<Guid> bookingIds, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, CustomerNotifyResendTarget>>(bookingIds.ToDictionary(id => id, _ =>
                new CustomerNotifyResendTarget(true, null, "การจอง BK-T", BookingStatus: BookingStatus.Confirmed,
                    BookingStartLocal: BookingSlotEngine.BangkokNow(DateTime.UtcNow).AddDays(1))));
    }

    private static TestResend Resend(AccountingDbContext db, BookingNotifier notifier, List<Guid> rebuilt)
    {
        var bookings = F4Proxy<ICmsBookingService>.Create((m, a) =>
        {
            if (m != nameof(ICmsBookingService.ResendCustomerNotifyAsync)) return null;
            var (cid, bookingId, eventKey, messageKey) = ((Guid)a[0]!, (Guid)a[1]!, (string)a[2]!, (string)a[3]!);
            lock (rebuilt) rebuilt.Add(bookingId);
            return Rebuild();
            // เส้นประกอบใหม่จริงใช้คีย์เดิม ⇒ claim แถวเดิม (เหมือน CmsBookingService.ResendCustomerNotifyAsync)
            async Task<string?> Rebuild() => (await SendAsync(notifier, cid, bookingId, eventKey, messageKey)).Error;
        });
        return new TestResend(db, bookings);
    }

    [Fact]
    public async Task Quota_full_rows_are_resent_after_topup_and_stop_again_when_quota_runs_out()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedAsync(seed, limit: 1);
        var delivered = new List<string>();
        using var sendDb = DbTestDatabase.TryCreateContext()!;
        var notifier = Notifier(sendDb, delivered);
        var b1 = Guid.NewGuid(); var b2 = Guid.NewGuid(); var b3 = Guid.NewGuid();
        Assert.True((await SendConfirmedAsync(notifier, cid, b1)).Sent);
        Assert.True((await SendConfirmedAsync(notifier, cid, b2)).IsSuppressed);   // เต็ม (เพดาน 1)
        Assert.True((await SendConfirmedAsync(notifier, cid, b3)).IsSuppressed);
        Assert.Single(delivered);

        using var db = DbTestDatabase.TryCreateContext()!;
        var reasons = await db.CustomerNotifyLogs.AsNoTracking().Where(l => l.CompanyId == cid && l.Status == NotifyLogStatus.Suppressed)
            .Select(l => l.SuppressReason).ToListAsync();
        Assert.Equal(new[] { NotifySuppressReason.QuotaFull, NotifySuppressReason.QuotaFull }, reasons);

        var rebuilt = new List<Guid>();
        var svc = Resend(db, notifier, rebuilt);
        var list = await svc.ListAsync(cid);
        Assert.Equal(2, list.ResendableCount);

        // 1) ยังไม่เติม ⇒ ลองแถวแรกแล้วหยุด · ไม่มีอะไรส่งออก · lease ถูกปล่อย (กดใหม่ได้ทันทีหลังเติม)
        var r1 = await svc.ResendAsync(cid, null, "tester");
        Assert.True(r1.StoppedForQuota);
        Assert.Equal(0, r1.Sent);
        Assert.Equal(CustomerNotifyResendService.TopUpUrl, r1.TopUpUrl);
        Assert.Single(rebuilt);
        Assert.Single(delivered);
        Assert.Equal(0, await db.CustomerNotifyLogs.CountAsync(l => l.CompanyId == cid && l.ResendRequestedAt != null));

        // 2) เติมโบนัส 1 ข้อความ ⇒ ส่งได้ 1 แล้วหยุดที่แถวถัดไป
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "Subscriptions" SET "NotifyBonusQuota" = 1, "NotifyBonusExpiresAt" = now() + interval '30 days' WHERE "CompanyId" = {0}""", cid);
        var r2 = await svc.ResendAsync(cid, null, "tester");
        Assert.Equal(1, r2.Sent);
        Assert.True(r2.StoppedForQuota);
        Assert.Equal(1, r2.StillPending);
        Assert.Equal(2, delivered.Count);
        var b2Log = await db.CustomerNotifyLogs.AsNoTracking().SingleAsync(l => l.CompanyId == cid && l.TargetId == b2);
        Assert.Equal(NotifyLogStatus.Sent, b2Log.Status);
        Assert.Null(b2Log.SuppressReason);
        Assert.NotNull(b2Log.ResendRequestedAt);   // ร่องรอย "ส่งซ้ำเมื่อ"

        // 3) ทิศตรงข้าม: กดส่งซ้ำแถวที่ส่งแล้ว ⇒ ข้าม ไม่ส่งซ้ำ
        var r3 = await svc.ResendAsync(cid, new[] { b2Log.Id }, "tester");
        Assert.Equal(0, r3.Sent);
        Assert.Equal(1, r3.Skipped);
        Assert.Equal(2, delivered.Count);

        // 4) บริษัทอื่นส่งรหัสแถวของบริษัทนี้ ⇒ "ไม่พบรายการ" + แถวไม่ถูกแตะ
        var other = await SeedAsync(seed, limit: 100);
        var b3Log = await db.CustomerNotifyLogs.AsNoTracking().SingleAsync(l => l.CompanyId == cid && l.TargetId == b3);
        var r4 = await svc.ResendAsync(other, new[] { b3Log.Id }, "intruder");
        Assert.Equal(0, r4.Sent);
        Assert.Equal("ไม่พบรายการ", r4.Items.Single().Detail);
        var b3After = await db.CustomerNotifyLogs.AsNoTracking().SingleAsync(l => l.Id == b3Log.Id);
        Assert.Equal(NotifyLogStatus.Suppressed, b3After.Status);
        Assert.Equal(b3Log.ResendCount, b3After.ResendCount);

        // ร่องรอยการกดอยู่ใน audit chain ของบริษัท
        Assert.True(await db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == cid && a.EntityType == "CustomerNotifyLog") >= 2);
    }

    [Fact]
    public async Task Double_click_resends_a_row_once()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var delivered = new List<string>();
        using var sendDb = DbTestDatabase.TryCreateContext()!;
        var notifier = Notifier(sendDb, delivered);
        var b = Guid.NewGuid();
        Assert.True((await SendConfirmedAsync(notifier, cid, b)).IsSuppressed);
        await seed.Database.ExecuteSqlRawAsync("""UPDATE "Subscriptions" SET "MaxSharedMessagesPerMonth" = 10 WHERE "CompanyId" = {0}""", cid);

        async Task<int> ClickAsync()
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            using var nDb = DbTestDatabase.TryCreateContext()!;
            return (await Resend(db, Notifier(nDb, delivered), new List<Guid>()).ResendAsync(cid, null, "tester")).Sent;
        }
        var sent = await Task.WhenAll(ClickAsync(), ClickAsync());
        Assert.Equal(1, sent.Sum());
        Assert.Single(delivered);   // ลูกค้าได้ข้อความเดียว
    }

    [Fact]
    public async Task Only_the_newest_of_two_pending_reschedules_is_resent()
    {
        // ฝ่ายค้านรอบ 213 S2: เลื่อน A→B แล้ว B→C ขณะโควตาเต็ม ⇒ ส่งฉบับล่าสุดฉบับเดียว (ไม่ใช่สองฉบับ + 2 หน่วยโควตา)
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var delivered = new List<string>();
        using var sendDb = DbTestDatabase.TryCreateContext()!;
        var notifier = Notifier(sendDb, delivered);
        var b = Guid.NewGuid();
        var k1 = CustomerNotifyKeys.BookingRescheduled(b, new DateTime(2026, 11, 1), new TimeOnly(9, 0));
        var k2 = CustomerNotifyKeys.BookingRescheduled(b, new DateTime(2026, 11, 2), new TimeOnly(10, 0));
        Assert.True((await SendAsync(notifier, cid, b, CustomerNotifyEvents.BookingRescheduled, k1)).IsSuppressed);
        await Task.Delay(20);
        Assert.True((await SendAsync(notifier, cid, b, CustomerNotifyEvents.BookingRescheduled, k2)).IsSuppressed);
        await seed.Database.ExecuteSqlRawAsync("""UPDATE "Subscriptions" SET "MaxSharedMessagesPerMonth" = 10 WHERE "CompanyId" = {0}""", cid);

        using var db = DbTestDatabase.TryCreateContext()!;
        var rebuilt = new List<Guid>();
        var svc = Resend(db, notifier, rebuilt);
        var list = await svc.ListAsync(cid);
        Assert.Equal(2, list.Total);
        Assert.Equal(1, list.ResendableCount);
        Assert.Equal(CustomerNotifyResend.CodeSuperseded, list.Items.Single(i => !i.Resendable).VerdictCode);
        var r = await svc.ResendAsync(cid, null, "tester");
        Assert.Equal(1, r.Sent);
        Assert.Single(delivered);
        var newest = await db.CustomerNotifyLogs.AsNoTracking().SingleAsync(l => l.CompanyId == cid && l.MessageKey == k2);
        Assert.Equal(NotifyLogStatus.Sent, newest.Status);
    }

    [Fact]
    public async Task Rows_stuck_claimed_or_failed_after_resend_stay_retriable()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var delivered = new List<string>();
        using var sendDb = DbTestDatabase.TryCreateContext()!;
        var b1 = Guid.NewGuid(); var b2 = Guid.NewGuid();
        Assert.True((await SendConfirmedAsync(Notifier(sendDb, delivered), cid, b1)).IsSuppressed);
        Assert.True((await SendConfirmedAsync(Notifier(sendDb, delivered), cid, b2)).IsSuppressed);
        await seed.Database.ExecuteSqlRawAsync("""UPDATE "Subscriptions" SET "MaxSharedMessagesPerMonth" = 10 WHERE "CompanyId" = {0}""", cid);

        // S6: ตัวส่งล้มกลางทาง ⇒ ค้าง Claimed (เหตุยังเป็น quota-full) — lease ยังไม่หมด = ไม่อยู่ในรายการ · หมดแล้ว = กลับมาให้ลองใหม่
        await seed.Database.ExecuteSqlRawAsync(
            """UPDATE "CustomerNotifyLogs" SET "Status" = 'Claimed', "ClaimedUntil" = now() + interval '5 minutes' WHERE "CompanyId" = {0} AND "TargetId" = {1}""", cid, b1);
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = Resend(db, Notifier(sendDb, delivered, emailOk: false), new List<Guid>());
        Assert.Equal(1, (await svc.ListAsync(cid)).Total);   // กำลังส่งอยู่ (lease ยังไม่หมด) ⇒ ไม่โผล่ ไม่ถูกส่งซ้อน
        await seed.Database.ExecuteSqlRawAsync(
            """UPDATE "CustomerNotifyLogs" SET "ClaimedUntil" = now() - interval '1 minute' WHERE "CompanyId" = {0} AND "TargetId" = {1}""", cid, b1);
        Assert.Equal(2, (await svc.ListAsync(cid)).ResendableCount);

        // S5: ส่งซ้ำแล้วช่องล้ม ⇒ Failed แต่ยังอยู่ในรายการพร้อมเหตุ · ลองใหม่ได้ (ช่องกลับมา ⇒ ส่งถึง)
        var r1 = await svc.ResendAsync(cid, null, "tester");
        Assert.Equal(0, r1.Sent);
        Assert.Equal(2, r1.Failed);
        var after = await svc.ListAsync(cid);
        Assert.Equal(2, after.ResendableCount);
        Assert.All(after.Items, i => Assert.StartsWith("ส่งซ้ำแล้วไม่สำเร็จ", i.ReasonLabel));
        var r2 = await Resend(db, Notifier(sendDb, delivered), new List<Guid>()).ResendAsync(cid, null, "tester");
        Assert.Equal(2, r2.Sent);
        Assert.Equal(2, delivered.Count);
        Assert.Equal(0, (await svc.ListAsync(cid)).Total);
    }

    [Fact]
    public async Task Legacy_suppressed_rows_get_their_reason_from_the_note()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(db, limit: 1);
        CustomerNotifyLog Row(string note) => new()
        {
            CompanyId = cid, MessageKey = "legacy:" + Guid.NewGuid().ToString("N"), EventKey = CustomerNotifyEvents.BookingConfirmed,
            Status = NotifyLogStatus.Suppressed, Note = CustomerNotifyLogRule.Redact(note),
        };
        var quota = Row(NotifyQuotaPolicy.FullReason(300));
        var off = Row(CustomerNotifyPolicy.SuppressedByPolicyReason);
        var failed = Row("อีเมล: ส่งไม่สำเร็จ");
        failed.Status = NotifyLogStatus.Failed;
        db.CustomerNotifyLogs.AddRange(quota, off, failed);
        await db.SaveChangesAsync();

        foreach (var sql in CustomerNotifyResendMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        foreach (var sql in CustomerNotifyResendMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);   // รันซ้ำได้

        string? Reason(Guid id) => db.CustomerNotifyLogs.AsNoTracking().Where(l => l.Id == id).Select(l => l.SuppressReason).Single();
        Assert.Equal(NotifySuppressReason.QuotaFull, Reason(quota.Id));
        Assert.Equal(NotifySuppressReason.EventOff, Reason(off.Id));
        Assert.Null(Reason(failed.Id));   // ทิศตรงข้าม: แถวที่ไม่ใช่ระงับไม่ถูกแตะ
    }
}
