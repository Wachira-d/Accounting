using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 P3 (R-209-C4/E2 · คำตัดสินข้อ 168 ซ) — แจ้งเตือนการจองผ่าน <see cref="Interfaces.ICmsShopNotifier"/> (ช่องเดิม <c>IBookingNotifier</c>):
/// ยืนยันการจองถึงลูกค้า + แจ้งร้านเมื่อมีการจองใหม่ · แจ้งลูกค้าเมื่อร้าน/ระบบยกเลิก (ลูกค้ายกเลิกเองผ่านลิงก์ = ไม่แจ้งซ้ำ) ·
/// เรียกหลัง commit · ส่งไม่ถึง ⇒ ประทับ <see cref="CmsNotificationText.FailureTag"/> บนหมายเหตุภายในของการจอง (ไม่ล้มการจอง · ไม่เงียบ — F2 #7) ·
/// แยกจากเส้น "ได้คิว" (<c>Notify.cs</c> — ประทับ <c>WaitlistNotifiedAt</c> + job ส่งซ้ำ) ซึ่งไม่แตะ
/// </summary>
public partial class CmsBookingService
{
    private sealed record BookingNotifyFacts(string BookingNumber, string ServiceName, DateTime BookingDate, TimeOnly StartTime, BookingStatus Status,
        int GuestCount, string? GuestName, string? GuestEmail, string? GuestPhone, string? CancellationReason, string? CustomerName, string? CustomerEmail,
        string? AuthProvider, string? AuthProviderId, string? ResourceName, string? PublicToken, Guid? CustomerId);

    private Task<BookingNotifyFacts?> LoadBookingNotifyFactsAsync(Guid companyId, Guid siteId, Guid bookingId)
        => _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            .Select(b => new BookingNotifyFacts(b.BookingNumber, b.BookingService.Name, b.BookingDate, b.StartTime, b.Status,
                b.GuestCount, b.GuestName, b.GuestEmail, b.GuestPhone, b.CancellationReason,
                b.Customer != null ? b.Customer.FullName : null, b.Customer != null ? b.Customer.Email : null,
                b.Customer != null ? b.Customer.AuthProvider : null, b.Customer != null ? b.Customer.AuthProviderId : null,
                b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
                b.PublicToken, b.CustomerId))
            .FirstOrDefaultAsync();

    /// <summary>รอบ 210: บรรทัด "ดู/จัดการการจอง" ท้ายข้อความถึงลูกค้า — ลิงก์ my-booking จากฐานหน้าร้าน (<see cref="CmsStorefrontLink"/>) ·
    /// ประกอบไม่ได้ ⇒ เลขที่ + หน้า "ตรวจสอบการจอง / คำสั่งซื้อ" (ไม่เงียบ)</summary>
    private async Task<CmsNotification> WithBookingLinkAsync(Guid companyId, Guid siteId, BookingNotifyFacts f, CmsNotification message)
    {
        var baseUrl = _shopNotifier == null ? null : await _shopNotifier.StorefrontBaseUrlAsync(companyId, siteId);
        return CmsNotificationText.WithLine(message, CmsStorefrontLink.BookingManageLine(baseUrl, f.PublicToken, f.BookingNumber));
    }

    private async Task<string> SiteNameAsync(Guid companyId, Guid siteId)
        => await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync() ?? "";

    private static string? BookingDisplayName(BookingNotifyFacts f) => !string.IsNullOrWhiteSpace(f.CustomerName) ? f.CustomerName : f.GuestName;

