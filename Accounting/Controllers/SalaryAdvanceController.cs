using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/salary-advances")]
[Authorize]
public class SalaryAdvanceController : ControllerBase
{
    private readonly ISalaryAdvanceService _service;
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _permissions;

    public SalaryAdvanceController(ISalaryAdvanceService service, AccountingDbContext db, IPermissionService permissions)
    { _service = service; _db = db; _permissions = permissions; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<SalaryAdvanceResponse>>>> GetAll(
        Guid companyId, [FromQuery] string? status = null, [FromQuery] Guid? employeeId = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        // Row-level scope — same model as ExpenseClaimController.
        // When the caller lacks BOTH perm:HR.Admin AND perm:Advance.Approve,
        // they can only see THEIR OWN salary-advance requests. Otherwise
        // honour the employeeId query param so HR can drill in.
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var canSeeAll = await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.HrAdmin)
                     || await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.AdvanceApprove);
        if (!canSeeAll)
        {
            // Resolve the caller's Employee row from their JWT user id;
            // override employeeId so the underlying query is forced to
            // the right scope no matter what the caller passed.
            var myEmpId = await _db.Employees.AsNoTracking()
                .Where(e => e.CompanyId == companyId && e.UserId == userId && !e.IsDeleted)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync();
            if (!myEmpId.HasValue)
                // No employee record — return empty list rather than 403 so
                // the UI can render a sensible "no advances" state.
                return Ok(new ApiResponse<PagedResponse<SalaryAdvanceResponse>>(true,
                    new PagedResponse<SalaryAdvanceResponse>(new List<SalaryAdvanceResponse>(), 0, page, pageSize, 0)));
            employeeId = myEmpId.Value;
        }
        var result = await _service.GetAllAsync(companyId, status, employeeId, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<SalaryAdvanceResponse>>(true, result));
    }

    [HttpGet("{advanceId:guid}")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> GetById(Guid companyId, Guid advanceId)
    {
        var result = await _service.GetByIdAsync(companyId, advanceId);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Create(
        Guid companyId, [FromBody] CreateSalaryAdvanceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _service.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<SalaryAdvanceResponse>(true, result, "สร้างรายการเงินทดรองจ่ายสำเร็จ"));
    }

    [HttpPut("{advanceId:guid}")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Update(
        Guid companyId, Guid advanceId, [FromBody] UpdateSalaryAdvanceRequest request)
    {
        var result = await _service.UpdateAsync(companyId, advanceId, request);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result));
    }

    [HttpPost("{advanceId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Submit(Guid companyId, Guid advanceId)
    {
        var result = await _service.SubmitAsync(companyId, advanceId);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result, "ส่งอนุมัติสำเร็จ"));
    }

    [HttpPost("{advanceId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Approve(
        Guid companyId, Guid advanceId, [FromBody] ApproveSalaryAdvanceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _service.ApproveAsync(companyId, advanceId, userId, request);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result, "อนุมัติสำเร็จ"));
    }

    [HttpPost("{advanceId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Reject(
        Guid companyId, Guid advanceId, [FromBody] RejectSalaryAdvanceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _service.RejectAsync(companyId, advanceId, userId, request);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result, "ปฏิเสธรายการแล้ว"));
    }

    [HttpPost("{advanceId:guid}/disburse")]
    public async Task<ActionResult<ApiResponse<SalaryAdvanceResponse>>> Disburse(
        Guid companyId, Guid advanceId, [FromBody] DisburseSalaryAdvanceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _service.DisburseAsync(companyId, advanceId, request, userId);
        return Ok(new ApiResponse<SalaryAdvanceResponse>(true, result, "จ่ายเงินทดรองและสร้างใบสำคัญจ่ายสำเร็จ"));
    }

    [HttpPost("{advanceId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> Void(Guid companyId, Guid advanceId)
    {
        await _service.VoidAsync(companyId, advanceId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกรายการสำเร็จ"));
    }
}
