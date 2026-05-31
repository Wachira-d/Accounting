namespace Accounting.Models.DTOs.Project;

public record CreateProjectRequest(
    string Code, string Name, string? NameEn, string? Description,
    Guid? ContactId, string? ProjectManagerName,
    DateTime StartDate, DateTime? EndDate,
    decimal BudgetAmount, decimal ContractAmount,
    string BillingMethod, string RevenueRecognitionMethod,
    Guid? DimensionId,
    // External-system linkage at creation time — partner can both
    // create + claim the external id in one POST instead of needing
    // a follow-up /external-link call.
    string? ExternalId = null,
    string? ExternalSystem = null,
    string? ExternalUrl = null);

public record UpdateProjectRequest(
    string? Name, string? Description, DateTime? EndDate,
    decimal? BudgetAmount, decimal? ContractAmount,
    decimal? CompletionPercent, string? Status,
    string? ExternalUrl = null);

public record ProjectResponse(
    Guid Id, string Code, string Name, string? Description,
    string? CustomerName, DateTime StartDate, DateTime? EndDate,
    string Status, decimal BudgetAmount, decimal ContractAmount,
    decimal ActualCost, decimal ActualRevenue,
    decimal CompletionPercent, string BillingMethod,
    DateTime CreatedAt,
    string? ExternalId = null,
    string? ExternalSystem = null,
    string? ExternalUrl = null,
    DateTime? LastSyncedAt = null);

public record CreateProjectTaskRequest(
    string Name, string? Description, Guid? ParentTaskId,
    DateTime? StartDate, DateTime? EndDate,
    decimal EstimatedHours, decimal EstimatedCost,
    string? AssignedTo);

public record UpdateProjectTaskRequest(
    string? Name, decimal? ActualHours, decimal? ActualCost,
    decimal? CompletionPercent, string? Status);

public record ProjectTaskResponse(
    Guid Id, Guid ProjectId, string Name, string? Description,
    DateTime? StartDate, DateTime? EndDate,
    decimal EstimatedHours, decimal ActualHours,
    decimal EstimatedCost, decimal ActualCost,
    decimal CompletionPercent, string Status, string? AssignedTo);

public record CreateProjectCostEntryRequest(
    Guid? ProjectTaskId, DateTime EntryDate, string CostType,
    string Description, decimal Quantity, decimal UnitCost,
    Guid? EmployeeId, bool IsBillable,
    string CostBehavior = "Variable");

public record UpdateProjectCostEntryRequest(
    DateTime? EntryDate = null,
    string? CostType = null,
    string? Description = null,
    decimal? Quantity = null,
    decimal? UnitCost = null,
    bool? IsBillable = null,
    string? CostBehavior = null);

public record ProjectCostEntryResponse(
    Guid Id, Guid ProjectId, DateTime EntryDate, string CostType,
    string Description, decimal Quantity, decimal UnitCost,
    decimal Amount, bool IsBillable, bool IsBilled,
    string CostBehavior = "Variable");

/// <summary>Per-employee labour breakdown for one project over a date
/// window. Answers the question "ค่าแรงพนักงานไปลงโครงการไหน บ้าง"
/// — for project X, who contributed how many hours and how much
/// salary cost was allocated.</summary>
public record ProjectLabourBreakdown(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    DateTime? From,
    DateTime? To,
    decimal TotalHours,
    decimal TotalAmount,
    int EmployeeCount,
    List<ProjectLabourByEmployee> ByEmployee);

public record ProjectLabourByEmployee(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? Department,
    string? Position,
    decimal Hours,
    decimal Amount,
    decimal AverageRate,           // amount / hours
    string CostBehavior,           // Fixed / Variable (employee-level default)
    int PayrollRunCount,           // how many runs allocated to this employee on this project
    int BillableHours,
    int NonBillableHours);

public record ProjectProfitabilityResponse(
    Guid ProjectId, string ProjectName, decimal ContractAmount,
    decimal TotalCost, decimal TotalRevenue, decimal GrossProfit,
    decimal GrossProfitPercent, decimal BudgetVariance,
    decimal CompletionPercent,
    Dictionary<string, decimal> CostBreakdown);

public record ProjectSummaryResponse(
    Guid Id, string Code, string Name, string Status,
    decimal BudgetAmount, decimal ActualCost,
    decimal CompletionPercent, decimal ProfitPercent);

public record ProjectGlSummaryResponse(
    Guid ProjectId, string ProjectName, DateTime FromDate, DateTime ToDate,
    decimal TotalRevenue, decimal TotalExpense, decimal NetIncome,
    int JournalEntryCount, int LineCount,
    List<ProjectGlAccountSummary> Accounts);

public record ProjectGlAccountSummary(
    string AccountCode, string AccountName, string AccountType,
    decimal DebitTotal, decimal CreditTotal, decimal Balance);
