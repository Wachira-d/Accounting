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

    // Number of times this scan has been retried by the user/admin.
    // Increments on retry; bounded by SiteSettings.OcrMaxRetriesPerScan.
    // Retries do NOT consume additional quota (quota was charged on initial scan).
    public int RetryCount { get; set; } = 0;

    // Content-based fingerprint for cross-format duplicate detection.
    // Computed AFTER extraction as SHA256 of:
    //   "{VendorTaxId}|{DocumentNumber}|{DocumentDate:yyyy-MM-dd}|{TotalAmount:0.00}"
    // Catches the case where same invoice is uploaded as JPG one time and PDF another —
    // file hash differs but the content fingerprint matches.
    public string? ContentFingerprint { get; set; }

    // Extracted data
    public string? ExtractedVendorName { get; set; }
    public string? ExtractedVendorTaxId { get; set; }
    public string? ExtractedDocumentNumber { get; set; }
    public DateTime? ExtractedDate { get; set; }
    public decimal? ExtractedSubTotal { get; set; }
    public decimal? ExtractedVatAmount { get; set; }
    public decimal? ExtractedTotalAmount { get; set; }
    public string? ExtractedItemsJson { get; set; }       // JSON of line items

    // ─── Document role inference ──────────────────────────────────────
    // Thai-accounting workflow separates THREE distinct concepts:
    //   • ScannedDocumentType — the physical paper we OCR'd (e.g. "Receipt")
    //   • OurRole              — "Buyer" or "Seller" depending on whose tax-id
    //                            matches the company doing the scanning
    //   • TargetDocumentType   — what to CREATE in our books (e.g.
    //                            "PaymentVoucher" when we scanned a supplier
    //                            receipt — we paid them, so we book a payment
    //                            voucher, NOT a "Receipt" document)
    //
    // The legacy DocumentType field (above) is kept for backwards-compat and
    // mirrors ScannedDocumentType for now; downstream AutoCreate logic and
    // VendorIntel learning consume TargetDocumentType instead.
    public string? ScannedDocumentType { get; set; }
    public string? OurRole { get; set; }                  // "Buyer" | "Seller"
    public string? TargetDocumentType { get; set; }       // enum-string from DocumentType

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

    // ─── Which OCR engine actually produced the text ───
    // Records which tier of the cascade returned the result that was used:
    //   "AzureDI"           — Azure Document Intelligence (cloud, prebuilt models)
    //   "LocalPython"        — PaddleOCR + EasyOCR microservice
    //   "EmbeddedTesseract"  — in-process Tesseract via NuGet (always-on fallback)
    //   "Cached"             — duplicate-detection short-circuit; copied an earlier scan
    // Surfaced in the debug panel so users can see at a glance which engine
    // handled their document — useful when troubleshooting accuracy regressions
    // ("the embedded fallback ran because the Python service was down").
    public string? OcrEngine { get; set; }

    // ─── Potential Fixed Asset detection (Phase 4) ───
    // True when at least one line item crossed the asset detection
    // threshold (unit price + keyword). The UI uses this flag to surface
    // a "Needs Review — Potential Asset" alert in the review modal so
    // the user can register the asset(s) before the scan auto-creates
    // an expense document.
    public bool HasPotentialFixedAsset { get; set; }

    // JSON-serialized list of FixedAssetDetector.LineDecision rows for
    // each line flagged as a potential asset. Schema:
    //   [{ "lineIndex":0, "suggestedCategory":"คอมพิวเตอร์...",
    //      "suggestedUsefulLifeMonths":36, "confidenceScore":0.85,
    //      "description":"...", "unitPrice":29900, "amount":29900,
    //      "reasons":["..."] }]
    public string? PotentialAssetLinesJson { get; set; }

    // ─── Handwriting flag (from Azure DI styleFont feature) ───
    // True when at least one field on the document appears to be
    // hand-written on a printed form. Triggers a "✋ ตรวจสอบยอดเงิน
    // ด้วยตา" alert in the UI, suppresses auto-create, and docks the
    // scan quality grade so the review queue surfaces it first.
    public bool HasHandwriting { get; set; }
    public decimal? HandwritingConfidence { get; set; }
}

