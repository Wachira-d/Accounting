namespace Accounting.Models.DTOs.Payroll;

public record CreateSalaryAdvanceRequest(
    Guid EmployeeId,
    DateTime RequestDate,
    decimal Amount,
    string? Reason,
    // 0 = recover the full outstanding balance on the next payroll run.
    decimal MonthlyDeduction = 0);

public record UpdateSalaryAdvanceRequest(
    DateTime? RequestDate,
    decimal? Amount,
    string? Reason,
    decimal? MonthlyDeduction);

public record ApproveSalaryAdvanceRequest(string? Notes);

public record RejectSalaryAdvanceRequest(string Reason);

/// <summary>จ่ายเงินทดรอง — จ่ายจากบัญชีไหน: <paramref name="BankAccountId"/> (BankAccounts.Id → ผังที่ผูก) <b>หรือ</b>
/// <paramref name="PaymentAccountId"/> (ผังเงินสด/ช่องทางอื่นตรง) อย่างใดอย่างหนึ่ง · ไม่ส่งทั้งคู่ = ใบสำคัญจ่ายลงเงินสด (ค่าตั้งต้นเดิมของเอกสาร) ·
/// ทีม F1 2026-10-14: เดิมหน้าเว็บส่ง <c>bankAccountId: null</c> ตายตัว ⇒ เงินโอนถูกลงเป็นเงินสดทุกครั้ง (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 19)</summary>
public record DisburseSalaryAdvanceRequest(
    DateTime? DisbursementDate,
    Guid? BankAccountId,
    Guid? PaymentAccountId = null);

public record SalaryAdvanceResponse(
    Guid Id,
    string AdvanceNumber,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateTime RequestDate,
    decimal Amount,
    string? Reason,
    string Status,
    decimal MonthlyDeduction,
    decimal ClearedAmount,
    decimal OutstandingAmount,
    DateTime? ApprovedAt,
    string? ApprovedByName,
    string? ApprovalNotes,
    string? RejectionReason,
    DateTime? DisbursedAt,
    Guid? DisbursementDocumentId,
    DateTime CreatedAt);
