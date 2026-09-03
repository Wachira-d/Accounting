using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>หน้าเว็บสาธารณะของที่พัก (storefront) — ไม่ต้องล็อกอิน scope ด้วย siteId
/// (ที่พักผูกกับ Site 1:1; ทุกเมธอดของบริการกรอง CompanyId + SiteId) ·
/// การจองที่สร้างแล้วเข้าถึงได้ด้วย PublicToken เท่านั้น (ไม่มี id เดาได้)</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/lodging")]
[AllowAnonymous]
public class LodgingPublicController : ControllerBase
{
    private readonly ILodgingService _svc;
    public LodgingPublicController(ILodgingService svc) { _svc = svc; }

    [HttpGet("info")]
    public async Task<ActionResult<ApiResponse<LodgingPublicInfo>>> Info(Guid companyId, Guid siteId)
    {
        var info = await _svc.GetPublicInfoAsync(companyId, siteId);
        if (info == null) return NotFound(new ApiResponse<LodgingPublicInfo>(false, null, "เว็บไซต์นี้ไม่มีที่พักเปิดให้จอง"));
        return Ok(new ApiResponse<LodgingPublicInfo>(true, info));
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResponse<List<LodgingSearchResult>>>> Search(Guid companyId, Guid siteId, [FromBody] LodgingSearchRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<List<LodgingSearchResult>>(false, null, "ไม่พบที่พัก"));
        return Ok(new ApiResponse<List<LodgingSearchResult>>(true, await _svc.SearchAsync(companyId, pid.Value, req)));
    }

    [HttpPost("quote")]
    public async Task<ActionResult<ApiResponse<LodgingQuoteResponse>>> Quote(Guid companyId, Guid siteId, [FromBody] LodgingQuoteRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<LodgingQuoteResponse>(false, null, "ไม่พบที่พัก"));
        return Ok(new ApiResponse<LodgingQuoteResponse>(true, await _svc.QuoteAsync(companyId, pid.Value, req)));
    }

    [HttpPost("reservations")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Create(Guid companyId, Guid siteId, [FromBody] LodgingCreateReservationRequest req)
    {
        var pid = await _svc.ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบที่พัก"));
        // ช่องฝั่งพนักงานถูกตัดทิ้งเสมอ — แขกกำหนด source/contact/ยืนยันทันทีเองไม่ได้
        var clean = req with { Source = null, SourceReference = null, ContactId = null, InternalNotes = null, ConfirmImmediately = false };
        var r = await _svc.CreateReservationAsync(companyId, pid.Value, clean, LodgingReservationSource.Web, "storefront-guest", siteId);
        var msg = r.Status == LodgingReservationStatus.Confirmed
            ? $"จองสำเร็จ เลขที่ {r.ReservationNumber}"
            : $"รับคำขอจอง {r.ReservationNumber} แล้ว — กรุณาชำระมัดจำ {r.DepositRequired:N2} บาท และอัปโหลดสลิป";
        return StatusCode(201, new ApiResponse<LodgingReservationResponse>(true, r, msg));
    }

    [HttpGet("reservations/{token}")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Get(Guid companyId, Guid siteId, string token)
    {
        var r = await _svc.GetReservationByTokenAsync(companyId, siteId, token);
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r));
    }

    [HttpPost("reservations/{token}/slip")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> UploadSlip(Guid companyId, Guid siteId, string token, IFormFile? file, [FromForm] string? reference)
    {
        if (file == null || file.Length == 0) return BadRequest(new ApiResponse<LodgingReservationResponse>(false, null, "กรุณาเลือกไฟล์สลิป"));
        var r = await _svc.UploadSlipByTokenAsync(companyId, siteId, token, file, reference);
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r, "ส่งสลิปแล้ว — ที่พักจะตรวจสอบและยืนยันการจอง"));
    }

    [HttpPost("reservations/{token}/cancel")]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Cancel(Guid companyId, Guid siteId, string token, [FromBody] LodgingCancelRequest? req)
    {
        var r = await _svc.CancelByTokenAsync(companyId, siteId, token, req ?? new LodgingCancelRequest());
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        var msg = r.RefundAmount > 0 ? $"ยกเลิกแล้ว · ที่พักจะคืนเงิน {r.RefundAmount:N2} บาท" : (r.CancellationFee > 0 ? $"ยกเลิกแล้ว · ค่าปรับตามนโยบาย {r.CancellationFee:N2} บาท" : "ยกเลิกแล้ว");
        return Ok(new ApiResponse<LodgingReservationResponse>(true, r, msg));
    }

    [HttpPost("reservations/{token}/requests")]
    public async Task<ActionResult<ApiResponse<LodgingGuestRequestDto>>> GuestRequest(Guid companyId, Guid siteId, string token, [FromBody] LodgingGuestRequestCreate req)
    {
        var r = await _svc.CreateGuestRequestByTokenAsync(companyId, siteId, token, req);
        if (r == null) return NotFound(new ApiResponse<LodgingGuestRequestDto>(false, null, "ไม่พบการจอง"));
        return Ok(new ApiResponse<LodgingGuestRequestDto>(true, r, "ส่งคำขอถึงที่พักแล้ว"));
    }
}
