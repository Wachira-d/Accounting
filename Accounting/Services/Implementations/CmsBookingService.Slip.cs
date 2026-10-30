using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177) — <b>สถานะสลิปบนการจอง</b> (ผู้เขียนแถว <c>SiteBooking</c> คนเดียว · ใต้ล็อกเดียวกับเส้นสถานะ/รับเงิน) ·
/// เงินจริงไม่ได้เขียนที่นี่: ร้านยืนยันสลิป ⇒ <c>BookingSlipService</c> ⇒ <c>PaymentIntentService.ApplyChargeAsync</c> ⇒ <c>SiteBookingPaymentHandler</c> ⇒
/// <c>RecordDepositPaymentAsync</c> (ล้าง "รอตรวจ" ผ่าน <see cref="BookingSlipRule.SettlesPendingSlip"/>) · ไฟล์สลิปไม่ถูกลบเมื่อปฏิเสธ (หลักฐานข้อพิพาท)
/// </summary>
public partial class CmsBookingService
{
    public async Task<BookingResponse> MarkSlipUploadedAsync(Guid companyId, Guid siteId, Guid bookingId, Guid attachmentId, Guid intentId, decimal amount)
    {
        var now = DateTime.UtcNow;
        await WithBookingSessionLockAsync(companyId, BookingStatusLockPart(bookingId), async () =>
        {
            var b = await _db.SiteBookings.Include(x => x.BookingService)
                .FirstOrDefaultAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            // ตัดสินซ้ำใต้ล็อก (สองแท็บแนบพร้อมกัน / ร้านยกเลิกระหว่างแนบ)
            var due = CmsBookingOnlinePayment.DepositDue(b.BookingService.BookingType, b.Status, b.DepositAmount, b.PaidAmount);
            if (BookingSlipRule.UploadProblem(b.Status, due, transferReady: true, slipPending: b.SlipUploadedAt != null && b.SlipAttachmentId != null, fileBytes: 1)
                is string problem)
                throw new BusinessRuleException(problem, BookingSlipRule.RuleCode, 409);
            b.SlipAttachmentId = attachmentId;
            b.SlipUploadedAt = now;
            b.SlipIntentId = intentId;
            b.SlipRejectedReason = null;
            b.SlipRejectedAt = null;
            b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes,
                $"[{BookingSlipRule.RuleCode}] ลูกค้าแนบสลิปยอด {amount:N2} เมื่อ {now.AddHours(7):dd/MM/yyyy HH:mm} — ตรวจยอดแล้วกด “ยืนยันสลิป” (บันทึกรับเงิน + ใบรับมัดจำลงวันที่รับเงิน) หรือ “ปฏิเสธสลิป”");
            b.UpdatedBy = CreditBookingLink.CustomerActorPrefix + "slip";
            await _db.SaveChangesAsync();
            return true;
        });
        await NotifyShopSlipUploadedAsync(companyId, siteId, bookingId, amount);
        return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingResponse> MarkSlipRejectedAsync(Guid companyId, Guid siteId, Guid bookingId, Guid expectedIntentId, string reason, string actor)
    {
        var now = DateTime.UtcNow;
        DateTime? holdAfter = null;
        await WithBookingSessionLockAsync(companyId, BookingStatusLockPart(bookingId), async () =>
        {
            var b = await _db.SiteBookings
                .FirstOrDefaultAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            if (BookingSlipRule.DecideProblem(b.SlipIntentId, b.SlipUploadedAt) != null || b.SlipIntentId != expectedIntentId)
                throw new BusinessRuleException("การจองนี้ไม่มีสลิปรอตรวจ (อาจถูกตรวจไปแล้ว — รีเฟรชหน้า)", BookingSlipRule.RuleCode, 409);
            b.SlipUploadedAt = null;      // ไม่ "รอตรวจ" แล้ว — ไฟล์ยังอยู่ในที่เก็บ (ผูกกับการจองนี้) เป็นหลักฐาน
            b.SlipAttachmentId = null;
            b.SlipRejectedReason = reason;
            b.SlipRejectedAt = now;
            b.PaymentHoldUntil = BookingSlipRule.HoldAfterReject(b.PaymentHoldUntil, now, CreditBookingLink.StartUtc(b.BookingDate, b.StartTime));
            holdAfter = b.PaymentHoldUntil;
            b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes,
                $"[{BookingSlipRule.RuleCode}] ปฏิเสธสลิปโดย {actor} เมื่อ {now.AddHours(7):dd/MM/yyyy HH:mm} — {reason}");
            b.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            return true;
        });
        await NotifyCustomerSlipRejectedAsync(companyId, siteId, bookingId, reason, holdAfter);
        return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
    }

    /// <summary>บรรทัดวิธีชำระท้ายข้อความยืนยันการจอง (null = ไม่มียอดค้าง) — ตัวตัดสินเดียว <see cref="BookingPaymentPolicy.NotifyLine"/></summary>
    private async Task<string?> PaymentNotifyLineAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.Status, x.DepositAmount, x.PaidAmount, x.PaymentHoldUntil, x.BookingService.BookingType })
            .FirstOrDefaultAsync();
        if (b == null) return null;
        var due = CmsBookingOnlinePayment.DepositDue(b.BookingType, b.Status, b.DepositAmount, b.PaidAmount);
        if (due <= 0.005m) return null;
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        return BookingPaymentPolicy.NotifyLine(due, b.PaymentHoldUntil, ctx.TransferReady, ctx.GatewayReady);
    }

    private async Task NotifyShopSlipUploadedAsync(Guid companyId, Guid siteId, Guid bookingId, decimal amount)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return;
            var msg = BookingSlipRule.ShopSlipUploaded(await SiteNameAsync(companyId, siteId), f.BookingNumber, f.ServiceName, BookingDisplayName(f), amount);
            var err = await _shopNotifier.NotifyShopAsync(companyId, siteId, msg, orderEvent: false);
            if (err != null) await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { CmsNotificationText.FailureNote("ร้าน", msg.Subject, err, DateTime.UtcNow) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // สลิปบันทึกแล้ว — ห้ามให้ขั้นแจ้งเตือนทำให้ลูกค้าเห็น "แนบไม่สำเร็จ" แล้วแนบซ้ำ · ล้มดังที่บันทึกระบบ
            _logger.LogError(ex, "แจ้งร้านเรื่องสลิปของการจอง {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
        }
    }

    private async Task NotifyCustomerSlipRejectedAsync(Guid companyId, Guid siteId, Guid bookingId, string reason, DateTime? holdUntil)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return;
            var msg = await WithBookingLinkAsync(companyId, siteId, f,
                BookingSlipRule.CustomerSlipRejected(await SiteNameAsync(companyId, siteId), f.BookingNumber, f.ServiceName, BookingDisplayName(f), reason, holdUntil));
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.BookingSlipRejected,
                CustomerNotifyKeys.BookingSlipRejected(bookingId, DateTime.UtcNow),
                ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId, CustomerLineTarget.ForBooking(bookingId, f.CustomerId)), msg);
            if (err != null) await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าเรื่องสลิปไม่ผ่านของการจอง {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
        }
    }
}
