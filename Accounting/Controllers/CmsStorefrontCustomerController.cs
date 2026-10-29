using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 209 · P1 — ทางเข้าฝั่ง<b>ลูกค้าหน้าร้าน</b> (อ่านอย่างเดียว):
/// <list type="bullet">
/// <item>หน้าสถานะออเดอร์ของแขก — พิสูจน์สิทธิ์ด้วย <c>SiteOrder.PublicToken</c> (สุ่ม · ไม่ใช่ GUID) · DTO จำกัดฟิลด์ (คำตัดสินข้อ 168 ข)</item>
/// <item>QR พร้อมเพย์สร้างที่เซิร์ฟเวอร์ (ไม่ส่งเลขพร้อมเพย์/ยอดไปบริการภายนอก — ข้อ 168 ฑ)</item>
/// <item>"บัญชีของฉัน" — โทเคนลูกค้าหน้าร้านใน header <c>X-Storefront-Token</c> (<see cref="StorefrontCustomerAuth"/>) · อ่านเฉพาะข้อมูลของ id ในโทเคน</item>
/// </list>
/// <c>[AllowAnonymous]</c> ทั้งคลาส เพราะโทเคนหน้าร้านถูก scheme ของ ERP ปฏิเสธโดยตั้งใจ (A-02) — ด่านจริงคือ <see cref="Me"/> ที่ทุก action "ของฉัน" เรียก
/// </summary>
[ApiController]
[AllowAnonymous]
public class CmsStorefrontCustomerController : ControllerBase
{
    private readonly IStorefrontCustomerService _svc;
    private readonly IConfiguration _config;

    public CmsStorefrontCustomerController(IStorefrontCustomerService svc, IConfiguration config)
    {
        _svc = svc;
        _config = config;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/orders/public/{token}")]
    public async Task<ActionResult<ApiResponse<PublicOrderResponse>>> GetPublicOrder(
        Guid companyId, Guid siteId, string token, [FromServices] IChatRateLimiter limiter)
    {
        if (!await limiter.TryConsumeAsync($"cms-order-public:{siteId}:{Ip}", perMinute: 30, perDay: 600))
            return StatusCode(429, new ApiResponse<PublicOrderResponse>(false, null, "เปิดดูถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var result = await _svc.GetPublicOrderAsync(companyId, siteId, token);
        if (result == null)
            return NotFound(new ApiResponse<PublicOrderResponse>(false, null, "ไม่พบคำสั่งซื้อ — ตรวจลิงก์อีกครั้ง หรือดูจากหน้า \"บัญชีของฉัน\""));
        return Ok(new ApiResponse<PublicOrderResponse>(true, result));
    }

    /// <summary>รูป QR พร้อมเพย์ของเว็บ (PNG) — ยอดที่หน้าเว็บได้จากเซิร์ฟเวอร์ (ยอดค้างของออเดอร์/ยอดโอนของการจอง) · ไม่มีพร้อมเพย์/ยอดผิด ⇒ 404</summary>
    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/promptpay-qr")]
    public async Task<IActionResult> PromptPayQr(Guid companyId, Guid siteId, [FromQuery] decimal? amount, [FromServices] IChatRateLimiter limiter)
    {
        if (!await limiter.TryConsumeAsync($"cms-promptpay-qr:{siteId}:{Ip}", perMinute: 30, perDay: 600))
            return StatusCode(429);
        var png = await _svc.PromptPayQrPngAsync(companyId, siteId, amount);
        if (png == null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(png, "image/png");
    }

    // ===== บัญชีของฉัน (โทเคนลูกค้าหน้าร้าน) =====

    /// <summary>ด่านเดียวของทุก action "ของฉัน": โทเคนถูกต้อง + ออกโดยเว็บนี้ + ลูกค้ายังมีอยู่/ใช้งานในบริษัท+เว็บนี้ · ไม่ผ่าน ⇒ (null, 401/403)</summary>
    private async Task<(StorefrontMeResponse? Me, ActionResult? Fail)> Me(Guid companyId, Guid siteId)
    {
        var id = StorefrontCustomerAuth.FromRequest(Request, _config);
        if (id == null)
            return (null, StatusCode(401, new ApiResponse<object>(false, null, "กรุณาเข้าสู่ระบบสมาชิก (เซสชันหมดอายุหรือยังไม่ได้เข้าสู่ระบบ)")));
        if (StorefrontCustomerAuth.ForSite(id, siteId) == null)
            return (null, StatusCode(403, new ApiResponse<object>(false, null, "บัญชีสมาชิกนี้เป็นของเว็บไซต์อื่น — เข้าสู่ระบบที่เว็บนี้ใหม่")));
        var me = await _svc.GetMeAsync(companyId, siteId, id.CustomerId);
        if (me == null)
            return (null, StatusCode(401, new ApiResponse<object>(false, null, "ไม่พบบัญชีสมาชิก หรือบัญชีถูกปิด — เข้าสู่ระบบใหม่")));
        return (me, null);
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me")]
    public async Task<ActionResult> GetMe(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<StorefrontMeResponse>(true, me!));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/orders")]
    public async Task<ActionResult> MyOrders(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyOrderItem>>(true, await _svc.MyOrdersAsync(companyId, siteId, me!.CustomerId)));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/bookings")]
    public async Task<ActionResult> MyBookings(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyBookingItem>>(true, await _svc.MyBookingsAsync(companyId, siteId, me!.CustomerId)));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/credits")]
    public async Task<ActionResult> MyCredits(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyCreditItem>>(true, await _svc.MyCreditsAsync(companyId, siteId, me!.CustomerId)));
    }
}
