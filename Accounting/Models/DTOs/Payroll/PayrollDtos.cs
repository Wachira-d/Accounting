namespace Accounting.Models.DTOs.Payroll;

public record CreateEmployeeRequest(
    string EmployeeCode, string TitleTh, string FirstNameTh,
    string LastNameTh, string? FirstNameEn, string? LastNameEn,
    string? CitizenId, DateTime? DateOfBirth, string? Gender,
    string? Address, string? Phone, string? Email,
    string? Department, string? Position, string? EmploymentType,
    DateTime StartDate, decimal BaseSalary, string? SalaryType,
    string? BankName, string? BankAccountNumber,
    string? BankAccountName, string? SocialSecurityNumber,
    string? SocialSecurityHospital, bool IsSubjectToSocialSecurity,
    bool HasProvidentFund, decimal ProvidentFundEmployeePercent,
    decimal ProvidentFundEmployerPercent,
    Guid? BranchId, Guid? DimensionId,
    // Org structure (preferred over the legacy string Department/Position)
    Guid? DepartmentId = null, Guid? PositionId = null,
    Guid? DirectManagerId = null);

public record UpdateEmployeeRequest(
    string? Position, string? Department, string? Phone,
    string? Email, decimal? BaseSalary, string? BankName,
    string? BankAccountNumber, string? SocialSecurityHospital,
    bool? HasProvidentFund,
    decimal? ProvidentFundEmployeePercent,
    decimal? ProvidentFundEmployerPercent,
    Guid? BranchId, Guid? DimensionId,
    Guid? DepartmentId = null, Guid? PositionId = null,
    Guid? DirectManagerId = null,
    // Onboarding / offboarding toggle (preserves all historical HR + GL
    // records — does NOT delete the employee).
    bool? IsActive = null);

public record EmployeeResponse(
    Guid Id, string EmployeeCode, string TitleTh,
    string FirstNameTh, string LastNameTh,
    string? FirstNameEn, string? LastNameEn,
    string? CitizenId, string? Department, string? Position,
    string? EmploymentType, DateTime StartDate, DateTime? EndDate,
    decimal BaseSalary, string SalaryType, bool IsActive,
    DateTime CreatedAt,
    Guid? DepartmentId = null, string? DepartmentName = null,
    Guid? PositionId = null, string? PositionTitle = null,
    Guid? DirectManagerId = null, string? DirectManagerName = null,
    Guid? ContactId = null);

public record CreatePayrollItemRequest(
    string Code, string Name, string? NameEn,
    string ItemType, string CalculationType,
    decimal? FixedAmount, decimal? Percentage,
    bool IsTaxable, Guid? AccountId);

public record PayrollItemResponse(
    Guid Id, string Code, string Name, string ItemType,
    string CalculationType, decimal? FixedAmount,
    decimal? Percentage, bool IsTaxable, bool IsActive);

public record CreatePayrollRunRequest(
    string Name, int Year, int Month,
    DateTime PayDate, DateTime PeriodStart, DateTime PeriodEnd);

public record PayrollRunResponse(
    Guid Id, string PayrollNumber, string Name,
    int Year, int Month, DateTime PayDate, string Status,
    decimal TotalGrossSalary, decimal TotalDeductions,
    decimal TotalNetPay, decimal TotalWithholdingTax,
    decimal TotalSocialSecurityEmployee,
    decimal TotalSocialSecurityEmployer,
    int EmployeeCount, DateTime CreatedAt);

public record PayrollDetailResponse(
    Guid EmployeeId, string EmployeeCode, string EmployeeName,
    decimal BaseSalary, decimal OvertimePay, decimal Allowances,
    decimal Commission, decimal Bonus, decimal GrossIncome,
    decimal SocialSecurityEmployee, decimal WithholdingTax,
    decimal ProvidentFundEmployee, decimal OtherDeductions,
    decimal TotalDeductions, decimal NetPay);

public record PayslipResponse(
    Guid EmployeeId, string EmployeeName,
    int Year, int Month, byte[] PdfData, string FileName);

public record CreateLeaveRequest(
    Guid EmployeeId, string LeaveType,
    DateTime StartDate, DateTime EndDate,
    decimal TotalDays, string? Reason,
    // Half-day support — 0=full, 1=morning, 2=afternoon. TotalDays
    // should be 0.5 when marker > 0; server validates.
    int HalfDayMarker = 0);

public record LeaveResponse(
    Guid Id, Guid EmployeeId, string EmployeeName,
    string LeaveType, DateTime StartDate, DateTime EndDate,
    decimal TotalDays, string Status, string? Reason,
    string? ApprovedBy = null,
    string? RejectionReason = null,
    int HalfDayMarker = 0,
    DateTime? CreatedAt = null);

// HR config — leave-type catalog CRUD.
public record LeaveTypeRequest(
    string Code, string NameTh, string? NameEn,
    decimal AnnualQuota, bool IsPaid, bool AllowHalfDay,
    bool CarryForward, decimal? CarryForwardCap,
    int AdvanceNoticeDays, bool RequiresAttachment,
    int SortOrder, bool IsActive, string Color, string? Icon);

public record LeaveTypeResponse(
    Guid Id, string Code, string NameTh, string? NameEn,
    decimal AnnualQuota, bool IsPaid, bool AllowHalfDay,
    bool CarryForward, decimal? CarryForwardCap,
    int AdvanceNoticeDays, bool RequiresAttachment,
    int SortOrder, bool IsActive, string Color, string? Icon);

public record PublicHolidayRequest(
    DateTime Date, string NameTh, string? NameEn,
    string Category, bool IsSubstitute);

public record PublicHolidayResponse(
    Guid Id, DateTime Date, string NameTh, string? NameEn,
    string Category, bool IsSubstitute);

public record RejectLeaveRequest(string Reason);

public record LeaveBalanceItem(
    string LeaveType,
    decimal AllocatedDays,
    decimal UsedDays,           // counts Approved + Pending requests for the year
    decimal RemainingDays);

public record LeaveBalanceResponse(
    Guid EmployeeId,
    string EmployeeName,
    int Year,
    List<LeaveBalanceItem> Balances);

// ===== Severance Pay (ค่าชดเชย — Labor Code §118) =====

/// <summary>
/// คำขอคำนวณค่าชดเชยตามอายุงาน เพื่อ preview ก่อนเลิกจ้าง
/// </summary>
public record SeverancePreviewRequest(
    DateTime EndDate,
    string? TerminationReason);

/// <summary>
/// ผลคำนวณค่าชดเชยพร้อม breakdown ตามเกณฑ์ Labor Code §118
///   • <120 days   →   0 days
///   • 120d–1y     →  30 days
///   • 1–3y        →  90 days
///   • 3–6y        → 180 days
///   • 6–10y       → 240 days
///   • 10–20y      → 300 days
///   • >20y        → 400 days
/// </summary>
public record SeverancePreviewResponse(
    Guid EmployeeId,
    string EmployeeName,
    DateTime StartDate,
    DateTime EndDate,
    decimal YearsOfService,           // exact years (fractional)
    int SeveranceDaysGranted,         // 0/30/90/180/240/300/400
    decimal DailyRate,                // BaseSalary / 30 (monthly → daily)
    decimal SeveranceAmount,          // dailyRate × days
    bool IsEligible,                  // false when terminationReason indicates misconduct/voluntary
    string Explanation);              // human-readable reasoning
