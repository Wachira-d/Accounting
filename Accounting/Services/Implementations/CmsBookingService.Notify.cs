using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 205 แพ็ก W3 (R-13 แจ้งลูกค้าเมื่อได้คิว · R-16 เลือก/เปลี่ยนผู้ให้บริการ) — แยกไฟล์จากเส้นเงิน/ยกเลิกใน <c>CmsBookingService.cs</c> (ทีม W2)
/// <list type="bullet">
///   <item><see cref="NotifyWaitlistPromotedAsync"/>: ส่งข้อความ "ได้คิวแล้ว" ผ่าน <see cref="IBookingNotifier"/> · ประทับ <c>WaitlistNotifiedAt</c> <b>หลังส่งถึงจริง</b> ·
///     ส่งไม่ถึง ⇒ <c>NotifyLastError</c> + ให้ <c>BookingReminderJob</c> ลองใหม่ (ไม่โยน — การเปลี่ยนสถานะที่ทำให้ได้คิวสำเร็จไปแล้ว)</item>
///   <item><see cref="AssignResourceAsync"/>: พนักงานเลือกผู้ให้บริการ (หรือให้ระบบจัดใหม่) — ตัดสินผ่าน <see cref="BookingSlotEngine.Decide"/> ตัวเดียว
///     (ว่าง · เข้างาน · รับบริการนี้ · ไม่ชนงานอื่น) ใต้ล็อกไซต์ · ไม่ข้ามด่าน</item>
/// </list>
/// </summary>
public partial class CmsBookingService
{
    /// <summary>เปลี่ยน/เลือกผู้ให้บริการได้เมื่อ: บริการใช้ผู้ให้บริการ · สถานะรอยืนยัน/ยืนยันแล้ว · นัดยังไม่เริ่ม (เวลาไทย) — server ตัดสิน หน้าเว็บแสดงปุ่มตามนี้</summary>
    internal static bool CanAssignResource(BookingResourceMode mode, BookingStatus status, DateTime bookingDate, string startHhmm)
    {
        if (mode == BookingResourceMode.None) return false;
        if (status is not (BookingStatus.Pending or BookingStatus.Confirmed)) return false;
        var start = BookingSlotEngine.ParseTime(startHhmm);
        if (start == null) return false;
        return DateOnly.FromDateTime(bookingDate).ToDateTime(start.Value) > BookingSlotEngine.BangkokNow(DateTime.UtcNow);
    }

    /// <summary>ช่องทางติดต่อของการจอง — บัญชีลูกค้า: อีเมล + LINE (ล็อกอินด้วย LINE ⇒ <c>AuthProviderId</c> = LINE userId) · แขก: อีเมล</summary>
    internal static BookingContact ContactOf(string? guestEmail, string? customerEmail, string? authProvider, string? authProviderId)
    {
        var email = !string.IsNullOrWhiteSpace(customerEmail) ? customerEmail : guestEmail;
        var line = string.Equals(authProvider, "line", StringComparison.OrdinalIgnoreCase) ? authProviderId : null;
        return new BookingContact(string.IsNullOrWhiteSpace(email) ? null : email, string.IsNullOrWhiteSpace(line) ? null : line);
    }

