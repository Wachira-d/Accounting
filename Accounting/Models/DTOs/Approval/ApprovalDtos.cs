using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Approval;

public record CreateApprovalRuleRequest(
    string Name,
    string? Description,
    DocumentType? DocumentType,
    decimal? MinAmount,
    decimal? MaxAmount,
    List<ApprovalStepRequest> Steps,
    /// <summary>Optional project scope. When set, the rule only fires for
    /// documents tagged with this project. Null = applies to all projects.</summary>
    Guid? ProjectId = null);

public record ApprovalStepRequest(
    int StepOrder,
    Guid ApproverUserId,
    bool IsRequired = true);

public record ApprovalRuleResponse(
    Guid Id,
    string Name,
    string? Description,
    DocumentType? DocumentType,
    decimal? MinAmount,
    decimal? MaxAmount,
    bool IsActive,
    List<ApprovalStepResponse> Steps,
    Guid? ProjectId = null);

public record ApprovalStepResponse(
    int StepOrder,
    Guid ApproverUserId,
    string ApproverName,
    bool IsRequired);

public record ApprovalRequestResponse(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string RequestedByName,
    DateTime RequestedAt,
    ApprovalStatus OverallStatus,
    int CurrentStep,
    List<ApprovalActionResponse> Actions);

public record ApprovalActionResponse(
    int StepOrder,
    string ApproverName,
    ApprovalStatus Status,
    DateTime? ActionAt,
    string? Comments);

public record SubmitApprovalActionRequest(
    ApprovalStatus Status,
    string? Comments);
