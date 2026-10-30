using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 แพ็ก W3 (R-15 · B14 · TEST_PLAN BKE-10/BKE-16) — เครื่องจองคิว CMS บนฐานจริง (แบบ <c>LodgingDoubleBookingDbTests</c>):
/// <list type="bullet">
///   <item>รอบสุดท้าย N คำขอพร้อมกัน (คนละ DbContext) ⇒ ได้ใบเดียว · ที่เหลือ 409 <c>CMS-BOOKING-FULL</c> (ล็อกไซต์ <c>AdvisoryLockKey.CmsBookingSlot</c> จริง)</item>
///   <item>ทิศตรงข้าม: ความจุ 2 ⇒ 2 คำขอได้ครบ</item>
///   <item>ยกเลิกใบที่กินรอบ ⇒ คิวสำรองใบแรกเลื่อนขึ้น ใบที่สองยังอยู่คิว · ได้คิวแล้วถือรอบ (R-13) ⇒ หมดเวลาไม่ยืนยัน job ปล่อยรอบให้ใบถัดไป</item>
///   <item>เตือนก่อนนัด (R-12): ส่งถึง ⇒ <c>ReminderSentAt</c> ประทับ · ส่งล้ม ⇒ ยัง null + <c>NotifyLastError</c> (ทิศตรงข้าม)</item>
///   <item>เวลาทำการระดับเว็บ (R-14): บริการที่ไม่ตั้งเวลาเห็นรอบของเว็บ · บริการที่ตั้งเองไม่เปลี่ยน</item>
/// </list>
/// ไม่มีฐาน (ไม่ตั้ง <c>ACCOUNTING_TEST_PG</c>) ⇒ ข้ามพร้อมข้อความ — หลักฐานคือ job <c>db-test</c>
/// </summary>
[Trait("Category", "Db")]
public class CmsBookingDoubleBookingDbTests
{
    private readonly ITestOutputHelper _out;
    public CmsBookingDoubleBookingDbTests(ITestOutputHelper output) => _out = output;

    /// <summary>ตัวส่งปลอม — บอกผลตามที่ตั้ง (ส่งถึง/ล้ม) + จำข้อความที่ส่ง</summary>
    private sealed class FakeNotifier : IBookingNotifier
    {
        public bool Succeed { get; set; } = true;
        public List<(BookingContact Contact, string Subject, string Text)> Sent { get; } = new();
        public Task<BookingSendResult> SendAsync(CustomerNotifyRequest request, CancellationToken ct = default)
        {
            Sent.Add((request.Contact, request.Subject, request.Text));
            return Task.FromResult(Succeed ? BookingSendResult.Ok("email") : BookingSendResult.Fail("SMTP ปลอมล้ม"));
        }
    }

