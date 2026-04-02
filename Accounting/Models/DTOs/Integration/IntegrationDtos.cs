namespace Accounting.Models.DTOs.Integration;

// ===== Integration Configuration =====

public record IntegrationResponse(
    Guid Id, string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    string ApiKeyPrefix, bool IsActive, DateTime? LastSyncAt, int TotalSyncCount, int ErrorCount,
    int RateLimitPerMinute, string? WebhookUrl, bool WebhookEnabled, DateTime CreatedAt);

public record CreateIntegrationRequest(
    string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    int RateLimitPerMinute = 60,
    string? WebhookUrl = null, bool WebhookEnabled = false);

public record IntegrationCreatedResponse(
    Guid Id, string SystemName, string ApiKey, string ApiKeyPrefix, string? SecretKey, DateTime CreatedAt);

public record UpdateIntegrationRequest(
    string? SystemName, string? SystemType, string? SystemVersion, string? BaseUrl,
    bool? IsActive, int? RateLimitPerMinute,
    string? WebhookUrl, bool? WebhookEnabled);

// ===== Account Mapping =====

public record AccountMappingResponse(
    Guid Id, Guid IntegrationId,
    string ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, string? DebitAccountCode, string? DebitAccountName,
    Guid? CreditAccountId, string? CreditAccountCode, string? CreditAccountName,
    string? JournalDescription, bool IsActive, bool AutoCreateJournal);

public record CreateAccountMappingRequest(
    string ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, Guid? CreditAccountId,
    string? JournalDescription, bool AutoCreateJournal = true);

public record UpdateAccountMappingRequest(
    string? ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, Guid? CreditAccountId,
    string? JournalDescription, bool? IsActive, bool? AutoCreateJournal);

/// <summary>Mapping templates สำหรับตั้งค่าเร็ว — แยกตามประเภทธุรกิจ</summary>
public record MappingTemplateResponse(
    string IndustryType, string IndustryName,
    List<MappingTemplateItem> Mappings);

public record MappingTemplateItem(
    string ExternalCategory, string Description,
    string? SuggestedDebitCode, string? SuggestedCreditCode);

// ===== Sync Log =====

public record SyncLogResponse(
    Guid Id, string EventType, string? ExternalId, string? ExternalRef,
    string Status, string? ErrorMessage,
    Guid? CreatedDocumentId, Guid? CreatedContactId, Guid? CreatedJournalEntryId,
    int ProcessingTimeMs, DateTime CreatedAt);

// ===== Inbound Data (received from ANY external system) =====

public record InboundCustomerRequest(
    string? ExternalId,
    string Name, string? NameEn,
    string? TaxId, string? Phone, string? Email,
    string? Address, string? SubDistrict, string? District, string? Province, string? PostalCode,
    string? ContactType,       // "Individual", "JuristicPerson", "GovernmentAgency"
    string? BranchCode,
    bool? IsCustomer,          // default true
    bool? IsSupplier,          // default false
    string? Notes);

public record InboundInvoiceRequest(
    string? ExternalId, string? ExternalRef,
    string? CustomerExternalId, string? CustomerName, string? CustomerTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    string? DocumentType,      // "Invoice", "TaxInvoice", "Receipt", "Quotation" — default TaxInvoice
    string? Description,
    List<InboundInvoiceLineRequest> Lines,
    string? PaymentMethod,
    decimal? VatRate,
    bool IncludeVat = true,
    string? Currency,          // default "THB"
    string? Notes);

public record InboundInvoiceLineRequest(
    string? ItemCode, string ItemName, decimal Quantity, decimal UnitPrice,
    decimal? DiscountAmount, string? AccountCode, string? Category,
    string? Unit, decimal? VatRate);

public record InboundPaymentRequest(
    string? ExternalId, string? ExternalRef,
    string? InvoiceExternalRef, Guid? DocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime PaymentDate, decimal Amount,
    string PaymentMethod,      // "Cash", "BankTransfer", "CreditCard", "PromptPay", "Cheque", "EWallet"
    string? BankAccountName, string? ReferenceNo, string? SlipUrl,
    string? Notes);

public record InboundCreditNoteRequest(
    string? ExternalId, string? ExternalRef,
    string? OriginalInvoiceRef, Guid? OriginalDocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime DocumentDate,
    string Reason,
    List<InboundInvoiceLineRequest> Lines,
    string? Notes);

public record InboundDebitNoteRequest(
    string? ExternalId, string? ExternalRef,
    string? OriginalInvoiceRef, Guid? OriginalDocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime DocumentDate,
    string Reason,
    List<InboundInvoiceLineRequest> Lines,
    string? Notes);

/// <summary>ค่าใช้จ่ายจากระบบภายนอก</summary>
public record InboundExpenseRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    bool IncludeVat = true,
    string? Notes);

/// <summary>สินค้า/บริการจากระบบภายนอก</summary>
public record InboundProductRequest(
    string? ExternalId,
    string Code, string Name, string? NameEn,
    string? ProductType,       // "Product", "Service", "NonStock"
    decimal? Price, decimal? CostPrice,
    string? Unit,
    string? Category,
    string? AccountCode,       // Revenue account code
    bool? IsActive,
    string? Notes);

