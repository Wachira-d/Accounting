using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 218 — <b>ฝั่งลูกค้า (ไม่ล็อกอิน)</b>: ตัวอย่างการชำระก่อนกดจอง · แนบ/เปิดสลิปของตัวเองด้วยโทเคนการจอง (สิทธิ์ = โทเคน · ยอดจากเซิร์ฟเวอร์ ·
/// จำกัดความถี่ต่อ IP ด้วย <c>IChatRateLimiter</c> แบบ DB — หลาย instance) · ชำระออนไลน์ใช้ <c>PublicPaymentController</c> เดิม
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/booking")]
[AllowAnonymous]
public class CmsBookingPaymentPublicController : ControllerBase
{
    private readonly ICmsBookingService _bookings;
    private readonly IBookingSlipService _slips;

    public CmsBookingPaymentPublicController(ICmsBookingService bookings, IBookingSlipService slips)
    { _bookings = bookings; _slips = slips; }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>ก่อนกดจอง: ยอดที่ต้องจ่าย · บังคับไหม · กำหนดเวลา · ช่องทาง · นโยบายยกเลิก (อ่านอย่างเดียว · server ตัดสิน)</summary>
    [HttpPost("payment-preview")]
    public async Task<ActionResult<ApiResponse<BookingPaymentPreviewResponse>>> Preview(Guid companyId, Guid siteId,
        [FromBody] BookingPaymentPreviewRequest request)
    {
        var result = await _bookings.PreviewPaymentAsync(companyId, siteId, request);
        if (result == null) return NotFound(new ApiResponse<BookingPaymentPreviewResponse>(false, null, "ไม่พบบริการ"));
        return Ok(new ApiResponse<BookingPaymentPreviewResponse>(true, result));
    }

    /// <summary>ลูกค้าแนบสลิป (รูป/PDF ≤ 10 MB · ชนิดจากไบต์จริง) ⇒ รายการชำระ manual-slip ยอดจากเซิร์ฟเวอร์ + แจ้งร้าน · ร้านตรวจแล้วจึงบันทึกรับเงิน</summary>
    [HttpPost("bookings/by-token/{token}/slip")]
    [RequestSizeLimit(BookingSlipRule.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> UploadSlip(Guid companyId, Guid siteId, string token, IFormFile? file,
        [FromServices] IChatRateLimiter limiter, CancellationToken ct)
    {
        if (!await limiter.TryConsumeAsync($"cms-booking-slip:{siteId}:{Ip}", perMinute: 6, perDay: 60))
            return StatusCode(429, new ApiResponse<BookingResponse>(false, null, "แนบสลิปถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<BookingResponse>(false, null, "กรุณาเลือกไฟล์สลิป"));
        var result = await _slips.UploadByTokenAsync(companyId, siteId, token, file, ct);
        if (result == null) return NotFound(new ApiResponse<BookingResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<BookingResponse>(true, result, "ส่งสลิปแล้ว — ร้านจะตรวจสอบและยืนยันการจอง"));
    }

    /// <summary>ลูกค้าเปิดสลิปของตัวเอง — ต้องถือโทเคนของการจองนั้น (ไม่มี URL สาธารณะของไฟล์)</summary>
    [HttpGet("bookings/by-token/{token}/slip")]
    public async Task<IActionResult> ViewSlip(Guid companyId, Guid siteId, string token, [FromServices] IChatRateLimiter limiter, CancellationToken ct)
    {
        if (!await limiter.TryConsumeAsync($"cms-booking-slip-view:{siteId}:{Ip}", perMinute: 30, perDay: 600))
            return StatusCode(429);
        var f = await _slips.ReadSlipByTokenAsync(companyId, siteId, token, ct);
        if (f == null) return NotFound(new ApiResponse<string>(false, null, "ไม่พบไฟล์สลิป"));
        Response.Headers.CacheControl = "private, no-store";
        return File(f.Value.Content, f.Value.ContentType);
    }
}