    private static DateOnly TargetDate() => DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(DateTime.UtcNow)).AddDays(10);

    private static async Task<(Guid CompanyId, Guid SiteId, Guid ServiceId)> SeedAsync(Accounting.Data.AccountingDbContext db, int capacity,
        bool waitlist = false, bool autoConfirm = true, bool withOwnHours = true)
    {
        var company = new Company { Name = "สปาทดสอบจองซ้อน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "สปาบ้านสวน", Slug = "spa-" + tag, Subdomain = "spa-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "นวดไทย", Slug = "thai-" + tag,
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = capacity,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = BookingType.PrePayment,   // ไม่มีมัดจำ ⇒ ไม่ออกเอกสาร ERP (เทสต์นี้ไม่มี IDocumentService — ทดสอบล็อก/คิว/แจ้ง ไม่ใช่เส้นเอกสาร)
            AutoConfirm = autoConfirm, WaitlistEnabled = waitlist, IsActive = true, Price = 500m,
        };
        db.SiteBookingServices.Add(svc);
        await db.SaveChangesAsync();
        if (withOwnHours)
        {
            db.SiteBookingSlots.Add(new SiteBookingSlot
            {
                CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
                StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0), MaxCapacity = capacity, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        return (company.Id, site.Id, svc.Id);
    }

    private static CmsBookingService Svc(Accounting.Data.AccountingDbContext db, IBookingNotifier? notifier = null)
        => new(db, NullLogger<CmsBookingService>.Instance, null, notifier);

    private static async Task<(bool Ok, string? Code, int Status, Guid? Id)> TryBookAsync(Guid companyId, Guid siteId, Guid serviceId, int n, string start = "09:00")
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        try
        {
            var r = await Svc(db).CreateBookingAsync(companyId, siteId, new CreateBookingRequest
            {
                BookingServiceId = serviceId, BookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), StartTime = start,
                GuestName = $"ลูกค้า {n}", GuestEmail = $"guest{n}@example.test", GuestCount = 1,
            }, "db-test");
            return (r.Status != BookingStatus.Waitlisted, r.Status.ToString(), 201, r.Id);
        }
        catch (BusinessRuleException ex) { return (false, ex.RuleCode, ex.StatusCode, null); }
    }

    private static async Task<SiteBooking> LoadAsync(Guid companyId, Guid id)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == id && b.CompanyId == companyId);
    }

    [Fact]
    public async Task รอบสุดท้าย_ยิงพร้อมกันหกคำขอ_ได้ใบเดียว_ที่เหลือ409เต็ม()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 1);

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(n => Task.Run(() => TryBookAsync(cid, sid, svcId, n))));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.All(results.Where(r => !r.Ok), r => { Assert.Equal("CMS-BOOKING-FULL", r.Code); Assert.Equal(409, r.Status); });
        using var db = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await db.SiteBookings.CountAsync(b => b.CompanyId == cid && b.SiteId == sid && b.Status != BookingStatus.Cancelled));
    }

    [Fact]
    public async Task ความจุสอง_สองคำขอพร้อมกัน_ได้ครบทั้งสอง()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 2);

        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(n => Task.Run(() => TryBookAsync(cid, sid, svcId, n))));

        Assert.All(results, r => Assert.True(r.Ok));
    }

    [Fact]
    public async Task ยกเลิกใบที่กินรอบ_คิวสำรองใบแรกเลื่อนขึ้น_ใบที่สองยังอยู่คิว_แจ้งลูกค้าถึงจริงประทับ()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 1, waitlist: true);
        var first = await TryBookAsync(cid, sid, svcId, 1);
        var w1 = await TryBookAsync(cid, sid, svcId, 2);
        var w2 = await TryBookAsync(cid, sid, svcId, 3);
        Assert.True(first.Ok);
        Assert.Equal("Waitlisted", w1.Code);
        Assert.Equal("Waitlisted", w2.Code);

        var notifier = new FakeNotifier();
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var res = await Svc(db, notifier).UpdateBookingStatusAsync(cid, sid, first.Id!.Value,
                new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = "เทสต์" }, "db-test");
            Assert.Contains(res.ErpNotices, n => n.Contains("แจ้งลูกค้าทาง"));
        }
        var promoted = await LoadAsync(cid, w1.Id!.Value);
        var still = await LoadAsync(cid, w2.Id!.Value);
        Assert.Equal(BookingStatus.Confirmed, promoted.Status);   // AutoConfirm
        Assert.NotNull(promoted.WaitlistPromotedAt);
        Assert.NotNull(promoted.WaitlistNotifiedAt);              // ส่งถึงจริง ⇒ ประทับ
        Assert.Null(promoted.WaitlistHoldUntil);                  // ยืนยันอัตโนมัติ — ไม่ถือรอบ
        Assert.Equal(BookingStatus.Waitlisted, still.Status);
        Assert.Single(notifier.Sent);
        Assert.Equal("guest2@example.test", notifier.Sent[0].Contact.Email);
    }

    [Fact]
    public async Task ได้คิวแล้วถือรอบ_ไม่ยืนยันจนหมดเวลา_jobปล่อยรอบ_ใบถัดไปได้คิว_ยืนยันแล้วไม่ถูกปล่อย()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 1, waitlist: true, autoConfirm: false);
        seedDb.SiteBookingSettings.Add(new SiteBookingSettings { CompanyId = cid, SiteId = sid, WaitlistHoldMinutes = 15 });
        await seedDb.SaveChangesAsync();
        var first = await TryBookAsync(cid, sid, svcId, 1);
        var w1 = await TryBookAsync(cid, sid, svcId, 2);
        var w2 = await TryBookAsync(cid, sid, svcId, 3);
        Assert.True(first.Ok);

        var notifier = new FakeNotifier();
        using (var db = DbTestDatabase.TryCreateContext()!)
            await Svc(db, notifier).UpdateBookingStatusAsync(cid, sid, first.Id!.Value, new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled }, "db-test");
        var held = await LoadAsync(cid, w1.Id!.Value);
        Assert.Equal(BookingStatus.Pending, held.Status);
        Assert.NotNull(held.WaitlistHoldUntil);
        Assert.Contains("ภายใน", notifier.Sent.Single().Text);

        // ยังไม่หมดเวลา ⇒ job ไม่แตะ (ทิศตรงข้าม)
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, notifier, Svc(db, notifier), DateTime.UtcNow, CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal(0, r.HoldsReleased);
        }
        Assert.Equal(BookingStatus.Pending, (await LoadAsync(cid, w1.Id!.Value)).Status);

        // หมดเวลา ⇒ ปล่อยรอบ (ยกเลิกผ่านเส้นสถานะปกติ) · ใบถัดไปได้คิว + ถือรอบใหม่
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, notifier, Svc(db, notifier), DateTime.UtcNow.AddMinutes(16), CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal(1, r.HoldsReleased);
        }
        var releasedRow = await LoadAsync(cid, w1.Id!.Value);
        var next = await LoadAsync(cid, w2.Id!.Value);
        Assert.Equal(BookingStatus.Cancelled, releasedRow.Status);
        Assert.Equal(BookingReminderPlan.HoldExpiredReason, releasedRow.CancellationReason);
        Assert.Equal(BookingStatus.Pending, next.Status);
        Assert.NotNull(next.WaitlistHoldUntil);

        // ยืนยันแล้ว ⇒ เลิกถือรอบ · job รอบถัดไปไม่ปล่อย (ทิศตรงข้าม)
        using (var db = DbTestDatabase.TryCreateContext()!)
            await Svc(db, notifier).UpdateBookingStatusAsync(cid, sid, w2.Id!.Value, new UpdateBookingStatusRequest { Status = BookingStatus.Confirmed }, "db-test");
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, notifier, Svc(db, notifier), DateTime.UtcNow.AddHours(2), CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal(0, r.HoldsReleased);
        }
        var confirmed = await LoadAsync(cid, w2.Id!.Value);
        Assert.Equal(BookingStatus.Confirmed, confirmed.Status);
        Assert.Null(confirmed.WaitlistHoldUntil);
    }

    [Fact]
    public async Task เตือนก่อนนัด_ส่งถึงประทับReminderSentAt_ส่งล้มยังnullพร้อมร่องรอย()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 2);
        var a = await TryBookAsync(cid, sid, svcId, 1, "10:00");
        var b = await TryBookAsync(cid, sid, svcId, 2, "11:00");
        Assert.True(a.Ok && b.Ok);
        // "ตอนนี้" = 1.5 ชม. ก่อนนัด 10:00 (เวลาไทย) ⇒ ใบ 10:00 ถึงขั้น 2 ชม. · ใบ 11:00 เหลือ 2.5 ชม. ⇒ ถึงขั้น 24 ชม. (จองไว้ล่วงหน้า 10 วัน)
        // รอบ 205 W2 แก้ตาม CI 475: เดิม 08:30 ⇒ รอบลองใหม่ 09:01 ใบ 11:00 เหลือ 1:59 กลายเป็นขั้น 2 ชม. ทั้งคู่ · 07:55 ⇒ ใบ 10:00 เหลือ 2:05 (ขั้น 24 ชม. ก่อน · ลองใหม่ 08:26 ถึงขั้น 2 ชม.) · ใบ 11:00 ยังขั้น 24 ชม. ("1 วัน")
        var nowLocal = TargetDate().ToDateTime(new TimeOnly(7, 55));
        var nowUtc = DateTime.SpecifyKind(nowLocal.AddHours(-7), DateTimeKind.Utc);

        var failing = new FakeNotifier { Succeed = false };
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, failing, Svc(db), nowUtc, CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal((0, 2), (r.RemindersSent, r.RemindersFailed));
        }
        var afterFail = await LoadAsync(cid, a.Id!.Value);
        Assert.Null(afterFail.ReminderSentAt);                      // ส่งล้ม ⇒ ห้ามประทับ
        Assert.False(afterFail.ReminderSent);
        Assert.Equal("SMTP ปลอมล้ม", afterFail.NotifyLastError);
        Assert.NotNull(afterFail.NotifyAttemptedAt);

        // ลองใหม่ทันทีถูกเว้นระยะ (30 นาที) ⇒ ไม่ยิงซ้ำ · พ้นระยะแล้วส่งถึง ⇒ ประทับ
        var ok = new FakeNotifier();
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, ok, Svc(db), nowUtc.AddMinutes(1), CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal(0, r.RemindersSent + r.RemindersFailed);
        }
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var job = new BookingReminderJob(null!, NullLogger<BookingReminderJob>.Instance);
            var r = await job.ProcessAsync(db, ok, Svc(db), nowUtc.AddMinutes(31), CancellationToken.None, onlyCompanyId: cid);
            Assert.Equal((2, 0), (r.RemindersSent, r.RemindersFailed));
        }
        var sent = await LoadAsync(cid, a.Id!.Value);
        Assert.True(sent.ReminderSent);
        Assert.NotNull(sent.ReminderSentAt);
        Assert.Null(sent.NotifyLastError);
        Assert.Equal(2, ok.Sent.Count);
        Assert.Contains("นวดไทย", ok.Sent[0].Text);
        Assert.Contains(ok.Sent, m => m.Subject.Contains("1 วัน"));       // ใบ 11:00 = ขั้น 24 ชม.
        Assert.Contains(ok.Sent, m => m.Subject.Contains("2 ชั่วโมง"));   // ใบ 10:00 = ขั้น 2 ชม.
        Assert.NotNull((await LoadAsync(cid, b.Id!.Value)).ReminderSentAt);
    }

    [Fact]
    public async Task เวลาทำการระดับเว็บ_บริการไม่ตั้งเวลาเห็นรอบของเว็บ_บริการที่ตั้งเองไม่เปลี่ยน()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, ownSvc) = await SeedAsync(seedDb, capacity: 1);   // ตั้งเวลาเอง 09:00–12:00
        var noHours = new SiteBookingService
        {
            CompanyId = cid, SiteId = sid, Name = "สปาหน้า", Slug = "facial-" + Guid.NewGuid().ToString("N")[..8],
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 1,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = BookingType.PrePayment,   // ไม่มีมัดจำ ⇒ ไม่ออกเอกสาร ERP (เทสต์นี้ไม่มี IDocumentService — ทดสอบล็อก/คิว/แจ้ง ไม่ใช่เส้นเอกสาร) AutoConfirm = true, IsActive = true,
        };
        seedDb.SiteBookingServices.Add(noHours);
        await seedDb.SaveChangesAsync();

        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var before = await Svc(db).GetAvailableSlotsAsync(cid, sid, new AvailableSlotsRequest { BookingServiceId = noHours.Id, Date = TargetDate().ToDateTime(TimeOnly.MinValue) });
            Assert.Empty(before);   // เว็บยังไม่ตั้ง ⇒ ว่าง (ไม่เปิดรอบ)
            await Svc(db).UpdateSiteSettingsAsync(cid, sid, new BookingSiteSettingsDto
            {
                Schedule = new BookingScheduleDto { Weekly = { new BookingWeeklyHoursDto { DayOfWeek = TargetDate().DayOfWeek, Start = "13:00", End = "15:00" } } },
            }, "db-test");
        }
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Svc(db);
            var inherited = await svc.GetAvailableSlotsAsync(cid, sid, new AvailableSlotsRequest { BookingServiceId = noHours.Id, Date = TargetDate().ToDateTime(TimeOnly.MinValue) });
            Assert.Equal(new[] { "13:00", "14:00" }, inherited.Select(x => x.StartTime));
            var own = await svc.GetAvailableSlotsAsync(cid, sid, new AvailableSlotsRequest { BookingServiceId = ownSvc, Date = TargetDate().ToDateTime(TimeOnly.MinValue) });
            Assert.Equal(new[] { "09:00", "10:00", "11:00" }, own.Select(x => x.StartTime));   // ทิศตรงข้าม: ไม่ถูกแตะ
            var echoed = await svc.GetSiteSettingsAsync(cid, sid);
            Assert.Single(echoed.Schedule!.Weekly);
            var list = await svc.GetServicesAsync(cid, sid, includeInactive: true);
            Assert.True(list.Single(s => s.Id == noHours.Id).UsesSiteHours);
            Assert.False(list.Single(s => s.Id == ownSvc).UsesSiteHours);
        }
    }
}