/// <summary>
/// Learned extraction patterns from user corrections.
/// The zone analyzer uses these to improve accuracy over time.
/// </summary>
public class OcrLearnedPattern : TenantEntity
{
    public string? VendorTaxId { get; set; }
    public string FieldName { get; set; } = "";          // SellerName, SellerTaxId, DocumentNumber, etc.
    public string ContextKeyword { get; set; } = "";      // The keyword found near the field value
    public string? ExtractionRegex { get; set; }           // Regex to extract the value near the keyword
    public int SearchRadius { get; set; } = 300;           // How far from keyword to search
    public int TimesConfirmed { get; set; } = 1;           // Increases each time this pattern is confirmed
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    // Negative learning: a value that was previously extracted but corrected away from
    // — should be down-weighted when seen again for this vendor.
    public bool IsNegativeExample { get; set; } = false;
    public string? NegativeValue { get; set; }              // The wrong value that was rejected
    public int FailureCount { get; set; } = 0;              // How many times this pattern was wrong
}

/// <summary>
/// Learned mapping: vendor + line-item description keyword → chart-of-account code.
/// Built incrementally from user corrections and from approved auto-created documents.
/// On a new scan, OcrService queries this table to suggest expense categories that
/// match the company's actual booking habits — far more accurate than generic rules.
/// </summary>
public class OcrCategoryMapping : TenantEntity
{
    /// <summary>Vendor TaxId (preferred) or normalized vendor name when TaxId missing.</summary>
    public string VendorKey { get; set; } = "";
    /// <summary>Lower-cased substring match key from line description / expense category text.</summary>
    public string DescriptionKeyword { get; set; } = "";
    /// <summary>Suggested debit account code from CoA (e.g. "5402" for fuel).</summary>
    public string AccountCode { get; set; } = "";
    public string? AccountName { get; set; }
    /// <summary>How many times user confirmed/booked with this account for this vendor+keyword.</summary>
    public int TimesUsed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Optional: which user originally trained this mapping (for audit).</summary>
    public Guid? TrainedByUserId { get; set; }
}

/// <summary>
/// Per-vendor aggregated intelligence cache. Built from approved Documents history
/// and refreshed on each new document approval. Drives auto-suggestion of:
///   • DocumentType (most common type used with this vendor)
///   • Debit account (most common booking)
///   • WHT habits (does this vendor usually have WHT? what rate?)
///   • Amount sanity range (flag scans with anomalous totals)
///   • Payment terms
/// One row per (CompanyId, VendorKey). Denormalized for sub-millisecond lookup
/// during ScanAsync — full per-document scans on every OCR would be too slow.
/// </summary>
public class OcrVendorIntelligence : TenantEntity
{
    public string VendorKey { get; set; } = "";              // tax:1234567890123 or name:lower
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }

    // ─── DocumentType prediction ───
    public string? MostCommonDocumentType { get; set; }      // e.g. "PurchaseInvoice"
    public int MostCommonDocumentTypeCount { get; set; }
    public int TotalDocuments { get; set; }
    public string? DocumentTypeBreakdownJson { get; set; }   // {"PurchaseInvoice":12,"Expense":3}

    // ─── Debit account prediction ───
    public string? MostCommonDebitAccountCode { get; set; }
    public string? MostCommonDebitAccountName { get; set; }
    public int MostCommonDebitAccountCount { get; set; }
    public string? DebitAccountBreakdownJson { get; set; }   // {"5300":8,"5402":4}

    // ─── WHT habits ───
    public bool TypicallyHasWht { get; set; }                // >50% of past docs had WHT
    public decimal? TypicalWhtRate { get; set; }             // mode of past WHT rates
    public int WhtUsageCount { get; set; }

    // ─── Amount sanity range ───
    public decimal? AvgTotalAmount { get; set; }
    public decimal? MinTotalAmount { get; set; }
    public decimal? MaxTotalAmount { get; set; }
    public decimal? MedianTotalAmount { get; set; }
    // Running log-amount stats — feeds AmountAnomalyDetector.CheckZScore
    // for robust anomaly detection that handles heavy-tailed amount
    // distributions far better than raw min/max.
    public decimal? LogAmountMean { get; set; }
    public decimal? LogAmountVariance { get; set; }

    // ─── Payment terms ───
    public int? TypicalPaymentTermsDays { get; set; }

    // ─── Learned patterns for OCR boosting ───
    // TypicalDocNumberPrefix: when this vendor's document numbers always
    // start with the same prefix (e.g. HomePro "612XXX", PTT "TAX-"), the
    // OCR can use that as a high-confidence anchor to disambiguate
    // candidate numbers. Empty when no consistent pattern detected.
    public string? TypicalDocNumberPrefix { get; set; }

    // TopLineKeywordsJson: frequency map of words seen in line-item
    // descriptions from prior approved docs, e.g. {"น้ำมัน":12, "Diesel":5}.
    // Lets the category resolver boost confidence on a new scan whose
    // descriptions match the vendor's historical pattern, even when the
    // global rule library would only score weakly.
    public string? TopLineKeywordsJson { get; set; }

    // Audit
    public DateTime LastTrainedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDocumentDate { get; set; }
}

