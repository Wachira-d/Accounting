namespace Accounting.Models.Entities;

// ===== Payroll System =====

/// <summary>
/// พนักงาน
/// </summary>
public class Employee : TenantEntity
{
    public string EmployeeCode { get; set; } = "";
    public string TitleTh { get; set; } = "";           // นาย, นาง, นางสาว
    public string FirstNameTh { get; set; } = "";
    public string LastNameTh { get; set; } = "";
    public string? FirstNameEn { get; set; }
    public string? LastNameEn { get; set; }
    public string? NickName { get; set; }
    public string? CitizenId { get; set; }               // เลขบัตรประชาชน 13 หลัก
    public string? PassportNumber { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? MaritalStatus { get; set; }           // Single, Married, Divorced
    public string? Nationality { get; set; } = "Thai";

    // Contact
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LineId { get; set; }

    // Employment
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? EmploymentType { get; set; }          // FullTime, PartTime, Contract, Probation
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? ProbationEndDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }
    public Guid? DimensionId { get; set; }               // Cost Center / Department
    public AccountingDimension? Dimension { get; set; }

    // === Organisation structure (Department/Position entities replace
    // the legacy string fields above; the strings are retained for
    // historical / payslip-display continuity and migration). ===
    public Guid? DepartmentId { get; set; }
    public Models.Entities.Department? DepartmentRef { get; set; }
    public Guid? PositionId { get; set; }
    public Models.Entities.Position? PositionRef { get; set; }

    // Direct reporting line — Level 1 approver for Leave / Advance / Claim.
    // Self-reference on the same Employee table. Top of the chain (CEO) is
    // null. ApprovalWorkflowResolver walks this chain to identify approvers.
    public Guid? DirectManagerId { get; set; }
    public Employee? DirectManager { get; set; }

    // Compensation
    public decimal BaseSalary { get; set; }
    public string SalaryType { get; set; } = "Monthly";  // Monthly, Daily, Hourly
    public string PayFrequency { get; set; } = "Monthly"; // Monthly, BiMonthly, Weekly
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }

    // Social Security
    public string? SocialSecurityNumber { get; set; }
    public string? SocialSecurityHospital { get; set; }
    public bool IsSubjectToSocialSecurity { get; set; } = true;

    // Tax
    public string? TaxId { get; set; }
    public int TaxAllowances { get; set; } = 0;          // จำนวนค่าลดหย่อน
    public bool HasProvidentFund { get; set; } = false;
    public decimal ProvidentFundEmployeePercent { get; set; }
    public decimal ProvidentFundEmployerPercent { get; set; }

    // User link (optional)
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    // Accounting payee link — every employee is mirrored as a Contact so
    // payroll vouchers, advances and reimbursements treat them as a valid
    // payee in the core accounting system (no separate HR payee list).
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    // Linked account
    public Guid? SalaryExpenseAccountId { get; set; }
    public ChartOfAccount? SalaryExpenseAccount { get; set; }

    /// <summary>Cost behavior of this employee's base salary. Monthly
    /// salaried = Fixed (รับเงินไม่ว่าจะทำงานหรือไม่). Daily/Hourly =
    /// Variable (จ่ายตามที่ทำ). Drives fix-vs-variable cost reports.
    /// Default inferred from SalaryType on create.</summary>
    public string CostBehavior { get; set; } = "Fixed";  // Fixed, Variable

    /// <summary>Optional external HR system identifier (HRIS, attendance
    /// software, parent-company SAP, etc.) — lets a sync push/pull this
    /// employee row without name-matching. ExternalSystem labels the
    /// origin so multi-source syncs can co-exist.</summary>
    public string? ExternalId { get; set; }
    public string? ExternalSystem { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public ICollection<PayrollRun> PayrollRuns { get; set; } = new List<PayrollRun>();
    public ICollection<EmployeeLeave> Leaves { get; set; } = new List<EmployeeLeave>();
}

