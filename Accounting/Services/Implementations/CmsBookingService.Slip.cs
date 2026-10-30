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
    public Task<T> RunUnderBookingLockAsync<T>(Guid companyId, Guid bookingId, Func<Task<T>> body)
        => WithBookingSessionLockAsync(companyId, BookingStatusLockPart(bookingId), body);

    public Task AppendBookingNoteAsync(Guid companyId, Guid siteId, Guid bookingId, string note, string actor)
        => AppendBookingNotesAsync(companyId, siteId, bookingId, new List<string> { note }, actor);

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
            // ฝ่ายค้านรอบ 218 (S4): ยอดค้างเปลี่ยนระหว่างแนบ (ร้านรับเงินบางส่วน/เปลี่ยนยอด) ⇒ ยอดของรายการชำระไม่ตรง ⇒ ปฏิเสธ (ผู้เรียกปิดรายการ)
            if (Math.Abs(due - amount) > 0.005m)
                throw new BusinessRuleException(BookingSlipRule.AmountChangedMessage, BookingSlipRule.RuleCode, 409);
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

    /// <summary>ฝ่ายค้านรอบ 218 (S1): ปิดรายการชำระ manual-slip ที่ไม่ใช้แล้ว (ถูกแทน/การจองยกเลิก) ผ่าน <c>PaymentIntentService</c> ตัวเดียว ·
    /// resolve ตอนใช้ (ไม่ใช่ constructor) — intent service → handler → booking service จะเป็นวงกลม DI ถ้าฉีดตรง ·
    /// ฝ่ายค้านรอบ 2 (F6): resolve ไม่ได้/ล้ม ⇒ ล้มดัง: LogError + หมายเหตุบนการจองบอกพนักงานให้ปิดรายการเอง (ไม่ return เงียบ)</summary>
    private async Task FailSlipIntentAsync(Guid companyId, Guid siteId, Guid bookingId, Guid intentId, string rawStatus, string reason, string actor)
    {
        string? failure = null;
        try
        {
            if (_services?.GetService(typeof(Accounting.Services.Payments.IPaymentIntentService)) is not Accounting.Services.Payments.IPaymentIntentService intents)
            {
                failure = "ระบบรับชำระเงินไม่พร้อม";
                _logger.LogError("ปิดรายการชำระของสลิป {Intent} ไม่ได้ — resolve IPaymentIntentService ไม่ได้ (company {CompanyId})", intentId, companyId);
            }
            else
            {
                var intent = await intents.FindAsync(companyId, intentId);
                if (intent == null || !PaymentIntentPolicy.IsOpen(intent.Status)) return;
                await intents.ApplyChargeAsync(intentId,
                    new Accounting.Services.Payments.ProviderCharge(intent.ProviderRef ?? $"slip:{intentId:N}", PaymentIntentStatus.Failed, rawStatus, intent.Amount,
                        FailureCode: rawStatus, FailureMessage: reason),
                    PaymentEventSource.Manual, PaymentIntentPolicy.CapConfirmedBy(actor));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ปิดรายการชำระของสลิป {Intent} ไม่สำเร็จ (company {CompanyId})", intentId, companyId);
            failure = "ข้อผิดพลาดของระบบ";
        }
        if (failure != null)
            await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { BookingSlipRule.FailIntentFailedNote(intentId, failure) });
    }

    /// <summary>ฝ่ายค้านรอบ 218 (S1/S2): การจองยกเลิก/ไม่มา ขณะมีสลิปรอตรวจ ⇒ ล้างสถานะรอตรวจ (ไฟล์คงไว้) + ปิดรายการชำระของสลิป ·
    /// ลูกค้าโอนแล้วจริง ⇒ ร้านคืนเงินเอง (หมายเหตุบนการจองบอก)</summary>
    private async Task CloseSlipOnBookingClosedAsync(Guid companyId, Guid siteId, Guid bookingId, string actor)
    {
        var b = await _db.SiteBookings.FirstOrDefaultAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId);
        if (b == null || b.SlipIntentId is not Guid intentId || b.SlipUploadedAt == null) return;
        b.SlipUploadedAt = null;
        b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes,
            $"[{BookingSlipRule.RuleCode}] การจองปิดขณะมีสลิปรอตรวจ — ปิดรายการชำระของสลิป · ถ้าลูกค้าโอนแล้วจริง ให้คืนเงินที่ธนาคาร (ไฟล์สลิปเก็บไว้เป็นหลักฐาน)");
        await _db.SaveChangesAsync();
        await FailSlipIntentAsync(companyId, siteId, bookingId, intentId, BookingSlipRule.BookingClosedRaw, "การจองถูกยกเลิก/บันทึกไม่มา", actor);
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
