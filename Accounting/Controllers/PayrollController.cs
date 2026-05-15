using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/payroll")]
[Authorize]
public class PayrollController : ControllerBase
{
    private readonly IPayrollService _service;
    public PayrollController(IPayrollService service) => _service = service;

    // Employees
    [HttpPost("employees")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> CreateEmployee(Guid companyId, [FromBody] CreateEmployeeRequest request)
        => StatusCode(201, new ApiResponse<EmployeeResponse>(true, await _service.CreateEmployeeAsync(companyId, request)));

    [HttpGet("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> GetEmployee(Guid companyId, Guid employeeId)
        => Ok(new ApiResponse<EmployeeResponse>(true, await _service.GetEmployeeAsync(companyId, employeeId)));

    [HttpGet("employees")]
    public async Task<ActionResult<ApiResponse<PagedResponse<EmployeeResponse>>>> GetEmployees(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<EmployeeResponse>>(true, await _service.GetEmployeesAsync(companyId, new PagedRequest(page, pageSize))));

    [HttpPut("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> UpdateEmployee(Guid companyId, Guid employeeId, [FromBody] UpdateEmployeeRequest request)
        => Ok(new ApiResponse<EmployeeResponse>(true, await _service.UpdateEmployeeAsync(companyId, employeeId, request)));

    [HttpPost("employees/{employeeId:guid}/terminate")]
    public async Task<ActionResult<ApiResponse<bool>>> Terminate(Guid companyId, Guid employeeId, [FromQuery] DateTime endDate)
    { await _service.TerminateEmployeeAsync(companyId, employeeId, endDate); return Ok(new ApiResponse<bool>(true, true)); }

    // Payroll Items
    [HttpPost("items")]
    public async Task<ActionResult<ApiResponse<PayrollItemResponse>>> CreateItem(Guid companyId, [FromBody] CreatePayrollItemRequest request)
        => StatusCode(201, new ApiResponse<PayrollItemResponse>(true, await _service.CreatePayrollItemAsync(companyId, request)));

    [HttpGet("items")]
    public async Task<ActionResult<ApiResponse<List<PayrollItemResponse>>>> GetItems(Guid companyId)
        => Ok(new ApiResponse<List<PayrollItemResponse>>(true, await _service.GetPayrollItemsAsync(companyId)));

    // Payroll Runs
    [HttpPost("runs")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> CreateRun(Guid companyId, [FromBody] CreatePayrollRunRequest request)
        => StatusCode(201, new ApiResponse<PayrollRunResponse>(true, await _service.CreatePayrollRunAsync(companyId, request, User.Identity?.Name ?? "")));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> GetRun(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.GetPayrollRunAsync(companyId, runId)));

    [HttpGet("runs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<PayrollRunResponse>>>> GetRuns(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<PayrollRunResponse>>(true, await _service.GetPayrollRunsAsync(companyId, new PagedRequest(page, pageSize))));

    [HttpPost("runs/{runId:guid}/calculate")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Calculate(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.CalculatePayrollAsync(companyId, runId)));

    [HttpPost("runs/{runId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Approve(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.ApprovePayrollAsync(companyId, runId, User.Identity?.Name ?? "")));

    [HttpPost("runs/{runId:guid}/pay")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Pay(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.ProcessPaymentAsync(companyId, runId, User.Identity?.Name ?? "")));

    [HttpGet("runs/{runId:guid}/employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<PayrollDetailResponse>>> GetDetail(Guid companyId, Guid runId, Guid employeeId)
        => Ok(new ApiResponse<PayrollDetailResponse>(true, await _service.GetPayrollDetailAsync(companyId, runId, employeeId)));

    [HttpGet("runs/{runId:guid}/employees/{employeeId:guid}/payslip")]
    public async Task<ActionResult> GetPayslip(Guid companyId, Guid runId, Guid employeeId)
    { var slip = await _service.GeneratePayslipAsync(companyId, runId, employeeId); return File(slip.PdfData, "application/pdf", slip.FileName); }

    // Leave
    [HttpPost("leaves")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CreateLeave(Guid companyId, [FromBody] CreateLeaveRequest request)
        => StatusCode(201, new ApiResponse<LeaveResponse>(true, await _service.CreateLeaveAsync(companyId, request)));

    [HttpGet("leaves/{leaveId:guid}")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> GetLeave(Guid companyId, Guid leaveId)
        => Ok(new ApiResponse<LeaveResponse>(true, await _service.GetLeaveAsync(companyId, leaveId)));

    [HttpPost("leaves/{leaveId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> ApproveLeave(Guid companyId, Guid leaveId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.ApproveLeaveAsync(companyId, leaveId, userId, name)));
    }

    [HttpPost("leaves/{leaveId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> RejectLeave(Guid companyId, Guid leaveId, [FromBody] RejectLeaveRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.RejectLeaveAsync(companyId, leaveId, userId, name, request)));
    }

    [HttpPost("leaves/{leaveId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CancelLeave(Guid companyId, Guid leaveId)
        => Ok(new ApiResponse<LeaveResponse>(true, await _service.CancelLeaveAsync(companyId, leaveId)));

    [HttpGet("leaves")]
    public async Task<ActionResult<ApiResponse<List<LeaveResponse>>>> GetLeaves(Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] int? year)
        => Ok(new ApiResponse<List<LeaveResponse>>(true, await _service.GetLeavesAsync(companyId, employeeId, year)));

    [HttpGet("leaves/balance")]
    public async Task<ActionResult<ApiResponse<LeaveBalanceResponse>>> GetLeaveBalance(
        Guid companyId, [FromQuery] Guid employeeId, [FromQuery] int? year)
        => Ok(new ApiResponse<LeaveBalanceResponse>(true,
            await _service.GetLeaveBalanceAsync(companyId, employeeId, year ?? DateTime.UtcNow.Year)));

    [HttpPost("runs/{runId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> VoidRun(Guid companyId, Guid runId)
    { await _service.VoidPayrollAsync(companyId, runId); return Ok(new ApiResponse<bool>(true, true)); }

    // Reports
    [HttpGet("pnd1/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd1(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GeneratePnd1Async(companyId, year, month)));

    [HttpGet("pnd3/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd3(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GeneratePnd3Async(companyId, year, month)));

    [HttpGet("sso/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetSso(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GenerateSsoReportAsync(companyId, year, month)));
}
