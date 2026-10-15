using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Consolidation;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Security;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// กลุ่มบริษัท / งบรวม
///
/// <para>═══ ด่านข้ามบริษัท (ทีม X5 2026-10-15 · ผลตรวจ EXPORT_AUDIT §X5) ═══
/// เดิม endpoint ระดับกลุ่มทุกตัวรับ <c>groupId</c> โดย <b>ไม่ตรวจว่ากลุ่มเป็นของบริษัทใน route</b> และ
/// สร้างกลุ่ม/เพิ่มสมาชิกได้ด้วย <c>ParentCompanyId</c>/<c>CompanyId</c> ใด ๆ ⇒ ผู้ใช้บริษัท A สร้างกลุ่มใต้ A
/// แล้วเพิ่มบริษัท B เป็นสมาชิก → เปิด "งบดุลรวม" อ่านยอดบัญชีทั้งผังของ B ได้ (TenantGuardFilter ตรวจแค่ companyId ใน route).
/// ตอนนี้: กลุ่มต้องเป็นของบริษัทใน route (ไม่ใช่ = 404 เหมือนไม่มี) · บริษัทแม่ตอนสร้าง = บริษัทใน route ·
/// บริษัทสมาชิกที่เพิ่ม/ที่ถูกอ่านในรายงาน ผู้ใช้ต้องผ่านด่านสมาชิกบริษัทนั้นด้วย (<see cref="ITenantGuard"/> ตัวเดียวกับ filter กลาง) —
/// ตรวจตอนอ่านรายงานด้วย จึงครอบสมาชิกที่ถูกเพิ่มไว้ก่อนแก้ (ข้อมูลที่ persist แล้วไม่ต้อง migration)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/consolidation")]
[Authorize]
public class ConsolidationController : ControllerBase
{
    private readonly IConsolidationService _service;
    private readonly ITenantGuard _guard;

    public ConsolidationController(IConsolidationService service, ITenantGuard guard)
    {
        _service = service;
        _guard = guard;
    }

