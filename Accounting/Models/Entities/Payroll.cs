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
    /// <summary>LINE userId (Uxxxx…) — ใช้ push แจ้งเตือน/สลิปเงินเดือนทาง LINE
    /// (NotificationEngine + PayslipLineDeliveryService). เติมได้ 2 ทาง:
    /// HR กรอกเอง หรือพนักงานผูกเองผ่าน LINE OA บริษัท (EmployeeLineBindCode →
    /// bot เขียนค่านี้ให้). LINE Messaging API push ต้องใช้ userId นี้เท่านั้น
    /// (ใช้ @handle ไม่ได้).</summary>
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
    public int TaxAllowances { get; set; } = 0;          // จำนวนค่าลดหย่อน (legacy — count of 30K dependants)

    // ── ค่าลดหย่อนภาษีตามมาตรา 47/47ทวิ (ละเอียดขึ้น) ──
    // ใช้แทน TaxAllowances เก่าเมื่อต้องการความแม่นยำ — payroll calc รวม
    // ทุกฟิลด์เป็นค่าลดหย่อนต่อปี เดิม TaxAllowances × 30,000 อาจเพี้ยน
    // สำหรับครอบครัวที่มีคู่สมรสไม่มีเงินได้ + ลูกหลายคน + บิดามารดา.
    /// <summary>คู่สมรสที่ไม่มีเงินได้ลดหย่อน 60,000/ปี (true = ใช่)</summary>
    public bool HasSpouseAllowance { get; set; }
    /// <summary>จำนวนบุตรที่ลดหย่อนได้ (30,000/คน — บุตรคนที่ 2 ขึ้นไป
    /// ที่เกิดในหรือหลังปี 2561 ลดหย่อนคนละ 60,000 ตั้งค่าแยกใน
    /// SecondAndLaterChildren)</summary>
    public int ChildAllowanceCount { get; set; }
    /// <summary>จำนวนบุตรที่เกิดในหรือหลังปี 2561 และเป็นคนที่ 2 ขึ้นไป
    /// (ได้ +30,000/คน เพิ่มจาก ChildAllowanceCount ปกติ)</summary>
    public int SecondAndLaterChildren { get; set; }
    /// <summary>จำนวนบิดามารดาที่อายุ ≥60 ปี (30,000/คน — สูงสุด 4 คน
    /// รวมพ่อแม่ตัวเอง + พ่อแม่คู่สมรส)</summary>
    public int ParentAllowanceCount { get; set; }
    /// <summary>เบี้ยประกันชีวิตที่ลดหย่อนได้ (ไม่เกิน 100,000/ปี ตาม §47(1)(ง))</summary>
    public decimal LifeInsurancePremium { get; set; }
    /// <summary>เงินสะสมกองทุน RMF/SSF/Thai ESG ที่ลดหย่อนได้ (รวมไม่เกิน 500,000)</summary>
    public decimal RmfSsfContribution { get; set; }
    /// <summary>เงินบริจาคที่ลดหย่อนได้ (สูงสุด 10% ของเงินได้สุทธิหลังหักลดหย่อนอื่น)</summary>
    public decimal DonationAmount { get; set; }
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

    // ── ประกันสังคมรอนำส่ง (settle to สำนักงานประกันสังคม) ──
    // เดิม payroll JE ตอนจ่ายเงินเดือนจะ Cr 21815 (ประกันสังคมค้างจ่าย) ค้าง
    // ไว้ → ผู้ใช้ต้องจ่ายให้ สปส. ภายในวันที่ 15 ของเดือนถัดไป (พ.ร.บ.
    // ประกันสังคม §47, แบบ สปส.1-10) แล้วบันทึก JE คู่ที่สอง (Dr 21815 /
    // Cr Bank) เพื่อล้างหนี้. field ด้านล่างเก็บสถานะการ settle นี้:
    //   • SsoSettledAt — วันที่นำส่ง (null = ยังไม่ได้นำส่ง)
    //   • SsoSettlementJournalEntryId — JE ที่ล้างหนี้
    //   • SsoSettlementDocumentId — PV/Document ที่ใช้ (ถ้ามี)
    //   • SsoFilingNumber — เลขรับใบ สปส.1-10 จาก portal (กรอกเมื่อยื่นแล้ว)
    //   • SsoLateFeeAmount — เงินเพิ่ม 2%/เดือน ที่เกิดเมื่อยื่นช้า
    //     (พ.ร.บ.ประกันสังคม §49 — เริ่มนับจากวันที่ 16 ของเดือนถัดไป)
    public DateTime? SsoSettledAt { get; set; }
    public Guid? SsoSettlementJournalEntryId { get; set; }
    public Guid? SsoSettlementDocumentId { get; set; }
    public string? SsoFilingNumber { get; set; }
    public decimal SsoLateFeeAmount { get; set; }

    // กองทุนเงินทดแทน (กท.20ก) — นายจ้างฝ่ายเดียว 0.2%–1.0% ตามประเภทกิจการ
    // cap 240,000/คน/ปี ยื่นรายปี (มี.ค.). เก็บยอดที่บันทึกในรอบนี้เพื่อทำ
    // รายงานสรุปสิ้นปี.
    public decimal TotalWorkersCompensation { get; set; }

    // ── External import (ระบบนอกคำนวณเงินเดือนเอง เช่น TakeTime) ──
    // เมื่อ run ถูกสร้างผ่าน POST /payroll/runs/import — ใช้ยอดที่ส่งมาตรง ๆ
    // (ไม่ recalc). ExternalRunRef = idempotency key (กันสร้างซ้ำ).
    // SalaryExpense/NetPaymentAccountCode = override ผังบัญชีตอน post GL
    // (null = ใช้ default ของ NextAcc 54111 / 11122).
    public string? ExternalSystem { get; set; }
    public string? ExternalRunRef { get; set; }
    public bool IsExternalImport { get; set; }
    public string? SalaryExpenseAccountCode { get; set; }
    public string? NetPaymentAccountCode { get; set; }

    // ── กลับรายการจ่าย (Paid → Approved) เพื่อแก้ยอดย้อนหลัง ──
    // ProcessPaymentAsync ลง JE + สร้าง ภ.ง.ด.1/สปส.1-10/สลิป แนบไว้กับ run
    // ⇒ การ "แก้ยอดหลังจ่าย" ไม่ใช่การแก้ field เฉย ๆ ต้องกลับรายการ JE +
    // คืนเงินทดรองที่หักไว้ก่อน (ReopenPaidRunAsync). field ชุดนี้เก็บร่องรอย
    // ว่าใครกลับรายการเมื่อไรเพราะอะไร — ใช้ทั้ง audit และแบนเนอร์เตือนบนจอ
    // ว่า "เอกสารที่แนบไว้ยังเป็นฉบับก่อนแก้ จนกว่าจะกดจ่ายใหม่"
    public DateTime? ReopenedAt { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }

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

    /// <summary>รายได้ที่นำมาคำนวณ WHT — ปกติ = GrossIncome แต่
    /// ถ้ามีสวัสดิการที่ได้รับยกเว้นภาษี (เช่นค่ารักษาพยาบาล / เงินค่าทำงาน
    /// ในเขตพัฒนาพิเศษ §50(1)(ข)) ให้หักออกก่อน. ใช้ใน ภ.ง.ด.1 export +
    /// 50 ทวิ rendering. Default 0 = engine ใช้ GrossIncome แทน
    /// (backward compat กับ data เก่า).</summary>
    public decimal TaxableGross { get; set; }

    // Deductions
    /// <summary>**ค่าจ้างที่ใช้เป็นฐานคำนวณเงินสมทบประกันสังคม** (ม.33) — ตัวเลข
    /// ที่ต้องปรากฏในช่อง "ค่าจ้าง" ของ สปส.1-10 และเป็นฐานของทั้งฝั่งลูกจ้าง
    /// และนายจ้าง
    ///
    /// ═══ ทำไมต้องเก็บ ไม่คำนวณสดตอน export ═══
    /// เดิมไฟล์ สปส.1-10 เอา <c>GrossIncome</c> ไปใส่ช่องค่าจ้าง แล้วเอา
    /// <c>SocialSecurityEmployee</c> ที่เก็บไว้ไปใส่ช่องเงินสมทบ — **สองตัวเลข
    /// คนละแหล่ง** ⇒ พอยอดใดยอดหนึ่งถูกแก้ (หรือรอบถูก import มาโดยคิดฐานคนละ
    /// แบบ) ไฟล์จะประกาศคู่ที่ขัดกันเอง เช่น ค่าจ้าง 14,094 คู่กับสมทบ 683
    /// ทั้งที่ 5% ของ 14,094 = 705 ⇒ สปส. e-Service ตีกลับ/ตั้งคำถาม
    ///
    /// ยังมีเหตุผลทางกฎหมายด้วย: **ค่าจ้างตาม ม.5 ≠ รายได้รวม** — เบี้ยเลี้ยง/
    /// ค่าน้ำมันแบบเหมาจ่ายที่ไม่ใช่ค่าตอบแทนการทำงาน ไม่นับเป็นฐานสมทบ
    /// ผู้ใช้จึงต้องระบุฐานเองได้ (เดิมไม่มีช่อง ต้องไปแก้ยอดสมทบแทน
    /// ซึ่งทำให้คู่ตัวเลขเพี้ยนทันที)
    ///
    /// 0 = ข้อมูลเก่าที่ยังไม่เคยตั้งฐาน → ตัวอ่านต้อง fallback ไปใช้ค่าที่
    /// อนุมานได้ (ดู <c>Helpers/SsoWageBase</c>) ห้ามตีความว่า "ฐานเป็นศูนย์"</summary>
    public decimal SocialSecurityBase { get; set; }
    public decimal SocialSecurityEmployee { get; set; }  // สมทบประกันสังคม (ลูกจ้าง)
    public decimal SocialSecurityEmployer { get; set; }  // สมทบประกันสังคม (นายจ้าง)
    // กองทุนเงินทดแทน — นายจ้างฝ่ายเดียว 0.2-1.0% × min(salary, cap). cap = 240,000/ปี/คน
    public decimal WorkersCompensation { get; set; }
    public decimal WithholdingTax { get; set; }          // ภาษีหัก ณ ที่จ่าย
    public decimal ProvidentFundEmployee { get; set; }   // กองทุนสำรองเลี้ยงชีพ (ลูกจ้าง)
    public decimal ProvidentFundEmployer { get; set; }   // กองทุนสำรองเลี้ยงชีพ (นายจ้าง)
    public decimal LoanDeduction { get; set; }           // หักเงินกู้
    /// <summary>ยอดเงินทดรองที่หักคืนในรอบนี้ — บันทึกตอน Pay เพื่อให้
    /// Void สามารถ restore ยอด OutstandingAmount ของ SalaryAdvance กลับ
    /// ได้ถูกต้อง (เดิมไม่มี → void แล้วยอดเงินทดรองหาย).</summary>
    public decimal AdvanceRecovered { get; set; }
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

    /// <summary>แหล่งจ่ายเงินสุทธิรายคน (AccountCode ของ ChartOfAccount เงินสด/
    /// ธนาคาร/ช่องจ่ายอื่น). null = ใช้ค่าระดับ run (PayrollRun.NetPaymentAccountCode)
    /// → fallback default 11122/111x. รับจาก import (PaymentAccountCode รายคน)
    /// หรือผู้ใช้แก้ในหน้า run detail ก่อนจ่าย. ตอน Pay ระบบ group Cr เงินสด/
    /// ธนาคารตามบัญชีนี้ → จ่ายแต่ละคนจากบัญชีของตัวเองได้.</summary>
    public string? NetPaymentAccountCode { get; set; }
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

