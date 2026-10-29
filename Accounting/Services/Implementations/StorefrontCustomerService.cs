using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P1 (R-209-A1/B3/E1/C3 + ฑ) — ฝั่งอ่านของลูกค้าหน้าร้าน · ตัวตัดสินอยู่ที่ <see cref="SiteOrderPublic"/> ·
/// ยอด/งวดของออเดอร์มาจาก <see cref="ICmsCommerceService.GetOrderAsync"/> ตัวเดียวกับหลังบ้าน (ไม่คิดซ้ำ) แล้วเลือกฟิลด์ผ่าน <see cref="SiteOrderPublic.ToPublic"/>
/// </summary>
public class StorefrontCustomerService : IStorefrontCustomerService
{
    private readonly AccountingDbContext _db;
    private readonly ICmsCommerceService _commerce;
    private readonly IPromptPayService _promptPay;

    public StorefrontCustomerService(AccountingDbContext db, ICmsCommerceService commerce, IPromptPayService promptPay)
    {
        _db = db;
        _commerce = commerce;
        _promptPay = promptPay;
    }

    public async Task<PublicOrderResponse?> GetPublicOrderAsync(Guid companyId, Guid siteId, string token)
    {
        if (!SiteOrderPublic.IsWellFormedToken(token)) return null;
        var orderId = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.PublicToken == token)
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync();
        if (orderId is not Guid id) return null;
        var full = await _commerce.GetOrderAsync(companyId, siteId, id);
        if (full == null) return null;
        return SiteOrderPublic.ToPublic(full, await PromptPayIdAsync(companyId, siteId) != null);
    }

    public async Task<byte[]?> PromptPayQrPngAsync(Guid companyId, Guid siteId, decimal? amount)
    {
        if (SiteOrderPublic.QrAmount(amount) is not decimal amt) return null;
        var ppId = await PromptPayIdAsync(companyId, siteId);
        return ppId == null ? null : _promptPay.GenerateQrImage(ppId, amt, 300);
    }

    /// <summary>พร้อมเพย์ของเว็บ (ช่องทางชำระตัวแรกที่เปิด — ตัวเดียวกับ <c>GetStorefrontPaymentOptionsAsync</c>) · รูปแบบผิด ⇒ null</summary>
    private async Task<string?> PromptPayIdAsync(Guid companyId, Guid siteId)
    {
        var id = await _db.Set<SitePaymentGateway>().AsNoTracking()
            .Where(g => g.CompanyId == companyId && g.SiteId == siteId && g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.SortOrder)
            .Select(g => g.PromptPayId).FirstOrDefaultAsync();
        return !string.IsNullOrWhiteSpace(id) && _promptPay.ValidatePromptPayId(id) ? id : null;
    }

    public async Task<StorefrontMeResponse?> GetMeAsync(Guid companyId, Guid siteId, Guid customerId)
        => await _db.SiteCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.CompanyId == companyId && c.SiteId == siteId && c.IsActive)
            .Select(c => new StorefrontMeResponse { CustomerId = c.Id, Email = c.Email, FullName = c.FullName, HasErpContact = c.ContactId != null })
            .FirstOrDefaultAsync();

    public async Task<List<StorefrontMyOrderItem>> MyOrdersAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        var rows = await _db.SiteOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt).Take(100)
            .Select(o => new
            {
                o.OrderNumber, o.Status, o.TotalAmount, o.Currency, o.CreatedAt, o.PublicToken,
                o.ShippingCarrier, o.TrackingNumber, o.FulfillmentMethod,
            })
            .ToListAsync();
        return rows.Select(o => new StorefrontMyOrderItem
        {
            OrderNumber = o.OrderNumber, Status = o.Status,
            StatusLabel = SiteOrderPublic.StatusLabel(o.Status, o.FulfillmentMethod),
            TotalAmount = o.TotalAmount, Currency = o.Currency, CreatedAt = o.CreatedAt, PublicToken = o.PublicToken,
            ShippingCarrier = o.ShippingCarrier, TrackingNumber = o.TrackingNumber,
            TrackingUrl = SiteOrderPublic.TrackingUrl(o.ShippingCarrier, o.TrackingNumber),
        }).ToList();
    }

    public async Task<List<StorefrontMyBookingItem>> MyBookingsAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        var rows = await (from b in _db.SiteBookings.AsNoTracking()
                          join s in _db.SiteBookingServices.AsNoTracking() on b.BookingServiceId equals s.Id
                          where b.CompanyId == companyId && b.SiteId == siteId && b.CustomerId == customerId
                                && s.CompanyId == companyId && !b.IsDeleted
                          orderby b.BookingDate descending, b.StartTime descending
                          select new { b.BookingNumber, s.Name, b.BookingDate, b.StartTime, b.EndTime, b.Status, b.PublicToken })
            .Take(100).ToListAsync();
        return rows.Select(b => new StorefrontMyBookingItem
        {
            BookingNumber = b.BookingNumber, ServiceName = b.Name,
            BookingDate = b.BookingDate.ToString("yyyy-MM-dd"),
            StartTime = b.StartTime.ToString("HH:mm"), EndTime = b.EndTime.ToString("HH:mm"),
            Status = b.Status, StatusLabel = BookingStatusLabels.Customer(b.Status), PublicToken = b.PublicToken,
        }).ToList();
    }

    public async Task<List<StorefrontMyCreditItem>> MyCreditsAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        // บัตรผูกกับผู้ติดต่อ ERP ของลูกค้า (PDPA: อ่านเฉพาะ ContactId ของตัวเอง — ผู้เรียกได้ customerId จากโทเคนเท่านั้น)
        var contactId = await _db.SiteCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.CompanyId == companyId && c.SiteId == siteId && c.IsActive)
            .Select(c => c.ContactId).FirstOrDefaultAsync();
        if (contactId is not Guid cid) return new List<StorefrontMyCreditItem>();
        var passes = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.ContactId == cid && !p.IsDeleted)
            .OrderByDescending(p => p.PurchasedAt).Take(100)
            .ToListAsync();
        var now = DateTime.UtcNow;
        return passes.Select(p =>
        {
            var available = CourseCreditMath.Available(p.CreditsTotal, p.CreditsUsed, p.CreditsReserved);
            var expired = CourseCreditMath.IsExpired(p.ExpiresOn, now);
            return new StorefrontMyCreditItem
            {
                PassId = p.Id, PassNumber = p.PassNumber, PackageName = p.PackageName,
                UnitLabel = SiteOrderPublic.CreditUnitLabel(p.Unit),
                CreditsTotal = p.CreditsTotal, CreditsUsed = p.CreditsUsed, CreditsReserved = p.CreditsReserved, CreditsAvailable = available,
                ExpiresOn = p.ExpiresOn?.ToString("yyyy-MM-dd"), IsExpired = expired,
                Status = p.Status, StatusLabel = CourseCreditMath.StatusTh(p.Status),
                Usable = SiteOrderPublic.CreditUsable(p.Status, expired, available),
            };
        }).ToList();
    }
}
