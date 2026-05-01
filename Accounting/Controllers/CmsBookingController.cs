using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
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

    // ===== Booking Services =====

    [HttpPost("services")]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> CreateService(
        Guid companyId, Guid siteId, [FromBody] CreateBookingServiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.CreateServiceAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<BookingServiceResponse>(true, result, "สร้างบริการสำเร็จ"));
    }

    [HttpGet("services")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<BookingServiceResponse>>>> GetServices(Guid companyId, Guid siteId)
    {
        var result = await _bookingService.GetServicesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<BookingServiceResponse>>(true, result));
    }

    [HttpGet("services/{serviceId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> GetService(Guid companyId, Guid siteId, Guid serviceId)
    {
        var result = await _bookingService.GetServiceAsync(companyId, siteId, serviceId);
        if (result == null) return NotFound(new ApiResponse<BookingServiceResponse>(false, null, "ไม่พบบริการ"));
        return Ok(new ApiResponse<BookingServiceResponse>(true, result));
    }

    [HttpPut("services/{serviceId:guid}")]
    public async Task<ActionResult<ApiResponse<BookingServiceResponse>>> UpdateService(
        Guid companyId, Guid siteId, Guid serviceId, [FromBody] UpdateBookingServiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.UpdateServiceAsync(companyId, siteId, serviceId, request, userId);
        return Ok(new ApiResponse<BookingServiceResponse>(true, result, "อัปเดตบริการสำเร็จ"));
    }

    [HttpDelete("services/{serviceId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteService(Guid companyId, Guid siteId, Guid serviceId)
    {
        var result = await _bookingService.DeleteServiceAsync(companyId, siteId, serviceId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบบริการ"));
        return Ok(new ApiResponse<bool>(true, true, "ลบบริการสำเร็จ"));
    }

    // ===== Booking Slots =====

    [HttpPost("services/{serviceId:guid}/slots")]
    public async Task<ActionResult<ApiResponse<BookingSlotResponse>>> CreateSlot(
        Guid companyId, Guid siteId, Guid serviceId, [FromBody] CreateBookingSlotRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.CreateSlotAsync(companyId, siteId, serviceId, request, userId);
        return StatusCode(201, new ApiResponse<BookingSlotResponse>(true, result, "สร้างช่วงเวลาสำเร็จ"));
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
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบช่วงเวลา"));
        return Ok(new ApiResponse<bool>(true, true, "ลบช่วงเวลาสำเร็จ"));
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
        return StatusCode(201, new ApiResponse<BookingResponse>(true, result, "สร้างการจองสำเร็จ"));
    }

    [HttpGet("bookings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BookingListResponse>>>> GetBookings(
        Guid companyId, Guid siteId,
        [FromQuery] string? status = null, [FromQuery] Guid? serviceId = null,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _bookingService.GetBookingsAsync(companyId, siteId, status, serviceId, fromDate, toDate, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<BookingListResponse>>(true, result));
    }

    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> GetBooking(Guid companyId, Guid siteId, Guid bookingId)
    {
        var result = await _bookingService.GetBookingAsync(companyId, siteId, bookingId);
        if (result == null) return NotFound(new ApiResponse<BookingResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<BookingResponse>(true, result));
    }

    [HttpPut("bookings/{bookingId:guid}/status")]
    public async Task<ActionResult<ApiResponse<BookingResponse>>> UpdateBookingStatus(
        Guid companyId, Guid siteId, Guid bookingId, [FromBody] UpdateBookingStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bookingService.UpdateBookingStatusAsync(companyId, siteId, bookingId, request, userId);
        return Ok(new ApiResponse<BookingResponse>(true, result, "อัปเดตสถานะการจองสำเร็จ"));
    }

    [HttpPost("bookings/{bookingId:guid}/sync-erp")]
    public async Task<ActionResult<ApiResponse<Guid?>>> SyncBookingToErp(Guid companyId, Guid siteId, Guid bookingId)
    {
        var documentId = await _bookingService.SyncBookingToErpAsync(companyId, siteId, bookingId);
        return Ok(new ApiResponse<Guid?>(true, documentId, documentId != null ? "ซิงค์เอกสาร ERP สำเร็จ" : "ไม่สามารถซิงค์ได้"));
    }
}
