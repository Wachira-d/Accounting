using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.CourseCredit;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 209 · P4 — ตารางคลาสบนฐานจริง (TEST_PLAN CMS209-P4-08): เทอม → สร้างรอบ (สร้างซ้ำไม่ซ้ำ) → หน้าเวลาว่างคืนรอบเรียนแทนเวลาทำการรายสัปดาห์ →
/// จอง 2 ใบ (ใบหนึ่งจ่ายด้วยบัตรคอร์ส — แถวสมุดเครดิต ExternalRef) → ร้านยกเลิกทั้งคลาส ⇒ ทั้งสองใบยกเลิกผ่านเส้นสถานะเดิม · บัตร ⇒ Release(byBusiness: true) ·
/// แจ้งลูกค้าทั้งสอง · รอบที่ยกเลิกจองต่อไม่ได้ (Closed) · ไม่มีฐาน ⇒ ข้ามพร้อมข้อความ (หลักฐานคือ job db-test)
/// </summary>
[Trait("Category", "Db")]
public class ClassScheduleDbTests
{
    private readonly ITestOutputHelper _out;
    public ClassScheduleDbTests(ITestOutputHelper output) => _out = output;

    private sealed class FakeNotifier : IBookingNotifier
    {
        public List<(BookingContact Contact, string Subject, string Text)> Sent { get; } = new();
        public Task<BookingSendResult> SendAsync(CustomerNotifyRequest request, CancellationToken ct = default)
        {
            Sent.Add((request.Contact, request.Subject, request.Text));
            return Task.FromResult(BookingSendResult.Ok("email"));
        }
    }

    /// <summary>สมุดเครดิตปลอม — จำการคืนเครดิต (ExternalRef · byBusiness) · การกัน/ตัดไม่ถูกเรียกในเรื่องนี้</summary>
    private sealed class FakeLedger : ICreditBookingLedger
    {
        public List<(string Ref, bool ByBusiness)> Released { get; } = new();
        public Task<CreditHoldResult> HoldAsync(Guid companyId, CreditHoldRequest request, string actor, CancellationToken ct = default)
            => throw new NotSupportedException("เรื่องนี้ไม่กันเครดิตผ่านสมุดปลอม");
        public Task<CreditBookingResponse> ReleaseAsync(Guid companyId, string externalRef, bool byBusiness, string actor, CancellationToken ct = default)
        {
            Released.Add((externalRef, byBusiness));
            return Task.FromResult(new CreditBookingResponse(Guid.NewGuid(), null, null, externalRef, Guid.NewGuid(), "CP-TEST", Guid.NewGuid(), null,
                null, null, DateTime.UtcNow, 1m, CreditBookingStatus.Cancelled, "คืนเครดิต", 0m, DateTime.UtcNow, null));
        }
        public Task<CreditBookingResponse> SettleAsync(Guid companyId, string externalRef, bool attended, string actor, CancellationToken ct = default)
            => throw new NotSupportedException("เรื่องนี้ไม่ตัดเครดิต");
    }

