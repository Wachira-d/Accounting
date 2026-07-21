using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Cost Center / Profit Center / Department / Branch =====

/// <summary>
/// มิติบัญชี — หน่วยงาน "ถาวร" ตามโครงสร้างองค์กร: Cost Center, Profit Center,
/// Department (+ Branch ผ่านลิงก์จาก Branch entity). รองรับ hierarchy + budget.
/// ⚠️ "โครงการ" ไม่ใช่มิติ — ใช้ Project entity (งานชั่วคราว มีลูกค้า/งบ/POC/
/// กำไรต่องาน); DimensionType.Project เป็น legacy ห้ามสร้างใหม่.
/// </summary>
public class AccountingDimension : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public DimensionType DimensionType { get; set; }
    public Guid? ParentId { get; set; }
    public AccountingDimension? Parent { get; set; }
    public int Level { get; set; } = 1;
    public string? Description { get; set; }
    public string? ManagerName { get; set; }
    public string? ManagerEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Budget tracking
    public decimal? AnnualBudget { get; set; }

    public ICollection<AccountingDimension> Children { get; set; } = new List<AccountingDimension>();
}

/// <summary>
/// สาขา/สำนักงาน
/// </summary>
public class Branch : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Address { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TaxBranchCode { get; set; }    // รหัสสาขา สรรพากร (00000=สำนักงานใหญ่)
    public bool IsHeadOffice { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public string? ManagerName { get; set; }
    public Guid? DimensionId { get; set; }         // link to AccountingDimension
    public AccountingDimension? Dimension { get; set; }
}

/// <summary>
/// Journal Entry Line extension - มิติบัญชีที่ผูกกับบรรทัดบันทึกบัญชี
/// </summary>
public class JournalLineDimension : TenantEntity
{
    public Guid JournalEntryLineId { get; set; }
    public JournalEntryLine JournalEntryLine { get; set; } = null!;
    public Guid DimensionId { get; set; }
    public AccountingDimension Dimension { get; set; } = null!;
    public decimal? AllocatedAmount { get; set; }
    public decimal? AllocatedPercent { get; set; }
}

// ===== Intercompany =====

/// <summary>
/// ธุรกรรมระหว่างบริษัท
/// </summary>
public class IntercompanyTransaction : TenantEntity
{
    public Guid SourceCompanyId { get; set; }
    public Company SourceCompany { get; set; } = null!;
    public Guid TargetCompanyId { get; set; }
    public Company TargetCompany { get; set; } = null!;
    public string TransactionNumber { get; set; } = "";
    public DateTime TransactionDate { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public IntercompanyStatus Status { get; set; } = IntercompanyStatus.Pending;

    // Journal entries in both companies
    public Guid? SourceJournalEntryId { get; set; }
    public JournalEntry? SourceJournalEntry { get; set; }
    public Guid? TargetJournalEntryId { get; set; }
    public JournalEntry? TargetJournalEntry { get; set; }

    // Matching for consolidation elimination
    public bool IsEliminated { get; set; } = false;
    public Guid? EliminationEntryId { get; set; }
    public JournalEntry? EliminationEntry { get; set; }

    public ICollection<IntercompanyTransactionLine> Lines { get; set; } = new List<IntercompanyTransactionLine>();
}

public class IntercompanyTransactionLine : TenantEntity
{
    public Guid IntercompanyTransactionId { get; set; }
    public IntercompanyTransaction IntercompanyTransaction { get; set; } = null!;
    public int LineOrder { get; set; }
    public string Description { get; set; } = "";
    public Guid SourceAccountId { get; set; }
    public ChartOfAccount SourceAccount { get; set; } = null!;
    public Guid TargetAccountId { get; set; }
    public ChartOfAccount TargetAccount { get; set; } = null!;
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
}

// ===== Financial Consolidation =====

/// <summary>
/// กลุ่มบริษัทสำหรับงบรวม
/// </summary>
public class ConsolidationGroup : BaseEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid ParentCompanyId { get; set; }
    public Company ParentCompany { get; set; } = null!;
    public string Currency { get; set; } = "THB";
    public int FiscalYearStartMonth { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public ICollection<ConsolidationMember> Members { get; set; } = new List<ConsolidationMember>();
}

public class ConsolidationMember : BaseEntity
{
    public Guid ConsolidationGroupId { get; set; }
    public ConsolidationGroup Group { get; set; } = null!;
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public decimal OwnershipPercent { get; set; } = 100;  // %การถือหุ้น
    public ConsolidationMethod Method { get; set; } = ConsolidationMethod.Full;
    public bool IsActive { get; set; } = true;
}

public class ConsolidationReport : BaseEntity
{
    public Guid ConsolidationGroupId { get; set; }
    public ConsolidationGroup Group { get; set; } = null!;
    public string ReportType { get; set; } = "BalanceSheet"; // BalanceSheet, ProfitLoss, CashFlow
    public DateTime AsOfDate { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string Status { get; set; } = "Draft";
    public string? ReportDataJson { get; set; }  // JSON of consolidated data
    public string? EliminationEntriesJson { get; set; }
    public string? MinorityInterestJson { get; set; }
    public string? GeneratedBy { get; set; }
}
