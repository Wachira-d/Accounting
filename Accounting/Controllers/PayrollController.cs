using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/payroll")]
[Authorize]
public class PayrollController : ControllerBase
{
    private readonly IPayrollService _service;
    private readonly ISensitivityService _sensitivity;
    public PayrollController(IPayrollService service, ISensitivityService sensitivity)
    {
        _service = service;
        _sensitivity = sensitivity;
    }

    /// <summary>Gate every payroll endpoint behind the Payroll sensitivity rule.
    /// Returns 403 with a structured body so integrations distinguish "no access"
    /// from "no such record". Owner / SystemAdmin pass through.</summary>
    private async Task<ActionResult?> CheckPayrollAccessAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _sensitivity.CanViewAsync(companyId, userId, Models.Enums.SensitivityKind.Payroll))
            return StatusCode(403, new ApiResponse<object>(false, new
            {
                redacted = true,
                kind = "Payroll",
                requiredPermission = Models.Constants.PermissionKeys.PayrollView,
            }, "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน"));
        return null;
    }

    // Employees
    [HttpPost("employees")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> CreateEmployee(Guid companyId, [FromBody] CreateEmployeeRequest request)
        => StatusCode(201, new ApiResponse<EmployeeResponse>(true, await _service.CreateEmployeeAsync(companyId, request)));

    [HttpGet("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> GetEmployee(Guid companyId, Guid employeeId)
        => Ok(new ApiResponse<EmployeeResponse>(true, await _service.GetEmployeeAsync(companyId, employeeId)));

    [HttpGet("employees")]
    public async Task<ActionResult<ApiResponse<PagedResponse<EmployeeResponse>>>> GetEmployees(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
        => Ok(new ApiResponse<PagedResponse<EmployeeResponse>>(true, await _service.GetEmployeesAsync(companyId, new PagedRequest(page, pageSize, search))));

    /// <summary>Lookup-by-external for partner ERPs/HRIS — caller passes
    /// its own (ExternalSystem, ExternalId) and gets back our employee
    /// row without needing to remember our GUID.</summary>
    [HttpGet("employees/by-external/{externalSystem}/{externalId}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> GetByExternal(
        Guid companyId, string externalSystem, string externalId)
    {
        var emp = await _service.GetEmployeeByExternalAsync(companyId, externalSystem, externalId);
        return emp == null
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบพนักงาน"))
            : Ok(new ApiResponse<EmployeeResponse>(true, emp));
    }

    [HttpDelete("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteEmployee(Guid companyId, Guid employeeId)
    {
        try { await _service.DeleteEmployeeAsync(companyId, employeeId); return Ok(new ApiResponse<bool>(true, true, "ลบพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("employees/{employeeId:guid}/restore")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> RestoreEmployee(Guid companyId, Guid employeeId)
    {
        try { return Ok(new ApiResponse<EmployeeResponse>(true, await _service.RestoreEmployeeAsync(companyId, employeeId), "กู้คืนพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPut("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> UpdateEmployee(Guid companyId, Guid employeeId, [FromBody] UpdateEmployeeRequest request)
        => Ok(new ApiResponse<EmployeeResponse>(true, await _service.UpdateEmployeeAsync(companyId, employeeId, request)));

    [HttpPost("employees/sync")]
    public async Task<ActionResult<ApiResponse<SyncEmployeesResponse>>> SyncEmployees(
        Guid companyId, [FromBody] SyncEmployeesRequest request)
    {
        var r = await _service.SyncEmployeesAsync(companyId, request);
        return Ok(new ApiResponse<SyncEmployeesResponse>(true, r,
            $"sync เสร็จ — เพิ่ม {r.Inserted} · อัปเดต {r.Updated} · ข้าม {r.Skipped}"));
    }

    [HttpPost("employees/{employeeId:guid}/terminate")]
    public async Task<ActionResult<ApiResponse<bool>>> Terminate(Guid companyId, Guid employeeId, [FromQuery] DateTime endDate)
    { await _service.TerminateEmployeeAsync(companyId, employeeId, endDate); return Ok(new ApiResponse<bool>(true, true)); }

    /// <summary>คำนวณค่าชดเชยตามมาตรา 118 (preview เท่านั้น) — ใช้แสดงตัวเลขให้ HR
    /// ดูก่อนออกใบเงินเดือนสุดท้ายหรือบันทึก Expense voucher; ไม่บันทึก GL.</summary>
    [HttpPost("employees/{employeeId:guid}/severance-preview")]
    public async Task<ActionResult<ApiResponse<SeverancePreviewResponse>>> PreviewSeverance(
        Guid companyId, Guid employeeId, [FromBody] SeverancePreviewRequest request)
        => Ok(new ApiResponse<SeverancePreviewResponse>(true,
            await _service.PreviewSeverancePayAsync(companyId, employeeId, request)));

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
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PayrollRunResponse>(true, await _service.GetPayrollRunAsync(companyId, runId)));
    }

    [HttpGet("runs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<PayrollRunResponse>>>> GetRuns(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PagedResponse<PayrollRunResponse>>(true, await _service.GetPayrollRunsAsync(companyId, new PagedRequest(page, pageSize))));
    }

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
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PayrollDetailResponse>(true, await _service.GetPayrollDetailAsync(companyId, runId, employeeId)));
    }

    [HttpGet("runs/{runId:guid}/employees/{employeeId:guid}/payslip")]
    public async Task<ActionResult> GetPayslip(Guid companyId, Guid runId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var slip = await _service.GeneratePayslipAsync(companyId, runId, employeeId);
        return File(slip.PdfData, "application/pdf", slip.FileName);
    }

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

    // Reports — ภงด.1 exposes individual employee salary/WHT and is sensitive
    // payroll data, so it sits behind the same Payroll permission gate. ภงด.3
    // (vendor / freelancer WHT) stays open since it's part of the regular AP flow.
    [HttpGet("pnd1/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd1(Guid companyId, int year, int month)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<object>(true, await _service.GeneratePnd1Async(companyId, year, month)));
    }

    [HttpGet("pnd3/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd3(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GeneratePnd3Async(companyId, year, month)));

    [HttpGet("sso/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetSso(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GenerateSsoReportAsync(companyId, year, month)));

    // ===== SSO year-config (เพดานค่าจ้าง/อัตราสมทบ ปรับได้รายปี) =====

    public sealed record SsoYearConfigRequest(int Year, decimal WageCeiling,
        decimal RatePercent = 5m, decimal EmployerRatePercent = 5m, string? Notes = null);

    /// <summary>Effective SSO parameters per year: company overrides merged
    /// over the statutory default schedule (15,000 → 17,500 ปี 2026 →
    /// 20,000 ปี 2572 → 23,000 ปี 2575). UI renders this as the editable
    /// year table.</summary>
    [HttpGet("sso-config")]
    public async Task<ActionResult<ApiResponse<object>>> GetSsoConfig(
        Guid companyId, [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int? fromYear = null, [FromQuery] int? toYear = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var start = fromYear ?? DateTime.UtcNow.Year - 1;
        var end = toYear ?? DateTime.UtcNow.Year + 6;
        var overrides = await db.SsoYearConfigs
            .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.Year >= start && c.Year <= end)
            .ToListAsync();
        var rows = Enumerable.Range(start, end - start + 1).Select(y =>
        {
            var ov = overrides.FirstOrDefault(o => o.Year == y);
            var (defCeiling, defRate) = SsoRateSchedule.GetDefault(y);
            var ceiling = ov?.WageCeiling ?? defCeiling;
            var rate = ov != null ? ov.RatePercent / 100m : defRate;
            return new
            {
                Year = y,
                WageCeiling = ceiling,
                RatePercent = rate * 100m,
                EmployerRatePercent = ov?.EmployerRatePercent ?? defRate * 100m,
                MaxMonthlyContribution = Math.Round(ceiling * rate, 2),
                IsOverride = ov != null,
                ov?.Notes,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>Upsert the SSO parameters for one year (ปี ค.ศ. — พ.ศ. ถูก
    /// normalize ให้). ใช้เมื่อประกาศ/พรฎ. ฉบับใหม่เปลี่ยนเพดานหรืออัตรา
    /// (รวมกรณีลดอัตราชั่วคราว) โดยไม่ต้องรออัปเดตระบบ.</summary>
    [HttpPut("sso-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertSsoConfig(
        Guid companyId, [FromBody] SsoYearConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var year = req.Year > 2400 ? req.Year - 543 : req.Year;
        if (year < 2000 || year > 2100)
            return BadRequest(new ApiResponse<object>(false, null, "ปีไม่ถูกต้อง"));
        if (req.WageCeiling <= 0 || req.RatePercent <= 0 || req.RatePercent > 30)
            return BadRequest(new ApiResponse<object>(false, null, "เพดานค่าจ้าง/อัตราสมทบไม่ถูกต้อง"));

        var existing = await db.SsoYearConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == year && !c.IsDeleted);
        if (existing == null)
        {
            existing = new Models.Entities.SsoYearConfig { CompanyId = companyId, Year = year };
            db.SsoYearConfigs.Add(existing);
        }
        existing.WageCeiling = req.WageCeiling;
        existing.RatePercent = req.RatePercent;
        existing.EmployerRatePercent = req.EmployerRatePercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            existing.Year,
            existing.WageCeiling,
            existing.RatePercent,
            MaxMonthlyContribution = Math.Round(existing.WageCeiling * existing.RatePercent / 100m, 2),
        }, $"บันทึกค่าประกันสังคมปี {year} แล้ว — สมทบสูงสุด {existing.WageCeiling * existing.RatePercent / 100m:N2} บาท/เดือน"));
    }

    /// <summary>Remove a year override — the statutory default takes over.</summary>
    [HttpDelete("sso-config/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSsoConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var y = year > 2400 ? year - 543 : year;
        var existing = await db.SsoYearConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == y && !c.IsDeleted);
        if (existing == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบค่าตั้งของปีนี้"));
        existing.IsDeleted = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, $"ลบค่าตั้งปี {y} แล้ว — กลับไปใช้ตารางตามกฎหมาย"));
    }
}
