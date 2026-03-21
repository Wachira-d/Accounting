using Accounting.Models.DTOs.Approval;

namespace Accounting.Services.Interfaces;

public interface IApprovalService
{
    // Rules
    Task<ApprovalRuleResponse> CreateRuleAsync(Guid companyId, CreateApprovalRuleRequest request);
    Task<List<ApprovalRuleResponse>> GetRulesAsync(Guid companyId);
    Task<ApprovalRuleResponse> UpdateRuleAsync(Guid companyId, Guid ruleId, CreateApprovalRuleRequest request);
    Task DeleteRuleAsync(Guid companyId, Guid ruleId);

    // Requests
    Task<ApprovalRequestResponse> SubmitForApprovalAsync(Guid companyId, string entityType, Guid entityId, Guid requestedByUserId);
    Task<ApprovalRequestResponse> GetApprovalRequestAsync(Guid companyId, Guid requestId);
    Task<List<ApprovalRequestResponse>> GetPendingApprovalsAsync(Guid companyId, Guid userId);
    Task<ApprovalRequestResponse> SubmitActionAsync(Guid companyId, Guid requestId, Guid userId, SubmitApprovalActionRequest request);

    // Escalation
    Task<int> EscalateOverdueApprovalsAsync();
}