/// <summary>
/// กฎภาษีเงินได้บุคคลธรรมดา (PIT) per company × per year — รองรับ
/// ภ.ง.ด.1 monthly + annual 50 ทวิ. เก็บ progressive brackets +
/// ค่าลดหย่อนหลัก + caps ทั้งหมดในที่เดียว เพื่อให้:
///   • สรรพากรปรับเกณฑ์ → admin แก้ในระบบได้ทันที (ไม่ต้อง deploy)
///   • บริษัทคิดต่างจาก default ได้ (เช่นกองทุน PVD พิเศษ)
///   • Engine fallback ถ้าไม่มี config ปีนั้น → ใช้ค่า default จาก
///     กฎหมายปัจจุบัน (PayrollService.Pit* constants)
/// </summary>
public class TaxRuleConfig : TenantEntity
{
    /// <summary>ปี ค.ศ. ของรายได้ (ระบบ normalize พ.ศ. ตอนบันทึก)</summary>
    public int FiscalYear { get; set; }

    /// <summary>Brackets JSON array — `[{"upperBound":150000,"rate":0},
    /// {"upperBound":300000,"rate":0.05}, ...]`. Engine sort by upperBound
    /// + walk เหมือน ThaiTaxBrackets เดิม. Rate เก็บเป็นทศนิยม (0.05 = 5%).
    /// Last bracket ใช้ decimal.MaxValue เป็น upperBound (catch-all).</summary>
    public string BracketsJson { get; set; } = "";

