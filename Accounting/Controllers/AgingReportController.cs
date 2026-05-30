using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Aging;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/aging")]
[Authorize]
public class AgingReportController : ControllerBase
{
    private readonly IAgingReportService _agingService;

    public AgingReportController(IAgingReportService agingService)
    {
        _agingService = agingService;
    }

    [HttpGet("receivables")]
    public async Task<ActionResult<ApiResponse<AgingReportResponse>>> GetReceivableAging(
        Guid companyId, [FromQuery] DateTime? asOfDate, [FromQuery] Guid? contactId, [FromQuery] Guid? projectId)
    {
        var result = await _agingService.GetReceivableAgingAsync(companyId, new AgingReportRequest(asOfDate, contactId, null, projectId));
        return Ok(new ApiResponse<AgingReportResponse>(true, result));
    }

    [HttpGet("payables")]
    public async Task<ActionResult<ApiResponse<AgingReportResponse>>> GetPayableAging(
        Guid companyId, [FromQuery] DateTime? asOfDate, [FromQuery] Guid? contactId, [FromQuery] Guid? projectId)
    {
        var result = await _agingService.GetPayableAgingAsync(companyId, new AgingReportRequest(asOfDate, contactId, null, projectId));
        return Ok(new ApiResponse<AgingReportResponse>(true, result));
    }

    [HttpGet("contacts/{contactId:guid}/receivables")]
    public async Task<ActionResult<ApiResponse<AgingContactDetail>>> GetContactReceivableDetail(
        Guid companyId, Guid contactId, [FromQuery] DateTime? asOfDate)
    {
        var result = await _agingService.GetContactAgingDetailAsync(companyId, contactId, AgingReportType.AccountsReceivable, asOfDate);
        return Ok(new ApiResponse<AgingContactDetail>(true, result));
    }

    [HttpGet("contacts/{contactId:guid}/payables")]
    public async Task<ActionResult<ApiResponse<AgingContactDetail>>> GetContactPayableDetail(
        Guid companyId, Guid contactId, [FromQuery] DateTime? asOfDate)
    {
        var result = await _agingService.GetContactAgingDetailAsync(companyId, contactId, AgingReportType.AccountsPayable, asOfDate);
        return Ok(new ApiResponse<AgingContactDetail>(true, result));
    }
}
