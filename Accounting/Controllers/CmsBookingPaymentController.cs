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
    [RequireSiteRole(SiteStaffRole.Admin)]   // ฝ่ายค้านรอบ 228 S-3: ค่าตั้งการรับเงิน (เขียนบัญชีรับโอนได้ด้วย) = แอดมินของเว็บ
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingPaymentSettingsDto>>> UpdatePaymentSettings(Guid companyId, Guid siteId,
        [FromBody] BookingPaymentSettingsDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.UpdatePaymentSettingsAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<BookingPaymentSettingsDto>(true, result, "บันทึกการชำระเงินของการจองแล้ว"));
    }

    /// <summary>รอบ 226 (คำตัดสิน 226-1): แก้บริการที่โหมดบังคับเก็บเงินไม่ได้ในคลิกเดียว — "convert" (เปลี่ยนชนิดเป็นชำระมัดจำล่วงหน้า) / "exempt" (ไม่รับชำระล่วงหน้า)</summary>
    [HttpPost("payment-settings/fix-services")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]   // ฝ่ายค้านรอบ 226 ข้อ 8: แก้บริการ = บทบาทที่แก้เว็บได้ + สิทธิ์ตั้งค่าเว็บ
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingPaymentSettingsDto>>> FixUncollectableServices(Guid companyId, Guid siteId,
        [FromBody] FixUncollectableServicesRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.FixUncollectableServicesAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<BookingPaymentSettingsDto>(true, result, "แก้บริการแล้ว — บันทึกโหมดการชำระได้"));
    }

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก E): แผง "สิ่งที่ยังไม่ได้ตั้งค่า" ของเว็บ (อ่านอย่างเดียว · ศูนย์ตั้งค่า · การ์ดเว็บ · ก่อนเผยแพร่)</summary>
    [HttpGet("setup-status")]
    public async Task<ActionResult<ApiResponse<CmsSiteSetupStatusDto>>> SetupStatus(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<CmsSiteSetupStatusDto>(true, await _bookings.GetSetupStatusAsync(companyId, siteId)));

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก D): ตาราง "มัดจำ & การยกเลิก ของทุกงาน" (อ่านอย่างเดียว · บอกที่แก้ต่อแถว)</summary>
    [HttpGet("deposit-overview")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<List<DepositCancelOverviewRow>>>> DepositOverview(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<List<DepositCancelOverviewRow>>(true, await _bookings.GetDepositCancelOverviewAsync(companyId, siteId)));

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก D · Q1): ตัวเลือกคำถาม "ลูกค้าต้องจ่ายก่อนไหม" ของฟอร์มบริการ (ป้าย/คำอธิบาย/ชนิดเอกสารจากเซิร์ฟเวอร์)</summary>
    [HttpGet("pay-first-options")]
    public ActionResult<ApiResponse<IReadOnlyList<BookingPayFirstOption>>> PayFirstOptions(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<IReadOnlyList<BookingPayFirstOption>>(true, BookingPayFirst.Options));

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก A): บัญชีรับโอน/พร้อมเพย์/รูป QR ของเว็บ — ที่เดียวของทุกงาน (คำสั่งซื้อ · การจอง · ที่พัก)</summary>
    [HttpGet("transfer-account")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<SiteTransferAccountDto>>> GetTransferAccount(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<SiteTransferAccountDto>(true, await _bookings.GetTransferAccountAsync(companyId, siteId)));

    [HttpPut("transfer-account")]
    [RequireSiteRole(SiteStaffRole.Admin)]   // ฝ่ายค้านรอบ 228 S-3: บัญชีที่เงินเข้า = แอดมินของเว็บเท่านั้น (+ audit ในเซอร์วิส)
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<SiteTransferAccountDto>>> UpdateTransferAccount(Guid companyId, Guid siteId,
        [FromBody] SiteTransferAccountDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.UpdateTransferAccountAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<SiteTransferAccountDto>(true, result, "บันทึกบัญชีรับโอนแล้ว — ใช้กับคำสั่งซื้อ การจอง และที่พักของเว็บนี้"));
    }

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก C): ค่าส่งเหมาจ่าย/ส่งฟรีของเว็บ + คำเตือนส่งฟรีเงียบ (คำตัดสิน Q3) — เว็บคำสั่งซื้อและเว็บการจอง</summary>
    [HttpGet("shipping-settings")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<SiteShippingSettingsDto>>> GetShippingSettings(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<SiteShippingSettingsDto>(true, await _bookings.GetShippingSettingsAsync(companyId, siteId)));

    [HttpPut("shipping-settings")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<SiteShippingSettingsDto>>> UpdateShippingSettings(Guid companyId, Guid siteId,
        [FromBody] SiteShippingSettingsDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.UpdateShippingSettingsAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<SiteShippingSettingsDto>(true, result, "บันทึกค่าส่งแล้ว — ใช้กับคำสั่งซื้อและการจองแบบจัดส่งของเว็บนี้"));
    }

    /// <summary>รอบ 226: จัดส่ง/รับเองของการจอง (ค่าส่งใช้โซน/อัตราของร้าน)</summary>
    [HttpGet("fulfillment-settings")]
    public async Task<ActionResult<ApiResponse<BookingFulfillmentSettingsDto>>> GetFulfillmentSettings(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<BookingFulfillmentSettingsDto>(true, await _bookings.GetFulfillmentSettingsAsync(companyId, siteId)));

    [HttpPut("fulfillment-settings")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingFulfillmentSettingsDto>>> UpdateFulfillmentSettings(Guid companyId, Guid siteId,
        [FromBody] BookingFulfillmentSettingsDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.UpdateFulfillmentSettingsAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<BookingFulfillmentSettingsDto>(true, result, "บันทึกการจัดส่ง/รับเองของการจองแล้ว"));
    }

    /// <summary>รอบ 228 (คำตัดสิน 228-6): ตั้งค่าเว็บทั้งชุดจากแม่แบบ (เช่น "สั่งทำ / พรีออเดอร์") — บริการตัวอย่าง + จัดส่ง/รับเอง + ช่องฟอร์มจอง ·
    /// ค่าที่ตั้งไว้แล้วชนะ · คืนสิ่งที่ทำ/ข้าม/ต้องทำต่อ</summary>
    [HttpPost("presets/{presetKey}/apply-site")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingPresetApplyResult>>> ApplyPresetToSite(Guid companyId, Guid siteId, string presetKey)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookings.ApplyPresetToSiteAsync(companyId, siteId, presetKey, userId);
        return Ok(new ApiResponse<BookingPresetApplyResult>(true, result, "ตั้งค่าเว็บจากแม่แบบแล้ว"));
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
