namespace Accounting.Models.Entities;

// ===== Project Accounting =====

/// <summary>
/// โครงการ - ติดตามต้นทุนและรายได้ตามโครงการ
/// </summary>
public class Project : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? CustomerName { get; set; }
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public Guid? ProjectManagerId { get; set; }
    public string? ProjectManagerName { get; set; }

    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? ActualEndDate { get; set; }
    public string Status { get; set; } = "Active";       // Active, OnHold, Completed, Cancelled

    // Budget
    public decimal BudgetAmount { get; set; }
    public decimal ContractAmount { get; set; }
    public decimal ActualCost { get; set; }
    public decimal ActualRevenue { get; set; }
    public decimal BilledAmount { get; set; }
    public string BillingMethod { get; set; } = "FixedPrice"; // FixedPrice, TimeAndMaterial, Milestone

    // Percentage of completion
    public decimal CompletionPercent { get; set; }
    public string RevenueRecognitionMethod { get; set; } = "PercentageOfCompletion";

    // Dimension link
    public Guid? DimensionId { get; set; }

    public ICollection<ProjectTask> Tasks { get; set; } = new List<ProjectTask>();
    public ICollection<ProjectCostEntry> CostEntries { get; set; } = new List<ProjectCostEntry>();
    public ICollection<RevenueContract> RevenueContracts { get; set; } = new List<RevenueContract>();
}

public class ProjectTask : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? ParentTaskId { get; set; }
    public int SortOrder { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public decimal EstimatedHours { get; set; }
    public decimal ActualHours { get; set; }
    public decimal EstimatedCost { get; set; }
    public decimal ActualCost { get; set; }
    public decimal CompletionPercent { get; set; }
    public string Status { get; set; } = "Open";
    public string? AssignedTo { get; set; }
}

public class ProjectCostEntry : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? ProjectTaskId { get; set; }
    public DateTime EntryDate { get; set; }
    public string CostType { get; set; } = "";           // Labor, Material, Subcontract, Overhead, Travel
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public bool IsBillable { get; set; } = true;
    public bool IsBilled { get; set; } = false;
}

// ===== Revenue Recognition (TFRS 15 / IFRS 15) =====

/// <summary>
/// สัญญารับรู้รายได้ตาม TFRS 15
/// </summary>
public class RevenueContract : TenantEntity
{
    public string ContractNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public DateTime ContractDate { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal TotalContractValue { get; set; }
    public string Status { get; set; } = "Active";        // Active, Completed, Terminated

    public ICollection<PerformanceObligation> Obligations { get; set; } = new List<PerformanceObligation>();
    public ICollection<RevenueSchedule> Schedules { get; set; } = new List<RevenueSchedule>();
}

/// <summary>
/// Performance Obligation (ภาระที่ต้องปฏิบัติ)
/// </summary>
public class PerformanceObligation : TenantEntity
{
    public Guid RevenueContractId { get; set; }
    public RevenueContract Contract { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal StandaloneSellingPrice { get; set; }
    public decimal AllocatedPrice { get; set; }           // ราคาหลัง allocate
    public string RecognitionMethod { get; set; } = "PointInTime"; // PointInTime, OverTime
    public string? MeasureOfProgress { get; set; }        // Output, Input, StraightLine
    public decimal CompletionPercent { get; set; }
    public decimal RecognizedRevenue { get; set; }
    public decimal DeferredRevenue { get; set; }
    public bool IsSatisfied { get; set; } = false;
    public DateTime? SatisfiedDate { get; set; }
}

/// <summary>
/// ตารางรับรู้รายได้
/// </summary>
public class RevenueSchedule : TenantEntity
{
    public Guid RevenueContractId { get; set; }
    public RevenueContract Contract { get; set; } = null!;
    public Guid? PerformanceObligationId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public decimal Amount { get; set; }
    public bool IsRecognized { get; set; } = false;
    public Guid? JournalEntryId { get; set; }
    public string? Notes { get; set; }
}

// ===== Warehouse Management =====

/// <summary>
/// คลังสินค้า
/// </summary>
public class Warehouse : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? ManagerName { get; set; }
    public Guid? BranchId { get; set; }
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;

