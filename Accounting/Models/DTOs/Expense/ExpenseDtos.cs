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
    string? CertificateInLieuDocumentNumber = null,
    // ฝ่ายค้านรอบ 201 รอบสาม P2-2: ข้อสังเกตรายจ่ายต้องห้าม §65 ตรีที่ผ่านตอนอนุมัติใบสำคัญจ่ายจากการกดจ่าย (ไม่บล็อก · คำตัดสินข้อ 110) —
    // มีค่าเฉพาะคำตอบของ POST pay (null = ไม่ได้มาจากการกดจ่าย · ว่าง = ไม่มีข้อสังเกต) · หน้า expense.html แสดงเป็น toast เตือน
    IReadOnlyList<string>? PassedApprovalNotes = null);

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

/// <summary>จ่ายใบเบิก — ทุกช่องไม่บังคับ (ทีม F1 · 2026-10-14): เดิม <c>PaymentMethod</c> บังคับแต่หน้า <c>expense.html</c> ส่งคำขอ<b>ไม่มี body</b> ⇒ 400 ·
/// และไม่มีช่องบัญชีที่จ่าย ⇒ ใบสำคัญจ่ายลงเงินสดเสมอแม้โอนจริง (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 ข้อ 8) ·
/// จ่ายจากบัญชีไหน: <paramref name="BankAccountId"/> (BankAccounts.Id → ผังที่ผูก) <b>หรือ</b> <paramref name="PaymentAccountId"/> (ผังเงินสดตรง) ·
/// ไม่ส่ง = เงินสด (ค่าตั้งต้นเดิมของใบสำคัญจ่าย) · <paramref name="PaymentMethod"/> ไม่ส่ง = เซิร์ฟเวอร์ตั้งจากช่องทาง (ธนาคาร = โอน · อื่น ๆ = เงินสด)</summary>
public record PayExpenseClaimRequest(
    PaymentMethod? PaymentMethod = null,
    string? Reference = null,
    string? Notes = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null);
