using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/salary-advances")]
[Authorize]
public class SalaryAdvanceController : ControllerBase
{
    private readonly ISalaryAdvanceService _service;

    public SalaryAdvanceController(ISalaryAdvanceService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<SalaryAdvanceResponse>>>> GetAll(
        Guid companyId, [FromQuery] string? status = null, [FromQuery] Guid? employeeId = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
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
