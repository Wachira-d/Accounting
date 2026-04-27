using Accounting.Models.DTOs.Executive;

namespace Accounting.Services.Interfaces;

public interface IExecutiveReportService
{
    Task<ExecutiveSummaryResponse> GetExecutiveSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<FinancialRatiosResponse> GetFinancialRatiosAsync(Guid companyId, DateTime asOfDate, DateTime? fromDate = null, DateTime? toDate = null);
    Task<TrendAnalysisResponse> GetTrendAnalysisAsync(Guid companyId, string granularity, int periods);
    Task<CustomerAnalyticsResponse> GetCustomerAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20);
    Task<SupplierAnalyticsResponse> GetSupplierAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20);
    Task<ProductAnalyticsResponse> GetProductAnalyticsAsync(Guid companyId, DateTime fromDate, DateTime toDate, int topN = 20);
    Task<BudgetVarianceResponse> GetBudgetVarianceAsync(Guid companyId, Guid? budgetId, DateTime? fromDate, DateTime? toDate);
    Task<CashFlowForecastResponse> GetCashFlowForecastAsync(Guid companyId, int days);
    Task<BreakEvenAnalysisResponse> GetBreakEvenAnalysisAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<SalesPerformanceResponse> GetSalesPerformanceAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<ProjectProfitabilityListResponse> GetProjectProfitabilityAsync(Guid companyId, DateTime fromDate, DateTime toDate);
}