    /// <summary>
    /// แจ้งลูกค้าที่ถูกเลื่อนจากคิวสำรองขึ้นเป็นการจอง (R-13) · คืนข้อความถึงผู้กด (ส่งถึง/ส่งไม่ถึงพร้อมเหตุ) · ไม่โยน ·
    /// เรียกซ้ำได้ (job): ใบที่ <c>WaitlistNotifiedAt</c> แล้วถูกข้าม · ใบที่สถานะเปลี่ยนไปแล้ว (ยกเลิก/คิวสำรองอีกครั้ง) ไม่ส่ง
    /// </summary>
    public async Task<List<string>> NotifyWaitlistPromotedAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct = default)
    {
        var notices = new List<string>();
        if (bookingIds.Count == 0) return notices;
        if (_notifier == null)
        {
            notices.Add($"เลื่อนคิวสำรองขึ้น {bookingIds.Count} รายการ — ระบบส่งข้อความไม่พร้อม จะส่งซ้ำอัตโนมัติ");
            return notices;
        }
        var siteName = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
        var rows = await _db.SiteBookings
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && bookingIds.Contains(b.Id)
                && b.WaitlistPromotedAt != null && b.WaitlistNotifiedAt == null
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .Select(b => new
            {
                Booking = b, b.BookingService.Name, b.BookingService.AutoConfirm,
                CustomerEmail = b.Customer != null ? b.Customer.Email : null,
                CustomerName = b.Customer != null ? b.Customer.FullName : null,
                AuthProvider = b.Customer != null ? b.Customer.AuthProvider : null,
                AuthProviderId = b.Customer != null ? b.Customer.AuthProviderId : null,
                ResourceName = b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        foreach (var x in rows)
        {
            var b = x.Booking;
            var contact = ContactOf(b.GuestEmail, x.CustomerEmail, x.AuthProvider, x.AuthProviderId);
            var text = BookingReminderPlan.WaitlistPromotedMessage(x.CustomerName ?? b.GuestName ?? "", x.Name, siteName,
                DateOnly.FromDateTime(b.BookingDate), b.StartTime, x.ResourceName, b.BookingNumber, b.WaitlistHoldUntil,
                autoConfirmed: b.Status == BookingStatus.Confirmed);
            var result = await _notifier.SendAsync(companyId, contact, BookingReminderPlan.WaitlistPromotedSubject(x.Name, b.BookingNumber), text, ct);
            b.NotifyAttemptedAt = DateTime.UtcNow;
            if (result.Sent)
            {
                b.WaitlistNotifiedAt = DateTime.UtcNow;   // ประทับหลังส่งถึงจริงเท่านั้น (F2 #3)
                b.NotifyLastError = null;
                notices.Add($"#{b.BookingNumber} ได้คิวแล้ว — แจ้งลูกค้าทาง {(result.Channel == "line" ? "LINE" : "อีเมล")} แล้ว");
            }
            else
            {
                b.NotifyLastError = result.Error;
                notices.Add($"#{b.BookingNumber} ได้คิวแล้ว — แจ้งลูกค้าไม่สำเร็จ ({result.Error}) กรุณาติดต่อลูกค้าเอง · ระบบจะลองส่งซ้ำ");
            }
        }
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
        return notices;
    }

    /// <summary>
    /// พนักงานเลือก/เปลี่ยนผู้ให้บริการของการจอง (R-16) — <paramref name="resourceId"/> null = ให้ระบบจัดให้ใหม่ (<see cref="BookingSlotEngine.PickResource"/>
    /// ผ่าน <see cref="BookingSlotEngine.Decide"/>) · ตัดสินใต้ล็อกไซต์โดยไม่นับใบนี้เป็นคิวที่ชน · ไม่ผ่าน ⇒ <see cref="BusinessRuleException"/> ข้อความไทย
    /// (ผู้ให้บริการไม่รับบริการนี้/ไม่เข้างาน/ไม่ว่าง) · ไม่แตะเวลา/ยอด/สถานะ/เอกสาร
    /// </summary>
    public async Task<BookingResponse> AssignResourceAsync(Guid companyId, Guid siteId, Guid bookingId, Guid? resourceId, string userId)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            var svc = await _db.SiteBookingServices.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == booking.BookingServiceId && s.CompanyId == companyId && !s.IsDeleted)
                ?? throw new KeyNotFoundException("Booking service not found.");
            // รอบ 207: โหมดที่มีผลจริง (ไซต์ปิดจัดสรรผู้ให้บริการ ⇒ None) — ตัวประกอบเดียว RulesOf
            var effectiveMode = BookingSlotEngine.RulesOf(svc, await SiteDefaultsAsync(companyId, siteId)).ResourceMode;
            if (!CanAssignResource(effectiveMode, booking.Status, booking.BookingDate, booking.StartTime.ToString("HH:mm")))
                throw new BusinessRuleException(effectiveMode == BookingResourceMode.None
                    ? "บริการนี้ไม่ใช้ผู้ให้บริการ"
                    : "เปลี่ยนผู้ให้บริการได้เฉพาะการจองที่รอยืนยัน/ยืนยันแล้วและยังไม่ถึงเวลานัด", "CMS-BOOKING-ASSIGN");

            var date = DateOnly.FromDateTime(booking.BookingDate);
            var ctx = await LoadDayAsync(companyId, siteId, svc, date, excludeBookingId: booking.Id);
            if (resourceId is Guid want && ctx.Resources.All(r => r.Id != want))
                throw new BusinessRuleException("ผู้ให้บริการที่เลือกไม่รับบริการนี้หรือถูกปิดใช้", "CMS-BOOKING-ASSIGN");
            // โหมด "ระบบจัดให้" ไม่รับค่าจากลูกค้า แต่พนักงานเลือกเองได้ (ตัดสินใจของคน) ⇒ ส่งเป็น requested ให้ engine ตรวจความว่าง — ใช้กติกาแบบลูกค้าเลือก
            var rules = ctx.Rules.ResourceMode == BookingResourceMode.Auto && resourceId != null
                ? ctx.Rules with { ResourceMode = BookingResourceMode.CustomerChoice }
                : ctx.Rules;
            var decision = BookingSlotEngine.Decide(rules, date, booking.StartTime, ctx.Rows, ctx.Occupied,
                BookingSlotEngine.BangkokNow(DateTime.UtcNow), booking.GuestCount, Math.Max(1, booking.Units), ctx.Resources, resourceId, ignoreAdvance: true);
            if (!decision.Allowed || decision.Slot?.ResourceId == null)
                throw new BusinessRuleException(decision.Message ?? "ไม่มีผู้ให้บริการที่ว่างในรอบนี้", "CMS-BOOKING-ASSIGN", 409);

            var before = booking.ResourceId;
            booking.ResourceId = decision.Slot.ResourceId;
            if (before != booking.ResourceId)
            {
                var name = ctx.Resources.First(r => r.Id == booking.ResourceId).Name;
                booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes,
                    $"เปลี่ยนผู้ให้บริการเป็น {name} {BookingSlotEngine.BangkokNow(DateTime.UtcNow):dd/MM/yyyy HH:mm}");
            }
            booking.UpdatedBy = userId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
    }
}