/// <summary>
/// การจ่ายเงินเดือน (รอบจ่าย)
/// </summary>
public class PayrollRun : TenantEntity
{
    public string PayrollNumber { get; set; } = "";
    public string Name { get; set; } = "";               // e.g. "เงินเดือน มีนาคม 2569"
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTime PayDate { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string Status { get; set; } = "Draft";        // Draft, Calculated, Approved, Paid, Voided

    // Totals
    public decimal TotalGrossSalary { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNetPay { get; set; }
    public decimal TotalSocialSecurityEmployee { get; set; }
    public decimal TotalSocialSecurityEmployer { get; set; }
    public decimal TotalWithholdingTax { get; set; }
    public decimal TotalProvidentFundEmployee { get; set; }
    public decimal TotalProvidentFundEmployer { get; set; }

    public int EmployeeCount { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public ICollection<PayrollDetail> Details { get; set; } = new List<PayrollDetail>();
}

/// <summary>
/// รายละเอียดการจ่ายเงินเดือนรายคน
/// </summary>
public class PayrollDetail : TenantEntity
{
    public Guid PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    // Earnings
    public decimal BaseSalary { get; set; }
    public decimal OvertimePay { get; set; }
    public decimal Allowances { get; set; }              // ค่าเบี้ยเลี้ยง, ค่าครองชีพ
    public decimal Commission { get; set; }
    public decimal Bonus { get; set; }
    public decimal OtherIncome { get; set; }
    public decimal GrossIncome { get; set; }

    // Deductions
    public decimal SocialSecurityEmployee { get; set; }  // สมทบประกันสังคม (ลูกจ้าง)
    public decimal SocialSecurityEmployer { get; set; }  // สมทบประกันสังคม (นายจ้าง)
    public decimal WithholdingTax { get; set; }          // ภาษีหัก ณ ที่จ่าย
    public decimal ProvidentFundEmployee { get; set; }   // กองทุนสำรองเลี้ยงชีพ (ลูกจ้าง)
    public decimal ProvidentFundEmployer { get; set; }   // กองทุนสำรองเลี้ยงชีพ (นายจ้าง)
    public decimal LoanDeduction { get; set; }           // หักเงินกู้
    public decimal OtherDeductions { get; set; }
    public decimal TotalDeductions { get; set; }

    // Net
    public decimal NetPay { get; set; }

    // Calculation reference
    public decimal CumulativeIncomeYTD { get; set; }     // รายได้สะสมปีนี้
    public decimal CumulativeTaxYTD { get; set; }        // ภาษีสะสมปีนี้
    public decimal EstimatedAnnualIncome { get; set; }
    public decimal EstimatedAnnualTax { get; set; }

    // Working days
    public int WorkDays { get; set; }
    public int AbsentDays { get; set; }
    public int LeaveDays { get; set; }
    public decimal OvertimeHours { get; set; }

    public string? Remarks { get; set; }
    public string? BreakdownJson { get; set; }           // JSON of detailed breakdown
}

/// <summary>
/// ลาพักร้อน/ลาป่วย
/// </summary>
public class EmployeeLeave : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public string LeaveType { get; set; } = "";          // Annual, Sick, Personal, Maternity
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal TotalDays { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "Pending";      // Pending, Approved, Rejected, Cancelled
    public string? ApprovedBy { get; set; }
    public string? RejectionReason { get; set; }         // Filled when Status = Rejected

    /// <summary>0 = full day(s) only, 1 = half-day morning, 2 = half-day
    /// afternoon. When set to 1/2, TotalDays should be 0.5. The quota
    /// deducts the fractional value.</summary>
    public int HalfDayMarker { get; set; } = 0;
}

/// <summary>
/// HR-configurable leave-type catalog. Replaces the hardcoded string-key
/// LeaveQuotasJson in CompanySettings (kept as fallback for legacy
/// tenants). Each row = one leave type per company. The defaults
/// auto-seeded on first read mirror Thai labor-law (Annual 6d, Sick 30d,
/// Personal 3d, Maternity 98d) — HR adjusts via /pages/leave-types.html.
/// </summary>
public class LeaveType : TenantEntity
{
    /// <summary>Stable key referenced by EmployeeLeave.LeaveType — "Annual",
    /// "Sick", "Personal", "Maternity", "Other" plus any custom keys the
    /// company defines (e.g. "Bereavement").</summary>
    public string Code { get; set; } = "";

    /// <summary>Thai display name shown on the request form.</summary>
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }

    /// <summary>Days allowed per calendar year. Decimal so half-days +
    /// hourly conversions stay clean.</summary>
    public decimal AnnualQuota { get; set; }

    /// <summary>Paid leave deducts from quota but pays salary. Unpaid
    /// (= false) flows to payroll's UnpaidLeave deduction line.</summary>
    public bool IsPaid { get; set; } = true;

    /// <summary>Allow half-day requests (TotalDays = 0.5).</summary>
    public bool AllowHalfDay { get; set; } = true;

    /// <summary>Allow unused days to roll to next year. Standard Thai
    /// practice: Annual allows ≤5d roll-over; Sick resets every year.</summary>
    public bool CarryForward { get; set; } = false;

    /// <summary>Max days that can carry forward when CarryForward = true.
    /// Null = unlimited (rare).</summary>
    public decimal? CarryForwardCap { get; set; }

    /// <summary>Required advance-notice days. 0 = same-day allowed
    /// (Sick / emergency). UI warns when violated; not server-enforced.</summary>
    public int AdvanceNoticeDays { get; set; } = 0;

    /// <summary>Require photo / cert attachment. Thai labor law §32:
    /// Sick > 3 days needs doctor's certificate. Server gates Submit.</summary>
    public bool RequiresAttachment { get; set; } = false;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Hex colour for calendar pills + dashboard charts.</summary>
    public string Color { get; set; } = "#6366f1";
    public string? Icon { get; set; }
}

/// <summary>
/// Per-employee, per-year, per-type balance adjustment. Holds the
/// carry-forward day-count from the previous year (after applying
/// LeaveType.CarryForwardCap) plus any manual HR adjustment (e.g. add
/// 3 days as a perk, deduct 1 day for a forgotten clock-in).
///
/// Effective quota for an employee × year × type =
///     LeaveType.AnnualQuota
///   + EmployeeLeaveBalance.CarriedForwardDays
///   + EmployeeLeaveBalance.AdjustmentDays
///
/// Rows are written by the year-end carry-forward job
/// (Phase=YearEnd) or by the HR adjustment endpoint (Phase=Manual).
/// Unique on (CompanyId, EmployeeId, Year, LeaveTypeCode) so each
/// employee has exactly one balance row per type per year — the job
/// upserts; HR adjustments overwrite the AdjustmentDays portion.
/// </summary>
public class EmployeeLeaveBalance : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>Calendar year this row applies to (the YEAR FOR WHICH
    /// the quota is being adjusted, not the year the unused days came
    /// from). E.g. Year=2026 row carries forward 2025's unused days.</summary>
    public int Year { get; set; }

    /// <summary>References LeaveType.Code — stable string key.</summary>
    public string LeaveTypeCode { get; set; } = "";

    /// <summary>Days rolled forward from Year-1. Already clamped by
    /// LeaveType.CarryForwardCap when the year-end job ran. Can be 0
    /// when the type doesn't carry forward (e.g. Sick).</summary>
    public decimal CarriedForwardDays { get; set; } = 0m;

    /// <summary>Free-form HR adjustment (positive = add, negative =
    /// deduct). Independent of carry-forward; HR uses this for one-off
    /// changes (perk days, disciplinary deduction, prorated hire).</summary>
    public decimal AdjustmentDays { get; set; } = 0m;

    public string? Notes { get; set; }

    /// <summary>"YearEnd" (written by the auto-roll job at Jan 1) or
    /// "Manual" (HR-adjustment endpoint). Lets the audit see who
    /// produced the entry.</summary>
    public string Phase { get; set; } = "Manual";
}

/// <summary>
/// Public-holiday calendar — company-configurable list. Used by the
/// leave engine to skip non-working days when computing TotalDays + by
/// payroll for §29 holiday-pay multipliers.
/// </summary>
public class PublicHoliday : TenantEntity
{
    public DateTime Date { get; set; }
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }

    /// <summary>"Public" (ราชการ), "Religious" (ทางศาสนา), "Substitute"
    /// (วันหยุดชดเชย), "Company" (วันหยุดบริษัทเอง).</summary>
    public string Category { get; set; } = "Public";

    /// <summary>Flag when this is a labor-law substitute day for a
    /// weekend-overlapping holiday.</summary>
    public bool IsSubstitute { get; set; } = false;
}

/// <summary>
/// รายการรายได้/หักประจำ (เบี้ยเลี้ยง, ค่าล่วงเวลา ฯลฯ)
/// </summary>
public class PayrollItem : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string ItemType { get; set; } = "Earning";    // Earning, Deduction
    public string CalculationType { get; set; } = "Fixed"; // Fixed, Percentage, Formula
    public decimal? FixedAmount { get; set; }
    public decimal? Percentage { get; set; }
    public bool IsTaxable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public Guid? AccountId { get; set; }                 // GL account
    public ChartOfAccount? Account { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// ค่าตั้งประกันสังคมรายปี (override) — เพดานค่าจ้าง + อัตราสมทบ ของปีนั้น
/// ของบริษัทนั้น เมื่อไม่มีแถว ระบบใช้ตารางตามกฎหมายใน
/// Helpers.SsoRateSchedule (15,000 → 17,500 ปี 2026 → 20,000 ปี 2029 →
/// 23,000 ปี 2032). มีไว้เพราะเพดาน/อัตราปรับได้อีกตามประกาศแต่ละปี
/// (รวมประกาศลดอัตราชั่วคราว) — ผู้ใช้ปรับเองได้ทันทีไม่ต้องรออัปเดตระบบ
/// </summary>
public class SsoYearConfig : TenantEntity
{
    /// <summary>ปี ค.ศ. (ระบบ normalize พ.ศ. ให้ตอนบันทึก)</summary>
    public int Year { get; set; }

    /// <summary>เพดานค่าจ้ายรายเดือนที่ใช้คำนวณสมทบ (เช่น 17,500)</summary>
    public decimal WageCeiling { get; set; }

    /// <summary>อัตราสมทบลูกจ้าง เป็นเปอร์เซ็นต์ (เช่น 5 = 5%)</summary>
    public decimal RatePercent { get; set; } = 5m;

    /// <summary>อัตราสมทบนายจ้าง เป็นเปอร์เซ็นต์ — ปกติเท่าลูกจ้าง</summary>
    public decimal EmployerRatePercent { get; set; } = 5m;

    public string? Notes { get; set; }
}
