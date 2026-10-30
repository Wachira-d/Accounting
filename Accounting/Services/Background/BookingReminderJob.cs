using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// งานเบื้องหลังของเครื่องจองคิว CMS (รอบ 205 แพ็ก W3 · R-12 R-13 · <c>docs/BACKLOG_REGISTER_2026-10-26.md</c>) — รอบละ 5 นาที · เดินทีละเครื่องผ่าน
/// <see cref="JobLock"/> (F4 #6) · ตัวตัดสินทุกข้อเป็น pure ใน <see cref="BookingReminderPlan"/>:
/// <list type="number">
///   <item><b>เตือนก่อนนัด</b> — คอลัมน์ <c>ReminderSent/ReminderSentAt</c> มีมาตั้งแต่ต้นแต่ไม่มีผู้เขียน (ทะเบียน R-12 · F2 #2) · กี่ชั่วโมงก่อน + เทมเพลต ตั้งต่อเว็บ
///     (<c>SiteBookingSettings</c>) · ประทับ <c>ReminderSentAt</c> <b>หลังส่งถึงจริง</b>เท่านั้น (F2 #3) · ส่งล้ม ⇒ <c>NotifyLastError</c> + ลองใหม่หลัง 30 นาที · ไม่ล้มทั้งงาน</item>
///   <item><b>แจ้งได้คิวซ้ำ</b> — ใบที่เลื่อนจากคิวสำรองแล้วแจ้งลูกค้าไม่ถึง (<c>WaitlistNotifiedAt</c> null) ส่งซ้ำผ่าน <c>CmsBookingService.NotifyWaitlistPromotedAsync</c></item>
///   <item><b>ปล่อยรอบที่ถือไว้</b> — ได้คิวแล้วไม่ยืนยันภายใน <c>WaitlistHoldMinutes</c> ⇒ ยกเลิกผ่านเส้นสถานะปกติ (<c>UpdateBookingStatusAsync</c> · เอกสาร ERP/คิวถัดไป
///     เดินด่านเดียวกับพนักงานกดยกเลิก — ไม่เขียนสถานะตรง)</item>
/// </list>
/// ขอบเขตการสแกน = การจองในหน้าต่างวันที่ (เมื่อวาน … วันนี้ + ชั่วโมงเตือนสูงสุด) ⇒ ไม่กวาดทั้งฐาน · ทุก query มี CompanyId ติดแถว (ใบเป็นของบริษัทไหน เขียนกลับใบนั้น)
/// </summary>
public class BookingReminderJob : BackgroundService
{
    public const string JobName = "BookingReminder";
    public const string SystemUser = "system:booking-reminder-job";
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingReminderJob> _logger;

    public BookingReminderJob(IServiceScopeFactory scopeFactory, ILogger<BookingReminderJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "BookingReminderJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสองเครื่องส่งข้อความถึงลูกค้าซ้ำ (try ไม่ wait — อีกเครื่องทำอยู่ = ข้ามรอบ)</summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(BookingReminderJob), () => RunOnceAsync(ct), _logger, ct: ct);
    }

    /// <summary>หนึ่งรอบ (เรียกตรงได้จากเทสต์ Db) · คืน (เตือนส่งถึง, เตือนส่งล้ม, แจ้งได้คิวที่ลองซ้ำ, รอบที่ปล่อย)</summary>
    public async Task<(int RemindersSent, int RemindersFailed, int WaitlistRetried, int HoldsReleased)> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var notifier = scope.ServiceProvider.GetService<IBookingNotifier>();
        var bookings = scope.ServiceProvider.GetRequiredService<ICmsBookingService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var started = DateTime.UtcNow;

        var result = await ProcessAsync(db, notifier, bookings, DateTime.UtcNow, ct);