    public ICollection<WarehouseStock> Stocks { get; set; } = new List<WarehouseStock>();
}

/// <summary>
/// สต็อกสินค้าแยกตามคลัง
/// </summary>
public class WarehouseStock : TenantEntity
{
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }         // จอง
    public decimal AvailableQuantity { get; set; }
    public string? Location { get; set; }                 // ตำแหน่งในคลัง (A1-01)
    public string? LotNumber { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

/// <summary>
/// ใบโอนสินค้าระหว่างคลัง
/// </summary>
public class StockTransfer : TenantEntity
{
    public string TransferNumber { get; set; } = "";
    public Guid FromWarehouseId { get; set; }
    public Warehouse FromWarehouse { get; set; } = null!;
    public Guid ToWarehouseId { get; set; }
    public Warehouse ToWarehouse { get; set; } = null!;
    public DateTime TransferDate { get; set; }
    public string Status { get; set; } = "Draft";         // Draft, InTransit, Received, Cancelled
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public ICollection<StockTransferLine> Lines { get; set; } = new List<StockTransferLine>();
}

public class StockTransferLine : TenantEntity
{
    public Guid StockTransferId { get; set; }
    public StockTransfer StockTransfer { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public string? LotNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? Notes { get; set; }
}

// ===== Loan & Financing =====

/// <summary>
/// สินเชื่อ/เงินกู้
/// </summary>
public class Loan : TenantEntity
{
    public string LoanNumber { get; set; } = "";
    public string LoanType { get; set; } = "";            // BankLoan, Mortgage, Overdraft, Leasing, EmployeeLoan
    public string Name { get; set; } = "";
    public string? Lender { get; set; }                   // ชื่อผู้ให้กู้
    public Guid? ContactId { get; set; }

    public decimal PrincipalAmount { get; set; }           // เงินต้น
    public decimal InterestRate { get; set; }              // อัตราดอกเบี้ยต่อปี
    public string InterestType { get; set; } = "Fixed";    // Fixed, Floating, FlatRate
    public int TermMonths { get; set; }
    public DateTime DisbursementDate { get; set; }
    public DateTime FirstPaymentDate { get; set; }
    public DateTime MaturityDate { get; set; }
    public string RepaymentFrequency { get; set; } = "Monthly";
    public decimal MonthlyPayment { get; set; }

    // Current state
    public decimal OutstandingPrincipal { get; set; }
    public decimal TotalInterestPaid { get; set; }
    public decimal TotalPrincipalPaid { get; set; }
    public string Status { get; set; } = "Active";        // Active, PaidOff, Defaulted, Restructured

    // GL accounts
    public Guid? LoanAccountId { get; set; }               // บัญชีเงินกู้ (Liability)
    public Guid? InterestExpenseAccountId { get; set; }     // บัญชีดอกเบี้ยจ่าย
    public Guid? BankAccountId { get; set; }

    public ICollection<LoanSchedule> Schedules { get; set; } = new List<LoanSchedule>();
    public ICollection<LoanPayment> Payments { get; set; } = new List<LoanPayment>();
}

public class LoanSchedule : TenantEntity
{
    public Guid LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public int InstallmentNumber { get; set; }
    public DateTime DueDate { get; set; }
    public decimal PaymentAmount { get; set; }
    public decimal PrincipalPortion { get; set; }
    public decimal InterestPortion { get; set; }
    public decimal RemainingBalance { get; set; }
    public bool IsPaid { get; set; } = false;
    public DateTime? PaidDate { get; set; }
}

public class LoanPayment : TenantEntity
{
    public Guid LoanId { get; set; }
    public Loan Loan { get; set; } = null!;
    public int InstallmentNumber { get; set; }
    public DateTime PaymentDate { get; set; }
    public decimal PrincipalPaid { get; set; }
    public decimal InterestPaid { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal? LateFee { get; set; }
    public string PaymentMethod { get; set; } = "BankTransfer";
    public string? Reference { get; set; }
    public Guid? JournalEntryId { get; set; }
}

// ===== Commission / Incentive =====

/// <summary>
/// แผนค่าคอมมิชชัน
/// </summary>
public class CommissionPlan : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string CalculationBasis { get; set; } = "Revenue"; // Revenue, Profit, Quantity, CollectedAmount
    public string CalculationMethod { get; set; } = "Percentage"; // Percentage, Tiered, Fixed
    public decimal? FlatRate { get; set; }                 // ถ้า method = Percentage
    public bool IsActive { get; set; } = true;

    public ICollection<CommissionTier> Tiers { get; set; } = new List<CommissionTier>();
    public ICollection<CommissionAssignment> Assignments { get; set; } = new List<CommissionAssignment>();
}

public class CommissionTier : TenantEntity
{
    public Guid CommissionPlanId { get; set; }
    public CommissionPlan Plan { get; set; } = null!;
    public decimal FromAmount { get; set; }
    public decimal? ToAmount { get; set; }                 // null = no limit
    public decimal Rate { get; set; }                     // % or fixed amount
}

public class CommissionAssignment : TenantEntity
{
    public Guid CommissionPlanId { get; set; }
    public CommissionPlan Plan { get; set; } = null!;
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid? UserId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public class CommissionCalculation : TenantEntity
{
    public Guid CommissionPlanId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? UserId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal BasisAmount { get; set; }              // ยอดขาย/กำไร ที่ใช้คำนวณ
    public decimal CommissionAmount { get; set; }
    public string Status { get; set; } = "Calculated";    // Calculated, Approved, Paid
    public string? BreakdownJson { get; set; }
    public Guid? PayrollDetailId { get; set; }
}

// ===== Tax Calendar =====

/// <summary>
/// ปฏิทินภาษี - กำหนดยื่นแบบต่างๆ
/// </summary>
public class TaxCalendarEvent : TenantEntity
{
    public string TaxFormCode { get; set; } = "";         // ภ.พ.30, ภ.ง.ด.1, ภ.ง.ด.3, ภ.ง.ด.53, ภ.ง.ด.50, ภ.ง.ด.51, สปส.1-10
    public string TaxFormName { get; set; } = "";
    public int Year { get; set; }
    public int Month { get; set; }                        // 0 = annual
    public DateTime DueDate { get; set; }
    public DateTime? EFilingDueDate { get; set; }         // กำหนดยื่นออนไลน์ (อาจได้เพิ่ม 8 วัน)
    public string Status { get; set; } = "Pending";       // Pending, InProgress, Filed, Overdue, NotApplicable
    public DateTime? FiledDate { get; set; }
    public string? FilingReference { get; set; }
    public decimal? TaxAmount { get; set; }
    public Guid? TaxReportId { get; set; }                // link to generated report
    public bool IsRecurring { get; set; } = true;
    public string? Notes { get; set; }
    public int ReminderDaysBefore { get; set; } = 7;
    public bool ReminderSent { get; set; } = false;
}

// ===== Advanced AR/AP =====

/// <summary>
/// Credit limit ลูกค้า/Supplier
/// </summary>
public class ContactCreditSetting : TenantEntity
{
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public decimal CreditLimit { get; set; }
    public int CreditTermDays { get; set; } = 30;
    public decimal CurrentBalance { get; set; }
    public decimal AvailableCredit { get; set; }
    public string? CreditRating { get; set; }             // A, B, C, D
    public bool IsOnHold { get; set; } = false;
    public string? HoldReason { get; set; }
    public DateTime? LastReviewDate { get; set; }
    public DateTime? NextReviewDate { get; set; }
}

/// <summary>
/// จดหมายทวงหนี้ (Dunning)
/// </summary>
public class DunningLetter : TenantEntity
{
    public string LetterNumber { get; set; } = "";
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public int DunningLevel { get; set; } = 1;            // 1=เตือน, 2=ทวง, 3=เข้ม, 4=กฎหมาย
    public DateTime LetterDate { get; set; }
    public decimal TotalOverdueAmount { get; set; }
    public int OldestOverdueDays { get; set; }
    public string Status { get; set; } = "Draft";         // Draft, Sent, Responded, Escalated
    public DateTime? SentAt { get; set; }
    public string? SentVia { get; set; }                  // Email, Mail, Fax
    public string? ResponseNotes { get; set; }
    public string? LetterContent { get; set; }

    public ICollection<DunningLetterLine> Lines { get; set; } = new List<DunningLetterLine>();
}

public class DunningLetterLine : TenantEntity
{
    public Guid DunningLetterId { get; set; }
    public DunningLetter DunningLetter { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public string DocumentNumber { get; set; } = "";
    public DateTime DocumentDate { get; set; }
    public DateTime DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal OverdueAmount { get; set; }
    public int OverdueDays { get; set; }
}

/// <summary>
/// การแจ้งเตือนชำระเงิน
/// </summary>
public class PaymentReminder : TenantEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public Guid ContactId { get; set; }
    public int ReminderLevel { get; set; }                // จำนวนครั้งที่ส่ง
    public DateTime ReminderDate { get; set; }
    public DateTime? SentAt { get; set; }
    public string Channel { get; set; } = "Email";        // Email, SMS, LINE
    public string Status { get; set; } = "Pending";       // Pending, Sent, Acknowledged, Paid
}
