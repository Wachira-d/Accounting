namespace Accounting.Models.DTOs.Budget;

public record CreateBudgetRequest(
    string Name,
    int FiscalYear,
    List<BudgetLineRequest> Lines);

public record BudgetLineRequest(
    Guid AccountId,
    decimal Month1 = 0, decimal Month2 = 0, decimal Month3 = 0,
    decimal Month4 = 0, decimal Month5 = 0, decimal Month6 = 0,
    decimal Month7 = 0, decimal Month8 = 0, decimal Month9 = 0,
    decimal Month10 = 0, decimal Month11 = 0, decimal Month12 = 0);

public record UpdateBudgetRequest(
    string? Name,
    bool? IsActive,
    List<BudgetLineRequest>? Lines);

public record BudgetResponse(
    Guid Id,
    string Name,
    int FiscalYear,
    bool IsActive,
    decimal TotalBudget,
    List<BudgetLineResponse> Lines,
    DateTime CreatedAt);

public record BudgetLineResponse(
    Guid Id,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal Month1, decimal Month2, decimal Month3,
    decimal Month4, decimal Month5, decimal Month6,
    decimal Month7, decimal Month8, decimal Month9,
    decimal Month10, decimal Month11, decimal Month12,
    decimal TotalBudget);

public record BudgetVsActualResponse(
    Guid BudgetId,
    string BudgetName,
    int FiscalYear,
    List<BudgetVsActualLine> Lines);

public record BudgetVsActualLine(
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal BudgetAmount,
    decimal ActualAmount,
    decimal Variance,
    decimal VariancePercent);
