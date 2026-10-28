using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 206 — <b>เลื่อนนัด</b> (reschedule) การจองนัดหมาย ทั้งพนักงานและลูกค้าผ่านลิงก์โทเคน (ทะเบียน §3 W2 "ยังไม่ทำ" · คำตัดสินข้อ 165)
/// <para>หลัก: เลื่อน = <b>แถวเดิม</b> เปลี่ยนวัน/เวลา (เอกสารมัดจำ/ใบยืนยัน/เงินที่รับยังผูกอยู่ — ไม่สูญมัดจำ ไม่ออกเอกสารใหม่) ·
/// รอบใหม่ตัดสินด้วย <c>BookingSlotEngine.Decide</c> ตัวเดิมใต้ล็อกไซต์ โดย<b>ไม่นับการจองตัวเอง</b> (<c>LoadDayAsync(excludeBookingId)</c>) · ไม่ลงคิวสำรองตอนเลื่อน (เต็ม = ปฏิเสธ) ·
/// ลูกค้าเลื่อนเองได้เฉพาะในหน้าต่างยกเลิกฟรีของบริการ (ตัวตัดสินเดียว <see cref="BookingCancellationFee"/> — นอกหน้าต่าง = ติดต่อร้าน) ·
/// รอบเดิมว่าง ⇒ เลื่อนคิวสำรองของวันเดิมขึ้นเหมือนยกเลิก · เตือนก่อนนัดรีเซ็ตให้เตือนรอบใหม่</para>
/// </summary>
public partial class CmsBookingService
{
    public const string RescheduleRuleCode = "CMS-BOOKING-RESCHEDULE";

    public Task<BookingResponse> RescheduleAsync(Guid companyId, Guid siteId, Guid bookingId, RescheduleBookingRequest request, string userId)
        => RescheduleCoreAsync(companyId, siteId, bookingId, request, userId, byCustomer: false);

