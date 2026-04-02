namespace Accounting.Models.DTOs.Integration;

// ===== Integration Configuration =====

public record IntegrationResponse(
    Guid Id, string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    string ApiKeyPrefix, bool IsActive, DateTime? LastSyncAt, int TotalSyncCount, int ErrorCount,
    int RateLimitPerMinute, DateTime CreatedAt);

public record CreateIntegrationRequest(
    string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    int RateLimitPerMinute = 60);

public record IntegrationCreatedResponse(
    Guid Id, string SystemName, string ApiKey, string ApiKeyPrefix, string? SecretKey, DateTime CreatedAt);

public record UpdateIntegrationRequest(
    string? SystemName, string? SystemType, string? SystemVersion, string? BaseUrl,
    bool? IsActive, int? RateLimitPerMinute);

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

// ===== Sync Log =====

public record SyncLogResponse(
    Guid Id, string EventType, string? ExternalId, string? ExternalRef,
    string Status, string? ErrorMessage,
    Guid? CreatedDocumentId, Guid? CreatedContactId, Guid? CreatedJournalEntryId,
    int ProcessingTimeMs, DateTime CreatedAt);

// ===== Inbound Data (received from external systems) =====

public record InboundCustomerRequest(
    string? ExternalId,
    string Name, string? NameEn,
    string? TaxId, string? Phone, string? Email,
    string? Address, string? SubDistrict, string? District, string? Province, string? PostalCode,
    string? ContactType,   // "Individual", "JuristicPerson"
    string? BranchCode,
    string? Notes);

public record InboundInvoiceRequest(
    string? ExternalId, string? ExternalRef,
    string? CustomerExternalId, string? CustomerName, string? CustomerTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    string? Description,
    List<InboundInvoiceLineRequest> Lines,
    string? PaymentMethod,
    decimal? VatRate,
    bool IncludeVat = true,
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
    string PaymentMethod,    // "Cash", "BankTransfer", "CreditCard", "PromptPay"
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

public record InboundDailySummaryRequest(
    DateTime SummaryDate,
    string? Description,
    List<DailySummaryLineRequest> Lines);

public record DailySummaryLineRequest(
    string Category,        // "ROOM_REVENUE", "F&B_REVENUE", "DEPOSIT_RECEIVED", etc.
    string? Description,
    decimal Amount,
    string? PaymentMethod);

// ===== Response for inbound operations =====

public record InboundSyncResponse(
    bool Success, string? Message,
    Guid? DocumentId, Guid? ContactId, Guid? JournalEntryId, Guid? PaymentId,
    string? DocumentNumber);

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

// ===== Revenue Reports (Hotel / Integration) =====

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
    decimal RoomRevenue, decimal FnbRevenue, decimal OtherRevenue);
