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
    string? SocialSecurityHospital,
    // ═══ D-S1: ไม่ส่ง = **ไม่หัก ปกส.** เงียบ ๆ ═══
    // เดิมเป็นพารามิเตอร์บังคับไม่มี default ⇒ ฟอร์มที่ไม่ส่งช่องนี้ (payroll.html)
    // ได้ `false` โดยไม่มีอะไรเตือน ขณะที่อีกฟอร์ม (employees.html) ส่ง = drift ·
    // ผลตาม ม.33: ไม่หัก ไม่นำส่ง ไม่ขึ้น สปส.1-10/1-03 ⇒ เงินเพิ่ม §49 และ
    // ลูกจ้างเสียสิทธิ์ · แก้ย้อนหลังผ่าน UI ก็ไม่ได้เพราะ Update DTO ไม่มีช่องนี้
    // ⇒ default = true (ลูกจ้างส่วนใหญ่อยู่ในระบบ ปกส.) — "ไม่รู้" ต้องเข้าทาง
    //   ที่ถูกกฎหมาย ไม่ใช่ทางที่เงียบที่สุด
    bool IsSubjectToSocialSecurity = true,
    bool HasProvidentFund = false,
    decimal ProvidentFundEmployeePercent = 0m,
    decimal ProvidentFundEmployerPercent = 0m,
    Guid? BranchId = null, Guid? DimensionId = null,
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
    string? LineId = null,
    // ═══ ค่าลดหย่อนภาษี §47 (D-T2) ═══
    // 8 ช่องนี้มีอยู่บน entity + คอลัมน์ในฐานมาตลอด และเครื่องคิดภาษีก็อ่าน
    // ครบทุกช่อง — แต่ **ไม่มีจุดเขียนเลยทั้งเรพ** (ไม่มีใน DTO ไม่มีในฟอร์ม)
    // ⇒ ทุกคนได้ลดหย่อนแค่ 60,000 ⇒ พนักงานที่มีคู่สมรส + บุตร 2 คน เงินเดือน
    // 50,000 ถูกหักภาษี 31,925 ทั้งที่ควรเป็น 6,475 (4.9 เท่า)
    // "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" — ครึ่งเซิร์ฟเวอร์ ship ไปคนเดียว
    bool HasSpouseAllowance = false,
    int ChildAllowanceCount = 0,
    int SecondAndLaterChildren = 0,
    int ParentAllowanceCount = 0,
    decimal LifeInsurancePremium = 0m,
    decimal RmfSsfContribution = 0m,
    decimal DonationAmount = 0m,
    int TaxAllowances = 0);

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
    string? LineId = null,
    /// <summary>D-S1 — เดิมไม่มีช่องนี้ใน Update เลย ⇒ พนักงานที่ถูกตั้งเป็น
    /// false ตอนสร้าง แก้กลับผ่าน UI ไม่ได้ตลอดกาล</summary>
    bool? IsSubjectToSocialSecurity = null,
    // ═══ ค่าลดหย่อนภาษี §47 (D-T2) ═══
    // 8 ช่องนี้มีอยู่บน entity + คอลัมน์ในฐานมาตลอด และเครื่องคิดภาษีก็อ่าน
    // ครบทุกช่อง — แต่ **ไม่มีจุดเขียนเลยทั้งเรพ** (ไม่มีใน DTO ไม่มีในฟอร์ม)
    // ⇒ ทุกคนได้ลดหย่อนแค่ 60,000 ⇒ พนักงานที่มีคู่สมรส + บุตร 2 คน เงินเดือน
    // 50,000 ถูกหักภาษี 31,925 ทั้งที่ควรเป็น 6,475 (4.9 เท่า)
    // "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" — ครึ่งเซิร์ฟเวอร์ ship ไปคนเดียว
    // nullable ทั้งหมด — ไม่ส่ง = ไม่แตะค่าเดิม (ฟอร์มที่ยังไม่มีแท็บนี้
    // ต้องไม่ล้างค่าลดหย่อนของพนักงานทิ้งโดยไม่ตั้งใจ)
    bool? HasSpouseAllowance = null,
    int? ChildAllowanceCount = null,
    int? SecondAndLaterChildren = null,
    int? ParentAllowanceCount = null,
    decimal? LifeInsurancePremium = null,
    decimal? RmfSsfContribution = null,
    decimal? DonationAmount = null,
    int? TaxAllowances = null);

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
    string? LineId = null,
    bool IsSubjectToSocialSecurity = true,
    // echo กลับ — เก็บแล้วต้องแสดงได้ ไม่งั้น "เปิดแก้แล้วบันทึก ค่าหายเงียบ ๆ"
    bool HasSpouseAllowance = false,
    int ChildAllowanceCount = 0,
    int SecondAndLaterChildren = 0,
    int ParentAllowanceCount = 0,
    decimal LifeInsurancePremium = 0m,
    decimal RmfSsfContribution = 0m,
    decimal DonationAmount = 0m,
    int TaxAllowances = 0);

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