    /// <summary>ค่าลดหย่อนส่วนตัว §47(1)(ก) — default 60,000.</summary>
    public decimal PersonalAllowance { get; set; } = 60_000m;

    /// <summary>ค่าลดหย่อนคู่สมรส §47(1)(ข) — default 60,000.</summary>
    public decimal SpouseAllowance { get; set; } = 60_000m;

    /// <summary>ค่าลดหย่อนบุตรคนละ §47(1)(ค) — default 30,000.</summary>
    public decimal ChildAllowance { get; set; } = 30_000m;

    /// <summary>ค่าลดหย่อนบุตรคนที่ 2 ขึ้นไป (เกิดหลัง 2561)
    /// §47(1)(ค) วรรค 2 — default 60,000.</summary>
    public decimal ChildAllowancePost2561 { get; set; } = 60_000m;

    /// <summary>ค่าลดหย่อนบิดามารดาคนละ §47(1)(ง) — default 30,000
    /// (อายุ 60+, รายได้ไม่เกิน 30,000/ปี).</summary>
    public decimal ParentAllowance { get; set; } = 30_000m;

    /// <summary>เพดาน §42 ทวิ ค่าใช้จ่าย 50% — default 100,000 (ตาม
    /// ประกาศ คทอ. 2560 — เงินเดือนหักได้ 50% ไม่เกิน 100K).</summary>
    public decimal Section42TwiCap { get; set; } = 100_000m;

    /// <summary>เพดานเบี้ยประกันชีวิต — default 100,000.</summary>
    public decimal LifeInsuranceCap { get; set; } = 100_000m;

    /// <summary>เพดานเบี้ยประกันสุขภาพ — default 25,000 (รวม + ประกันชีวิต ≤ 100K).</summary>
    public decimal HealthInsuranceCap { get; set; } = 25_000m;

    /// <summary>เพดานรวม PVD + RMF + SSF + กบข. — default 500,000.</summary>
    public decimal PvdCap { get; set; } = 500_000m;

    /// <summary>เพดานดอกเบี้ยที่อยู่อาศัย — default 100,000.</summary>
    public decimal MortgageInterestCap { get; set; } = 100_000m;

    /// <summary>เพดานบริจาคทั่วไป — เป็น % ของ net income post-allowances.
    /// Default 10. (บริจาคพิเศษ เช่น การศึกษา หัก 2 เท่า → คนเงินเดือน
    /// คงไม่ใช้ — ทำเฉพาะเคสที่ HR ระบุพิเศษ.)</summary>
    public decimal DonationCapPercent { get; set; } = 10m;

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
