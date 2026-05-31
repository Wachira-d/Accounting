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
            var canSeeAll = await permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin)
                         || await permissions.HasPermissionAsync(companyId, userId, PermissionKeys.ExpenseApprove);
            if (!canSeeAll) restrictToUserId = userId;
        }
        var result = await _expenseService.GetAllAsync(companyId, status, new PagedRequest(page, pageSize), restrictToUserId);
        return Ok(new ApiResponse<PagedResponse<ExpenseClaimResponse>>(true, result));
    }

    [HttpPut("{claimId:guid}")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Update(
        Guid companyId, Guid claimId, [FromBody] UpdateExpenseClaimRequest request)
    {
        var result = await _expenseService.UpdateAsync(companyId, claimId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result));
    }

    [HttpPost("{claimId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Submit(Guid companyId, Guid claimId)
    {
        var result = await _expenseService.SubmitAsync(companyId, claimId);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "ส่งอนุมัติสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Approve(
        Guid companyId, Guid claimId, [FromBody] ApproveExpenseClaimRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.ApproveAsync(companyId, claimId, userId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "อนุมัติสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> Reject(
        Guid companyId, Guid claimId, [FromBody] RejectExpenseClaimRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _expenseService.RejectAsync(companyId, claimId, userId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "ปฏิเสธสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/pay")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimResponse>>> MarkAsPaid(
        Guid companyId, Guid claimId, [FromBody] PayExpenseClaimRequest request)
    {
        var result = await _expenseService.MarkAsPaidAsync(companyId, claimId, request);
        return Ok(new ApiResponse<ExpenseClaimResponse>(true, result, "บันทึกการจ่ายเงินสำเร็จ"));
    }

    [HttpPost("{claimId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid claimId)
    {
        await _expenseService.VoidAsync(companyId, claimId);
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
