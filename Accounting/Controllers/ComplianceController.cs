using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Compliance;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/compliance")]
[Authorize]
public class ComplianceController : ControllerBase
{
    private readonly IComplianceService _service;
    public ComplianceController(IComplianceService service) => _service = service;

    [HttpPost("filings")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Create(Guid companyId, [FromBody] CreateComplianceFilingRequest request)
        => StatusCode(201, new ApiResponse<ComplianceFilingResponse>(true, await _service.CreateFilingAsync(companyId, request)));

    [HttpGet("filings/{filingId:guid}")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> GetById(Guid companyId, Guid filingId)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.GetFilingAsync(companyId, filingId)));

    [HttpGet("filings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ComplianceFilingResponse>>>> GetAll(Guid companyId, [FromQuery] int? year, [FromQuery] string? filingType, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ComplianceFilingResponse>>(true, await _service.GetFilingsAsync(companyId, year, filingType, new PagedRequest(page, pageSize))));

    [HttpPost("filings/{filingId:guid}/validate")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Validate(Guid companyId, Guid filingId)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.ValidateFilingAsync(companyId, filingId)));

    [HttpPost("filings/{filingId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<ComplianceFilingResponse>>> Submit(Guid companyId, Guid filingId)
        => Ok(new ApiResponse<ComplianceFilingResponse>(true, await _service.SubmitFilingAsync(companyId, filingId, User.Identity?.Name ?? "")));

    [HttpGet("pending")]
    public async Task<ActionResult<ApiResponse<List<ComplianceFilingResponse>>>> GetPending(Guid companyId)
        => Ok(new ApiResponse<List<ComplianceFilingResponse>>(true, await _service.GetPendingFilingsAsync(companyId)));

    [HttpPost("initialize/{year:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Initialize(Guid companyId, int year)
    { await _service.InitializeFilingCalendarAsync(companyId, year); return Ok(new ApiResponse<bool>(true, true)); }
}
