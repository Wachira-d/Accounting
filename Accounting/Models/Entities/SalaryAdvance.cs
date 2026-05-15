namespace Accounting.Models.Entities;

/// <summary>
/// เงินทดรองจ่าย / เงินเบิกล่วงหน้าพนักงาน (Salary Advance).
///
/// Workflow: Draft → Submitted → Approved → Disbursed → Cleared.
/// On Disbursed a PaymentVoucher <see cref="Document"/> is auto-generated
/// (Dr Advance Receivable / Cr Cash) via the central DocumentService — the
/// advance is NOT an isolated HR ledger, it posts straight to the GL.
/// Outstanding balance is recovered automatically by the payroll run, which
/// adds a "Cr Advance Receivable" line and reduces the employee's net pay.
/// </summary>
public class SalaryAdvance : TenantEntity
{
    public string AdvanceNumber { get; set; } = "";          // HR-ADV-yyyyMM-NNNN
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTime RequestDate { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }

    // Draft, Submitted, Approved, Disbursed, Cleared, Rejected, Voided
    public string Status { get; set; } = "Draft";

    // Repayment plan. MonthlyDeduction = 0 means "recover the full
    // outstanding amount on the next payroll run".
    public decimal MonthlyDeduction { get; set; }
    public decimal ClearedAmount { get; set; }               // recovered via payroll so far
    public decimal OutstandingAmount { get; set; }           // Amount - ClearedAmount

    // Approval
    public Guid? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? RejectionReason { get; set; }

    // Disbursement — links to the generated PaymentVoucher document so the
    // accountant can trace the advance to a standard accounting voucher.
    public DateTime? DisbursedAt { get; set; }
    public Guid? DisbursementDocumentId { get; set; }
    public Document? DisbursementDocument { get; set; }
}