        if (recorder != null)
            await recorder.RecordAsync(JobName, true,
                $"เตือนส่งถึง {result.RemindersSent} · ส่งล้ม {result.RemindersFailed} · แจ้งได้คิวซ้ำ {result.WaitlistRetried} · ปล่อยรอบ {result.HoldsReleased}",
                result.RemindersSent + result.WaitlistRetried + result.HoldsReleased, started);
        return result;
    }

    /// <summary>ตัวงานจริง — รับ DbContext/ตัวส่ง/เวลาจากผู้เรียก (เทสต์ Db ป้อนตัวส่งปลอม + เวลา)</summary>
    public async Task<(int RemindersSent, int RemindersFailed, int WaitlistRetried, int HoldsReleased)> ProcessAsync(
        AccountingDbContext db, IBookingNotifier? notifier, ICmsBookingService bookings, DateTime nowUtc, CancellationToken ct,
        Guid? onlyCompanyId = null)   // เทสต์ Db บนฐานร่วม: จำกัดบริษัท (null = ทุกบริษัท — เส้นจริง)
    {
        var nowLocal = BookingSlotEngine.BangkokNow(nowUtc);
        var today = DateOnly.FromDateTime(nowLocal);
        int sent = 0, failed = 0, retried = 0, released = 0;

        // ── 1. เตือนก่อนนัด ──
        if (notifier != null)
        {
            var settings = await db.SiteBookingSettings.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync(ct);
            var bySite = settings.GroupBy(x => x.SiteId).ToDictionary(g => g.Key, g => g.First());
            var maxHours = settings.Count == 0 ? BookingReminderPlan.ParseHours(null).Max()
                : settings.Select(x => BookingReminderPlan.ParseHours(x.ReminderHoursBefore).Max()).DefaultIfEmpty(24).Max();
            var from = today.AddDays(-1).ToDateTime(TimeOnly.MinValue);
            var to = today.AddDays((int)Math.Ceiling(maxHours / 24.0) + 1).ToDateTime(TimeOnly.MinValue);
            var rows = await db.SiteBookings
                .Where(b => onlyCompanyId == null || b.CompanyId == onlyCompanyId)
                .Where(b => !b.IsDeleted && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
                    && b.BookingDate >= from && b.BookingDate < to)
                .Select(b => new
                {
                    Booking = b, ServiceName = b.BookingService.Name, SiteName = b.Site.Name,
                    CustomerEmail = b.Customer != null ? b.Customer.Email : null,
                    CustomerName = b.Customer != null ? b.Customer.FullName : null,
                    AuthProvider = b.Customer != null ? b.Customer.AuthProvider : null,
                    AuthProviderId = b.Customer != null ? b.Customer.AuthProviderId : null,
                    ResourceName = b.ResourceId == null ? null
                        : db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == b.CompanyId).Select(r => r.Name).FirstOrDefault(),
                })
                .ToListAsync(ct);
            foreach (var x in rows)
            {
                if (ct.IsCancellationRequested) break;
                var b = x.Booking;
                var cfg = bySite.GetValueOrDefault(b.SiteId);
                if (cfg != null && !cfg.ReminderEnabled) continue;
                if (!BookingReminderPlan.NeedsReminder(b.Status)) continue;
                var date = DateOnly.FromDateTime(b.BookingDate);
                var stage = BookingReminderPlan.DueStage(BookingReminderPlan.ParseHours(cfg?.ReminderHoursBefore),
                    date.ToDateTime(b.StartTime), nowLocal, BookingSlotEngine.BangkokNow(b.CreatedAt),
                    b.ReminderSentAt is DateTime s ? BookingSlotEngine.BangkokNow(s) : null);
                if (stage == null) continue;
                if (!BookingReminderPlan.MayAttempt(b.NotifyAttemptedAt, nowUtc)) continue;
                try
                {
                    var contact = Implementations.CmsBookingService.ContactOf(b.GuestEmail, x.CustomerEmail, x.AuthProvider, x.AuthProviderId,
                        Accounting.Helpers.CustomerLineTarget.ForBooking(b.Id, b.CustomerId));
                    var text = BookingReminderPlan.Render(cfg?.ReminderTemplate, x.CustomerName ?? b.GuestName ?? "", x.ServiceName, x.SiteName,
                        date, b.StartTime, x.ResourceName, b.BookingNumber);
                    var r = await notifier.SendAsync(b.CompanyId, contact, BookingReminderPlan.ReminderSubject(stage.Value, x.ServiceName, b.BookingNumber), text, ct);
                    b.NotifyAttemptedAt = nowUtc;
                    if (r.Sent)
                    {
                        b.ReminderSent = true;
                        b.ReminderSentAt = nowUtc;   // หลังส่งถึงจริงเท่านั้น
                        b.NotifyLastError = null;
                        sent++;
                    }
                    else { b.NotifyLastError = r.Error; failed++; }
                    await db.SaveChangesAsync(ct);   // ต่อแถว — ล้มกลางทางไม่เสียของที่ส่งแล้ว (ไม่ส่งซ้ำ)
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogError(ex, "เตือนนัดหมาย #{BookingNumber} (company {CompanyId}) ล้ม — แถวถัดไปเดินต่อ", b.BookingNumber, b.CompanyId);
                    db.ChangeTracker.Clear();
                }
            }
            db.ChangeTracker.Clear();
        }

        // ── 2. แจ้งได้คิวซ้ำ (ส่งไม่ถึงตอนเลื่อนขึ้น) ──
        var retryFrom = today.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var pendingNotify = await db.SiteBookings.AsNoTracking()
            .Where(b => onlyCompanyId == null || b.CompanyId == onlyCompanyId)
            .Where(b => !b.IsDeleted && b.WaitlistPromotedAt != null && b.WaitlistNotifiedAt == null
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed) && b.BookingDate >= retryFrom)
            .Select(b => new { b.CompanyId, b.SiteId, b.Id, b.NotifyAttemptedAt })
            .ToListAsync(ct);
        foreach (var g in pendingNotify.Where(b => BookingReminderPlan.MayAttempt(b.NotifyAttemptedAt, nowUtc)).GroupBy(b => (b.CompanyId, b.SiteId)))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var notices = await bookings.NotifyWaitlistPromotedAsync(g.Key.CompanyId, g.Key.SiteId, g.Select(b => b.Id).ToList(), ct);
                retried += notices.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "แจ้งได้คิวซ้ำ (site {SiteId}) ล้ม", g.Key.SiteId);
                db.ChangeTracker.Clear();
            }
        }

        // ── 3. ปล่อยรอบที่ถือไว้แล้วไม่ยืนยัน ──
        var expired = await db.SiteBookings.AsNoTracking()
            .Where(b => onlyCompanyId == null || b.CompanyId == onlyCompanyId)
            // รอบ 208 (ฝ่ายค้าน W2 ข้อ 2): ใบที่รับเงินแล้ว (มัดจำบางส่วน) ไม่ปล่อยเอง — ยกเลิกจะ void ใบร่างทั้งที่ถือเงินลูกค้าอยู่ ⇒ ให้คนตัดสิน
            .Where(b => !b.IsDeleted && b.Status == BookingStatus.Pending && b.WaitlistHoldUntil != null && b.WaitlistHoldUntil <= nowUtc
                && b.PaidAmount <= 0m)
            .Select(b => new { b.CompanyId, b.SiteId, b.Id, b.BookingNumber, b.Status, b.WaitlistHoldUntil })
            .ToListAsync(ct);
        foreach (var e in expired)
        {
            if (ct.IsCancellationRequested) break;
            if (!BookingReminderPlan.HoldExpired(e.Status, e.WaitlistHoldUntil, nowUtc)) continue;
            try
            {
                await bookings.UpdateBookingStatusAsync(e.CompanyId, e.SiteId, e.Id,
                    new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = BookingReminderPlan.HoldExpiredReason,
                        ExpectedCurrentStatus = BookingStatus.Pending, HoldLapsedAsOfUtc = nowUtc }, SystemUser);   // ลูกค้าจ่าย/ยืนยันระหว่างทาง ⇒ เส้นสถานะปฏิเสธใต้ล็อก
                released++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ปล่อยรอบคิวสำรอง #{BookingNumber} (company {CompanyId}) ล้ม", e.BookingNumber, e.CompanyId);
                db.ChangeTracker.Clear();
            }
        }
        return (sent, failed, retried, released);
    }
}
