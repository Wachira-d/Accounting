using Accounting.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 210 (คำตัดสินข้อ 169) — แขกที่ไม่ได้ล็อกอินกลับมาดูการจอง/คำสั่งซื้อด้วย <b>เลขที่ + เบอร์โทรหรืออีเมล</b> ·
/// ค้นเฉพาะบริษัท + เว็บ + เลขที่ (กฎ M) แล้วให้ตัวตัดสินเดียว <see cref="GuestRecordLookup.Match"/> เทียบข้อมูลติดต่อแบบตรงตัว (ไม่ fuzzy) ·
/// คืนเฉพาะโทเคนสาธารณะ (หน้า my-booking / track-order มี DTO จำกัดฟิลด์อยู่แล้ว) · จำกัดความถี่อยู่ที่ controller
/// </summary>
public partial class StorefrontCustomerService
{
    private const int LookupCandidateCap = 5;

    public async Task<string?> LookupBookingTokenAsync(Guid companyId, Guid siteId, string? bookingNumber, string? contact)
    {
        if (!GuestRecordLookup.IsValidRequest(bookingNumber, contact)) return null;
        var n = GuestRecordLookup.NormalizeNumber(bookingNumber)!;
        var rows = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.BookingNumber.ToUpper() == n)
            .Take(LookupCandidateCap)
            .Select(b => new
            {
                b.CompanyId, b.SiteId, b.BookingNumber, b.PublicToken, b.GuestEmail, b.GuestPhone,
                CustomerEmail = b.Customer != null ? b.Customer.Email : null,
                CustomerPhone = b.Customer != null ? b.Customer.Phone : null,
            })
            .ToListAsync();
        return GuestRecordLookup.Match(
            rows.Select(r => new GuestLookupCandidate(r.CompanyId, r.SiteId, r.BookingNumber, r.PublicToken,
                new[] { r.GuestEmail, r.CustomerEmail }, new[] { r.GuestPhone, r.CustomerPhone })),
            companyId, siteId, bookingNumber, contact);
    }

    public async Task<string?> LookupOrderTokenAsync(Guid companyId, Guid siteId, string? orderNumber, string? contact)
    {
        if (!GuestRecordLookup.IsValidRequest(orderNumber, contact)) return null;
        var n = GuestRecordLookup.NormalizeNumber(orderNumber)!;
        var rows = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.OrderNumber.ToUpper() == n)
            .Take(LookupCandidateCap)
            .Select(o => new
            {
                o.CompanyId, o.SiteId, o.OrderNumber, o.PublicToken, o.ShippingEmail, o.ShippingPhone,
                CustomerEmail = o.Customer != null ? o.Customer.Email : null,
                CustomerPhone = o.Customer != null ? o.Customer.Phone : null,
            })
            .ToListAsync();
        return GuestRecordLookup.Match(
            rows.Select(r => new GuestLookupCandidate(r.CompanyId, r.SiteId, r.OrderNumber, r.PublicToken,
                new[] { r.ShippingEmail, r.CustomerEmail }, new[] { r.ShippingPhone, r.CustomerPhone })),
            companyId, siteId, orderNumber, contact);
    }
}
