using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IProjectAccountingService
{
    Task<ProjectResponse> CreateAsync(Guid companyId, CreateProjectRequest request);
    Task<ProjectResponse> GetByIdAsync(Guid companyId, Guid projectId);
    Task<PagedResponse<ProjectResponse>> GetAllAsync(Guid companyId, string? status, PagedRequest request);
    Task<ProjectResponse> UpdateAsync(Guid companyId, Guid projectId, UpdateProjectRequest request);
    Task<ProjectResponse> CompleteAsync(Guid companyId, Guid projectId);

    // Tasks
    Task<ProjectTaskResponse> CreateTaskAsync(Guid companyId, Guid projectId, CreateProjectTaskRequest request);
    Task<List<ProjectTaskResponse>> GetTasksAsync(Guid companyId, Guid projectId);
    Task<ProjectTaskResponse> UpdateTaskAsync(Guid companyId, Guid taskId, UpdateProjectTaskRequest request);

    // Cost entries
    Task<ProjectCostEntryResponse> AddCostEntryAsync(Guid companyId, Guid projectId, CreateProjectCostEntryRequest request);
    Task<PagedResponse<ProjectCostEntryResponse>> GetCostEntriesAsync(Guid companyId, Guid projectId, PagedRequest request);

    // Reports
    Task<ProjectProfitabilityResponse> GetProfitabilityAsync(Guid companyId, Guid projectId);
    Task<List<ProjectSummaryResponse>> GetProjectSummaryAsync(Guid companyId);
}

public record CreateProjectRequest(string Code, string Name, string? NameEn, string? Description, Guid? ContactId, string? ProjectManagerName, DateTime StartDate, DateTime? EndDate, decimal BudgetAmount, decimal ContractAmount, string BillingMethod, string RevenueRecognitionMethod, Guid? DimensionId);
public record UpdateProjectRequest(string? Name, string? Description, DateTime? EndDate, decimal? BudgetAmount, decimal? ContractAmount, decimal? CompletionPercent, string? Status);
public record ProjectResponse(Guid Id, string Code, string Name, string? Description, string? CustomerName, DateTime StartDate, DateTime? EndDate, string Status, decimal BudgetAmount, decimal ContractAmount, decimal ActualCost, decimal ActualRevenue, decimal CompletionPercent, string BillingMethod, DateTime CreatedAt);

public record CreateProjectTaskRequest(string Name, string? Description, Guid? ParentTaskId, DateTime? StartDate, DateTime? EndDate, decimal EstimatedHours, decimal EstimatedCost, string? AssignedTo);
public record UpdateProjectTaskRequest(string? Name, decimal? ActualHours, decimal? ActualCost, decimal? CompletionPercent, string? Status);
public record ProjectTaskResponse(Guid Id, Guid ProjectId, string Name, string? Description, DateTime? StartDate, DateTime? EndDate, decimal EstimatedHours, decimal ActualHours, decimal EstimatedCost, decimal ActualCost, decimal CompletionPercent, string Status, string? AssignedTo);

public record CreateProjectCostEntryRequest(Guid? ProjectTaskId, DateTime EntryDate, string CostType, string Description, decimal Quantity, decimal UnitCost, Guid? EmployeeId, bool IsBillable);
public record ProjectCostEntryResponse(Guid Id, Guid ProjectId, DateTime EntryDate, string CostType, string Description, decimal Quantity, decimal UnitCost, decimal Amount, bool IsBillable, bool IsBilled);

public record ProjectProfitabilityResponse(Guid ProjectId, string ProjectName, decimal ContractAmount, decimal TotalCost, decimal TotalRevenue, decimal GrossProfit, decimal GrossProfitPercent, decimal BudgetVariance, decimal CompletionPercent, Dictionary<string, decimal> CostBreakdown);
public record ProjectSummaryResponse(Guid Id, string Code, string Name, string Status, decimal BudgetAmount, decimal ActualCost, decimal CompletionPercent, decimal ProfitPercent);
