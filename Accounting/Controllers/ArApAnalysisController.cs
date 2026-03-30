using Accounting.Models.DTOs;
using Accounting.Models.DTOs.ArApAnalysis;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/arap-analysis")]
[Authorize]
public class ArApAnalysisController : ControllerBase
{
    private readonly IArApAnalysisService _service;

    public ArApAnalysisController(IArApAnalysisService service) => _service = service;

    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<ArApOverviewResponse>>> GetOverview(Guid companyId)
    {
        var result = await _service.GetOverviewAsync(companyId);
        return Ok(new ApiResponse<ArApOverviewResponse>(true, result));
    }

    [HttpGet("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<ContactArApDetailResponse>>> GetContactDetail(
        Guid companyId, Guid contactId, [FromQuery] string type = "ar")
    {
        var result = await _service.GetContactDetailAsync(companyId, contactId, type);
        return Ok(new ApiResponse<ContactArApDetailResponse>(true, result));
    }

    [HttpGet("bad-debt")]
    public async Task<ActionResult<ApiResponse<BadDebtAnalysisResponse>>> GetBadDebtAnalysis(Guid companyId)
    {
        var result = await _service.GetBadDebtAnalysisAsync(companyId);
        return Ok(new ApiResponse<BadDebtAnalysisResponse>(true, result));
    }
}
