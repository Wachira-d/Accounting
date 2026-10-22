using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/booking")]
[Authorize]
public class CmsBookingController : ControllerBase
{
    private readonly ICmsBookingService _bookingService;

    public CmsBookingController(ICmsBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    private string Lang => CmsMessages.ResolveLanguage(HttpContext);

    // ===== Booking Services =====

    [HttpPost("services")]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> CreateService(
        Guid companyId, Guid siteId, [FromBody] CreateBookingServiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.CreateServiceAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<BookingServiceResponse>(true, result, CmsMessages.Get("booking.serviceCreated", Lang)));
    }

    [HttpGet("services")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<BookingServiceResponse>>>> GetServices(Guid companyId, Guid siteId, [FromQuery] bool all = false)
    {
        // all=true (บริการที่ปิดไว้ด้วย) เฉพาะผู้เรียกที่ล็อกอิน (หน้า CMS — TenantAccessMiddleware ตรวจสิทธิ์บริษัทแล้ว) · ผู้เยี่ยมชมเห็นเฉพาะที่เปิด
        var includeInactive = all && User.Identity?.IsAuthenticated == true;
        var result = await _bookingService.GetServicesAsync(companyId, siteId, includeInactive);
        return Ok(new ApiResponse<List<BookingServiceResponse>>(true, result));
    }

    /// <summary>แม่แบบบริการตามประเภทธุรกิจ — อ่านอย่างเดียว (ไม่เขียนฐาน) · หน้า CMS ใช้เติมฟอร์ม "เพิ่มจากแม่แบบ"</summary>
    [HttpGet("presets")]
    public ActionResult<ApiResponse<IReadOnlyList<BookingServicePresetResponse>>> GetPresets(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<IReadOnlyList<BookingServicePresetResponse>>(true, _bookingService.GetPresets()));

    [HttpGet("services/{serviceId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> GetService(Guid companyId, Guid siteId, Guid serviceId)
    {
        var result = await _bookingService.GetServiceAsync(companyId, siteId, serviceId);
        if (result == null) return NotFound(new ApiResponse<BookingServiceResponse>(false, null, CmsMessages.Get("booking.serviceNotFound", Lang)));
        return Ok(new ApiResponse<BookingServiceResponse>(true, result));
    }

    [HttpPut("services/{serviceId:guid}")]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> UpdateService(
        Guid companyId, Guid siteId, Guid serviceId, [FromBody] UpdateBookingServiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.UpdateServiceAsync(companyId, siteId, serviceId, request, userId);
        return Ok(new ApiResponse<BookingServiceResponse>(true, result, CmsMessages.Get("booking.serviceUpdated", Lang)));
    }

    [HttpDelete("services/{serviceId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteService(Guid companyId, Guid siteId, Guid serviceId)
    {
        var result = await _bookingService.DeleteServiceAsync(companyId, siteId, serviceId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("booking.serviceNotFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("booking.serviceDeleted", Lang)));
    }

    // ===== ค่าตั้งระดับไซต์ + ผู้ให้บริการ (ทีม B) — เขียนต้องมีสิทธิ์ตั้งค่าเว็บไซต์ =====

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<BookingSiteSettingsDto>>> GetSiteSettings(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<BookingSiteSettingsDto>(true, await _bookingService.GetSiteSettingsAsync(companyId, siteId)));

    [HttpPut("settings")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingSiteSettingsDto>>> UpdateSiteSettings(
        Guid companyId, Guid siteId, [FromBody] BookingSiteSettingsDto request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.UpdateSiteSettingsAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<BookingSiteSettingsDto>(true, result, "บันทึกค่าตั้งการจองของเว็บไซต์แล้ว"));
    }

    [HttpGet("resources")]
    public async Task<ActionResult<ApiResponse<List<BookingResourceResponse>>>> GetResources(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<List<BookingResourceResponse>>(true, await _bookingService.GetResourcesAsync(companyId, siteId)));

    [HttpPost("resources")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingResourceResponse>>> CreateResource(
        Guid companyId, Guid siteId, [FromBody] BookingResourceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.SaveResourceAsync(companyId, siteId, null, request, userId);
        return StatusCode(201, new ApiResponse<BookingResourceResponse>(true, result, "เพิ่มผู้ให้บริการแล้ว"));
    }

    [HttpPut("resources/{resourceId:guid}")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingResourceResponse>>> UpdateResource(
        Guid companyId, Guid siteId, Guid resourceId, [FromBody] BookingResourceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.SaveResourceAsync(companyId, siteId, resourceId, request, userId);
        return Ok(new ApiResponse<BookingResourceResponse>(true, result, "บันทึกผู้ให้บริการแล้ว"));
    }

    [HttpDelete("resources/{resourceId:guid}")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteResource(Guid companyId, Guid siteId, Guid resourceId)
    {
        var ok = await _bookingService.DeleteResourceAsync(companyId, siteId, resourceId);
        if (!ok) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบผู้ให้บริการ"));
        return Ok(new ApiResponse<bool>(true, true, "ลบผู้ให้บริการแล้ว"));
    }

    // ===== Booking Slots =====

    [HttpPost("services/{serviceId:guid}/slots")]
    public async Task<ActionResult<ApiResponse<BookingSlotResponse>>> CreateSlot(
        Guid companyId, Guid siteId, Guid serviceId, [FromBody] CreateBookingSlotRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.CreateSlotAsync(companyId, siteId, serviceId, request, userId);
        return StatusCode(201, new ApiResponse<BookingSlotResponse>(true, result, CmsMessages.Get("booking.slotCreated", Lang)));
    }

    [HttpGet("services/{serviceId:guid}/slots")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<BookingSlotResponse>>>> GetSlots(Guid companyId, Guid siteId, Guid serviceId)
    {
        var result = await _bookingService.GetSlotsAsync(companyId, siteId, serviceId);
        return Ok(new ApiResponse<List<BookingSlotResponse>>(true, result));
    }

    [HttpDelete("services/{serviceId:guid}/slots/{slotId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteSlot(Guid companyId, Guid siteId, Guid serviceId, Guid slotId)
    {
        var result = await _bookingService.DeleteSlotAsync(companyId, siteId, serviceId, slotId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, CmsMessages.Get("booking.slotNotFound", Lang)));
        return Ok(new ApiResponse<bool>(true, true, CmsMessages.Get("booking.slotDeleted", Lang)));
    }

    // ===== Available Slots (public query) =====

    [HttpPost("available-slots")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<AvailableSlotResponse>>>> GetAvailableSlots(
        Guid companyId, Guid siteId, [FromBody] AvailableSlotsRequest request)
    {
        var result = await _bookingService.GetAvailableSlotsAsync(companyId, siteId, request);
        return Ok(new ApiResponse<List<AvailableSlotResponse>>(true, result));
    }

    // ===== Bookings =====

    [HttpPost("bookings")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> CreateBooking(
        Guid companyId, Guid siteId, [FromBody] CreateBookingRequest request)
    {
        string? userId = null;
        try { userId = JwtHelper.GetUserIdFromClaims(User).ToString(); } catch { }
        var result = await _bookingService.CreateBookingAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<BookingResponse>(true, result, CmsMessages.Get("booking.created", Lang)));
    }

    [HttpGet("bookings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BookingListResponse>>>> GetBookings(
        Guid companyId, Guid siteId,
        [FromQuery] string? status = null, [FromQuery] Guid? serviceId = null,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _bookingService.GetBookingsAsync(companyId, siteId, status, serviceId, fromDate, toDate, page, pageSize, search);
        return Ok(new ApiResponse<PagedResponse<BookingListResponse>>(true, result));
    }

    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> GetBooking(Guid companyId, Guid siteId, Guid bookingId)
    {
        var result = await _bookingService.GetBookingAsync(companyId, siteId, bookingId);
        if (result == null) return NotFound(new ApiResponse<BookingResponse>(false, null, CmsMessages.Get("booking.notFound", Lang)));
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    [HttpPut("bookings/{bookingId:guid}/status")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> UpdateBookingStatus(
        Guid companyId, Guid siteId, Guid bookingId, [FromBody] UpdateBookingStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.UpdateBookingStatusAsync(companyId, siteId, bookingId, request, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, CmsMessages.Get("booking.statusUpdated", Lang)));
    }

    /// <summary>คำตัดสินข้อ 155: "รับเงินแล้ว — ออกใบกำกับภาษี" ของการจอง Guaranteed (tax point §78/1 = วันรับเงิน) · ออกแล้วไม่ออกซ้ำ</summary>
    [HttpPost("bookings/{bookingId:guid}/issue-tax-invoice")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    // ออก + อนุมัติใบกำกับเลขจริง (§86/4 แก้ย้อนหลังไม่ได้) ⇒ คีย์เดียวกับเส้นเอกสารรายรับ (แบบ POS issue-tax-invoice)
    [RequirePermission(PermissionKeys.DocumentRevenueApprove)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> IssueTaxInvoiceOnPayment(Guid companyId, Guid siteId, Guid bookingId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.IssueTaxInvoiceOnPaymentAsync(companyId, siteId, bookingId, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    [HttpPost("bookings/{bookingId:guid}/sync-erp")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<Guid?>>> SyncBookingToErp(Guid companyId, Guid siteId, Guid bookingId)
    {
        var documentId = await _bookingService.SyncBookingToErpAsync(companyId, siteId, bookingId);
        var msg = documentId != null ? CmsMessages.Get("erp.synced", Lang) : CmsMessages.Get("erp.syncFailed", Lang);
        return Ok(new ApiResponse<Guid?>(true, documentId, msg));
    }
}
