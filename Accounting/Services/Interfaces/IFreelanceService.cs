using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Freelance;

namespace Accounting.Services.Interfaces;

public interface IFreelanceService
{
    // Invitations
    Task<InvitationResponse> InviteFreelanceAsync(Guid companyId, InviteFreelanceRequest request, Guid invitedByUserId);
    Task<InvitationResponse> AcceptInvitationAsync(AcceptInvitationRequest request);
    Task RevokeInvitationAsync(Guid companyId, Guid invitationId);
    Task<List<InvitationResponse>> GetInvitationsAsync(Guid companyId);

    // Access Management
    Task<FreelanceAccessResponse> GetFreelanceAccessAsync(Guid companyId, Guid accessId);
    Task<List<FreelanceAccessResponse>> GetCompanyFreelancersAsync(Guid companyId);
    Task<FreelanceAccessResponse> UpdateFreelanceAccessAsync(Guid companyId, Guid accessId, UpdateFreelanceAccessRequest request);
    Task DeactivateFreelanceAccessAsync(Guid companyId, Guid accessId);

    // Tasks
    Task<FreelanceTaskResponse> CreateTaskAsync(Guid companyId, CreateFreelanceTaskRequest request, Guid createdBy);
    Task<FreelanceTaskResponse> GetTaskAsync(Guid companyId, Guid taskId);
    Task<List<FreelanceTaskSummary>> GetTasksAsync(Guid companyId, Guid? freelanceAccessId = null);
    Task<FreelanceTaskResponse> UpdateTaskAsync(Guid companyId, Guid taskId, UpdateFreelanceTaskRequest request, Guid userId);
    Task<FreelanceTaskResponse> SubmitTaskAsync(Guid companyId, Guid taskId, Guid userId);
    Task<FreelanceTaskResponse> ReviewTaskAsync(Guid companyId, Guid taskId, bool approve, string? notes, Guid reviewerId);

    // Comments & Time Logs
    Task<TaskCommentResponse> AddCommentAsync(Guid taskId, Guid userId, AddTaskCommentRequest request);
    Task<TimeLogResponse> AddTimeLogAsync(Guid taskId, Guid userId, CreateTimeLogRequest request);

    // Activity Log
    Task<PagedResponse<FreelanceActivityLogResponse>> GetActivityLogsAsync(Guid companyId, Guid? freelanceAccessId, PagedRequest request);

    // Freelance Dashboard
    Task<FreelanceDashboardResponse> GetFreelanceDashboardAsync(Guid userId);
}
