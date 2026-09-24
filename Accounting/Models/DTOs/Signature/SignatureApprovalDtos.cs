using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Signature;

// ===== User Signature =====
public record UploadSignatureRequest(
    string SignatureData,           // Base64 encoded image
    string SignatureFormat = "PNG", // PNG, SVG, JPEG
    string? Label = null,
    bool IsDefault = true);

public record UserSignatureResponse(
    Guid Id, Guid UserId, string SignatureData, string SignatureFormat,
    string? Label, bool IsDefault, bool IsActive, DateTime CreatedAt);

// ===== Document Approval Setup =====
public record SetupDocumentApprovalRequest(
    Guid DocumentId,
    List<ApprovalStepInput> Steps);

public record ApprovalStepInput(
    string ApproverRole,             // "Preparer", "Reviewer", "Approver", "Customer", "Supplier"
    string ApprovalType,             // "Internal", "External", "Customer", "Supplier"
    int StepOrder,
    Guid? ApproverUserId = null,     // Internal user
    string? ApproverName = null,     // External
    string? ApproverEmail = null,
    string? ApproverTitle = null,
    string? PostApprovalAction = null // "ConvertToInvoice", "ConvertToReceipt", "CreatePO"
);

// ===== Approve / Sign =====
public record ApproveDocumentRequest(
    Guid? SignatureId = null,        // ใช้ลายเซ็นที่มีอยู่ (internal user)
    string? SignatureData = null,    // ส่งลายเซ็น Base64 มาตรงๆ (external)
    string? SignatureFormat = "PNG",
    string? Comments = null,
    string? ApproverName = null,     // ชื่อผู้อนุมัติ (สำหรับ external API)
    string? ApproverEmail = null,
    string? ApproverTitle = null,
    // รอบ 193 (ฝ่ายค้านรอบสอง N6): ขั้นสุดท้ายที่ทำให้เอกสารอนุมัติจริง — ผู้เซ็นเห็นคำเตือนแล้วกด "รับทราบ" (422 → ส่งซ้ำพร้อม true)
    bool AcknowledgeWarnings = false);

public record RejectDocumentRequest(
    string Comments);

// ===== External API: Approve Quotation (ลูกค้าอนุมัติใบเสนอราคา) =====
public record ExternalApproveRequest(
    string SignatureData,            // Base64 ลายเซ็นลูกค้า
    string SignatureFormat = "PNG",
    string ApproverName = "",        // ชื่อผู้ลงนาม
    string? ApproverTitle = null,    // ตำแหน่ง
    string? ApproverEmail = null,
    string? Comments = null,
    bool AutoConvert = true,         // อนุมัติแล้วสร้างเอกสารต่อ (Quotation → Invoice)
    bool AcknowledgeWarnings = false); // ผู้บันทึก (สมาชิกที่ล็อกอิน) เห็นคำเตือนแล้วกดรับทราบ — รอบ 193 N6

// ===== Responses =====
public record DocumentApprovalResponse(
    Guid Id, Guid DocumentId, string DocumentNumber,
    string ApprovalType, string ApproverRole, int StepOrder,
    ApprovalStatus Status,
    Guid? ApproverUserId, string? ApproverName, string? ApproverEmail, string? ApproverTitle,
    bool HasSignature,
    DateTime? ApprovedAt, DateTime? RejectedAt, string? Comments,
    string? PostApprovalAction,
    // รอบ 193 R4-1: มีค่า = ลายเซ็นลูกค้าแถวนี้ไม่นับกับเนื้อหาตอนนี้ (เซ็นกับเนื้อหาก่อนแก้) — ข้อความจาก DocumentSignedContent.StaleReason
    string? SignatureStaleReason = null);

public record DocumentApprovalDetailResponse(
    Guid Id, Guid DocumentId, string DocumentNumber, string DocumentType,
    string ApprovalType, string ApproverRole, int StepOrder,
    ApprovalStatus Status,
    Guid? ApproverUserId, string? ApproverName, string? ApproverEmail, string? ApproverTitle,
    string? SignatureData, string? SignatureFormat,
    DateTime? ApprovedAt, DateTime? RejectedAt, string? Comments,
    string? PostApprovalAction, string? IpAddress);

public record DocumentSignatureResponse(
    Guid Id, string SignerRole, string SignerName, string? SignerTitle,
    string SignatureData, string SignatureFormat,
    DateTime SignedAt);

public record DocumentWithApprovalsResponse(
    Guid DocumentId, string DocumentNumber, string DocumentType, string Status,
    decimal TotalAmount, string? ContactName,
    List<DocumentApprovalResponse> Approvals,
    List<DocumentSignatureResponse> Signatures);

// ===== Quotation approval result =====
public record QuotationApprovalResult(
    Guid QuotationId, string QuotationNumber,
    ApprovalStatus ApprovalStatus,
    Guid? ConvertedDocumentId, string? ConvertedDocumentNumber, string? ConvertedDocumentType,
    string Message);
