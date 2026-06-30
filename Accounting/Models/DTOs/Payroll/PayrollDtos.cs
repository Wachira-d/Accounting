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
    Guid? DirectManagerId = null,
    /// <summary>"Fixed" (salaried — cost incurred whether they work or not)
    /// or "Variable" (paid per day/hour worked). Defaults from SalaryType:
    /// Monthly→Fixed, Daily/Hourly→Variable.</summary>
    string? CostBehavior = null,
    /// <summary>External HR system identifier — enables 2-way sync without
    /// name-matching. ExternalSystem labels the source.</summary>
    string? ExternalId = null,
    string? ExternalSystem = null,
    /// <summary>LINE User ID ของพนักงาน (รูปแบบ Uxxxxxxxx) — ใช้ส่งแจ้งเตือน
    /// ผลอนุมัติลา/สลิปเงินเดือนตรงถึงพนักงานโดยไม่ต้องเป็น user ในระบบ.</summary>
    string? LineId = null);

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
    bool? IsActive = null,
    string? CostBehavior = null,
    string? ExternalId = null,
    string? ExternalSystem = null,
    string? SalaryType = null,
    string? LineId = null);

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
    Guid? ContactId = null,
    string CostBehavior = "Fixed",
    string? ExternalId = null,
    string? ExternalSystem = null,
    DateTime? LastSyncedAt = null,
    string? Phone = null,
    string? Email = null,
    string? LineId = null);

/// <summary>Bulk-sync envelope for employees from an external HRIS. Each
/// row is upserted on (CompanyId, ExternalSystem, ExternalId). Rows
/// with no ExternalId are skipped (sync requires the external ID for
/// dedupe — use CreateEmployee for blind insert).</summary>
public record SyncEmployeesRequest(
    string ExternalSystem,
    List<CreateEmployeeRequest> Rows);

public record SyncEmployeesResponse(
    int Inserted,
    int Updated,
    int Skipped,
    List<string> Errors);

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

/// <summary>Import payroll run จากระบบนอกที่คำนวณยอดเองแล้ว (เช่น TakeTime).
/// recalculate=false → NextAcc ใช้ยอดที่ส่งมาตรง ๆ ไม่คำนวณใหม่ → run ออกมา
/// สถานะ Calculated ทันที (ข้าม calculate). idempotency ผ่าน ExternalRunRef.</summary>
public record ImportPayrollRunRequest(
    string Name, int Year, int Month,
    DateTime PayDate, DateTime PeriodStart, DateTime PeriodEnd,
    List<ImportPayrollLine> Lines,
    string? ExternalSystem = null,
    string? ExternalRunRef = null,
    bool Recalculate = false);

/// <summary>ยอดเงินเดือนสำเร็จรูปต่อพนักงาน 1 คน (จาก TakeTime). map พนักงาน
/// ด้วย EmployeeExternalId (Employee.ExternalId) ก่อน, fallback CitizenId.</summary>
public record ImportPayrollLine(
    string? EmployeeExternalId,
    string? CitizenId,
    string? EmployeeName,
    // รายได้
    decimal BaseSalary,
    decimal OvertimePay,
    decimal Allowances,
    decimal Commission,
    decimal Bonus,
    decimal OtherEarnings,
    decimal GrossIncome,
    // หัก
    decimal SocialSecurityEmployee,
    decimal SocialSecurityEmployer,
    decimal WithholdingTax,
    decimal ProvidentFundEmployee,
    decimal ProvidentFundEmployer,
    decimal SalaryAdvance,
    decimal OtherDeductions,
    decimal TotalDeductions,
    decimal NetPay,
    // override (optional)
    string? SalaryExpenseAccountCode = null,
    string? PaymentAccountCode = null,
    string? IncomeTypeCode = null,
    // taxableGross (ถ้าต่างจาก gross — สวัสดิการยกเว้นภาษี). null = ใช้ gross
    decimal? TaxableGross = null);

