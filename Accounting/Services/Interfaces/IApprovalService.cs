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

    /// <summary>คำขออนุมัติล่าสุดของเอกสาร/รายการนั้น (null = ยังไม่เคยส่ง) —
    /// ให้หน้าจอบอกได้ว่า "ตอนนี้รอใครอยู่ ขั้นที่เท่าไร" โดยไม่ต้องรู้ requestId</summary>
    Task<ApprovalRequestResponse?> GetLatestForEntityAsync(Guid companyId, string entityType, Guid entityId);
    Task<List<ApprovalRequestResponse>> GetPendingApprovalsAsync(Guid companyId, Guid userId);
    Task<ApprovalRequestResponse> SubmitActionAsync(Guid companyId, Guid requestId, Guid userId, SubmitApprovalActionRequest request);

    // Escalation
    Task<int> EscalateOverdueApprovalsAsync();

    /// <summary>หา ApprovalRule ที่ match document นี้ (docType + amount +
    /// optional project scope). คืน null ถ้าไม่ต้อง multi-level approval —
    /// allow flow ปกติ. คืน rule ถ้าต้อง — caller สร้าง ApprovalRequest +
    /// block direct approval ปกติ.</summary>
    Task<Models.Entities.ApprovalRule?> FindMatchingRuleAsync(
        Guid companyId, Models.Enums.DocumentType docType, decimal amount, Guid? projectId);

    /// <summary>ตรวจว่า document ผ่าน workflow ครบหรือยัง. Pending →
    /// throw (block direct approve). Approved → ok. ไม่มี request →
    /// ok (rule ไม่ match). ใช้ใน DocumentService.ApproveDocumentAsync
    /// hook ก่อน status transition.</summary>
    Task<ApprovalWorkflowGate> CheckGateAsync(Guid companyId, Guid documentId);
}

public sealed record ApprovalWorkflowGate(bool CanApprove, string? BlockReason, Guid? PendingRequestId);
