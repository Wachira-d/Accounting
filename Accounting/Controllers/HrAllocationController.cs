using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Hr;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// HR allocation endpoints — per-day employee → project time entries,
/// bulk sync from external attendance systems, payroll → ProjectCostEntry
/// labour allocation, and the fix-vs-variable cost report.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/hr")]
[Authorize]
public class HrAllocationController : ControllerBase
{
    private readonly IEmployeeProjectTimeService _time;
    private readonly IFixVariableCostReportService _report;
    private readonly ISensitivityService _sensitivity;

    public HrAllocationController(IEmployeeProjectTimeService time,
        IFixVariableCostReportService report, ISensitivityService sensitivity)
    {
        _time = time;
        _report = report;
        _sensitivity = sensitivity;
    }

    /// <summary>Gate write/allocate endpoints behind the Payroll sensitivity
    /// rule — labour cost allocation can shift money between projects and
    /// reveals gross-salary information. Read endpoints stay open behind
    /// plain [Authorize] since aggregate cost reports are usually broadly
    /// shared with PMs.</summary>
    private async Task<ActionResult?> CheckPayrollAccessAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _sensitivity.CanViewAsync(companyId, userId, Models.Enums.SensitivityKind.Payroll))
            return StatusCode(403, new ApiResponse<object>(false, new
            {
                redacted = true, kind = "Payroll",
                requiredPermission = Models.Constants.PermissionKeys.PayrollView,
            }, "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน"));
        return null;
    }

    // ===== Employee project time CRUD =====

    [HttpPost("project-time")]
    public async Task<ActionResult<ApiResponse<EmployeeProjectTimeResponse>>> Create(
        Guid companyId, [FromBody] CreateEmployeeProjectTimeRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _time.CreateAsync(companyId, req, ct);
            return Ok(new ApiResponse<EmployeeProjectTimeResponse>(true, r,
                $"บันทึก {req.Hours:N2} ชั่วโมง"));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
        { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("project-time/{id:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeProjectTimeResponse>>> Get(
        Guid companyId, Guid id, CancellationToken ct)
    {
        try { return Ok(new ApiResponse<EmployeeProjectTimeResponse>(true, await _time.GetAsync(companyId, id, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>Partner systems look up a time row by their own
    /// (ExternalSystem, ExternalId) tag — symmetric with project
    /// and employee by-external endpoints.</summary>
    [HttpGet("project-time/by-external/{externalSystem}/{externalId}")]
    public async Task<ActionResult<ApiResponse<EmployeeProjectTimeResponse>>> GetByExternal(
        Guid companyId, string externalSystem, string externalId, CancellationToken ct)
    {
        var row = await _time.GetByExternalAsync(companyId, externalSystem, externalId, ct);
        return row == null
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการเวลาทำงาน"))
            : Ok(new ApiResponse<EmployeeProjectTimeResponse>(true, row));
    }

    [HttpPut("project-time/{id:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeProjectTimeResponse>>> Update(
        Guid companyId, Guid id, [FromBody] UpdateEmployeeProjectTimeRequest req, CancellationToken ct)
    {
        try { return Ok(new ApiResponse<EmployeeProjectTimeResponse>(true, await _time.UpdateAsync(companyId, id, req, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
        { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpDelete("project-time/{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid id, CancellationToken ct)
    {
        try
        {
            await _time.DeleteAsync(companyId, id, ct);
            return Ok(new ApiResponse<object>(true, null, "ลบรายการแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("project-time")]
    public async Task<ActionResult<ApiResponse<PagedResponse<EmployeeProjectTimeResponse>>>> List(
        Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] Guid? projectId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(new ApiResponse<PagedResponse<EmployeeProjectTimeResponse>>(true,
            await _time.ListAsync(companyId, employeeId, projectId, from, to,
                new PagedRequest(page, pageSize, null), ct)));

    [HttpPost("project-time/sync")]
    public async Task<ActionResult<ApiResponse<SyncEmployeeProjectTimesResponse>>> Sync(
        Guid companyId, [FromBody] SyncEmployeeProjectTimesRequest req, CancellationToken ct)
    {
        var r = await _time.SyncAsync(companyId, req, ct);
        return Ok(new ApiResponse<SyncEmployeeProjectTimesResponse>(true, r,
            $"sync เสร็จ — เพิ่ม {r.Inserted} · อัปเดต {r.Updated} · ข้าม {r.Skipped}"));
    }

    // ===== Payroll → ProjectCostEntry labour allocation =====

    [HttpPost("payroll-runs/{payrollRunId:guid}/allocate-labour")]
    public async Task<ActionResult<ApiResponse<PayrollLabourAllocationResponse>>> Allocate(
        Guid companyId, Guid payrollRunId, CancellationToken ct)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            var r = await _time.AllocatePayrollRunAsync(companyId, payrollRunId, ct);
            return Ok(new ApiResponse<PayrollLabourAllocationResponse>(true, r,
                $"จัดสรร labour cost {r.CostEntriesCreated} รายการ · รวม {r.TotalAllocated:N2} บาท · admin {r.UnallocatedAdmin:N2}"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    // ===== Fix vs Variable cost reports =====

    [HttpGet("reports/fix-variable-cost")]
    public async Task<ActionResult<ApiResponse<FixVariableCostReport>>> GetReport(
        Guid companyId, [FromQuery] int year, [FromQuery] int month,
        [FromQuery] Guid? projectId, CancellationToken ct) =>
        Ok(new ApiResponse<FixVariableCostReport>(true,
            await _report.GetMonthlyAsync(companyId, year, month, projectId, ct)));

    [HttpGet("reports/fix-variable-cost/trend")]
    public async Task<ActionResult<ApiResponse<List<FixVariableCostMonthlyTrend>>>> GetTrend(
        Guid companyId, [FromQuery] int fromYear, [FromQuery] int fromMonth,
        [FromQuery] int months = 12, [FromQuery] Guid? projectId = null,
        CancellationToken ct = default) =>
        Ok(new ApiResponse<List<FixVariableCostMonthlyTrend>>(true,
            await _report.GetTrendAsync(companyId, fromYear, fromMonth, months, projectId, ct)));
}
