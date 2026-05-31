using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Expense;

// ===== Expense Claim =====
public record CreateExpenseClaimRequest(
    string Title,
    string? Description,
    DateTime ExpenseDate,
    List<ExpenseClaimLineRequest> Lines,
    // §65 ทวิ no-receipt claim. When NoReceipt = true the service auto-
    // generates a Document(CertificateInLieu) on Approve, carrying these
    // fields onto the certificate. Reason is required when NoReceipt;
    // Witness is optional (some companies require 2 witnesses for audit).
    bool NoReceipt = false,
    string? NoReceiptReason = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    /// <summary>Project this claim was incurred against. Flows to the
    /// auto-generated PaymentVoucher's Document.ProjectId on approve
    /// so the cost lands in the right project's P&amp;L.</summary>
    Guid? ProjectId = null);

public record ExpenseClaimLineRequest(
    string Description,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount,
    decimal NetAmount,
    Guid? AccountId,
    string? Category,
    string? Reference,
    /// <summary>Per-line project override. Null = inherit claim
    /// header's ProjectId. Used when a single trip's lines split
    /// across multiple projects.</summary>
    Guid? ProjectId = null);

public record UpdateExpenseClaimRequest(
    string? Title,
    string? Description,
    DateTime? ExpenseDate,
    List<ExpenseClaimLineRequest>? Lines,
    bool? NoReceipt = null,
    string? NoReceiptReason = null,
    string? WitnessName = null,
    string? WitnessPosition = null);

public record ExpenseClaimResponse(
    Guid Id,
    string ClaimNumber,
    string Title,
    string? Description,
    DateTime ExpenseDate,
    ExpenseClaimStatus Status,
    decimal SubTotal,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    string? SubmittedByName,
    Guid SubmittedByUserId,
    DateTime? ApprovedAt,
    string? ApprovedByName,
    DateTime? PaidAt,
    string? PaidReference,
    List<ExpenseClaimLineResponse> Lines,
    DateTime CreatedAt,
    Guid? PaymentVoucherDocumentId = null,
    bool NoReceipt = false,
    string? NoReceiptReason = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    // Set after ApproveAsync runs on a NoReceipt claim — UI links
    // to /pages/documents.html?editDoc=<id> for the auto-generated
    // CertificateInLieu so the bookkeeper can review/print.
    Guid? CertificateInLieuDocumentId = null,
    string? CertificateInLieuDocumentNumber = null);

public record ExpenseClaimLineResponse(
    Guid Id,
    string Description,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount,
    decimal NetAmount,
    Guid? AccountId,
    string? AccountName,
    string? Category,
    string? Reference);

public record ApproveExpenseClaimRequest(
    string? Notes);

public record RejectExpenseClaimRequest(
    string Reason);

public record PayExpenseClaimRequest(
    PaymentMethod PaymentMethod,
    string? Reference,
    string? Notes);
