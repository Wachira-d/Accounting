using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Expense;

// ===== Expense Claim =====
public record CreateExpenseClaimRequest(
    string Title,
    string? Description,
    DateTime ExpenseDate,
    List<ExpenseClaimLineRequest> Lines);

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
    string? Reference);

public record UpdateExpenseClaimRequest(
    string? Title,
    string? Description,
    DateTime? ExpenseDate,
    List<ExpenseClaimLineRequest>? Lines);

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
    Guid? PaymentVoucherDocumentId = null);

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
