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
    public Guid? DimensionId { get; set; }               // Cost Center / Department

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

    // Linked account
    public Guid? SalaryExpenseAccountId { get; set; }

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
    public int SortOrder { get; set; }
}