    /// <summary>รอบ 217 (ข้อ 175 ง): จุดเข้าของคิวแจ้งเตือนตามหลัง (<c>DeferredNotifyDispatcher</c>) — เดิม CreateBookingAsync เรียก
    /// <see cref="NotifyBookingCreatedAsync"/> ในคำขอ ⇒ "กดยืนยันจองแล้วนิ่งไปพักนึง" (รอ SMTP/LINE ถึงลูกค้า + อีเมลร้านทีละผู้รับ) ·
    /// ฝ่ายค้าน S4: ถึงคิวแล้วการจองถูกยกเลิก/ไม่มา/ถูกลบ ⇒ ไม่ส่ง "ยืนยันการจอง"/"การจองใหม่" — คืนเหตุ (dispatcher ปิดแถวพร้อมหมายเหตุ) ·
    /// ฝ่ายค้าน S3: ข้อยกเว้นของโครงสร้าง (ฐานข้อมูล ฯลฯ) <b>โยนออก</b> ให้ dispatcher ถอยแล้วลองใหม่ (ผู้รับที่ส่งไม่ถึงยังประทับรายคนตามเดิม)</summary>
    public async Task<string?> DispatchCreatedNotifyAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var status = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            .Select(b => (BookingStatus?)b.Status).FirstOrDefaultAsync();
        if (DeferredNotifyPolicy.BookingSkipReason(status) is string skip) return skip;
        await NotifyBookingCreatedAsync(companyId, siteId, bookingId);
        return null;
    }

    /// <summary>การจองใหม่: ยืนยันถึงลูกค้า + แจ้งร้าน (คิวสำรองบอกว่าเป็นคิวสำรอง)</summary>
    private async Task NotifyBookingCreatedAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        if (_shopNotifier == null) return;
        // รอบ 217 ฝ่ายค้าน S3: ไม่กลืนข้อยกเว้นแล้ว — ผู้เรียกเดียวคือคิวแจ้งเตือนตามหลัง (ถอย/ลองใหม่ + LastError) · การจองบันทึกไปก่อนแล้ว
        // ⇒ ไม่มีทางทำให้แขกเห็น "จองไม่สำเร็จ" · ผู้รับที่ส่งไม่ถึง (ผลจากตัวส่ง ไม่ใช่ข้อยกเว้น) ยังประทับบนการจองเหมือนเดิม
        var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
        if (f == null) return;
        var site = await SiteNameAsync(companyId, siteId);
        var date = DateOnly.FromDateTime(f.BookingDate);
        var now = DateTime.UtcNow;
        var failures = new List<string>();

        var (customer, custErr) = await SendBookingConfirmedToCustomerAsync(companyId, siteId, bookingId, f, site);
        if (custErr != null) failures.Add(CmsNotificationText.FailureNote("ลูกค้า", customer.Subject, custErr, now));

        var shop = CmsNotificationText.ShopNewBooking(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f), f.GuestPhone, date, f.StartTime,
            f.GuestCount, waitlisted: f.Status == BookingStatus.Waitlisted);
        var shopErr = await _shopNotifier.NotifyShopAsync(companyId, siteId, shop, orderEvent: false);
        if (shopErr != null) failures.Add(CmsNotificationText.FailureNote("ร้าน", shop.Subject, shopErr, now));

        await StampBookingNotesAsync(companyId, siteId, bookingId, failures);
    }

    /// <summary>ยืนยันการจองถึงลูกค้า (ส่วนลูกค้าของ <see cref="NotifyBookingCreatedAsync"/>) — แยกออกมาให้ "ส่งซ้ำ" (รอบ 213) เดินเส้นเดียวกัน
    /// โดยไม่แจ้งร้านซ้ำ · คีย์กันซ้ำเดิม ⇒ ส่งซ้ำรับช่วงแถวบันทึกเดิม</summary>
    private async Task<(CmsNotification Message, string? Error)> SendBookingConfirmedToCustomerAsync(Guid companyId, Guid siteId, Guid bookingId, BookingNotifyFacts f, string site)
    {
        var customer = await WithBookingLinkAsync(companyId, siteId, f,
            CmsNotificationText.CustomerBookingConfirmation(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f), DateOnly.FromDateTime(f.BookingDate), f.StartTime, f.ResourceName, f.Status));
        var err = await _shopNotifier!.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.BookingConfirmed, CustomerNotifyKeys.Booking(bookingId, CustomerNotifyEvents.BookingConfirmed), ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId, CustomerLineTarget.ForBooking(bookingId, f.CustomerId)), customer);
        return (customer, err);
    }

    /// <summary>รอบ 213 (คำตัดสินข้อ 172): ส่งซ้ำข้อความถึงลูกค้าที่ค้างเพราะโควตา — ประกอบใหม่จาก<b>สถานะปัจจุบัน</b>ของการจองผ่านเส้นส่งเดิม
    /// (ไม่เก็บเนื้อข้อความ) · ผู้ตัดสินว่าส่งซ้ำได้ไหม = <see cref="CustomerNotifyResend.Evaluate"/> ที่ผู้เรียก · คืนเหตุเมื่อส่งไม่ถึง (null = ส่งถึง/ไม่มีตัวส่ง)</summary>
    public async Task<string?> ResendCustomerNotifyAsync(Guid companyId, Guid bookingId, string eventKey, string messageKey)
    {
        if (_shopNotifier == null) return "ระบบส่งข้อความไม่พร้อม";
        var siteId = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.CompanyId == companyId).Select(b => (Guid?)b.SiteId).FirstOrDefaultAsync();
        if (siteId is not Guid sid) return "ไม่พบการจอง";
        switch (eventKey)
        {
            case CustomerNotifyEvents.BookingConfirmed:
            {
                var f = await LoadBookingNotifyFactsAsync(companyId, sid, bookingId);
                if (f == null) return "ไม่พบการจอง";
                var (_, err) = await SendBookingConfirmedToCustomerAsync(companyId, sid, bookingId, f, await SiteNameAsync(companyId, sid));
                return err;
            }
            case CustomerNotifyEvents.BookingCancelled:
                return await NotifyBookingCancelledByShopAsync(companyId, sid, bookingId, stampNotes: false);
            case CustomerNotifyEvents.BookingRescheduled:
                // วัน-เวลาเดิมอ่านกลับจากคีย์ (ตัวตั้งเดียวของรูปคีย์) ⇒ คีย์ที่ประกอบใหม่ = คีย์เดิมทุกตัวอักษร
                return CustomerNotifyKeys.TryParseRescheduled(messageKey, out var oldDate, out var oldStart)
                    ? await NotifyBookingRescheduledAsync(companyId, sid, bookingId, oldDate, oldStart, stampNotes: false)
                    : "อ่านวัน-เวลาเดิมจากบันทึกไม่ได้";
            default:
                return CustomerNotifyResend.UnsupportedReason(eventKey);
        }
    }

    /// <summary>ร้าน/ระบบยกเลิก ⇒ แจ้งลูกค้า · คืนข้อความถึงผู้กดเมื่อส่งไม่ถึง (null = ส่งถึง/ไม่มีตัวส่ง)</summary>
    private async Task<string?> NotifyBookingCancelledByShopAsync(Guid companyId, Guid siteId, Guid bookingId, bool stampNotes = true)
    {
        if (_shopNotifier == null) return null;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return null;
            var site = await SiteNameAsync(companyId, siteId);
            var msg = await WithBookingLinkAsync(companyId, siteId, f,
                CmsNotificationText.CustomerBookingCancelledByShop(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f),
                    DateOnly.FromDateTime(f.BookingDate), f.StartTime, f.CancellationReason));
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.BookingCancelled, CustomerNotifyKeys.Booking(bookingId, CustomerNotifyEvents.BookingCancelled), ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId, CustomerLineTarget.ForBooking(bookingId, f.CustomerId)), msg);
            if (err == null) return null;
            var note = CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow);
            // ฝ่ายค้านรอบ 213 (a): ส่งซ้ำจากหน้าข้อความค้าง ⇒ ผลอยู่ที่หน้านั้น ไม่ต่อหมายเหตุบนการจองทุกครั้งที่ลอง
            if (stampNotes) await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { note });
            return note;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าเรื่องยกเลิกการจอง {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
            return $"{CmsNotificationText.FailureTag} แจ้งลูกค้าเรื่องยกเลิกไม่สำเร็จ (ข้อผิดพลาดภายใน) — กรุณาติดต่อลูกค้าเอง";
        }
    }

    /// <summary>รอบ 210: เลื่อนนัดแล้ว ⇒ แจ้งวัน-เวลาใหม่ + ลิงก์จัดการการจองถึงลูกค้า · คืนข้อความถึงผู้กดเมื่อส่งไม่ถึง (null = ส่งถึง/ไม่มีตัวส่ง) · ไม่โยน</summary>
    private async Task<string?> NotifyBookingRescheduledAsync(Guid companyId, Guid siteId, Guid bookingId, DateTime oldDate, TimeOnly oldStart, bool stampNotes = true)
    {
        if (_shopNotifier == null) return null;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return null;
            var site = await SiteNameAsync(companyId, siteId);
            var msg = await WithBookingLinkAsync(companyId, siteId, f,
                CmsNotificationText.CustomerBookingRescheduled(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f),
                    DateOnly.FromDateTime(oldDate), oldStart, DateOnly.FromDateTime(f.BookingDate), f.StartTime));
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.BookingRescheduled, CustomerNotifyKeys.BookingRescheduled(bookingId, oldDate, oldStart), ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId, CustomerLineTarget.ForBooking(bookingId, f.CustomerId)), msg);
            if (err == null) return null;
            var note = CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow);
            // ฝ่ายค้านรอบ 213 (a): ส่งซ้ำจากหน้าข้อความค้าง ⇒ ผลอยู่ที่หน้านั้น ไม่ต่อหมายเหตุบนการจองทุกครั้งที่ลอง
            if (stampNotes) await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { note });
            return note;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าเรื่องเลื่อนนัด {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
            return $"{CmsNotificationText.FailureTag} แจ้งลูกค้าเรื่องเลื่อนนัดไม่สำเร็จ (ข้อผิดพลาดภายใน) — กรุณาติดต่อลูกค้าเอง";
        }
    }

    private async Task StampBookingNotesAsync(Guid companyId, Guid siteId, Guid bookingId, IReadOnlyCollection<string> notes)
    {
        if (notes.Count == 0) return;
        var b = await _db.SiteBookings.FirstOrDefaultAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId);
        if (b == null) return;
        foreach (var n in notes) b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes, n);
        await _db.SaveChangesAsync();
    }
}