/// <summary>สร้างรอบเงินเดือน
///
/// <para>⚠️ <c>PeriodStart</c>/<c>PeriodEnd</c> เป็น <b>nullable</b> (D-F1) —
/// เดิมเป็น <c>DateTime</c> ธรรมดา ผู้เรียกที่ไม่ส่งมาจะได้ <c>default</c>
/// ทั้งคู่ ⇒ ด่าน <c>PeriodStart &gt;= PeriodEnd</c> เป็นจริงเสมอ ⇒
/// <b>สร้างรอบจากหน้าจอถูกปฏิเสธทุกครั้ง</b> ไม่มีใครสร้างรอบสำเร็จเลย
/// (ทางเดียวที่ใช้ได้คือ <c>POST /runs/import</c> ของคู่ค้า) ·
/// ไม่ส่ง = ใช้ต้นเดือน–สิ้นเดือนของงวดนั้น</para>
///
/// <para><c>Year</c> รับได้ทั้ง ค.ศ. และ พ.ศ. — เซิร์ฟเวอร์ normalize เอง
/// เพราะหน้าจอไทยแสดง พ.ศ. เป็นปกติ</para></summary>
public record CreatePayrollRunRequest(
    string Name, int Year, int Month,
    DateTime PayDate, DateTime? PeriodStart = null, DateTime? PeriodEnd = null);

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
    decimal? TaxableGross = null,
    // ค่าจ้างที่ใช้เป็นฐานสมทบประกันสังคม ม.33 (≠ รายได้รวม: เบี้ยเลี้ยง/ค่าน้ำมัน
    // เหมาจ่ายที่ไม่ใช่ค่าตอบแทนการทำงานไม่นับเป็นฐาน) — ตัวเลขนี้คือช่อง "ค่าจ้าง"
    // ที่จะปรากฏใน สปส.1-10. ไม่ส่งมา = ระบบอนุมานจากยอดสมทบที่ส่งมา (÷ อัตรา)
    // เพื่อให้คู่ (ค่าจ้าง, เงินสมทบ) บนไฟล์ตรงกันเสมอ
    decimal? SocialSecurityBase = null);

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
    Guid? JournalEntryId = null,
    // ── สิทธิ์แก้ไข: เซิร์ฟเวอร์ตัดสิน หน้าเว็บ "แสดง" อย่างเดียว ──
    // เดิม payroll.html คำนวณ payEditable เองจาก status (สำเนามือชุดที่ 3 ของ
    // กติกาเดียวกัน) และเมื่อแก้ไม่ได้ก็แค่**ซ่อนปุ่มเงียบ ๆ** ผู้ใช้จึงไม่รู้
    // ว่าทำไมและต้องทำอะไรต่อ. ย้ายมาที่ Helpers/PayrollRunEditPolicy ตัวเดียว
    // แล้วส่งทั้ง "ได้/ไม่ได้" + "เหตุผลพร้อมทางแก้" มาให้แสดง
    bool CanEditAmounts = false,
    string? EditLockReason = null,
    bool CanReopen = false,
    string? ReopenBlockReason = null,
    // ร่องรอยการกลับรายการจ่ายครั้งล่าสุด (Paid → Approved)
    DateTime? ReopenedAt = null,
    string? ReopenedBy = null,
    string? ReopenReason = null,
    // อัตรา/เพดานประกันสังคมของปีนั้น (ม.33) — ส่งมาให้หน้าจอคำนวณตัวอย่างสด ๆ
    // ตอนผู้ใช้แก้ฐานค่าจ้าง **ห้ามหน้าเว็บฝังอัตราเอง** (ตารางกฎหมายที่ถูกคัดลอก
    // ไปเขียนใหม่ใน JS = คิดผิดตลอดไป — กฎเหล็ก #4). เติมเฉพาะตอนดึง run เดี่ยว
    decimal SsoRatePercent = 5m,
    decimal SsoEmployerRatePercent = 5m,
    decimal SsoWageCeiling = 0m);

