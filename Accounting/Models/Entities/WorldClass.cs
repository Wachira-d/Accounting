namespace Accounting.Models.Entities;

// ===== Financial Planning & Analysis (FP&A) =====

/// <summary>
/// Scenario / What-if Analysis
/// </summary>
public class FinancialScenario : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ScenarioType { get; set; } = "WhatIf";   // WhatIf, BestCase, WorstCase, Budget, Forecast
    public int FiscalYear { get; set; }
    public string BaselineType { get; set; } = "Actual";    // Actual, Budget, Scenario
    public Guid? BaselineScenarioId { get; set; }
    public string Status { get; set; } = "Draft";          // Draft, Active, Archived

    public ICollection<ScenarioAssumption> Assumptions { get; set; } = new List<ScenarioAssumption>();
    public ICollection<ScenarioResult> Results { get; set; } = new List<ScenarioResult>();
}

public class ScenarioAssumption : TenantEntity
{
    public Guid FinancialScenarioId { get; set; }
    public FinancialScenario Scenario { get; set; } = null!;
    public string Category { get; set; } = "";            // Revenue, COGS, OpEx, Tax, Headcount, Pricing
    public string Description { get; set; } = "";
    public string AdjustmentType { get; set; } = "Percentage"; // Percentage, Absolute, Formula
    public decimal AdjustmentValue { get; set; }
    public Guid? AccountId { get; set; }                  // specific account
    public Guid? DimensionId { get; set; }                // specific cost center
    public int? ApplyToMonth { get; set; }                // null = all months
}

public class ScenarioResult : TenantEntity
{
    public Guid FinancialScenarioId { get; set; }
    public FinancialScenario Scenario { get; set; } = null!;
    public int Month { get; set; }
    public Guid AccountId { get; set; }
    public decimal BaselineAmount { get; set; }
    public decimal ScenarioAmount { get; set; }
    public decimal Variance { get; set; }
    public decimal VariancePercent { get; set; }
}

/// <summary>
/// Financial KPI Tracking
/// </summary>
public class FinancialKpi : TenantEntity
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";                // ROE, ROA, CurrentRatio, QuickRatio, DebtToEquity, etc.
    public string Category { get; set; } = "";            // Profitability, Liquidity, Efficiency, Leverage
    public string Formula { get; set; } = "";
    public decimal? TargetValue { get; set; }
    public decimal? WarningThreshold { get; set; }
    public decimal? CriticalThreshold { get; set; }
    public bool IsActive { get; set; } = true;
}

public class KpiSnapshot : TenantEntity
{
    public Guid FinancialKpiId { get; set; }
    public FinancialKpi Kpi { get; set; } = null!;
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Value { get; set; }
    public string? Status { get; set; }                   // Good, Warning, Critical
    public string? CalculationDetailJson { get; set; }
}

// ===== Open Banking =====

/// <summary>
/// การเชื่อมต่อธนาคาร
/// </summary>
public class BankConnection : TenantEntity
{
    public string BankCode { get; set; } = "";            // SCB, KBANK, BBL, BAY, KTB, TMB
    public string BankName { get; set; } = "";
    public string ConnectionType { get; set; } = "API";    // API, OFX, QIF, CSV, Scraping
    public string? ApiEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? EncryptedCredentials { get; set; }      // Encrypted
    public string? AccessToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastError { get; set; }
    public bool AutoSync { get; set; } = false;
    public int SyncIntervalMinutes { get; set; } = 60;
    public Guid? LinkedBankAccountId { get; set; }
}

