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
/// รอบ 218 (คำตัดสินข้อ 177 · <c>erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md</c>) — <b>ฝั่งพนักงาน</b>ของการชำระล่วงหน้าของการจองนัดหมาย:
/// ค่าตั้งเว็บ (โหมด/ยอด/กำหนดเวลา/ช่องทาง) · ตรวจสลิป (ยืนยัน = PaymentIntent สำเร็จ ⇒ handler บันทึกรับเงิน · ปฏิเสธ = ต้องมีเหตุผล) ·
/// ไฟล์สลิปเปิดผ่าน <c>attachments/{id}/download</c> (ด่าน AttachmentPermissionScope "SiteBooking" → CmsOrderManage) ·
/// ฝั่งลูกค้า (ไม่ล็อกอิน · โทเคน) อยู่ที่ <see cref="CmsBookingPaymentPublicController"/>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/booking")]
[Authorize]
public class CmsBookingPaymentController : ControllerBase
{
    private readonly ICmsBookingService _bookings;
    private readonly IBookingSlipService _slips;

    public CmsBookingPaymentController(ICmsBookingService bookings, IBookingSlipService slips)
    { _bookings = bookings; _slips = slips; }

    [HttpGet("payment-settings")]
    public async Task<ActionResult<ApiResponse<BookingPaymentSettingsDto>>> GetPaymentSettings(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<BookingPaymentSettingsDto>(true, await _bookings.GetPaymentSettingsAsync(companyId, siteId)));

    /// <summary>บันทึกนโยบาย + ช่องทาง — โหมดเก็บเงินที่ไม่มีช่องทางชำระ ⇒ 400 พร้อมทางไปต่อ (ไม่บันทึกค่าที่ไม่มีผล) · คีย์เดียวกับค่าตั้งการจองของเว็บ</summary>
    [HttpPut("payment-settings")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingPaymentSettingsDto>>> UpdatePaymentSettings(Guid companyId, Guid siteId,
        [FromBody] BookingPaymentSettingsDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.UpdatePaymentSettingsAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<BookingPaymentSettingsDto>(true, result, "บันทึกการชำระเงินของการจองแล้ว"));
    }

    /// <summary>ร้านยืนยันสลิป ⇒ เส้นเงินเดียวกับ gateway (ยืนยันการจอง + ใบรับมัดจำลงวันที่รับเงิน) · ล้มหลังยืนยัน ⇒ ErpNotices บอกทางไปต่อ</summary>
    [HttpPost("bookings/{bookingId:guid}/slip/verify")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> VerifySlip(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] VerifyBookingSlipRequest? request, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _slips.VerifyAsync(companyId, siteId, bookingId, request ?? new VerifyBookingSlipRequest(), userId, ct);
        return Ok(new ApiResponse<BookingResponse>(true, result, result.ErpNotices.Count == 0 ? "ยืนยันสลิปแล้ว — บันทึกรับเงินแล้ว" : "ยืนยันสลิปแล้ว (มีขั้นที่ต้องตรวจ)"));
    }

    [HttpPost("bookings/{bookingId:guid}/slip/reject")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> RejectSlip(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] RejectBookingSlipRequest? request, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _slips.RejectAsync(companyId, siteId, bookingId, request ?? new RejectBookingSlipRequest(), userId, ct);
        return Ok(new ApiResponse<BookingResponse>(true, result, "ปฏิเสธสลิปแล้ว — แจ้งลูกค้าพร้อมเหตุผลแล้ว"));
    }
}
