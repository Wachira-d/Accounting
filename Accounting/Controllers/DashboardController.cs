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

    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<DashboardResponse>>> GetDashboard(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int trendMonths = 6)
    {
        var result = await _dashboardService.GetDashboardAsync(companyId, new DashboardRequest(fromDate, toDate, trendMonths));
        return Ok(new ApiResponse<DashboardResponse>(true, result));
    }

    [HttpGet("kpis")]
    public async Task<ActionResult<ApiResponse<DashboardKpis>>> GetKpis(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var to = toDate ?? DateTime.UtcNow;
        var result = await _dashboardService.GetKpisAsync(companyId, from, to);
        return Ok(new ApiResponse<DashboardKpis>(true, result));
    }

    [HttpGet("cash-flow")]
    public async Task<ActionResult<ApiResponse<CashFlowSummary>>> GetCashFlow(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var to = toDate ?? DateTime.UtcNow;
        var result = await _dashboardService.GetCashFlowSummaryAsync(companyId, from, to);
        return Ok(new ApiResponse<CashFlowSummary>(true, result));
    }

    [HttpGet("revenue-trends")]
    public async Task<ActionResult<ApiResponse<List<RevenueTrend>>>> GetRevenueTrends(
        Guid companyId, [FromQuery] int months = 6)
    {
        var result = await _dashboardService.GetRevenueTrendsAsync(companyId, months);
        return Ok(new ApiResponse<List<RevenueTrend>>(true, result));
    }

    [HttpGet("expense-trends")]
    public async Task<ActionResult<ApiResponse<List<ExpenseTrend>>>> GetExpenseTrends(
        Guid companyId, [FromQuery] int months = 6)
    {
        var result = await _dashboardService.GetExpenseTrendsAsync(companyId, months);
        return Ok(new ApiResponse<List<ExpenseTrend>>(true, result));
    }
}
