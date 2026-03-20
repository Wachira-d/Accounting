using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Project;

public record CreateProjectRequest(
    string Code, string Name, string? Description, Guid? ContactId,
    DateTime StartDate, DateTime? EndDate, decimal BudgetAmount,
    decimal? ContractAmount, string? BillingMethod, string? RevenueRecognitionMethod);

public record UpdateProjectRequest(
    string? Name = null, string? Description = null, DateTime? EndDate = null,
    decimal? BudgetAmount = null, string? Status = null);

public record ProjectResponse(
    Guid Id, string Code, string Name, string? Description,
    Guid? ContactId, string? ContactName,
    DateTime StartDate, DateTime? EndDate,
    decimal BudgetAmount, decimal? ContractAmount, decimal ActualCost,
    decimal CompletionPercent, string Status,
    string? BillingMethod, string? RevenueRecognitionMethod,
    List<ProjectTaskResponse> Tasks, DateTime CreatedAt);

public record CreateProjectTaskRequest(
    string Name, string? Description, DateTime? StartDate, DateTime? DueDate,
    decimal? BudgetHours, decimal? BudgetAmount, Guid? AssignedToUserId);

public record ProjectTaskResponse(
    Guid Id, string Name, string? Description, DateTime? StartDate, DateTime? DueDate,
    decimal BudgetHours, decimal ActualHours, decimal BudgetAmount, decimal ActualCost,
    string Status, Guid? AssignedToUserId, string? AssignedToName);

public record CreateProjectCostRequest(
    Guid ProjectId, Guid? TaskId, string CostType, string Description,
    decimal Amount, DateTime EntryDate, Guid? AccountId);

public record ProjectCostResponse(
    Guid Id, Guid ProjectId, Guid? TaskId, string CostType,
    string Description, decimal Amount, DateTime EntryDate,
    Guid? AccountId, string? AccountName, DateTime CreatedAt);