/// <summary>
/// System-wide vendor → expense-account mappings. Trained by SystemAdmin from
/// the /admin/ocr-config page and shared across every tenant. Acts as a
/// fallback knowledge base when the tenant's own OcrCategoryMappings has no
/// match for a vendor/keyword combination — so newly-onboarded companies
/// get useful OCR predictions on day one.
///
/// Mirrors OcrCategoryMapping but without CompanyId. Tenant-specific
/// mappings always win at predict time; this is consulted only when no
/// tenant row matches.
/// </summary>
public class SystemOcrCategoryMapping : BaseEntity
{
    public string VendorKey { get; set; } = "";
    public string DescriptionKeyword { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string? AccountName { get; set; }
    public int TimesUsed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public Guid? TrainedByUserId { get; set; }

    // Industry breakdown of contributing tenants: e.g.
    //   {"Manufacturing": 5, "Trading": 3, "Service": 2}
    // Used at predict time to weight this row higher when the consuming
    // tenant's IndustryType matches the dominant industry of contributors.
    // Null / empty = universal (seeded data or industry-mixed sources).
    public string? IndustryBreakdownJson { get; set; }
}

/// <summary>
/// System-wide per-vendor intelligence. Trained by SystemAdmin from the
/// /admin/ocr-config page; shared across every tenant. Acts as a fallback
/// when the tenant has no OcrVendorIntelligence row for a given vendor yet.
///
/// Mirrors OcrVendorIntelligence but without CompanyId; one row per VendorKey
/// for the entire system.
/// </summary>
public class SystemOcrVendorIntelligence : BaseEntity
{
    public string VendorKey { get; set; } = "";
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }

    public string? MostCommonDocumentType { get; set; }
    public int MostCommonDocumentTypeCount { get; set; }
    public int TotalDocuments { get; set; }
    public string? DocumentTypeBreakdownJson { get; set; }

    public string? MostCommonDebitAccountCode { get; set; }
    public string? MostCommonDebitAccountName { get; set; }
    public int MostCommonDebitAccountCount { get; set; }
    public string? DebitAccountBreakdownJson { get; set; }

    public bool TypicallyHasWht { get; set; }
    public decimal? TypicalWhtRate { get; set; }
    public int WhtUsageCount { get; set; }

    public decimal? AvgTotalAmount { get; set; }
    public decimal? MinTotalAmount { get; set; }
    public decimal? MaxTotalAmount { get; set; }
    public decimal? MedianTotalAmount { get; set; }

    public int? TypicalPaymentTermsDays { get; set; }

    public DateTime LastTrainedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDocumentDate { get; set; }

