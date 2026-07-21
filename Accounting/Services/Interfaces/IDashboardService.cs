using Accounting.Models.DTOs.Dashboard;

namespace Accounting.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardResponse> GetDashboardAsync(Guid companyId, DashboardRequest request);
    Task<DashboardKpis> GetKpisAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<CashFlowSummary> GetCashFlowSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<List<RevenueTrend>> GetRevenueTrendsAsync(Guid companyId, int months);
    Task<List<ExpenseTrend>> GetExpenseTrendsAsync(Guid companyId, int months);
}
