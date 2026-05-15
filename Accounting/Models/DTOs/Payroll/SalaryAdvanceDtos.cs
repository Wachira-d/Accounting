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

public record DisburseSalaryAdvanceRequest(
    DateTime? DisbursementDate,
    // Optional bank/cash account the advance is paid from; null → the
    // PaymentVoucher posting falls back to the default cash account.
    Guid? BankAccountId);

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
