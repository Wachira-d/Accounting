using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/consolidation")]
[Authorize]
public class ConsolidationController : ControllerBase
{
    private readonly IConsolidationService _service;
    public ConsolidationController(IConsolidationService service) => _service = service;

    [HttpPost("groups")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> CreateGroup([FromBody] CreateConsolidationGroupRequest request)
        => Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.CreateGroupAsync(request, User.Identity?.Name ?? "")));

    [HttpGet("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> GetGroup(Guid groupId)
        => Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.GetGroupAsync(groupId)));

    [HttpGet("groups")]
    public async Task<ActionResult<ApiResponse<List<ConsolidationGroupResponse>>>> GetGroups([FromQuery] Guid parentCompanyId)
        => Ok(new ApiResponse<List<ConsolidationGroupResponse>>(true, await _service.GetGroupsAsync(parentCompanyId)));

    [HttpPost("groups/{groupId:guid}/members")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> AddMember(Guid groupId, [FromBody] AddConsolidationMemberRequest request)
        => Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.AddMemberAsync(groupId, request)));

    [HttpDelete("groups/{groupId:guid}/members/{memberId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveMember(Guid groupId, Guid memberId)
    { await _service.RemoveMemberAsync(groupId, memberId); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpGet("groups/{groupId:guid}/balance-sheet")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetBalanceSheet(Guid groupId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedBalanceSheetAsync(groupId, asOfDate)));

    [HttpGet("groups/{groupId:guid}/pnl")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetPnL(Guid groupId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedPnLAsync(groupId, fromDate, toDate)));

    [HttpGet("groups/{groupId:guid}/eliminations")]
    public async Task<ActionResult<ApiResponse<List<EliminationEntryResponse>>>> GetEliminations(Guid groupId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<List<EliminationEntryResponse>>(true, await _service.GetEliminationEntriesAsync(groupId, asOfDate)));
}
