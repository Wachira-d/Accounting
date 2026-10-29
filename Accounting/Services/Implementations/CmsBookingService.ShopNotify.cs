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
        string? AuthProvider, string? AuthProviderId, string? ResourceName);

    private Task<BookingNotifyFacts?> LoadBookingNotifyFactsAsync(Guid companyId, Guid siteId, Guid bookingId)
        => _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            .Select(b => new BookingNotifyFacts(b.BookingNumber, b.BookingService.Name, b.BookingDate, b.StartTime, b.Status,
                b.GuestCount, b.GuestName, b.GuestEmail, b.GuestPhone, b.CancellationReason,
                b.Customer != null ? b.Customer.FullName : null, b.Customer != null ? b.Customer.Email : null,
                b.Customer != null ? b.Customer.AuthProvider : null, b.Customer != null ? b.Customer.AuthProviderId : null,
                b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault()))
            .FirstOrDefaultAsync();

    private async Task<string> SiteNameAsync(Guid companyId, Guid siteId)
        => await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync() ?? "";

    private static string? BookingDisplayName(BookingNotifyFacts f) => !string.IsNullOrWhiteSpace(f.CustomerName) ? f.CustomerName : f.GuestName;

    /// <summary>การจองใหม่: ยืนยันถึงลูกค้า + แจ้งร้าน (คิวสำรองบอกว่าเป็นคิวสำรอง)</summary>
    private async Task NotifyBookingCreatedAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return;
            var site = await SiteNameAsync(companyId, siteId);
            var date = DateOnly.FromDateTime(f.BookingDate);
            var now = DateTime.UtcNow;
            var failures = new List<string>();

            var customer = CmsNotificationText.CustomerBookingConfirmation(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f), date, f.StartTime, f.ResourceName, f.Status);
            var custErr = await _shopNotifier.NotifyCustomerAsync(companyId, ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId), customer);
            if (custErr != null) failures.Add(CmsNotificationText.FailureNote("ลูกค้า", customer.Subject, custErr, now));

            var shop = CmsNotificationText.ShopNewBooking(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f), f.GuestPhone, date, f.StartTime,
                f.GuestCount, waitlisted: f.Status == BookingStatus.Waitlisted);
            var shopErr = await _shopNotifier.NotifyShopAsync(companyId, siteId, shop, orderEvent: false);
            if (shopErr != null) failures.Add(CmsNotificationText.FailureNote("ร้าน", shop.Subject, shopErr, now));

            await StampBookingNotesAsync(companyId, siteId, bookingId, failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // การจองบันทึกแล้ว — ห้ามให้ขั้นแจ้งเตือนทำให้แขกเห็น "จองไม่สำเร็จ" แล้วจองซ้ำ (B-3) · ล้มดังที่บันทึกระบบระดับ Error
            _logger.LogError(ex, "แจ้งเตือนการจองใหม่ {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
        }
    }

    /// <summary>ร้าน/ระบบยกเลิก ⇒ แจ้งลูกค้า · คืนข้อความถึงผู้กดเมื่อส่งไม่ถึง (null = ส่งถึง/ไม่มีตัวส่ง)</summary>
    private async Task<string?> NotifyBookingCancelledByShopAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        if (_shopNotifier == null) return null;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return null;
            var site = await SiteNameAsync(companyId, siteId);
            var msg = CmsNotificationText.CustomerBookingCancelledByShop(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f),
                DateOnly.FromDateTime(f.BookingDate), f.StartTime, f.CancellationReason);
            var err = await _shopNotifier.NotifyCustomerAsync(companyId, ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId), msg);
            if (err == null) return null;
            var note = CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow);
            await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { note });
            return note;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าเรื่องยกเลิกการจอง {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
            return $"{CmsNotificationText.FailureTag} แจ้งลูกค้าเรื่องยกเลิกไม่สำเร็จ (ข้อผิดพลาดภายใน) — กรุณาติดต่อลูกค้าเอง";
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