/// <summary>1 บรรทัดรายคนในรอบเงินเดือน (สำหรับตารางหน้าจอ run detail).
/// ชื่อ field ตรงกับที่ payroll.html viewRun อ่าน (employeeName/baseSalary/
/// allowances/incomeTax/socialSecurity/netPay).</summary>
public record PayrollRunLineDto(
    Guid EmployeeId,
    string EmployeeName,
    string? EmployeeCode,
    // รายได้ (raw — ใช้ pre-fill ตัวแก้ยอด)
    decimal BaseSalary,
    decimal OvertimePay,
    decimal Allowances,
    decimal Commission,
    decimal Bonus,
    decimal OtherIncome,
    decimal GrossIncome,
    // รายการหัก
    // ฐานค่าจ้าง ปกส. ที่ยอดสมทบคิดมาจริง (0 = ข้อมูลเก่ายังไม่เคยตั้ง)
    decimal SocialSecurityBase,
    decimal SocialSecurityEmployee,
    decimal SocialSecurityEmployer,
    decimal WithholdingTax,
    decimal ProvidentFundEmployee,
    decimal LoanDeduction,
    decimal OtherDeductions,
    decimal TotalDeductions,
    decimal NetPay,
    // แหล่งจ่ายเงินสุทธิรายคน (AccountCode; null = ใช้ค่าระดับ run/default)
    string? NetPaymentAccountCode = null);

/// <summary>นำส่งประกันสังคมให้ สปส. — เลือกวันที่จ่าย + บัญชีธนาคาร +
/// เลขรับใบ สปส.1-10 (optional). ระบบ post JE Dr 21815 / Cr Bank
/// (+ เงินเพิ่ม §49 2%/เดือนถ้าจ่ายช้า).</summary>
public record SettleSsoRequest(
    DateTime PayDate,
    Guid? BankAccountId = null,
    Guid? BankGlAccountId = null,   // ChartOfAccount.Id (เงินสด/เงินทดรองกรรมการ/ช่องจ่ายอื่น) → ใช้เป็นผัง Cr ตรง ๆ
    string? FilingNumber = null);

/// <summary>ตั้งแหล่งจ่ายเงินสุทธิรายคน — AccountCode = ผังเงินสด/ธนาคาร/ช่อง
/// จ่าย (null/ว่าง = ใช้ค่าระดับ run/default).</summary>
public record SetPaymentAccountRequest(string? AccountCode);

/// <summary>กลับรายการจ่ายเงินเดือน (Paid → Approved) เพื่อแก้ยอดย้อนหลัง.
/// เหตุผลบังคับ — เป็นรายการที่กลับ JE ที่ลงบัญชีไปแล้ว ต้องตอบผู้ตรวจได้ว่า
/// ทำไม (พ.ร.บ.การบัญชี ม.10 ร่องรอยการแก้ไข).</summary>
public record ReopenPayrollRunRequest(string Reason);

/// <summary>แก้ยอดรายคนในรอบ (ก่อนจ่าย). field ที่ส่งมา (HasValue) เท่านั้น
/// ที่อัปเดต; ระบบรวม Gross/หัก/สุทธิ + run totals ใหม่ให้. ค่าติดลบถูกปัดเป็น 0.</summary>
public record UpdatePayrollDetailRequest(
    // ค่าจ้างที่ใช้เป็นฐานสมทบประกันสังคม (ม.33) — ส่งมาแล้วระบบคิด**ทั้งสองฝั่ง**
    // ใหม่จากฐานนี้ (ไม่ต้องกรอกยอดสมทบเอง). ถ้าไม่ส่งแต่ส่ง SocialSecurityEmployee
    // มา ระบบจะย้อนหาฐานจากยอดนั้นแล้วให้ฝั่งนายจ้างตามฐานเดียวกัน
    decimal? SocialSecurityBase = null,
    decimal? BaseSalary = null,
    decimal? OvertimePay = null,
    decimal? Allowances = null,
    decimal? Commission = null,
    decimal? Bonus = null,
    decimal? OtherIncome = null,
    decimal? SocialSecurityEmployee = null,
    decimal? SocialSecurityEmployer = null,
    decimal? WithholdingTax = null,
    decimal? ProvidentFundEmployee = null,
    decimal? LoanDeduction = null,
    decimal? OtherDeductions = null);

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

/// <summary>ข้อมูลสำหรับ render สลิปเงินเดือน PDF (QuestPDF) — PdfGenerationService
/// โหลด branding (โลโก้/สีธีมจากใบกำกับ) เองจาก companyId.</summary>
public record PayslipPdfData(
    string EmployeeName, string EmployeeCode, string Department, string Position,
    int Year, int Month, DateTime PayDate,
    decimal BaseSalary, decimal OvertimePay, decimal Allowances, decimal Commission,
    decimal Bonus, decimal OtherIncome, decimal GrossIncome,
    decimal SocialSecurityEmployee, decimal WithholdingTax, decimal ProvidentFundEmployee,
    decimal LoanDeduction, decimal OtherDeductions, decimal TotalDeductions,
    decimal NetPay, decimal YtdIncome, decimal YtdTax);

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