    [HttpPost("groups")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> CreateGroup(Guid companyId, [FromBody] CreateConsolidationGroupRequest request)
    {
        if (request.ParentCompanyId != companyId)
            return BadRequest(new ApiResponse<object>(false, null,
                "บริษัทแม่ของกลุ่มต้องเป็นบริษัทที่เปิดอยู่ — สลับไปบริษัทแม่ก่อนแล้วสร้างกลุ่มจากที่นั่น"));
        return StatusCode(201, new ApiResponse<ConsolidationGroupResponse>(true, await _service.CreateGroupAsync(request, User.Identity?.Name ?? "")));
    }

    [HttpGet("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> GetGroup(Guid companyId, Guid groupId)
    {
        var g = await OwnedGroupAsync(companyId, groupId);
        return g == null ? GroupNotFound<ConsolidationGroupResponse>() : Ok(new ApiResponse<ConsolidationGroupResponse>(true, g));
    }

    [HttpGet("groups")]
    public async Task<ActionResult<ApiResponse<List<ConsolidationGroupResponse>>>> GetGroups(Guid companyId)
        => Ok(new ApiResponse<List<ConsolidationGroupResponse>>(true, await _service.GetGroupsAsync(companyId)));

    [HttpPut("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> UpdateGroup(Guid companyId, Guid groupId, [FromBody] UpdateConsolidationGroupRequest request)
    {
        if (await OwnedGroupAsync(companyId, groupId) == null) return GroupNotFound<ConsolidationGroupResponse>();
        return Ok(new ApiResponse<ConsolidationGroupResponse>(true, await _service.UpdateGroupAsync(groupId, request)));
    }

    [HttpDelete("groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteGroup(Guid companyId, Guid groupId)
    {
        if (await OwnedGroupAsync(companyId, groupId) == null) return GroupNotFound<bool>();
        await _service.DeleteGroupAsync(groupId);
        return Ok(new ApiResponse<bool>(true, true, "ลบกลุ่มบริษัทสำเร็จ"));
    }

    [HttpPost("groups/{groupId:guid}/members")]
    public async Task<ActionResult<ApiResponse<ConsolidationGroupResponse>>> AddMember(Guid companyId, Guid groupId, [FromBody] AddConsolidationMemberRequest request)
    {
        if (await OwnedGroupAsync(companyId, groupId) == null) return GroupNotFound<ConsolidationGroupResponse>();
        var check = await _guard.CheckAsync(request.CompanyId, User);
        if (check.IsBlocked)
            return StatusCode(403, new ApiResponse<object>(false, null,
                "เพิ่มบริษัทนี้เป็นสมาชิกไม่ได้ — คุณไม่ได้เป็นผู้ใช้ของบริษัทนั้น (งบรวมอ่านยอดบัญชีของสมาชิกทุกบริษัท)"));
        return StatusCode(201, new ApiResponse<ConsolidationGroupResponse>(true, await _service.AddMemberAsync(groupId, request)));
    }

    [HttpDelete("groups/{groupId:guid}/members/{memberId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveMember(Guid companyId, Guid groupId, Guid memberId)
    {
        if (await OwnedGroupAsync(companyId, groupId) == null) return GroupNotFound<bool>();
        await _service.RemoveMemberAsync(groupId, memberId);
        return Ok(new ApiResponse<bool>(true, true, "ลบสมาชิกสำเร็จ"));
    }

    [HttpGet("groups/{groupId:guid}/balance-sheet")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetBalanceSheet(Guid companyId, Guid groupId, [FromQuery] DateTime asOfDate)
    {
        var denied = await DenyUnlessReadableAsync(companyId, groupId);
        if (denied != null) return denied;
        return Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedBalanceSheetAsync(groupId, asOfDate)));
    }

    [HttpGet("groups/{groupId:guid}/pnl")]
    public async Task<ActionResult<ApiResponse<ConsolidatedReportResponse>>> GetPnL(Guid companyId, Guid groupId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
    {
        var denied = await DenyUnlessReadableAsync(companyId, groupId);
        if (denied != null) return denied;
        return Ok(new ApiResponse<ConsolidatedReportResponse>(true, await _service.GenerateConsolidatedPnLAsync(groupId, fromDate, toDate)));
    }

    [HttpGet("groups/{groupId:guid}/eliminations")]
    public async Task<ActionResult<ApiResponse<List<EliminationEntryResponse>>>> GetEliminations(Guid companyId, Guid groupId, [FromQuery] DateTime asOfDate)
    {
        var denied = await DenyUnlessReadableAsync(companyId, groupId);
        if (denied != null) return denied;
        return Ok(new ApiResponse<List<EliminationEntryResponse>>(true, await _service.GetEliminationEntriesAsync(groupId, asOfDate)));
    }

    // ── ด่าน ──

    /// <summary>กลุ่มที่เป็นของบริษัทใน route · ไม่พบ/เป็นของบริษัทอื่น = null (ตอบ 404 เหมือนไม่มี — ไม่บอกว่ามีกลุ่มนี้ในบริษัทอื่น)</summary>
    private async Task<ConsolidationGroupResponse?> OwnedGroupAsync(Guid companyId, Guid groupId)
    {
        ConsolidationGroupResponse g;
        try { g = await _service.GetGroupAsync(groupId); }
        catch (KeyNotFoundException) { return null; }
        return g.ParentCompanyId == companyId ? g : null;
    }

    /// <summary>รายงานงบรวมอ่านยอดบัญชีของสมาชิกทุกบริษัท ⇒ ผู้ใช้ต้องเป็นสมาชิกของ <b>ทุก</b> บริษัทในกลุ่ม</summary>
    private async Task<ActionResult?> DenyUnlessReadableAsync(Guid companyId, Guid groupId)
    {
        var g = await OwnedGroupAsync(companyId, groupId);
        if (g == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบกลุ่มบริษัท"));
        foreach (var m in g.Members.Where(m => m.IsActive))
        {
            var check = await _guard.CheckAsync(m.CompanyId, User);
            if (check.IsBlocked)
                return StatusCode(403, new ApiResponse<object>(false, null,
                    $"ดูงบรวมของกลุ่มนี้ไม่ได้ — คุณไม่ได้เป็นผู้ใช้ของบริษัทสมาชิก \"{m.CompanyName}\" · ให้ผู้ดูแลบริษัทนั้นเพิ่มคุณ หรือเอาบริษัทนั้นออกจากกลุ่ม"));
        }
        return null;
    }

    private ActionResult GroupNotFound<T>() => NotFound(new ApiResponse<T>(false, default, "ไม่พบกลุ่มบริษัท"));
}