    public async Task<BookingResponse> RescheduleByCustomerAsync(Guid companyId, Guid siteId, string token, RescheduleBookingRequest request)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new KeyNotFoundException("Booking not found.");
        var id = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Booking not found.");
        var resp = await RescheduleCoreAsync(companyId, siteId, id, request, "customer:" + token[..Math.Min(8, token.Length)], byCustomer: true);
        return ForCustomer(resp);
    }

    private async Task<BookingResponse> RescheduleCoreAsync(Guid companyId, Guid siteId, Guid bookingId, RescheduleBookingRequest request, string actor, bool byCustomer)
    {
        var start = BookingSlotEngine.ParseTime(request.StartTime)
            ?? throw new BusinessRuleException("กรุณาเลือกเวลาจากรายการเวลาว่าง", RescheduleRuleCode);
        var newDate = DateOnly.FromDateTime(request.BookingDate);
        var now = DateTime.UtcNow;

        DateTime oldDate; TimeOnly oldStart; string bookingNumber; Guid? erpDocId; string changeNote;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            var booking = await _db.SiteBookings.Include(b => b.BookingService)
                .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            var svc = booking.BookingService;
            if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
                throw new BusinessRuleException($"เลื่อนนัดได้เฉพาะการจองที่รอยืนยัน/ยืนยันแล้ว (สถานะปัจจุบัน {booking.Status})", RescheduleRuleCode, 409);
            var currentStartUtc = CreditBookingLink.StartUtc(booking.BookingDate, booking.StartTime);
            if (byCustomer)
            {
                if (currentStartUtc <= now)
                    throw new BusinessRuleException("เลยเวลานัดแล้ว — เลื่อนออนไลน์ไม่ได้ กรุณาติดต่อร้าน", RescheduleRuleCode, 409);
                // หน้าต่างเดียวกับยกเลิกฟรี (ตัวตัดสินเดียว) — นอกหน้าต่าง = ติดต่อร้าน (ร้านตัดสินค่าธรรมเนียม/ยกเว้นเอง)
                var plan = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, now, currentStartUtc,
                    svc.FreeCancellationHours, svc.CancellationFeePercent, booking.TotalAmount, booking.PaidAmount);
                if (!BookingCancellationFee.CanCustomerReschedule(plan))
                    throw new BusinessRuleException(BookingCancellationFee.RescheduleBlockedNote(svc.FreeCancellationHours), RescheduleRuleCode, 409);
            }
            if (DateOnly.FromDateTime(booking.BookingDate) == newDate && booking.StartTime == start)
                throw new BusinessRuleException("วันและเวลาเดิม — ไม่มีอะไรเปลี่ยน", RescheduleRuleCode);

            var ctx = await LoadDayAsync(companyId, siteId, svc, newDate, excludeBookingId: booking.Id);
            var requested = svc.ResourceMode == BookingResourceMode.Auto ? null : booking.ResourceId;
            var decision = BookingSlotEngine.Decide(ctx.Rules, newDate, start, ctx.Rows, ctx.Occupied,
                BookingSlotEngine.BangkokNow(now), booking.GuestCount, Math.Max(1, booking.Units), ctx.Resources, requested,
                ignoreAdvance: !byCustomer);
            if (!decision.Allowed || decision.Slot == null)
                throw new BusinessRuleException(decision.Message ?? "รอบที่เลือกไม่ว่าง — เลือกเวลาอื่น", RescheduleRuleCode,
                    decision.Reason == BookingSlotRejection.Full ? 409 : 400);
            var slot = decision.Slot;

            oldDate = booking.BookingDate; oldStart = booking.StartTime; bookingNumber = booking.BookingNumber; erpDocId = booking.ErpDocumentId;
            changeNote = $"[{RescheduleRuleCode}] เลื่อนนัดจาก {oldDate:dd/MM/yyyy} {oldStart:HH\\:mm} → {newDate:dd/MM/yyyy} {slot.Start:HH\\:mm} "
                + $"โดย {(byCustomer ? "ลูกค้า (ลิงก์)" : actor)} {ThaiDate.CalendarDateUtc(now):dd/MM/yyyy}"
                + (string.IsNullOrWhiteSpace(request.Reason) ? "" : $" · เหตุผล: {request.Reason.Trim()}");
            booking.BookingDate = newDate.ToDateTime(TimeOnly.MinValue);
            booking.StartTime = slot.Start; booking.EndTime = slot.BookedEnd; booking.DurationMinutes = slot.BookedMinutes;
            booking.ResourceId = slot.ResourceId ?? (svc.ResourceMode == BookingResourceMode.None ? null : booking.ResourceId);
            // เตือนก่อนนัดรอบเดิมไม่มีความหมายแล้ว ⇒ ให้ job เตือนรอบใหม่ (BookingReminderPlan อ่าน ReminderSentAt)
            booking.ReminderSent = false; booking.ReminderSentAt = null; booking.NotifyAttemptedAt = null; booking.NotifyLastError = null;
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, changeNote);
            booking.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        // เอกสารที่ผูกอยู่ (บรรทัดบรรยายวัน/เวลาเดิม) — จดหมายเหตุ ไม่แก้บรรทัดของใบที่ออกแล้ว (§86/4 ห้ามแก้ย้อนหลัง)
        if (erpDocId is Guid did)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == did && d.CompanyId == companyId);
            if (doc != null)
            {
                doc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(doc.InternalNotes, $"การจอง {bookingNumber}: {changeNote}");
                await _db.SaveChangesAsync();
            }
        }

        // รอบเดิมว่าง ⇒ เลื่อนคิวสำรองของวันเดิม (เส้นเดียวกับยกเลิก) + แจ้งคนได้คิว
        var notices = new List<string>();
        var promoted = await PromoteWaitlistAsync(companyId, siteId, oldDate, actor);
        foreach (var pid in promoted) await SyncIfBillableAsync(companyId, siteId, pid);
        if (promoted.Count > 0) notices.AddRange(await NotifyWaitlistPromotedAsync(companyId, siteId, promoted));

        var result = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
        result.ErpNotices = notices;
        return result;
    }
}
