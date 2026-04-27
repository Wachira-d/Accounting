using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Project;

namespace Accounting.Services.Interfaces;

public interface IProjectAccountingService
{
    Task<ProjectResponse> CreateAsync(Guid companyId, CreateProjectRequest request);
    Task<ProjectResponse> GetByIdAsync(Guid companyId, Guid projectId);
    Task<PagedResponse<ProjectResponse>> GetAllAsync(Guid companyId, string? status, PagedRequest request);
    Task<List<ProjectResponse>> GetActiveListAsync(Guid companyId);
    Task<ProjectResponse> UpdateAsync(Guid companyId, Guid projectId, UpdateProjectRequest request);
    Task<ProjectResponse> CompleteAsync(Guid companyId, Guid projectId);
    Task DeleteAsync(Guid companyId, Guid projectId);

    // Tasks
    Task<ProjectTaskResponse> CreateTaskAsync(Guid companyId, Guid projectId, CreateProjectTaskRequest request);
    Task<List<ProjectTaskResponse>> GetTasksAsync(Guid companyId, Guid projectId);
    Task<ProjectTaskResponse> UpdateTaskAsync(Guid companyId, Guid taskId, UpdateProjectTaskRequest request);
    Task DeleteTaskAsync(Guid companyId, Guid taskId);

    // Cost entries
    Task<ProjectCostEntryResponse> AddCostEntryAsync(Guid companyId, Guid projectId, CreateProjectCostEntryRequest request);
    Task<PagedResponse<ProjectCostEntryResponse>> GetCostEntriesAsync(Guid companyId, Guid projectId, PagedRequest request);
    Task DeleteCostEntryAsync(Guid companyId, Guid costEntryId);

    // Reports
    Task<ProjectProfitabilityResponse> GetProfitabilityAsync(Guid companyId, Guid projectId);
    Task<List<ProjectSummaryResponse>> GetProjectSummaryAsync(Guid companyId);
    Task<ProjectGlSummaryResponse> GetGlSummaryAsync(Guid companyId, Guid projectId, DateTime? fromDate, DateTime? toDate);
}
