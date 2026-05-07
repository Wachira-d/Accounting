namespace Accounting.Models.Entities;

// ===== AI-Powered Features =====

/// <summary>
/// กฎ Auto-categorization สำหรับ AI
/// </summary>
public class AutoCategorizationRule : TenantEntity
{
    public string RuleName { get; set; } = "";
    public string MatchType { get; set; } = "Contains";   // Contains, StartsWith, Regex, AI
    public string MatchField { get; set; } = "Description"; // Description, Reference, Payee, Amount
    public string? MatchPattern { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public Guid? TargetAccountId { get; set; }            // auto-assign GL account
    public Guid? TargetDimensionId { get; set; }          // auto-assign cost center
    public string? TargetCategory { get; set; }
    public int Priority { get; set; } = 0;
    public int TimesApplied { get; set; } = 0;
    public decimal ConfidenceThreshold { get; set; } = 0.8m;
    public bool IsActive { get; set; } = true;
    public bool IsAiGenerated { get; set; } = false;      // AI สร้างจาก pattern
}

/// <summary>
/// ผลการจัดหมวดหมู่อัตโนมัติ
/// </summary>
public class CategorizationResult : TenantEntity
{
    public string EntityType { get; set; } = "";          // BankTransaction, ExpenseClaim, Document
    public Guid EntityId { get; set; }
    public Guid? SuggestedAccountId { get; set; }
    public Guid? SuggestedDimensionId { get; set; }
    public string? SuggestedCategory { get; set; }
    public decimal Confidence { get; set; }
    public string? ReasoningJson { get; set; }            // AI reasoning
    public bool IsAccepted { get; set; } = false;
    public bool IsRejected { get; set; } = false;
    public Guid? AcceptedByUserId { get; set; }
    public Guid? AppliedRuleId { get; set; }
}

/// <summary>
/// Anomaly Detection Log
/// </summary>
public class AnomalyDetection : TenantEntity
{
    public string AnomalyType { get; set; } = "";         // UnusualAmount, DuplicateEntry, OutOfPattern, MissingEntry, BalanceDiscrepancy
    public string Severity { get; set; } = "Medium";       // Low, Medium, High, Critical
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Description { get; set; } = "";
    public string? DetailJson { get; set; }
    public decimal? ExpectedValue { get; set; }
    public decimal? ActualValue { get; set; }
    public decimal? DeviationPercent { get; set; }
    public string Status { get; set; } = "Open";           // Open, Acknowledged, Resolved, FalsePositive
    public string? ResolvedBy { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cash Flow Forecast
/// </summary>
public class CashFlowForecast : TenantEntity
{
    public string Name { get; set; } = "";
    public DateTime ForecastDate { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string ForecastMethod { get; set; } = "Historical"; // Historical, AI, Manual, Hybrid
    public decimal OpeningBalance { get; set; }
    public decimal ProjectedInflows { get; set; }
    public decimal ProjectedOutflows { get; set; }
    public decimal ProjectedClosingBalance { get; set; }
    public decimal? ActualClosingBalance { get; set; }
    public decimal AccuracyPercent { get; set; }
    public string? DetailJson { get; set; }               // weekly/daily breakdown

    public ICollection<CashFlowForecastLine> Lines { get; set; } = new List<CashFlowForecastLine>();
}

public class CashFlowForecastLine : TenantEntity
{
    public Guid CashFlowForecastId { get; set; }
    public CashFlowForecast Forecast { get; set; } = null!;
    public DateTime PeriodDate { get; set; }
    public string Category { get; set; } = "";            // Sales, Purchases, Payroll, Tax, Loan, Other
    public string FlowType { get; set; } = "Inflow";      // Inflow, Outflow
    public decimal ProjectedAmount { get; set; }
    public decimal? ActualAmount { get; set; }
    public decimal Confidence { get; set; }
    public string? Source { get; set; }                   // InvoiceDue, RecurringPayment, Payroll, Historical
}

// ===== Document OCR =====

/// <summary>
/// ผลการสแกน OCR เอกสาร
/// </summary>
public class OcrScanResult : TenantEntity
{
    public Guid? FileAttachmentId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string ScanStatus { get; set; } = "Pending";   // Pending, Processing, Completed, Failed
    public string? DocumentType { get; set; }              // Invoice, Receipt, TaxInvoice, WHT
    public decimal Confidence { get; set; }

    // Extracted data
    public string? ExtractedVendorName { get; set; }
    public string? ExtractedVendorTaxId { get; set; }
    public string? ExtractedDocumentNumber { get; set; }
    public DateTime? ExtractedDate { get; set; }
    public decimal? ExtractedSubTotal { get; set; }
    public decimal? ExtractedVatAmount { get; set; }
    public decimal? ExtractedTotalAmount { get; set; }
    public string? ExtractedItemsJson { get; set; }       // JSON of line items

    // Matching
    public Guid? MatchedContactId { get; set; }
    public Guid? CreatedDocumentId { get; set; }           // Document created from OCR
    public string? RawTextContent { get; set; }
    public string? ProcessingNotes { get; set; }
    public DateTime? ProcessedAt { get; set; }

    // GL & expense suggestions
    public string? ExpenseCategory { get; set; }
    public string? SuggestedAccountsJson { get; set; }
    public bool HasWht { get; set; }
    public decimal? WhtRate { get; set; }
    public int? PaymentTermsDays { get; set; }

    // Duplicate detection
    public string? FileHash { get; set; }
    public bool IsDuplicate { get; set; }
    public Guid? DuplicateOfScanId { get; set; }
}

// ===== Custom Report Builder =====

/// <summary>
/// รายงานที่ผู้ใช้สร้างเอง
/// </summary>
public class CustomReport : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ReportType { get; set; } = "Table";      // Table, Chart, Pivot, Dashboard
    public string Category { get; set; } = "Financial";    // Financial, Tax, Operational, Custom

    // Data source
    public string DataSourceType { get; set; } = "JournalEntry"; // JournalEntry, Document, BankTransaction, Product, Contact, Payroll
    public string? FilterJson { get; set; }                // Dynamic filters
    public string? ColumnsJson { get; set; }               // Column definitions
    public string? SortingJson { get; set; }
    public string? GroupingJson { get; set; }
    public string? AggregationJson { get; set; }           // Sum, Avg, Count, Min, Max

    // Display
    public string? ChartType { get; set; }                 // Bar, Line, Pie, Scatter, Area
    public string? ChartConfigJson { get; set; }
    public bool ShowTotals { get; set; } = true;
    public bool IsPublic { get; set; } = false;            // Shared with all users
    public string? CreatedByUserId { get; set; }

    // Schedule
    public bool IsScheduled { get; set; } = false;
    public string? ScheduleFrequency { get; set; }         // Daily, Weekly, Monthly
    public string? SendToEmails { get; set; }
    public string? ExportFormat { get; set; }              // PDF, Excel, CSV
}

// ===== Customer/Supplier Portal =====

/// <summary>
/// Token เข้าถึง Portal ของลูกค้า/Supplier
/// </summary>
public class PortalAccess : TenantEntity
{
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }

    // Permissions
    public bool CanViewInvoices { get; set; } = true;
    public bool CanViewStatements { get; set; } = true;
    public bool CanDownloadPdf { get; set; } = true;
    public bool CanMakePayment { get; set; } = false;      // Online payment
    public bool CanViewOrders { get; set; } = false;
    public bool CanCreateOrders { get; set; } = false;
    public bool CanViewDeliveries { get; set; } = false;
}

/// <summary>
/// Activity ใน Portal
/// </summary>
public class PortalActivity : TenantEntity
{
    public Guid PortalAccessId { get; set; }
    public PortalAccess PortalAccess { get; set; } = null!;
    public string ActivityType { get; set; } = "";        // Login, ViewInvoice, DownloadPdf, MakePayment
    public Guid? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ActivityAt { get; set; } = DateTime.UtcNow;
}
