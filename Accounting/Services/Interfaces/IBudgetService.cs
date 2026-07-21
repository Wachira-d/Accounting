using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Budget;

namespace Accounting.Services.Interfaces;

public interface IBudgetService
{
    Task<BudgetResponse> CreateAsync(Guid companyId, CreateBudgetRequest request, string createdBy);
    Task<BudgetResponse> GetByIdAsync(Guid companyId, Guid budgetId);
    Task<List<BudgetResponse>> GetAllAsync(Guid companyId, int? fiscalYear = null);
    Task<PagedResponse<BudgetResponse>> GetAllPagedAsync(Guid companyId, int? fiscalYear, PagedRequest request);
    Task<BudgetResponse> UpdateAsync(Guid companyId, Guid budgetId, UpdateBudgetRequest request);
    Task DeleteAsync(Guid companyId, Guid budgetId);
    Task<BudgetVsActualResponse> GetBudgetVsActualAsync(Guid companyId, Guid budgetId);

    /// <summary>Scenario modeling: 3 case projection (best/base/worst).
    /// best = budget × (1 + upliftPercent), worst = budget × (1 - downsidePercent).
    /// ใช้สำหรับ CFO planning + investor pitch.</summary>
    Task<BudgetScenarioResponse> GetScenariosAsync(Guid companyId, Guid budgetId,
        decimal upliftPercent = 15m, decimal downsidePercent = 20m);
}

public sealed record BudgetScenarioResponse(
    Guid BudgetId,
    string BudgetName,
    int FiscalYear,
    decimal TotalBudgeted,
    decimal TotalActual,
    decimal BestCase,
    decimal BaseCase,
    decimal WorstCase,
    decimal UpliftPercent,
    decimal DownsidePercent,
    List<BudgetScenarioLine> Lines);

public sealed record BudgetScenarioLine(
    string AccountCode,
    string AccountName,
    decimal Budgeted,
    decimal Actual,
    decimal Variance,
    decimal VariancePercent,
    decimal BestCase,
    decimal BaseCase,
    decimal WorstCase);
