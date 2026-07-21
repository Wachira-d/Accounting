using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Consolidation;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/consolidation")]
[Authorize]
public class ConsolidationController : ControllerBase
{
    private readonly IConsolidationService _service;
    public ConsolidationController(IConsolidationService service) => _service = service;

    [HttpPost("groups")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> CreateGroup(Guid companyId, [FromBody] CreateConsolidationGroupRequest request)
        => StatusCode(201, new ApiResponse<ConsolidationGroupResponse>(true, await _service.CreateGroupAsync(request, User.Identity?.Name ?? "")));

    [HttpGet("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> GetGroup(Guid companyId, Guid groupId)
        => Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.GetGroupAsync(groupId)));

    [HttpGet("groups")]
    public async Task<ActionResult<ApiResponse<List<ConsolidationGroupResponse>>>> GetGroups(Guid companyId)
        => Ok(new ApiResponse<List<ConsolidationGroupResponse>>(true, await _service.GetGroupsAsync(companyId)));

    [HttpPut("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> UpdateGroup(Guid companyId, Guid groupId, [FromBody] UpdateConsolidationGroupRequest request)
        => Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.UpdateGroupAsync(groupId, request)));

    [HttpDelete("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteGroup(Guid companyId, Guid groupId)
    {
        await _service.DeleteGroupAsync(groupId);
        return Ok(new ApiResponse<bool>(true, true, "ลบกลุ่มบริษัทสำเร็จ"));
    }

    [HttpPost("groups/{groupId:guid}/members")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> AddMember(Guid companyId, Guid groupId, [FromBody] AddConsolidationMemberRequest request)
        => StatusCode(201, new ApiResponse<ConsolidationGroupResponse>(true, await _service.AddMemberAsync(groupId, request)));

    [HttpDelete("groups/{groupId:guid}/members/{memberId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveMember(Guid companyId, Guid groupId, Guid memberId)
    {
        await _service.RemoveMemberAsync(groupId, memberId);
        return Ok(new ApiResponse<bool>(true, true, "ลบสมาชิกสำเร็จ"));
    }

    [HttpGet("groups/{groupId:guid}/balance-sheet")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetBalanceSheet(Guid companyId, Guid groupId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedBalanceSheetAsync(groupId, asOfDate)));

    [HttpGet("groups/{groupId:guid}/pnl")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetPnL(Guid companyId, Guid groupId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedPnLAsync(groupId, fromDate, toDate)));

    [HttpGet("groups/{groupId:guid}/eliminations")]
    public async Task<ActionResult<ApiResponse<List<EliminationEntryResponse>>>> GetEliminations(Guid companyId, Guid groupId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<List<EliminationEntryResponse>>(true, await _service.GetEliminationEntriesAsync(groupId, asOfDate)));
}
