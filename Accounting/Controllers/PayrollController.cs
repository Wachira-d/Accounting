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
    private readonly IPermissionService _permissions;
    public PayrollController(IPayrollService service, ISensitivityService sensitivity,
        IPermissionService permissions)
    {
        _service = service;
        _sensitivity = sensitivity;
        _permissions = permissions;
    }

    /// <summary>PDPA ม.26 — เปิดดู PII (CitizenId/Phone/Email) แบบ raw เฉพาะ
    /// user ที่มี permission Pii.View. คนอื่น ๆ ได้ค่า mask ตาม PiiMask helper.</summary>
    private async Task<bool> CanViewPiiAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return await _permissions.HasPermissionAsync(companyId, userId,
            Models.Constants.PermissionKeys.PiiView);
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
        => Ok(new ApiResponse<EmployeeResponse>(true,
            await _service.GetEmployeeAsync(companyId, employeeId, await CanViewPiiAsync(companyId))));

    [HttpGet("employees")]
    public async Task<ActionResult<ApiResponse<PagedResponse<EmployeeResponse>>>> GetEmployees(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
        => Ok(new ApiResponse<PagedResponse<EmployeeResponse>>(true,
            await _service.GetEmployeesAsync(companyId, new PagedRequest(page, pageSize, search),
                await CanViewPiiAsync(companyId))));

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

    /// <summary>ปิดปี: คำนวณวันลาคงเหลือทุกพนักงานของปี ที่ระบุ →
    /// upsert EmployeeLeaveBalance ของปีถัดไป (carry-forward).</summary>
    [HttpPost("leaves/carry-forward/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> RunLeaveCarryForward(Guid companyId, int year)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var count = await _service.RunYearEndLeaveCarryForwardAsync(companyId, year, actor);
        return Ok(new ApiResponse<object>(true, new { upserts = count, targetYear = year + 1 },
            $"ทำ carry-forward วันลาเข้าปี {year + 1} เรียบร้อย ({count} รายการ)"));
    }

    public sealed record PostSeveranceRequest(decimal Amount, DateTime PayDate);

    /// <summary>โพสต์ JE เงินชดเชยเลิกจ้าง §118 — Dr Severance / Cr Cash.
    /// ใช้คู่กับ severance-preview: HR กดยืนยันยอดที่คำนวณแล้วโพสต์เข้า GL.</summary>
    [HttpPost("employees/{employeeId:guid}/severance/post")]
    public async Task<ActionResult<ApiResponse<object>>> PostSeverance(
        Guid companyId, Guid employeeId, [FromBody] PostSeveranceRequest req)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var jeId = await _service.PostSeveranceAsync(companyId, employeeId,
            req.Amount, req.PayDate, JwtHelper.GetUserIdFromClaims(User).ToString());
        return Ok(new ApiResponse<object>(true, new { journalEntryId = jeId }, "โพสต์เงินชดเชยเข้า GL เรียบร้อย"));
    }

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

    /// <summary>นำส่งประกันสังคมให้ สปส. (สปส.1-10) — post JE คู่ที่สอง
    /// Dr 21815 / Cr Bank. คำนวณเงินเพิ่ม §49 อัตโนมัติ. ใช้กับรอบ Paid +
    /// ยังไม่นำส่ง.</summary>
    [HttpPost("runs/{runId:guid}/settle-sso")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> SettleSso(
        Guid companyId, Guid runId, [FromBody] SettleSsoRequest request)
        => Ok(new ApiResponse<PayrollRunResponse>(true,
            await _service.SettleSocialSecurityAsync(companyId, runId,
                request.PayDate, request.BankAccountId, request.FilingNumber,
                User.Identity?.Name ?? "")));

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

    /// <summary>ออกใบ 50 ทวิรายปีให้พนักงาน (ภงด.1 §40(1) เงินเดือน).
    /// employeeId=null → คืน Zip รวมทุกคน, ระบุ → คืน PDF เดียวคน.
    /// อิงเฉพาะ PayrollRun ที่ Status=Paid เท่านั้น.</summary>
    [HttpGet("wht-cert/annual/{year:int}")]
    public async Task<IActionResult> GetAnnualWhtCerts(Guid companyId, int year, [FromQuery] Guid? employeeId = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var requestedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        var (fileName, bytes) = await _service.GenerateAnnualEmployeeWhtCertsAsync(companyId, year, employeeId, requestedBy);
        var contentType = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip" : "application/pdf";
        return File(bytes, contentType, fileName);
    }

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

    // ===== Tax-rule config (PIT brackets + allowances รายปี) =====

    public sealed record TaxRuleConfigRequest(
        int FiscalYear,
        string? BracketsJson,
        decimal PersonalAllowance,
        decimal SpouseAllowance,
        decimal ChildAllowance,
        decimal ChildAllowancePost2561,
        decimal ParentAllowance,
        decimal Section42TwiCap,
        decimal LifeInsuranceCap,
        decimal HealthInsuranceCap,
        decimal PvdCap,
        decimal MortgageInterestCap,
        decimal DonationCapPercent,
        string? Notes);

    /// <summary>คืน TaxRuleConfig ของบริษัท × ช่วงปี (default 6 ปีย้อนหลัง +
    /// ล่วงหน้า). Row ที่ไม่มีใน DB คืน statutory default + IsOverride=false
    /// เพื่อให้ UI render ตารางครบทุกปีในช่วง.</summary>
    [HttpGet("tax-rule-config")]
    public async Task<ActionResult<ApiResponse<object>>> GetTaxRuleConfig(
        Guid companyId, [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int? fromYear = null, [FromQuery] int? toYear = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var start = fromYear ?? DateTime.UtcNow.Year - 1;
        var end = toYear ?? DateTime.UtcNow.Year + 4;
        var overrides = await db.TaxRuleConfigs
            .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.FiscalYear >= start && c.FiscalYear <= end)
            .ToListAsync();
        var defaultBrackets = "[{\"upperBound\":150000,\"rate\":0},{\"upperBound\":300000,\"rate\":0.05},{\"upperBound\":500000,\"rate\":0.10},{\"upperBound\":750000,\"rate\":0.15},{\"upperBound\":1000000,\"rate\":0.20},{\"upperBound\":2000000,\"rate\":0.25},{\"upperBound\":5000000,\"rate\":0.30},{\"upperBound\":0,\"rate\":0.35}]";
        var rows = Enumerable.Range(start, end - start + 1).Select(y =>
        {
            var ov = overrides.FirstOrDefault(o => o.FiscalYear == y);
            return new
            {
                FiscalYear = y,
                BracketsJson = ov?.BracketsJson ?? defaultBrackets,
                PersonalAllowance = ov?.PersonalAllowance ?? 60_000m,
                SpouseAllowance = ov?.SpouseAllowance ?? 60_000m,
                ChildAllowance = ov?.ChildAllowance ?? 30_000m,
                ChildAllowancePost2561 = ov?.ChildAllowancePost2561 ?? 60_000m,
                ParentAllowance = ov?.ParentAllowance ?? 30_000m,
                Section42TwiCap = ov?.Section42TwiCap ?? 100_000m,
                LifeInsuranceCap = ov?.LifeInsuranceCap ?? 100_000m,
                HealthInsuranceCap = ov?.HealthInsuranceCap ?? 25_000m,
                PvdCap = ov?.PvdCap ?? 500_000m,
                MortgageInterestCap = ov?.MortgageInterestCap ?? 100_000m,
                DonationCapPercent = ov?.DonationCapPercent ?? 10m,
                IsOverride = ov != null,
                ov?.Notes,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPut("tax-rule-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertTaxRuleConfig(
        Guid companyId, [FromBody] TaxRuleConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var year = req.FiscalYear > 2400 ? req.FiscalYear - 543 : req.FiscalYear;
        if (year < 2000 || year > 2100)
            return BadRequest(new ApiResponse<object>(false, null, "ปีไม่ถูกต้อง"));
        if (req.PersonalAllowance < 0 || req.SpouseAllowance < 0 || req.ChildAllowance < 0
            || req.ParentAllowance < 0 || req.PvdCap < 0 || req.DonationCapPercent < 0 || req.DonationCapPercent > 100)
            return BadRequest(new ApiResponse<object>(false, null, "ค่าลดหย่อนติดลบ หรือ donationCap > 100% ไม่ได้"));

        var existing = await db.TaxRuleConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.FiscalYear == year && !c.IsDeleted);
        if (existing == null)
        {
            existing = new Models.Entities.TaxRuleConfig { CompanyId = companyId, FiscalYear = year };
            db.TaxRuleConfigs.Add(existing);
        }
        existing.BracketsJson = req.BracketsJson ?? "";
        existing.PersonalAllowance = req.PersonalAllowance;
        existing.SpouseAllowance = req.SpouseAllowance;
        existing.ChildAllowance = req.ChildAllowance;
        existing.ChildAllowancePost2561 = req.ChildAllowancePost2561;
        existing.ParentAllowance = req.ParentAllowance;
        existing.Section42TwiCap = req.Section42TwiCap;
        existing.LifeInsuranceCap = req.LifeInsuranceCap;
        existing.HealthInsuranceCap = req.HealthInsuranceCap;
        existing.PvdCap = req.PvdCap;
        existing.MortgageInterestCap = req.MortgageInterestCap;
        existing.DonationCapPercent = req.DonationCapPercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { existing.FiscalYear, existing.PersonalAllowance, existing.PvdCap },
            $"บันทึกกฎภาษี ปี {year} แล้ว"));
    }

    [HttpDelete("tax-rule-config/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaxRuleConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var y = year > 2400 ? year - 543 : year;
        var existing = await db.TaxRuleConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.FiscalYear == y && !c.IsDeleted);
        if (existing == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบกฎภาษีของปีนี้"));
        existing.IsDeleted = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, $"ลบกฎภาษีปี {y} แล้ว — กลับไปใช้ default"));
    }
}
