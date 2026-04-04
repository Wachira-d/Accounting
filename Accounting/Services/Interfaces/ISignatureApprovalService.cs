using Accounting.Models.DTOs.Signature;

namespace Accounting.Services.Interfaces;

public interface ISignatureApprovalService
{
    // ===== User Signatures =====
    Task<UserSignatureResponse> UploadSignatureAsync(Guid userId, UploadSignatureRequest request);
    Task<List<UserSignatureResponse>> GetUserSignaturesAsync(Guid userId);
    Task<UserSignatureResponse?> GetDefaultSignatureAsync(Guid userId);
    Task DeleteSignatureAsync(Guid userId, Guid signatureId);
    Task SetDefaultSignatureAsync(Guid userId, Guid signatureId);

    // ===== Document Approval Workflow =====
    Task<List<DocumentApprovalResponse>> SetupApprovalAsync(Guid companyId, SetupDocumentApprovalRequest request, string userId);
    Task<List<DocumentApprovalResponse>> GetDocumentApprovalsAsync(Guid companyId, Guid documentId);
    Task<DocumentWithApprovalsResponse> GetDocumentWithApprovalsAsync(Guid companyId, Guid documentId);
    Task<List<DocumentApprovalResponse>> GetPendingApprovalsAsync(Guid companyId, Guid userId);

    // ===== Internal Approve / Reject =====
    Task<DocumentApprovalResponse> ApproveAsync(Guid companyId, Guid approvalId, ApproveDocumentRequest request, string userId, string? ipAddress);
    Task<DocumentApprovalResponse> RejectAsync(Guid companyId, Guid approvalId, RejectDocumentRequest request, string userId);

    // ===== External API: Approve via Token/API Key =====
    Task<QuotationApprovalResult> ExternalApproveQuotationAsync(Guid companyId, Guid documentId, ExternalApproveRequest request, string? ipAddress);

    // ===== Signatures on Document =====
    Task<List<DocumentSignatureResponse>> GetDocumentSignaturesAsync(Guid companyId, Guid documentId);
}
