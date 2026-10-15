using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ส่งออก CSV / Excel ของหน้าการเงินที่เหลือ (2026-10-15 ทีม X5 · คู่กับ <see cref="AccountingExportController"/> ของทีม X3) —
/// ทุก endpoint อ่านอย่างเดียว · ตัวเลขจาก service ตัวเดียวกับหน้า (<see cref="FinanceReportExportService"/>) · ตัวกรองชื่อเดียวกับ endpoint ข้อมูลของหน้า ·
/// ด่าน: ล็อกอิน + สมาชิกบริษัท (tenant guard กลาง) + "ส่งออกรายงาน" (<see cref="PermissionKeys.ReportsExport"/>) ·
/// หน้าที่ข้อมูลมีด่านดูของตัวเอง ด่านนั้นซ้อนอีกชั้นที่ action (ตรวจข้อมูล = รายงานการเงิน · บริหารการเงิน = แดชบอร์ด)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/finance/exports")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsExport)]
public class FinanceExportController : ControllerBase
{
    private readonly FinanceReportExportService _export;

    public FinanceExportController(FinanceReportExportService export) => _export = export;

    /// <summary>สินเชื่อ — ตัวกรองเดียวกับ <c>GET loans</c> (status) · <c>loanId</c> = สัญญาเดียว · CSV <c>part=list|schedule|payments</c></summary>
    [HttpGet("loans")]
    public async Task<IActionResult> Loans(Guid companyId, [FromQuery] string? format, [FromQuery] string? status = null,
        [FromQuery] Guid? loanId = null, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.LoansAsync(companyId, status, loanId, part, fmt));
    }

    [HttpGet("budgets")]
    public async Task<IActionResult> Budgets(Guid companyId, [FromQuery] string? format, [FromQuery] int? fiscalYear = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.BudgetsAsync(companyId, fiscalYear, fmt));
    }

    /// <summary>งบประมาณเทียบยอดจริง — ตัวเดียวกับ <c>GET budget/{id}/vs-actual</c> · CSV <c>part=vs-actual|monthly|alerts</c></summary>
    [HttpGet("budgets/{budgetId:guid}/vs-actual")]
    public async Task<IActionResult> BudgetVsActual(Guid companyId, Guid budgetId, [FromQuery] string? format, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.BudgetVsActualAsync(companyId, budgetId, part, fmt));
    }

    [HttpGet("projects")]
    public async Task<IActionResult> Projects(Guid companyId, [FromQuery] string? format, [FromQuery] string? status = null, [FromQuery] string? search = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.ProjectsAsync(companyId, status, search, fmt));
    }

    /// <summary>กำไรขาดทุนโครงการ + รายการต้นทุนทั้งหมด · CSV <c>part=summary|breakdown|entries|cash-flow</c></summary>
    [HttpGet("projects/{projectId:guid}/pnl")]
    public async Task<IActionResult> ProjectPnl(Guid companyId, Guid projectId, [FromQuery] string? format, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.ProjectPnlAsync(companyId, projectId, part, fmt));
    }

    [HttpGet("dimensions")]
    public async Task<IActionResult> Dimensions(Guid companyId, [FromQuery] string? format, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.DimensionsAsync(companyId, part, fmt));
    }

    /// <summary>กำไรขาดทุนตามมิติ — ตัวเดียวกับ <c>GET dimensions/{id}/pnl</c> (ช่วงวันที่บังคับ)</summary>
    [HttpGet("dimensions/{dimensionId:guid}/pnl")]
    public async Task<IActionResult> DimensionPnl(Guid companyId, Guid dimensionId, [FromQuery] string? format,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        if (!fromDate.HasValue || !toDate.HasValue)
            return BadRequest(new ApiResponse<object>(false, null, "ระบุช่วงวันที่ (fromDate, toDate) ของงบกำไรขาดทุนตามมิติ"));
        return FileOf(await _export.DimensionPnlAsync(companyId, dimensionId, fromDate.Value, toDate.Value, fmt));
    }

    [HttpGet("fiscal-periods")]
    public async Task<IActionResult> FiscalPeriods(Guid companyId, [FromQuery] string? format)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.FiscalPeriodsAsync(companyId, fmt));
    }

    /// <summary>รายงานตรวจข้อมูล — ด่านเดียวกับหน้า (<see cref="PermissionKeys.ReportsFinancial"/>) ซ้อนบน "ส่งออกรายงาน"</summary>
    [HttpGet("data-integrity")]
    [RequirePermission(PermissionKeys.ReportsFinancial)]
    public async Task<IActionResult> DataIntegrity(Guid companyId, [FromQuery] string? format)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.DataIntegrityAsync(companyId, fmt));
    }

    /// <summary>วิเคราะห์ AR/AP · CSV <c>part=overview|aging|trend|top-ar|top-ap|bad-debt|risk</c></summary>
    [HttpGet("arap-analysis")]
    public async Task<IActionResult> ArApAnalysis(Guid companyId, [FromQuery] string? format, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.ArApAnalysisAsync(companyId, part, fmt));
    }

    [HttpGet("petty-cash")]
    public async Task<IActionResult> PettyCashFunds(Guid companyId, [FromQuery] string? format)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.PettyCashFundsAsync(companyId, fmt));
    }

    /// <summary>ทะเบียนเงินสดย่อย — ตัวเดียวกับ <c>GET sme/petty-cash/funds/{id}/register</c></summary>
    [HttpGet("petty-cash/{fundId:guid}/register")]
    public async Task<IActionResult> PettyCashRegister(Guid companyId, Guid fundId, [FromQuery] string? format,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.PettyCashRegisterAsync(companyId, fundId, fromDate, toDate, fmt));
    }

    /// <summary>ผลคำนวณตีราคา (ตัวเดียวกับปุ่ม "คำนวณ" — ไม่ลงบัญชี) · <c>rates=USD:36.5,EUR:39.1</c></summary>
    [HttpGet("fx-revaluation")]
    public async Task<IActionResult> FxRevaluation(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime? asOf = null, [FromQuery] string? rates = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        if (!asOf.HasValue) return BadRequest(new ApiResponse<object>(false, null, "ระบุวันที่ตีราคา (asOf)"));
        return FileOf(await _export.FxRevaluationAsync(companyId, asOf.Value, rates, fmt));
    }

    /// <summary>บริหารการเงิน — ด่านเดียวกับหน้า (<see cref="PermissionKeys.ReportsDashboard"/>) · <c>kind</c> = แท็บ · CSV <c>part=list|detail</c></summary>
    [HttpGet("financial")]
    [RequirePermission(PermissionKeys.ReportsDashboard)]
    public async Task<IActionResult> Financial(Guid companyId, [FromQuery] string? format, [FromQuery] string? kind = null,
        [FromQuery] string? direction = null, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.FinancialAsync(companyId, kind, direction, part, fmt));
    }

    /// <summary>คาดการณ์เงินสด — ตัวกรองเดียวกับ <c>GET cash-forecast/detailed</c> · CSV <c>part=days|receipts|payments|debtors|creditors|risks|summary</c></summary>
    [HttpGet("cash-forecast")]
    public async Task<IActionResult> CashForecast(Guid companyId, [FromQuery] string? format, [FromQuery] int? days = null,
        [FromQuery] Guid? projectId = null, [FromQuery] decimal? minimumCashFloor = null, [FromQuery] string? part = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.CashForecastAsync(companyId, days, projectId, minimumCashFloor, part, fmt));
    }

    [HttpGet("cheques/outstanding")]
    public async Task<IActionResult> OutstandingCheques(Guid companyId, [FromQuery] string? format, [FromQuery] Guid? bankAccountId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.OutstandingChequesAsync(companyId, bankAccountId, fmt));
    }

    private IActionResult FileOf(TabularFile f) => File(f.Content, f.ContentType, f.FileName);

    private IActionResult BadFormat()
        => BadRequest(new ApiResponse<object>(false, null, "รูปแบบไฟล์ต้องเป็น csv หรือ xlsx"));
}
