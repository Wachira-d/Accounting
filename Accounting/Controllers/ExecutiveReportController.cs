using Accounting.Filters;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Executive;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/executive-reports")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsDashboard)]
public class ExecutiveReportController : ControllerBase
{
    private readonly IExecutiveReportService _svc;

    public ExecutiveReportController(IExecutiveReportService svc) { _svc = svc; }

    private (DateTime from, DateTime to) DefaultRange(DateTime? from, DateTime? to)
    {
        var t = to ?? DateTime.UtcNow;
        var f = from ?? new DateTime(t.Year, 1, 1);
        return (f, t);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<ExecutiveSummaryResponse>>> GetSummary(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetExecutiveSummaryAsync(companyId, f, t);
        return Ok(new ApiResponse<ExecutiveSummaryResponse>(true, result));
    }

    [HttpGet("ratios")]
    public async Task<ActionResult<ApiResponse<FinancialRatiosResponse>>> GetRatios(
        Guid companyId, [FromQuery] DateTime? asOfDate,
        [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var asOf = asOfDate ?? DateTime.UtcNow;
        var result = await _svc.GetFinancialRatiosAsync(companyId, asOf, fromDate, toDate);
        return Ok(new ApiResponse<FinancialRatiosResponse>(true, result));
    }

    [HttpGet("trends")]
    public async Task<ActionResult<ApiResponse<TrendAnalysisResponse>>> GetTrends(
        Guid companyId, [FromQuery] string granularity = "monthly", [FromQuery] int periods = 12)
    {
        var result = await _svc.GetTrendAnalysisAsync(companyId, granularity, periods);
        return Ok(new ApiResponse<TrendAnalysisResponse>(true, result));
    }

    [HttpGet("customers")]
    public async Task<ActionResult<ApiResponse<CustomerAnalyticsResponse>>> GetCustomers(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int topN = 20)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetCustomerAnalyticsAsync(companyId, f, t, topN);
        return Ok(new ApiResponse<CustomerAnalyticsResponse>(true, result));
    }

    [HttpGet("suppliers")]
    public async Task<ActionResult<ApiResponse<SupplierAnalyticsResponse>>> GetSuppliers(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int topN = 20)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetSupplierAnalyticsAsync(companyId, f, t, topN);
        return Ok(new ApiResponse<SupplierAnalyticsResponse>(true, result));
    }

    [HttpGet("products")]
    public async Task<ActionResult<ApiResponse<ProductAnalyticsResponse>>> GetProducts(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int topN = 20)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetProductAnalyticsAsync(companyId, f, t, topN);
        return Ok(new ApiResponse<ProductAnalyticsResponse>(true, result));
    }

    [HttpGet("budget-variance")]
    public async Task<ActionResult<ApiResponse<BudgetVarianceResponse>>> GetBudgetVariance(
        Guid companyId, [FromQuery] Guid? budgetId,
        [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var result = await _svc.GetBudgetVarianceAsync(companyId, budgetId, fromDate, toDate);
        return Ok(new ApiResponse<BudgetVarianceResponse>(true, result));
    }

    [HttpGet("cash-flow-forecast")]
    public async Task<ActionResult<ApiResponse<CashFlowForecastResponse>>> GetCashFlowForecast(
        Guid companyId, [FromQuery] int days = 30)
    {
        var result = await _svc.GetCashFlowForecastAsync(companyId, days);
        return Ok(new ApiResponse<CashFlowForecastResponse>(true, result));
    }

    [HttpGet("break-even")]
    public async Task<ActionResult<ApiResponse<BreakEvenAnalysisResponse>>> GetBreakEven(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetBreakEvenAnalysisAsync(companyId, f, t);
        return Ok(new ApiResponse<BreakEvenAnalysisResponse>(true, result));
    }

    [HttpGet("sales-performance")]
    public async Task<ActionResult<ApiResponse<SalesPerformanceResponse>>> GetSalesPerformance(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetSalesPerformanceAsync(companyId, f, t);
        return Ok(new ApiResponse<SalesPerformanceResponse>(true, result));
    }

    [HttpGet("project-profitability")]
    public async Task<ActionResult<ApiResponse<ProjectProfitabilityListResponse>>> GetProjectProfitability(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var (f, t) = DefaultRange(fromDate, toDate);
        var result = await _svc.GetProjectProfitabilityAsync(companyId, f, t);
        return Ok(new ApiResponse<ProjectProfitabilityListResponse>(true, result));
    }
}
