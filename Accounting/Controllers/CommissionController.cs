using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/commissions")]
[Authorize]
public class CommissionController : ControllerBase
{
    private readonly ICommissionService _service;
    public CommissionController(ICommissionService service) => _service = service;

    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<CommissionPlanResponse>>> CreatePlan(Guid companyId, [FromBody] CreateCommissionPlanRequest request)
        => StatusCode(201, new ApiResponse<CommissionPlanResponse>(true, await _service.CreatePlanAsync(companyId, request)));

    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<List<CommissionPlanResponse>>>> GetPlans(Guid companyId)
        => Ok(new ApiResponse<List<CommissionPlanResponse>>(true, await _service.GetPlansAsync(companyId)));

    [HttpPut("plans/{planId:guid}")]
    public async Task<ActionResult<ApiResponse<CommissionPlanResponse>>> UpdatePlan(Guid companyId, Guid planId, [FromBody] UpdateCommissionPlanRequest request)
        => Ok(new ApiResponse<CommissionPlanResponse>(true, await _service.UpdatePlanAsync(companyId, planId, request)));

    [HttpPost("plans/{planId:guid}/assign")]
    public async Task<ActionResult<ApiResponse<bool>>> Assign(Guid companyId, Guid planId, [FromBody] AssignCommissionRequest request)
    { await _service.AssignPlanAsync(companyId, planId, request); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpPost("calculate/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<List<CommissionCalcResponse>>>> Calculate(Guid companyId, int year, int month)
        => Ok(new ApiResponse<List<CommissionCalcResponse>>(true, await _service.CalculateAsync(companyId, year, month)));

    [HttpGet("calculations/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<List<CommissionCalcResponse>>>> GetCalculations(Guid companyId, int year, int month)
        => Ok(new ApiResponse<List<CommissionCalcResponse>>(true, await _service.GetCalculationsAsync(companyId, year, month)));

    [HttpPost("approve/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Approve(Guid companyId, int year, int month)
    { await _service.ApproveCalculationsAsync(companyId, year, month, User.Identity?.Name ?? ""); return Ok(new ApiResponse<bool>(true, true)); }
}