    private static DateOnly TargetDate() => DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(DateTime.UtcNow)).AddDays(10);

    [Fact]
    public async Task เทอม_สร้างรอบ_จอง_ยกเลิกทั้งคลาส_ทุกใบยกเลิก_บัตรคืนเครดิตแบบร้านยกเลิก_แจ้งลูกค้า_รอบปิดจองไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }

        var company = new Company { Name = "Core Studio ทดสอบ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "Core Studio", Slug = "core-" + tag, Subdomain = "core-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "Reformer", Slug = "reformer-" + tag,
            SlotMode = BookingSlotMode.Windows, DurationMinutes = 60, MaxCapacity = 8, CapacityUnit = BookingCapacityUnit.Guests,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 60, BookingType = BookingType.PrePayment, AutoConfirm = true, IsActive = true, Price = 450m,
        };
        db.SiteBookingServices.Add(svc);
        // เวลาทำการรายสัปดาห์เดิมของบริการ 09:00–12:00 — มีเทอมแล้วต้องไม่ถูกใช้
        db.SiteBookingSlots.Add(new SiteBookingSlot
        {
            CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0), MaxCapacity = 8, IsActive = true,
        });
        await db.SaveChangesAsync();
        var (cid, sid) = (company.Id, site.Id);

        var notifier = new FakeNotifier();
        var ledger = new FakeLedger();
        var service = new CmsBookingService(db, NullLogger<CmsBookingService>.Instance, null, notifier, ledger);

        var term = await service.SaveClassTermAsync(cid, sid, null, new SaveClassTermRequest
        {
            BookingServiceId = svc.Id, Weekdays = new List<int> { (int)TargetDate().DayOfWeek }, StartTime = "18:00",
            StartDate = TargetDate().ToDateTime(TimeOnly.MinValue), CapacityOverride = 6,
        }, "db-test");
        var gen = await service.GenerateClassSessionsAsync(cid, sid, term.Id, new GenerateClassSessionsRequest { FromDate = TargetDate().ToDateTime(TimeOnly.MinValue), Weeks = 1 }, "db-test");
        Assert.Equal(1, gen.Created);
        var again = await service.GenerateClassSessionsAsync(cid, sid, term.Id, new GenerateClassSessionsRequest { FromDate = TargetDate().ToDateTime(TimeOnly.MinValue), Weeks = 1 }, "db-test");
        Assert.Equal(0, again.Created);
        Assert.Equal(1, again.AlreadyExisting);

        var slots = await service.GetAvailableSlotsAsync(cid, sid, new AvailableSlotsRequest { BookingServiceId = svc.Id, Date = TargetDate().ToDateTime(TimeOnly.MinValue) });
        var only = Assert.Single(slots);   // 09:00 รายสัปดาห์หายไป — เหลือรอบคลาส 18:00
        Assert.Equal("18:00", only.StartTime);
        Assert.Equal(6, only.AvailableCapacity);   // ที่นั่งของเทอม ไม่ใช่ความจุบริการ 8
        Assert.NotNull(only.ClassSessionId);

        async Task<Guid> Book(int n) => (await service.CreateBookingAsync(cid, sid, new CreateBookingRequest
        {
            BookingServiceId = svc.Id, BookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), StartTime = "18:00",
            GuestName = $"สมาชิก {n}", GuestEmail = $"member{n}@example.test", GuestCount = 1,
        }, "db-test")).Id;
        var paid = await Book(1);
        var credit = await Book(2);
        // ใบที่สองจ่ายด้วยบัตร: ความจริงอยู่ที่สมุดเครดิต (ExternalRef) — CreditBookingLink.Ref
        db.CreditClassBookings.Add(new CreditClassBooking
        {
            CompanyId = cid, ExternalRef = CreditBookingLink.Ref(credit), PassId = Guid.NewGuid(), ContactId = Guid.NewGuid(),
            StartAt = CreditBookingLink.StartUtc(TargetDate().ToDateTime(TimeOnly.MinValue), new TimeOnly(18, 0)), Credits = 1m,
        });
        await db.SaveChangesAsync();

        var detail = await service.GetClassSessionAsync(cid, sid, only.ClassSessionId!.Value);
        Assert.Equal(2, detail.Taken);
        Assert.Equal(2, detail.Roster.Count);

        var result = await service.CancelClassSessionAsync(cid, sid, only.ClassSessionId!.Value, new ClassCancelRequest { Reason = "ครูป่วย", Notify = true }, "db-test");
        Assert.Equal(ClassSessionStatus.Cancelled, result.Session.Status);
        db.ChangeTracker.Clear();
        var statuses = await db.SiteBookings.AsNoTracking().Where(b => b.CompanyId == cid && (b.Id == paid || b.Id == credit)).Select(b => b.Status).ToListAsync();
        Assert.All(statuses, s => Assert.Equal(BookingStatus.Cancelled, s));
        var released = Assert.Single(ledger.Released);
        Assert.Equal(CreditBookingLink.Ref(credit), released.Ref);
        Assert.True(released.ByBusiness);   // ร้านยกเลิก ⇒ คืนเครดิตเต็มเสมอ
        Assert.Equal(2, notifier.Sent.Count(m => m.Subject.Contains("ยกเลิกคลาส")));
        Assert.Contains(notifier.Sent, m => m.Text.Contains("เครดิต"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Book(3));
        Assert.Equal("CMS-BOOKING-CLOSED", ex.RuleCode);
        Assert.Contains("ยกเลิก", ex.Message);
    }
}
