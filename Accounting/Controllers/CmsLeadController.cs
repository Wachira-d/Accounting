using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}")]
public class CmsLeadController : ControllerBase
{
    private readonly CmsLeadService _svc;
    public CmsLeadController(CmsLeadService svc) { _svc = svc; }

    // ====================================================================
    // PUBLIC submission — no auth, called from the storefront ContactForm
    // (and the specialized RFQ/Viewing/Demo/Enrollment forms when they
    // ship). Storefront passes captchaToken when the site is configured
    // with hcaptcha / recaptcha.
    // ====================================================================

    [AllowAnonymous]
    [HttpPost("storefront/leads")]
    public async Task<ActionResult<ApiResponse<LeadDetailResponse>>> SubmitLead(
        Guid companyId, Guid siteId, [FromBody] CreateLeadRequest req)
    {
        if (req == null)
            return BadRequest(new ApiResponse<LeadDetailResponse>(false, null, "Invalid request"));
        if (string.IsNullOrWhiteSpace(req.CustomerName) && string.IsNullOrWhiteSpace(req.CustomerEmail) && string.IsNullOrWhiteSpace(req.CustomerPhone))
            return BadRequest(new ApiResponse<LeadDetailResponse>(false, null, "กรุณากรอกชื่อหรือช่องทางติดต่ออย่างน้อย 1 ช่อง"));

        var result = await _svc.CreateAsync(companyId, siteId, req);
        return Ok(new ApiResponse<LeadDetailResponse>(true, result, "ขอบคุณค่ะ — ส่งคำขอแล้ว ทีมงานจะติดต่อกลับเร็วๆ นี้"));
    }

    // ====================================================================
    // OWNER reads + state transitions — requires the user to be a member
    // of the company (any role; this is a sales-funnel-tier action and
    // most company users should be able to view leads).
    // ====================================================================

    [Authorize]
    [HttpGet("leads")]
    public async Task<ActionResult<ApiResponse<PagedResponse<LeadListResponse>>>> List(
        Guid companyId, Guid siteId,
        [FromQuery] LeadType? type = null,
        [FromQuery] LeadStatus? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var result = await _svc.ListAsync(companyId, siteId, type, status, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<LeadListResponse>>(true, result));
    }

    [Authorize]
    [HttpGet("leads/{leadId:guid}")]
    public async Task<ActionResult<ApiResponse<LeadDetailResponse>>> Get(Guid companyId, Guid siteId, Guid leadId)
    {
        var result = await _svc.GetAsync(companyId, siteId, leadId);
        if (result == null) return NotFound(new ApiResponse<LeadDetailResponse>(false, null, "ไม่พบ lead"));
        return Ok(new ApiResponse<LeadDetailResponse>(true, result));
    }

    [Authorize]
    [HttpPut("leads/{leadId:guid}/status")]
    public async Task<ActionResult<ApiResponse<LeadDetailResponse>>> UpdateStatus(
        Guid companyId, Guid siteId, Guid leadId, [FromBody] UpdateLeadStatusRequest req)
    {
        var userId = User.Identity?.Name ?? "system";
        var result = await _svc.UpdateStatusAsync(companyId, siteId, leadId, req, userId);
        if (result == null) return NotFound(new ApiResponse<LeadDetailResponse>(false, null, "ไม่พบ lead"));
        return Ok(new ApiResponse<LeadDetailResponse>(true, result, "อัปเดตสถานะเรียบร้อย"));
    }

    [Authorize]
    [HttpPost("leads/{leadId:guid}/convert-to-quotation")]
    public async Task<ActionResult<ApiResponse<LeadDetailResponse>>> ConvertToQuotation(
        Guid companyId, Guid siteId, Guid leadId, [FromBody] ConvertLeadToQuotationRequest req)
    {
        var userId = User.Identity?.Name ?? "system";
        try
        {
            var result = await _svc.ConvertToQuotationAsync(companyId, siteId, leadId, req, userId);
            if (result == null) return NotFound(new ApiResponse<LeadDetailResponse>(false, null, "ไม่พบ lead"));
            return Ok(new ApiResponse<LeadDetailResponse>(true, result, "สร้างใบเสนอราคาแล้ว"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<LeadDetailResponse>(false, null, ex.Message));
        }
    }
}