/// <summary>ผลลัพธ์ import — run + เอกสารที่ออกให้.</summary>
public record ImportPayrollRunResult(
    Guid Id, string PayrollNumber, string Status,
    decimal TotalGrossSalary, decimal TotalWithholdingTax,
    decimal TotalSocialSecurityEmployee, decimal TotalSocialSecurityEmployer,
    decimal TotalNetPay, int EmployeeCount,
    Guid? JournalEntryId,
    bool WasExisting,
    List<string> Warnings);

public record PayrollRunResponse(
    Guid Id, string PayrollNumber, string Name,
    int Year, int Month, DateTime PayDate, string Status,
    decimal TotalGrossSalary, decimal TotalDeductions,
    decimal TotalNetPay, decimal TotalWithholdingTax,
    decimal TotalSocialSecurityEmployee,
    decimal TotalSocialSecurityEmployer,
    int EmployeeCount, DateTime CreatedAt,
    // ── ประกันสังคมรอนำส่ง (สปส.1-10) ──
    DateTime? SsoSettledAt = null,
    Guid? SsoSettlementJournalEntryId = null,
    string? SsoFilingNumber = null,
    decimal SsoLateFeeAmount = 0,
    decimal TotalWorkersCompensation = 0,
    // รายการรายคนในรอบ (เติมเฉพาะตอนดึง run เดี่ยว GetPayrollRunAsync — list
    // ปล่อย null เพื่อให้ payload เบา). ใช้แสดงตารางรายคน + ปุ่มสลิป/50ทวิ
    // บนหน้าจอ รวมถึง run ที่ import มาจากระบบนอก (TakeTime).
    List<PayrollRunLineDto>? Details = null,
    // ป้ายแหล่งที่มา — แยก run ที่ import จากระบบนอกกับที่สร้างในระบบ
    string? ExternalSystem = null,
    string? ExternalRunRef = null,
    // JE ที่ post ตอนจ่าย (Dr เงินเดือน/Cr ปกส.+ภงด.1+ธนาคาร) — ใช้ deep-link
    // ไปหน้าสมุดรายวันดูรายการบัญชีของรอบนี้
    Guid? JournalEntryId = null);

/// <summary>1 บรรทัดรายคนในรอบเงินเดือน (สำหรับตารางหน้าจอ run detail).
/// ชื่อ field ตรงกับที่ payroll.html viewRun อ่าน (employeeName/baseSalary/
/// allowances/incomeTax/socialSecurity/netPay).</summary>
public record PayrollRunLineDto(
    Guid EmployeeId,
    string EmployeeName,
    string? EmployeeCode,
    decimal BaseSalary,
    decimal Allowances,
    decimal OvertimePay,
    decimal Bonus,
    decimal GrossIncome,
    decimal IncomeTax,
    decimal SocialSecurity,
    decimal WithholdingTax,
    decimal OtherDeductions,
    decimal NetPay,
    // แหล่งจ่ายเงินสุทธิรายคน (AccountCode; null = ใช้ค่าระดับ run/default)
    string? NetPaymentAccountCode = null);

/// <summary>นำส่งประกันสังคมให้ สปส. — เลือกวันที่จ่าย + บัญชีธนาคาร +
/// เลขรับใบ สปส.1-10 (optional). ระบบ post JE Dr 21815 / Cr Bank
/// (+ เงินเพิ่ม §49 2%/เดือนถ้าจ่ายช้า).</summary>
public record SettleSsoRequest(
    DateTime PayDate,
    Guid? BankAccountId = null,
    string? FilingNumber = null);

/// <summary>ตั้งแหล่งจ่ายเงินสุทธิรายคน — AccountCode = ผังเงินสด/ธนาคาร/ช่อง
/// จ่าย (null/ว่าง = ใช้ค่าระดับ run/default).</summary>
public record SetPaymentAccountRequest(string? AccountCode);

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
