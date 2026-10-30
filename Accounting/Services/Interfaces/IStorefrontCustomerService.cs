using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 209 · P1 — ฝั่งอ่านของลูกค้าหน้าร้าน: หน้าสถานะออเดอร์ด้วยโทเคน · QR พร้อมเพย์ฝั่งเซิร์ฟเวอร์ · "บัญชีของฉัน" (ออเดอร์/การจอง/บัตรคอร์ส) ·
/// ทุกเมธอดกรอง <c>CompanyId</c> + <c>SiteId</c> · เมธอด "ของฉัน" รับ id ลูกค้าที่ controller ตรวจโทเคนแล้ว (<c>StorefrontCustomerAuth</c>) เท่านั้น</summary>
public interface IStorefrontCustomerService
{
    Task<PublicOrderResponse?> GetPublicOrderAsync(Guid companyId, Guid siteId, string token);
    /// <summary>PNG ของ QR พร้อมเพย์ของเว็บ (ยอดตามที่ขอ) · ร้านไม่ตั้งพร้อมเพย์/ยอดผิด ⇒ null</summary>
    Task<byte[]?> PromptPayQrPngAsync(Guid companyId, Guid siteId, decimal? amount);
    /// <summary>ลูกค้ามีอยู่ในบริษัท+เว็บนี้และยังใช้งาน · ไม่ใช่ ⇒ null (controller ตอบ 401)</summary>
    Task<StorefrontMeResponse?> GetMeAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<List<StorefrontMyOrderItem>> MyOrdersAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<List<StorefrontMyBookingItem>> MyBookingsAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<List<StorefrontMyCreditItem>> MyCreditsAsync(Guid companyId, Guid siteId, Guid customerId);
    /// <summary>รอบ 210: แขกค้นการจองด้วยเลขที่ + เบอร์/อีเมล (ตัวตัดสิน <c>Helpers/GuestRecordLookup</c>) · ไม่ตรง ⇒ null · ตรง ⇒ โทเคนหน้า my-booking</summary>
    Task<string?> LookupBookingTokenAsync(Guid companyId, Guid siteId, string? bookingNumber, string? contact);
    /// <summary>รอบ 210: แขกค้นคำสั่งซื้อด้วยเลขที่ + เบอร์/อีเมล · ไม่ตรง ⇒ null · ตรง ⇒ โทเคนหน้า track-order</summary>
    Task<string?> LookupOrderTokenAsync(Guid companyId, Guid siteId, string? orderNumber, string? contact);
}