/// <summary>
/// Import history สำหรับ bank feed
/// </summary>
public class BankFeedImport : TenantEntity
{
    public Guid? BankConnectionId { get; set; }       // null for manual file imports
    public BankConnection? Connection { get; set; }
    public DateTime ImportDate { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalTransactions { get; set; }
    public int NewTransactions { get; set; }
    public int DuplicateSkipped { get; set; }
    public int AutoMatched { get; set; }
    public string Status { get; set; } = "Completed";
    public string? ErrorMessage { get; set; }
}

// ===== Compliance Automation =====

/// <summary>
/// การยื่นแบบอิเล็กทรอนิกส์ (DBD e-Filing, สรรพากร e-Filing)
/// </summary>
public class ComplianceFiling : TenantEntity
{
    public string FilingType { get; set; } = "";          // DBD_FinancialStatement, RD_VAT, RD_WHT, RD_CIT, SSO_Contribution, BOI_Report
    public string FormCode { get; set; } = "";            // ภ.พ.30, ภ.ง.ด.50, สบช.3
    public int Year { get; set; }
    public int? Month { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? FiledDate { get; set; }
    public string Status { get; set; } = "NotStarted";    // NotStarted, InProgress, Validated, Filed, Accepted, Rejected
    public string? SubmissionReference { get; set; }
    public string? FileDataJson { get; set; }             // Prepared data
    public string? ValidationErrors { get; set; }
    public string? ConfirmationNumber { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? PenaltyAmount { get; set; }
    public string? Notes { get; set; }
    public string? FiledBy { get; set; }
}

// ===== Time & Billing =====

/// <summary>
/// Time Entry สำหรับ Professional Services
/// </summary>
public class TimeEntry : TenantEntity
{
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? ProjectTaskId { get; set; }
    public Guid? ContactId { get; set; }                  // Client
    public DateTime EntryDate { get; set; }
    public decimal Hours { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "Billable";    // Billable, NonBillable, Internal
    public decimal? BillingRate { get; set; }
    public decimal? BillableAmount { get; set; }
    public bool IsBilled { get; set; } = false;
    public Guid? InvoiceDocumentId { get; set; }
    public string Status { get; set; } = "Draft";         // Draft, Submitted, Approved, Billed
    public string? ApprovedBy { get; set; }
}

/// <summary>
/// Billing Rate Card
/// </summary>
public class BillingRate : TenantEntity
{
    public string Name { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string? Role { get; set; }                     // Partner, Senior, Junior, etc.
    public Guid? ContactId { get; set; }                  // Client-specific rate
    public Guid? ProjectId { get; set; }                  // Project-specific rate
    public decimal HourlyRate { get; set; }
    public decimal? DailyRate { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
}

// ===== Webhook System =====

/// <summary>
/// Webhook Registration
/// </summary>
public class WebhookRegistration : TenantEntity
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Secret { get; set; }                   // HMAC signing secret
    public string EventTypes { get; set; } = "";          // comma-separated: document.created,payment.received
    public bool IsActive { get; set; } = true;
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
    public string? HeadersJson { get; set; }              // custom headers
    public int FailureCount { get; set; } = 0;
    public DateTime? LastTriggeredAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Webhook Delivery Log
/// </summary>
public class WebhookDelivery : TenantEntity
{
    public Guid WebhookRegistrationId { get; set; }
    public WebhookRegistration Registration { get; set; } = null!;
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public int HttpStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public bool IsSuccess { get; set; }
    public decimal DurationMs { get; set; }
    public DateTime DeliveredAt { get; set; }
    public string? ErrorMessage { get; set; }
}

// ===== Mobile API Optimization =====

/// <summary>
/// User device registration for push notifications
/// </summary>
public class UserDevice : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string DeviceToken { get; set; } = "";
    public string Platform { get; set; } = "";            // iOS, Android, Web
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastActiveAt { get; set; }
}

/// <summary>
/// Offline sync queue for mobile
/// </summary>
public class SyncQueue : TenantEntity
{
    public Guid? UserId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string OperationType { get; set; } = "";       // Create, Update, Delete
    public string PayloadJson { get; set; } = "";
    public DateTime QueuedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; } = false;
    public string? ConflictResolution { get; set; }       // ServerWins, ClientWins, Manual
    public string? ErrorMessage { get; set; }
}