/// <summary>บันทึกบัญชีตรงจากระบบภายนอก</summary>
public record InboundJournalRequest(
    string? ExternalId, string? ExternalRef,
    DateTime EntryDate,
    string? JournalType,       // "General", "Sales", "Purchase", "CashReceipts", "CashPayments"
    string? Description,
    List<InboundJournalLineRequest> Lines);

public record InboundJournalLineRequest(
    string AccountCode, decimal DebitAmount, decimal CreditAmount,
    string? Description);

/// <summary>Batch import — ส่งข้อมูลหลายรายการพร้อมกัน</summary>
public record InboundBatchRequest(
    List<InboundCustomerRequest>? Customers,
    List<InboundInvoiceRequest>? Invoices,
    List<InboundPaymentRequest>? Payments,
    List<InboundExpenseRequest>? Expenses,
    List<InboundProductRequest>? Products,
    List<InboundJournalRequest>? Journals);

public record InboundBatchResponse(
    int TotalProcessed, int SuccessCount, int ErrorCount,
    List<BatchResultItem> Results);

public record BatchResultItem(
    string Type, string? ExternalRef, bool Success, string? Message,
    Guid? CreatedId, string? CreatedNumber);

public record InboundDailySummaryRequest(
    DateTime SummaryDate,
    string? Description,
    List<DailySummaryLineRequest> Lines);

public record DailySummaryLineRequest(
    string Category,           // ตั้งค่าได้ตาม mapping ของแต่ละบริษัท
    string? Description,
    decimal Amount,
    string? PaymentMethod);

// ===== Response =====

public record InboundSyncResponse(
    bool Success, string? Message,
    Guid? DocumentId, Guid? ContactId, Guid? JournalEntryId, Guid? PaymentId,
    string? DocumentNumber);

// ===== Outbound Data (external systems read FROM Next Acc) =====

public record OutboundDocumentResponse(
    Guid Id, string DocumentNumber, string DocumentType, string Status,
    DateTime DocumentDate, DateTime? DueDate,
    string? ContactName, string? ContactTaxId,
    decimal SubTotal, decimal VatAmount, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue,
    string? Reference, string? Notes,
    List<OutboundDocumentLineResponse> Lines,
    DateTime CreatedAt);

public record OutboundDocumentLineResponse(
    string? ProductCode, string Description, decimal Quantity,
    string? Unit, decimal UnitPrice, decimal DiscountAmount, decimal Amount,
    decimal VatRate, decimal VatAmount);

public record OutboundContactResponse(
    Guid Id, string Name, string? TaxId, string? BranchCode,
    string ContactType, bool IsCustomer, bool IsSupplier,
    string? Address, string? Phone, string? Email,
    DateTime CreatedAt);

public record OutboundPaymentResponse(
    Guid Id, string PaymentNumber, Guid DocumentId, string? DocumentNumber,
    DateTime PaymentDate, decimal Amount, string PaymentMethod,
    string? Reference, string? Notes, DateTime CreatedAt);

public record OutboundAccountBalanceResponse(
    string AccountCode, string AccountName, string AccountType,
    decimal DebitBalance, decimal CreditBalance, decimal NetBalance);

public record OutboundQueryParams(
    DateTime? FromDate, DateTime? ToDate,
    string? Status, string? Type,
    int Page = 1, int PageSize = 50);

public record OutboundPagedResponse<T>(
    List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);

// ===== Integration Dashboard =====

public record IntegrationDashboardResponse(
    int TotalIntegrations, int ActiveIntegrations,
    int TodaySyncCount, int TodayErrorCount,
    List<IntegrationSummary> Integrations,
    List<RecentSyncItem> RecentSyncs);

public record IntegrationSummary(
    Guid Id, string SystemName, string SystemType, bool IsActive,
    DateTime? LastSyncAt, int TotalSyncCount, int ErrorCount);

public record RecentSyncItem(
    Guid Id, string SystemName, string EventType, string Status,
    string? ExternalRef, DateTime CreatedAt);

// ===== Revenue Reports (generic — ใช้ได้ทุกธุรกิจ) =====

public record RevenueByCategoryItem(
    string AccountCode, string AccountName, decimal Amount, decimal Percentage);

public record RevenueBySourceItem(
    string Source, int InvoiceCount, decimal TotalAmount, decimal Percentage);

public record DepositSummaryResponse(
    decimal TotalDepositsReceived, decimal TotalDepositsApplied, decimal OutstandingDeposits,
    List<DepositDetailItem> Details);

public record DepositDetailItem(
    Guid DocumentId, string DocumentNumber, string? CustomerName,
    decimal DepositAmount, DateTime DocumentDate, string Status);

public record DailyRevenueItem(
    DateTime Date, decimal TotalRevenue, int InvoiceCount,
    List<DailyRevenueCategoryItem> Categories);

public record DailyRevenueCategoryItem(
    string AccountCodePrefix, string CategoryName, decimal Amount);