    // Industry breakdown of contributing tenants — same semantics as
    // SystemOcrCategoryMapping.IndustryBreakdownJson. Lets the query-
    // time consumer weight this row toward same-industry similarity.
    public string? IndustryBreakdownJson { get; set; }
}

/// <summary>
/// Discovered association rule from system-wide basket analysis. Each row
/// represents "when antecedent tokens are present in a document, the
/// consequent account is likely the right debit". Refreshed by the
/// AssociationRuleMiner background job. No CompanyId — these are
/// system-wide patterns shared across every tenant.
/// </summary>
public class SystemOcrAssociationRule : BaseEntity
{
    /// <summary>JSON array of antecedent tokens, e.g. ["brand:ptt","kw:น้ำมัน"].</summary>
    public string AntecedentJson { get; set; } = "[]";

    /// <summary>The consequent token: typically "acct:5402" (a debit account
    /// code) but the format is intentionally generic so we can mine other
    /// consequents (doc type, WHT rate) in the future.</summary>
    public string Consequent { get; set; } = "";

    /// <summary>Fraction of all transactions that contain the antecedent AND
    /// consequent — measures how OFTEN the pattern occurs.</summary>
    public decimal Support { get; set; }

    /// <summary>P(consequent | antecedent) — measures how RELIABLE the rule
    /// is. ≥ 0.5 typically required for usable rules.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Confidence / P(consequent). Lift > 1 means the antecedent
    /// actually moves the needle (vs. choosing the consequent at random).
    /// Used as the primary ranking metric.</summary>
    public decimal Lift { get; set; }

    /// <summary>Raw count of training transactions supporting this rule —
    /// used to weight lift (a lift of 10 from 3 docs is weaker than 5 from 300).</summary>
    public int TransactionCount { get; set; }

    public DateTime MinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// การซื้อเครดิต OCR เพิ่มเติม (add-on pages)
/// </summary>
public class OcrCreditPurchase : TenantEntity
{
    public Guid SubscriptionId { get; set; }
    public int PagesPurchased { get; set; }
    public int PagesRemaining { get; set; }
    public decimal AmountPaid { get; set; }
    public string Currency { get; set; } = "THB";
    public string Status { get; set; } = "Pending";
    public string? PaymentReference { get; set; }
    public string? SlipFileName { get; set; }
    public string? SlipStoragePath { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime? ExpiresAt { get; set; }
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

/// <summary>
/// Per-vendor "known good" field values, populated whenever Azure DI
/// extracts a high-confidence value for a vendor we recognize. The
/// local-OCR cascade (PaddleOCR / Tesseract) then fuzzy-matches its own
/// noisy output against these values and substitutes the canonical
/// version when similarity is high enough. Drastically reduces the
/// "หจก . แอมแฮปปี๊เนส" (with stray spaces) → "หจก. แอมแฮปปี๊เนส"
/// kind of noise that plagues Tesseract-only scans.
///
/// Keyed by (CompanyId, VendorTaxId, FieldName, Value). Repeated
/// confirmations bump ConfirmedCount — values that Azure has seen 5+
/// times beat one-off noise. Cleaned by background job after 12 months
/// of inactivity.
/// </summary>
public class VendorKnownGoodValue : TenantEntity
{
    /// <summary>Vendor's TaxId. NULL allowed for vendor-name-only matches.</summary>
    public string? VendorTaxId { get; set; }
    /// <summary>SellerName / SellerTaxId / DocumentNumber / BuyerName / Address / Phone / Email / BranchCode.</summary>
    public string FieldName { get; set; } = "";
    /// <summary>The canonical value extracted by Azure DI (or user correction).</summary>
    public string Value { get; set; } = "";
    /// <summary>Confidence score from Azure DI (0–1). Used to weight when multiple variants exist.</summary>
    public decimal Confidence { get; set; }
    /// <summary>How many distinct scans confirmed this exact value.</summary>
    public int ConfirmedCount { get; set; } = 1;
    /// <summary>"AzureDI" | "UserCorrection" — source lets us trust user corrections over Azure on conflict.</summary>
    public string Source { get; set; } = "AzureDI";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
