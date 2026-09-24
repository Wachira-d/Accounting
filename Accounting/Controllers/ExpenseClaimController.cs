using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/expense-claims")]
[Authorize]
public class ExpenseClaimController : ControllerBase
{
    private readonly IExpenseClaimService _expenseService;

    public ExpenseClaimController(IExpenseClaimService expenseService)
    {
        _expenseService = expenseService;
    }

    /// <summary>ด่านสิทธิ์ของใบเบิก (ฝ่ายค้านรอบสอง 193 · R2-C2) — เดิมทุก action มีแค่ <c>[Authorize]</c> ⇒ ผู้ยื่นอนุมัติ/จ่ายใบตัวเองได้ ·
    /// ตัวตัดสินอยู่ที่ <c>Helpers/ExpenseClaimActionPolicy</c> (ผ่าน service — เมธอดเขียนของ service เรียกด่านเดียวกันซ้ำเอง
    /// เพื่อให้มือถือ/ทางเข้าอื่นได้ด่านเดียวกัน) · ที่นี่ตอบ 403 พร้อมข้อความไทยก่อนเข้า service</summary>
    private async Task<ActionResult?> DenyClaimAsync<T>(Guid companyId, Guid claimId, ExpenseClaimAction action)
    {
        var deny = await _expenseService.DenyClaimActionAsync(companyId, claimId, JwtHelper.GetUserIdFromClaims(User), action);
        return deny is { } d ? StatusCode(d.Status, new ApiResponse<T>(false, default!, d.Message)) : null;
    }

    // สร้างใบเบิก = งานของผู้ยื่นเอง (ผู้ยื่น = ผู้ล็อกอิน · ไม่มีการสร้างแทนคนอื่น) — ไม่ต้องใช้คีย์ · checker รู้จักผ่าน
    // SELF_SERVICE_POSTS ของ write_permission_gate_check
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Create(
        Guid companyId, [FromBody] CreateExpenseClaimRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<ExpenseClaimResponse>(true, result, "สร้างใบเบิกค่าใช้จ่ายสำเร็จ"));
    }

    [HttpGet("{claimId:guid}")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> GetById(Guid companyId, Guid claimId)
    {
        // เดิมไม่มีด่าน — ใครในบริษัทก็เปิดใบเบิกของคนอื่นได้ด้วย id ทั้งที่รายการ (GetAll) กรองให้เห็นแค่ของตัวเอง
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.View) is { } deny) return deny;
        var result = await _expenseService.GetByIdAsync(companyId, claimId);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<ExpenseClaimResponse>>>> GetAll(
        Guid companyId, [FromQuery] ExpenseClaimStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromServices] IPermissionService? permissions = null)
    {
        // Row-level data-scope policy:
        //   Owner / SystemAdmin → see all (existing behaviour)
        //   perm:Expense.Approve OR perm:HR.Admin → see all (HR/manager view)
        //   anyone else → forced filter to their own SubmittedByUserId
        // Without this gate, a plain Employee role could read every
        // claim in the company because the controller only required
        // [Authorize] (any logged-in user). Auditors flagged this as a
        // material data leak.
        var userId = JwtHelper.GetUserIdFromClaims(User);
        Guid? restrictToUserId = null;
        if (permissions != null)
        {
            // ชุดคีย์เดียวกับด่านดูรายใบ (ExpenseClaimActionPolicy.ReviewerKeys(View)) — ผู้จ่าย/ผู้ปฏิเสธต้องเห็นคิวของตัวเองด้วย
            var canSeeAll = false;
            foreach (var key in ExpenseClaimActionPolicy.ReviewerKeys(ExpenseClaimAction.View))
            {
                if (!await permissions.HasPermissionAsync(companyId, userId, key)) continue;
                canSeeAll = true;
                break;
            }
            if (!canSeeAll) restrictToUserId = userId;
        }
        var result = await _expenseService.GetAllAsync(companyId, status, new PagedRequest(page, pageSize), restrictToUserId);
        return Ok(new ApiResponse<PagedResponse<ExpenseClaimResponse>>(true, result));
    }

    [HttpPut("{claimId:guid}")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Update(
        Guid companyId, Guid claimId, [FromBody] UpdateExpenseClaimRequest request)
    {
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.Edit) is { } deny) return deny;
        var result = await _expenseService.UpdateAsync(companyId, claimId, request, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result));
    }

    [HttpPost("{claimId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Submit(Guid companyId, Guid claimId)
    {
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.Submit) is { } deny) return deny;
        var result = await _expenseService.SubmitAsync(companyId, claimId, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "ส่งอนุมัติสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Approve(
        Guid companyId, Guid claimId, [FromBody] ApproveExpenseClaimRequest request)
    {
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.Approve) is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.ApproveAsync(companyId, claimId, userId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "อนุมัติสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Reject(
        Guid companyId, Guid claimId, [FromBody] RejectExpenseClaimRequest request)
    {
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.Reject) is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.RejectAsync(companyId, claimId, userId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "ปฏิเสธสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/pay")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> MarkAsPaid(
        Guid companyId, Guid claimId, [FromBody] PayExpenseClaimRequest request)
    {
        if (await DenyClaimAsync<ExpenseClaimResponse>(companyId, claimId, ExpenseClaimAction.Pay) is { } deny) return deny;
        var result = await _expenseService.MarkAsPaidAsync(companyId, claimId, request, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "บันทึกการจ่ายเงินสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid claimId)
    {
        if (await DenyClaimAsync<bool>(companyId, claimId, ExpenseClaimAction.Void) is { } deny) return deny;
        await _expenseService.VoidAsync(companyId, claimId, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }

    [HttpGet("my-claims")]
    public async Task<ActionResult<ApiResponse<List<ExpenseClaimResponse>>>> GetMyClaims(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.GetMyClaimsAsync(companyId, userId);
        return Ok(new ApiResponse<List<ExpenseClaimResponse>>(true, result));
    }
}
