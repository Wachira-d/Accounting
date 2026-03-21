using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Approval;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class ApprovalController : ControllerBase
{
    private readonly IApprovalService _approvalService;

    public ApprovalController(IApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    // ===== Rules =====

    [HttpGet("rules")]
    public async Task<ActionResult<ApiResponse<List<ApprovalRuleResponse>>>> GetRules(Guid companyId)
    {
        var result = await _approvalService.GetRulesAsync(companyId);
        return Ok(new ApiResponse<List<ApprovalRuleResponse>>(true, result));
    }

    [HttpPost("rules")]
    public async Task<ActionResult<ApiResponse<ApprovalRuleResponse>>> CreateRule(
        Guid companyId, [FromBody] CreateApprovalRuleRequest request)
    {
        var result = await _approvalService.CreateRuleAsync(companyId, request);
        return StatusCode(201, new ApiResponse<ApprovalRuleResponse>(true, result, "สร้างกฎการอนุมัติสำเร็จ"));
    }

    [HttpPut("rules/{ruleId:guid}")]
    public async Task<ActionResult<ApiResponse<ApprovalRuleResponse>>> UpdateRule(
        Guid companyId, Guid ruleId, [FromBody] CreateApprovalRuleRequest request)
    {
        var result = await _approvalService.UpdateRuleAsync(companyId, ruleId, request);
        return Ok(new ApiResponse<ApprovalRuleResponse>(true, result));
    }

    [HttpDelete("rules/{ruleId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteRule(Guid companyId, Guid ruleId)
    {
        await _approvalService.DeleteRuleAsync(companyId, ruleId);
        return NoContent();
    }

    // ===== Requests =====

    [HttpPost("submit")]
    public async Task<ActionResult<ApiResponse<ApprovalRequestResponse>>> SubmitForApproval(
        Guid companyId, [FromQuery] string entityType, [FromQuery] Guid entityId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _approvalService.SubmitForApprovalAsync(companyId, entityType, entityId, userId);
        return Ok(new ApiResponse<ApprovalRequestResponse>(true, result, "ส่งคำขออนุมัติสำเร็จ"));
    }

    [HttpGet("requests/{requestId:guid}")]
    public async Task<ActionResult<ApiResponse<ApprovalRequestResponse>>> GetRequest(Guid companyId, Guid requestId)
    {
        var result = await _approvalService.GetApprovalRequestAsync(companyId, requestId);
        return Ok(new ApiResponse<ApprovalRequestResponse>(true, result));
    }

    [HttpGet("pending")]
    public async Task<ActionResult<ApiResponse<List<ApprovalRequestResponse>>>> GetPending(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _approvalService.GetPendingApprovalsAsync(companyId, userId);
        return Ok(new ApiResponse<List<ApprovalRequestResponse>>(true, result));
    }

    [HttpPost("requests/{requestId:guid}/action")]
    public async Task<ActionResult<ApiResponse<ApprovalRequestResponse>>> SubmitAction(
        Guid companyId, Guid requestId, [FromBody] SubmitApprovalActionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _approvalService.SubmitActionAsync(companyId, requestId, userId, request);
        return Ok(new ApiResponse<ApprovalRequestResponse>(true, result));
    }
}
