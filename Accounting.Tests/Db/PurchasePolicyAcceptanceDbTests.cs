using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
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
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) บน PostgreSQL จริง — ด่านติ๊กยอมรับข้อกำหนด + นโยบายการยกเลิกและคืนเงิน (<see cref="PurchasePolicyAcceptance"/>):
/// ลูกค้าจองเองไม่ติ๊ก ⇒ ปฏิเสธก่อนบันทึก · ติ๊กแล้ว ⇒ หลักฐานผูกการจองและตรวจ hash กลับได้หลังผ่านฐาน · พนักงานจองแทน ⇒ ไม่ถูกแตะ (ทิศตรงข้าม) ·
/// ค่าบริการแพลตฟอร์มยอมรับไว้แล้วสำหรับรายการเดียวกัน ⇒ ไม่ต้องติ๊กซ้ำ · ยกเลิกแพ็กเกจมีผลสิ้นรอบ (ไม่เข้า PastDue)
/// </summary>
[Trait("Category", "Db")]
public class PurchasePolicyAcceptanceDbTests
{
    private readonly ITestOutputHelper _out;
    public PurchasePolicyAcceptanceDbTests(ITestOutputHelper output) => _out = output;

    private static DateOnly TargetDate() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(3);

    private static async Task<(Guid CompanyId, Guid SiteId, Guid ServiceId)> SeedBookingAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "ร้านทดสอบนโยบายคืนเงิน", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "ร้านทดสอบ", Slug = "rf-" + tag, Subdomain = "rf-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "นวด", Slug = "massage-" + tag,
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = BookingType.Appointment,
            AutoConfirm = true, IsActive = true, Price = 500m,
        };
        db.SiteBookingServices.Add(svc);
        db.SiteBookingSlots.Add(new SiteBookingSlot
        {
            CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), MaxCapacity = 5, IsActive = true,
        });
        await db.SaveChangesAsync();
        return (company.Id, site.Id, svc.Id);
    }

    private static CreateBookingRequest Req(Guid serviceId, string start, bool accepted, string? versionShown = null) => new()
    {
        BookingServiceId = serviceId, BookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), StartTime = start,
        GuestName = "คุณลูกค้า", GuestEmail = "guest-policy@example.test", GuestPhone = "0812345678", GuestCount = 1,
        PolicyAccepted = accepted, PolicyVersionShown = versionShown,
    };

    private static CmsBookingService Svc(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<CmsBookingService>.Instance);

    [Fact]
    public async Task Guest_booking_without_tick_is_rejected_before_anything_is_saved()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedBookingAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "10:00", accepted: false), userId: null));
        Assert.Equal(PurchasePolicyAcceptance.RuleCode, ex.RuleCode);
        Assert.Equal(PurchasePolicyAcceptance.MissingMessage, ex.Message);

        using var fresh = DbTestDatabase.TryCreateContext()!;
        Assert.False(await fresh.SiteBookings.AnyAsync(b => b.CompanyId == cid && b.SiteId == sid));
        Assert.False(await fresh.PolicyAcceptances.AnyAsync(a => a.CompanyId == cid));
    }

    [Fact]
    public async Task Guest_booking_with_tick_stores_evidence_bound_to_booking_and_hash_round_trips()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedBookingAsync(db);
        var facts = await PurchasePolicyAcceptance.LoadShopAsync(db, cid, sid);

        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "11:00", accepted: true, versionShown: facts.Version), userId: null);

        using var fresh = DbTestDatabase.TryCreateContext()!;
        var row = await fresh.PolicyAcceptances.AsNoTracking().SingleAsync(a => a.CompanyId == cid);
        Assert.Equal(created.Id, row.SourceId);
        Assert.Equal(PurchasePolicyAcceptance.ScopeShop, row.Scope);
        Assert.Equal(PurchasePolicyAcceptance.ChannelSiteBooking, row.Channel);
        Assert.Equal(facts.Version, row.PolicyVersion);
        Assert.Equal("guest-policy@example.test", row.Subject);
        Assert.True(PurchasePolicyAcceptance.Verify(row));   // control ที่ไม่มี round-trip test = ไม่มี control (กฎ #4 G)
        row.PolicyVersion = "shop-tampered";
        row.VersionShown = "shop-tampered";
        Assert.False(PurchasePolicyAcceptance.Verify(row));
    }

    [Fact]
    public async Task Stale_policy_version_from_an_open_page_is_rejected()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedBookingAsync(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "12:00", accepted: true, versionShown: "shop-000000000000"), userId: null));
        Assert.Equal(PurchasePolicyAcceptance.StaleMessage, ex.Message);
    }

    [Fact]
    public async Task Staff_booking_on_behalf_is_not_touched_by_the_tick_gate()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedBookingAsync(db);
        var created = await Svc(db).CreateBookingAsync(cid, sid, Req(svcId, "13:00", accepted: false), userId: null, staffOnBehalf: true);
        Assert.NotEqual(Guid.Empty, created.Id);
        using var fresh = DbTestDatabase.TryCreateContext()!;
        Assert.False(await fresh.PolicyAcceptances.AnyAsync(a => a.CompanyId == cid));   // ไม่แต่งหลักฐานให้คนที่ไม่ได้ติ๊ก
    }

    [Fact]
    public async Task Platform_acceptance_for_same_payment_is_reused_but_other_payment_needs_a_tick()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var company = new Company { Name = "ผู้เช่าทดสอบนโยบาย", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var legal = new LegalDocumentService(db, NullLogger<LegalDocumentService>.Instance);
        var user = Guid.NewGuid();
        var paymentA = Guid.NewGuid();
        // ผู้ซื้อทั่วไป (ไม่ใช่ SystemAdmin) ⇒ บังคับติ๊กเสมอ — ทางเข้าเดียวจาก controller
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.ToString()) }, "test"));
        var paymentB = Guid.NewGuid();

        await Assert.ThrowsAsync<BusinessRuleException>(() => PurchasePolicyAcceptance.EnforcePlatformForUserAsync(db, legal, company.Id, principal,
            PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentA, accepted: false, null, null, null));

        var row = await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(db, legal, company.Id, principal,
            PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentA, accepted: true, null, "203.0.113.9", "test-agent");
        await db.SaveChangesAsync();
        Assert.NotNull(row);
        Assert.StartsWith("terms ", row!.PolicyVersion);
        Assert.Contains(" · refund ", row.PolicyVersion);

        // แนบสลิปทีหลังโดยไม่ติ๊กซ้ำ ⇒ ผ่าน (ยอมรับฉบับนี้ไว้แล้ว) และไม่เพิ่มแถว
        var again = await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(db, legal, company.Id, principal,
            PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentA, accepted: false, null, null, null);
        Assert.Null(again);
        // ทิศตรงข้าม: รายการอื่นยังต้องติ๊ก
        await Assert.ThrowsAsync<BusinessRuleException>(() => PurchasePolicyAcceptance.EnforcePlatformForUserAsync(db, legal, company.Id, principal,
            PurchasePolicyAcceptance.ChannelPlatformSubscription, paymentB, accepted: false, null, null, null));

        using var fresh = DbTestDatabase.TryCreateContext()!;
        var stored = await fresh.PolicyAcceptances.AsNoTracking().SingleAsync(a => a.CompanyId == company.Id);
        Assert.True(PurchasePolicyAcceptance.Verify(stored));
        Assert.Equal(paymentA, stored.SourceId);
    }

    [Fact]
    public async Task Cancel_paid_plan_runs_until_period_end_then_becomes_cancelled_not_past_due()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var company = new Company { Name = "ผู้เช่ายกเลิกสิ้นรอบ", TaxId = "0105556000002" };
        var other = new Company { Name = "ผู้เช่าไม่ได้ยกเลิก", TaxId = "0105556000002" };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = company.Id, Plan = SubscriptionPlan.Pro, Status = SubscriptionStatus.Active, PricePerCycle = 990m,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(20), UsageResetDate = DateTime.UtcNow.AddDays(20),
        };
        var subOther = new Subscription
        {
            CompanyId = other.Id, Plan = SubscriptionPlan.Pro, Status = SubscriptionStatus.Active, PricePerCycle = 990m,
            StartDate = DateTime.UtcNow.AddDays(-40), EndDate = DateTime.UtcNow.AddDays(-1), UsageResetDate = DateTime.UtcNow.AddDays(-1),
        };
        db.Subscriptions.AddRange(sub, subOther);
        await db.SaveChangesAsync();

        var svc = new SubscriptionService(db, F4Proxy<INotificationService>.Create((_, _) => null));
        var msg = await svc.CancelSubscriptionAsync(company.Id, "owner");
        Assert.Contains("ใช้งานได้ตามสิทธิ์จนถึง", msg);
        var mid = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionStatus.Active, mid.Status);   // ยังใช้ได้ระหว่างรอบ
        Assert.True(mid.CancelAtPeriodEnd);

        // ถึงสิ้นรอบ
        var tracked = await db.Subscriptions.SingleAsync(s => s.Id == sub.Id);
        tracked.EndDate = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        await svc.ProcessExpiredSubscriptionsAsync();

        using var fresh = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(SubscriptionStatus.Cancelled, (await fresh.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).Status);
        // ทิศตรงข้าม: แพ็กเกจที่ไม่ได้ขอยกเลิกและหมดรอบ ⇒ PastDue ตามเดิม
        Assert.Equal(SubscriptionStatus.PastDue, (await fresh.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == subOther.Id)).Status);
    }
}
