using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Dashboard;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;
    private readonly IPermissionService _permissions;

    public DashboardController(IDashboardService dashboardService, IPermissionService permissions)
    {
        _dashboardService = dashboardService;
        _permissions = permissions;
    }

    /// <summary>Single-permission gate covering every dashboard endpoint
    /// — KPIs / cash flow / revenue + expense trends. Owner / SystemAdmin
    /// auto-pass via PermissionService. Without Reports.Dashboard the
    /// caller gets a 403 with a Thai message that names the missing key,
    /// so the admin can grant it from the role page.</summary>
    private async Task<ActionResult<ApiResponse<T>>?> GateAsync<T>(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.ReportsDashboard))
            return StatusCode(403, new ApiResponse<T>(false, default,
                "ไม่มีสิทธิ์ดูแดชบอร์ด (ต้องการ Reports.Dashboard)"));
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<DashboardResponse>>> GetDashboard(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int trendMonths = 6)
    {
        if (await GateAsync<DashboardResponse>(companyId) is { } denied) return denied;
        var result = await _dashboardService.GetDashboardAsync(companyId, new DashboardRequest(fromDate, toDate, trendMonths));
        return Ok(new ApiResponse<DashboardResponse>(true, result));
    }

    [HttpGet("kpis")]
    public async Task<ActionResult<ApiResponse<DashboardKpis>>> GetKpis(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        if (await GateAsync<DashboardKpis>(companyId) is { } denied) return denied;
        var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var to = toDate ?? DateTime.UtcNow;
        var result = await _dashboardService.GetKpisAsync(companyId, from, to);
        return Ok(new ApiResponse<DashboardKpis>(true, result));
    }

    [HttpGet("cash-flow")]
    public async Task<ActionResult<ApiResponse<CashFlowSummary>>> GetCashFlow(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        if (await GateAsync<CashFlowSummary>(companyId) is { } denied) return denied;
        var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var to = toDate ?? DateTime.UtcNow;
        var result = await _dashboardService.GetCashFlowSummaryAsync(companyId, from, to);
        return Ok(new ApiResponse<CashFlowSummary>(true, result));
    }

    [HttpGet("revenue-trends")]
    public async Task<ActionResult<ApiResponse<List<RevenueTrend>>>> GetRevenueTrends(
        Guid companyId, [FromQuery] int months = 6)
    {
        if (await GateAsync<List<RevenueTrend>>(companyId) is { } denied) return denied;
        var result = await _dashboardService.GetRevenueTrendsAsync(companyId, months);
        return Ok(new ApiResponse<List<RevenueTrend>>(true, result));
    }

    [HttpGet("expense-trends")]
    public async Task<ActionResult<ApiResponse<List<ExpenseTrend>>>> GetExpenseTrends(
        Guid companyId, [FromQuery] int months = 6)
    {
        if (await GateAsync<List<ExpenseTrend>>(companyId) is { } denied) return denied;
        var result = await _dashboardService.GetExpenseTrendsAsync(companyId, months);
        return Ok(new ApiResponse<List<ExpenseTrend>>(true, result));
    }
}
