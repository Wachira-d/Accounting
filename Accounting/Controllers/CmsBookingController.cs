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
        // รอบ 207 (ฝ่ายค้านข้อ 10): ผู้เยี่ยมชมไม่เห็นรหัสสินค้า/คำเตือนราคาภายใน
        if (User.Identity?.IsAuthenticated != true) result = BookingServicePublicView.Strip(result);
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
        // รอบ 209 (ทีม C ข้อ C1): ใช้บัตรคอร์สแทนลูกค้า = เขียนสมุดเครดิตของลูกค้า ⇒ ต้องเป็นพนักงานของบริษัทนี้ที่มีสิทธิ์จัดการการจอง
        // (โทเคนหน้าร้าน/บัญชี ERP ของบริษัทอื่นไม่ผ่าน) · ไม่ใช้บัตร = เส้นเดิมทุกตัว
        // รอบ 209 P1 (R-209-C3): ลูกค้าที่ล็อกอินหน้าร้าน (โทเคนหน้าร้านของเว็บนี้ · id ตรง CustomerId) ใช้บัตรของตัวเองได้ — ตัวตัดสินเดียว 3 ทิศ PayWithCreditGate
        var perms = HttpContext.RequestServices.GetService(typeof(IPermissionService)) as IPermissionService;
        var staffOk = userId != null && perms != null && await perms.HasPermissionAsync(companyId, Guid.Parse(userId), PermissionKeys.CmsOrderManage);
        var cfg = HttpContext.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
        var sf = staffOk || cfg == null ? null : StorefrontCustomerAuth.FromRequest(Request, cfg);
        // รอบ 209 ฝ่ายค้าน (security ข้อ 6): ลูกค้าของการจอง = ตัวตนที่ยืนยันแล้ว (โทเคนหน้าร้านของเว็บนี้) · พนักงานที่มีสิทธิ์ระบุจาก body ได้ ·
        // นิรนาม ⇒ ไม่มีลูกค้า (เดิมรับ CustomerId จาก body ⇒ ยัดการจองเข้า "การจองของฉัน" ของคนอื่น + ข้ามด่านยินยอมด้วย id ของคนอื่น) — ตัวตัดสินเดียวกับออเดอร์
        var bodyCustomerId = request.CustomerId;
        request.CustomerId = StorefrontCustomerAuth.OrderCustomerId(bodyCustomerId, staffOk, sf, siteId);
        if (request.PayWithCredit)
        {
            if (CreditBookingLink.PayWithCreditGate(staffOk, sf?.CustomerId, sf?.SiteId, siteId, bodyCustomerId ?? request.CustomerId) is string creditProblem)
                return StatusCode(403, new ApiResponse<BookingResponse>(false, null!, creditProblem));
            if (!staffOk && sf != null) userId = CreditBookingLink.StorefrontActor(sf.CustomerId);   // ผู้กระทำ = ลูกค้าคนนั้น (service ตรวจซ้ำ)
        }
        // รอบ 210: ด่าน "ต้องเป็นสมาชิกก่อนจอง" อยู่ใน service (ก่อน Decide) — ลูกค้า = request.CustomerId ที่แปลงจากโทเคนแล้วข้างบน · พนักงานจองแทนผ่าน
        var result = await _bookingService.CreateBookingAsync(companyId, siteId, request, userId, staffOnBehalf: staffOk);
        return StatusCode(201, new ApiResponse<BookingResponse>(true, result, CmsMessages.Get("booking.created", Lang)));
    }

    [HttpGet("bookings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BookingListResponse>>>> GetBookings(
        Guid companyId, Guid siteId,
        [FromQuery] string? status = null, [FromQuery] Guid? serviceId = null,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] Guid? resourceId = null, [FromQuery] string? sort = null)
    {
        // รอบ 207: กรองผู้ให้บริการ + เรียงตามเวลานัด (หน้าทำงานรายวัน) · pageSize สูงสุด 200
        var result = await _bookingService.GetBookingsAsync(companyId, siteId, status, serviceId, fromDate, toDate, page, Math.Clamp(pageSize, 1, 200), search,
            resourceId, sort);
        return Ok(new ApiResponse<PagedResponse<BookingListResponse>>(true, result));
    }

    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> GetBooking(Guid companyId, Guid siteId, Guid bookingId)
    {
        var result = await _bookingService.GetBookingAsync(companyId, siteId, bookingId);
        if (result == null) return NotFound(new ApiResponse<BookingResponse>(false, null, CmsMessages.Get("booking.notFound", Lang)));
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    // ── รอบ 205 W2 (R-08 R-10): เส้นเงิน + ลิงก์ลูกค้า ──

    /// <summary>ลูกค้าดูการจองของตนผ่านโทเคนที่ได้ตอนจอง (ไม่มีข้อมูลภายใน) — ยอดมัดจำค้าง/ปุ่มจ่าย/นโยบายยกเลิก มาจากเซิร์ฟเวอร์</summary>
    [HttpGet("bookings/by-token/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> GetBookingByToken(Guid companyId, Guid siteId, string token)
    {
        var result = await _bookingService.GetBookingByTokenAsync(companyId, siteId, token);
        if (result == null) return NotFound(new ApiResponse<BookingResponse>(false, null, CmsMessages.Get("booking.notFound", Lang)));
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    /// <summary>ลูกค้ายกเลิกเอง — ค่าธรรมเนียมตามนโยบายของบริการ (Helpers/BookingCancellationFee · ริบ ≤ มัดจำ ข้อ 126)</summary>
    [HttpPost("bookings/by-token/{token}/cancel")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> CancelByToken(Guid companyId, Guid siteId, string token,
        [FromBody] CustomerCancelBookingRequest? request)
    {
        var result = await _bookingService.CancelByCustomerAsync(companyId, siteId, token, request ?? new CustomerCancelBookingRequest());
        return Ok(new ApiResponse<BookingResponse>(true, result, "ยกเลิกการจองแล้ว"));
    }

    /// <summary>ลูกค้าเลื่อนนัดเอง (ในหน้าต่างยกเลิกฟรี · รอบ 206) — แถวเดิม เอกสาร/มัดจำคงผูก</summary>
    [HttpPost("bookings/by-token/{token}/reschedule")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> RescheduleByToken(Guid companyId, Guid siteId, string token,
        [FromBody] RescheduleBookingRequest request)
    {
        var result = await _bookingService.RescheduleByCustomerAsync(companyId, siteId, token, request);
        return Ok(new ApiResponse<BookingResponse>(true, result, "เลื่อนนัดแล้ว"));
    }

    /// <summary>พนักงานเลื่อนนัด (รอบ 206)</summary>
    [HttpPut("bookings/{bookingId:guid}/reschedule")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> Reschedule(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] RescheduleBookingRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.RescheduleAsync(companyId, siteId, bookingId, request, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "เลื่อนนัดแล้ว"));
    }

    /// <summary>พนักงานบันทึกรับเงินมัดจำ (โอน/เงินสด) — ครบมัดจำ = ยืนยันการจอง + ใบเสร็จมัดจำอนุมัติลงวันที่รับเงิน (ตัวเดียวกับ gateway)</summary>
    [HttpPost("bookings/{bookingId:guid}/payments")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> RecordPayment(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] RecordBookingPaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.RecordDepositPaymentAsync(companyId, siteId, bookingId, request, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "บันทึกรับเงินแล้ว"));
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

    /// <summary>รอบ 205 W3 R-16: พนักงานเลือก/เปลี่ยนผู้ให้บริการของการจอง (null = ให้ระบบจัดให้ใหม่) — server ตัดสินผ่าน <c>BookingSlotEngine.Decide</c> ตัวเดียว</summary>
    [HttpPut("bookings/{bookingId:guid}/resource")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]   // เขียนข้อมูลการจอง — ด่านสิทธิ์ระดับบริษัทด้วย (write_permission_gate_check)
    public async Task<ActionResult<ApiResponse<BookingResponse>>> AssignBookingResource(
        Guid companyId, Guid siteId, Guid bookingId, [FromBody] AssignBookingResourceRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.AssignResourceAsync(companyId, siteId, bookingId, request?.ResourceId, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "บันทึกผู้ให้บริการแล้ว"));
    }

    /// <summary>คำตัดสินข้อ 155: "รับเงินแล้ว — ออกใบกำกับภาษี" ของการจอง Guaranteed (tax point §78/1 = วันรับเงิน) · ออกแล้วไม่ออกซ้ำ</summary>
    [HttpPost("bookings/{bookingId:guid}/issue-tax-invoice")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    // ออก + อนุมัติใบกำกับเลขจริง (§86/4 แก้ย้อนหลังไม่ได้) ⇒ คีย์เดียวกับเส้นเอกสารรายรับ (แบบ POS issue-tax-invoice)
    [RequirePermission(PermissionKeys.DocumentRevenueApprove)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> IssueTaxInvoiceOnPayment(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] IssueBookingTaxInvoiceRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.IssueTaxInvoiceOnPaymentAsync(companyId, siteId, bookingId, request?.ReceivedDate, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    // ── รอบ 207: หน้าทำงานผู้ให้บริการ ──

    /// <summary>ผู้ให้บริการที่เลือกได้ตอนเริ่มให้บริการ (ว่าง/ไม่ว่าง + คำแนะนำตามตรรกะของไซต์)</summary>
    [HttpGet("bookings/{bookingId:guid}/provider-candidates")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<BookingProviderCandidatesResponse>>> GetProviderCandidates(Guid companyId, Guid siteId, Guid bookingId)
        => Ok(new ApiResponse<BookingProviderCandidatesResponse>(true, await _bookingService.GetProviderCandidatesAsync(companyId, siteId, bookingId)));

    /// <summary>เริ่มให้บริการ + ผู้ให้บริการ (หลายคน · แบ่ง %)</summary>
    [HttpPost("bookings/{bookingId:guid}/start")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> StartService(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] StartBookingServiceRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.StartServiceAsync(companyId, siteId, bookingId, request ?? new StartBookingServiceRequest(), userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "เริ่มให้บริการแล้ว"));
    }

    /// <summary>แก้ผู้ให้บริการ/สัดส่วน % หลังเริ่มให้บริการ</summary>
    [HttpPut("bookings/{bookingId:guid}/assignments")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> SetAssignments(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] StartBookingServiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.SetAssignmentsAsync(companyId, siteId, bookingId, request, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "บันทึกผู้ให้บริการแล้ว"));
    }

    /// <summary>เสร็จสิ้น (+ รับชำระ) — ออกใบกำกับ/ใบเสร็จตามค่าตั้งไซต์และสถานะ VAT ผ่านเส้นเดียวกับปุ่มสถานะ "เสร็จสิ้น" เดิม
    /// (ซึ่งออกเอกสารอัตโนมัติด้วยสิทธิ์ระดับไซต์อยู่แล้ว) ⇒ ด่านสิทธิ์เดียวกับการจัดการการจอง ไม่เข้มกว่าเส้นเดิม</summary>
    [HttpPost("bookings/{bookingId:guid}/complete")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> CompleteBooking(Guid companyId, Guid siteId, Guid bookingId,
        [FromBody] CompleteBookingRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.CompleteBookingAsync(companyId, siteId, bookingId, request ?? new CompleteBookingRequest(), userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "บันทึกให้บริการเสร็จแล้ว"));
    }

    /// <summary>ผูก/สร้างสินค้า/บริการในระบบให้บริการจอง (เอกสาร/POS ได้รหัสสินค้า · บัญชีรายได้ · อัตรา VAT ของสินค้า)</summary>
    [HttpPut("services/{serviceId:guid}/product")]
    [RequirePermission(PermissionKeys.CmsSiteSettings)]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> LinkProduct(Guid companyId, Guid siteId, Guid serviceId,
        [FromBody] LinkBookingProductRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.LinkProductAsync(companyId, siteId, serviceId, request ?? new LinkBookingProductRequest(), userId);
        return Ok(new ApiResponse<BookingServiceResponse>(true, result, "บันทึกการผูกสินค้า/บริการแล้ว"));
    }

    /// <summary>สรุปการทำงาน/รายงานผู้ให้บริการของช่วงวันที่ (ค่าเริ่มต้น = เดือนนี้)</summary>
    [HttpGet("provider-report")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<BookingProviderReportResponse>>> GetProviderReport(Guid companyId, Guid siteId,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        var today = BookingSlotEngine.BangkokNow(DateTime.UtcNow).Date;
        var from = fromDate ?? new DateTime(today.Year, today.Month, 1);
        var to = toDate ?? today;
        return Ok(new ApiResponse<BookingProviderReportResponse>(true, await _bookingService.GetProviderReportAsync(companyId, siteId, from, to)));
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
