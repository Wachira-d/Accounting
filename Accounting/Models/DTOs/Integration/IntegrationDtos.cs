using System.ComponentModel.DataAnnotations;

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
    string? Notes,
    string? Moo = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? StreetName = null);

public record InboundInvoiceRequest(
    string? ExternalId, string? ExternalRef,
    string? CustomerExternalId, string? CustomerName, string? CustomerTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    string? DocumentType,      // "Invoice", "TaxInvoice", "Receipt", "Quotation" — default TaxInvoice
    string? Description,
    List<InboundInvoiceLineRequest> Lines,
    string? PaymentMethod,
    decimal? VatRate,
    string? Currency,          // default "THB"
    string? Notes,
    bool IncludeVat = true,
    /// <summary>Optional attachments embedded in the same request as base64.
    /// Useful for POS systems that submit JSON-only and want to bundle the
    /// receipt image with the invoice. Each attachment is decoded and saved
    /// after the document is created — its FileAttachment row will be linked
    /// to the new document automatically.</summary>
    List<InboundAttachment>? Attachments = null,
    // ── Preparer identity from the source system (same as InboundExpenseRequest) ──
    // Name + signature image ("data:image/...;base64,..." or raw base64) of the
    // real preparer, stamped into the "ผู้จัดทำ" slot of the created document.
    // Both null → falls back to the company Owner as before.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null);

/// <summary>
/// Base64-encoded file attachment for external integrations. Server enforces:
///   - Maximum 25MB per file (post-decode)
///   - Magic-byte validation against ContentType (rejects mislabeled files)
///   - Allowed types: PDF, JPEG, PNG, BMP, TIFF, HEIF, WebP, Excel, Word
/// External systems can also use the multipart endpoint when sending many
/// large files — base64 has 33% overhead so isn't ideal beyond ~5MB.
/// </summary>
public record InboundAttachment(
    string FileName,           // Original filename to preserve in audit trail
    string ContentType,        // MIME type — verified against magic bytes
    string Base64Content);     // Standard RFC 4648 base64 (with or without padding)

public record InboundInvoiceLineRequest(
    string? ItemCode, string ItemName, decimal Quantity, decimal UnitPrice,
    decimal? DiscountAmount, string? AccountCode, string? Category,
    string? Unit, decimal? VatRate,
    // Withholding-tax rate (%) for this line. Used on purchase-side docs
    // (Expense) so integration sync can auto-issue the WHT certificate.
    decimal? WithholdingTaxRate = null,
    // Explicit VAT amount (บาท) for this line. When the external system has
    // ALREADY computed VAT — typically because the line mixes ภาษี 7% goods
    // with ของยกเว้น (exempt) where a single VatRate can't express the true
    // VAT — NextAcc honors THIS value instead of recomputing net × rate.
    // Null = recompute as before (backward compatible). Prevents the
    // "ยอดจ่าย ≠ ยอด NextAcc" mismatch that creates a phantom ค้างชำระ.
    decimal? VatAmount = null);

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
    string? Notes,
    List<InboundAttachment>? Attachments = null);

public record InboundDebitNoteRequest(
    string? ExternalId, string? ExternalRef,
    string? OriginalInvoiceRef, Guid? OriginalDocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime DocumentDate,
    string Reason,
    List<InboundInvoiceLineRequest> Lines,
    string? Notes,
    List<InboundAttachment>? Attachments = null);

/// <summary>ค่าใช้จ่ายจากระบบภายนอก</summary>
public record InboundExpenseRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null,
    // ── Preparer identity from the source system ──
    // The partner names the real person who prepared the voucher and may
    // ship their signature image inline (a "data:image/png;base64,..." URI
    // or raw base64). When present, this person + signature is stamped into
    // the "ผู้จัดทำ" slot of the document — even though they are not a
    // NextAcc User. Both null → falls back to the company Owner as before.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null);

/// <summary>ใบสำคัญจ่าย (การจ่ายเงินจริง) จากระบบภายนอก — สำหรับ voucher
/// ที่จ่ายเงินไปแล้วในระบบต้นทาง: สร้างเอกสาร PV เดียวจบ (Dr ค่าใช้จ่าย /
/// Cr เงินสด-ธนาคาร) ไม่ต้อง map เป็น expense + payment สองยก
/// ซึ่งสร้างหนี้หลอกที่ถูกตัดทันที</summary>
public record InboundPaymentVoucherRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate,
    DateTime? PaymentDate,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null,
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null);

/// <summary>ใบรับรองแทนใบเสร็จจากระบบภายนอก</summary>
public record InboundCertificateInLieuRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate,
    DateTime? PaymentDate,
    string CertificateReason,
    string CertifierName,
    string? CertifierPosition,
    string? WitnessName,
    string? WitnessPosition,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null);

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
    List<InboundJournalLineRequest> Lines,
    bool AutoBalanceVat = false);

public record InboundJournalLineRequest(
    string AccountCode, decimal DebitAmount, decimal CreditAmount,
    string? Description);

/// <summary>กลับรายการ Journal จากระบบภายนอก</summary>
public record InboundReverseJournalRequest(
    string? ExternalId, string? ExternalRef,
    Guid OriginalJournalEntryId,
    DateTime? ReversalDate,
    string? Description);

/// <summary>
/// ยกเลิกเอกสาร (Receipt / Invoice / TaxInvoice / PaymentVoucher ฯลฯ) จากระบบ
/// ภายนอก — รองรับกรณีเช่น TaketTime / booking ที่ออกใบเสร็จมัดจำมาแล้ว ต่อมา
/// ต้องการยกเลิกใบเดิมเพื่อออกใบรายรับเต็มเมื่อลูกค้าเข้าพักจริง. ระบบจะค้น
/// เอกสารด้วย ExternalRef (preferred) หรือ ExternalId แล้วเรียก VoidDocumentAsync
/// ที่ cascade ครบ: reverse Posted JE, void Payments ที่ผูก, ตัด bank match.
/// </summary>
public record InboundVoidDocumentRequest(
    string? ExternalId,
    string? ExternalRef,
    Guid? DocumentId,        // ทางเลือก: ส่ง internal Id โดยตรงก็ได้
    string? Reason);

/// <summary>Batch import — ส่งข้อมูลหลายรายการพร้อมกัน (สูงสุด 500 รายการต่อประเภท)</summary>
public record InboundBatchRequest(
    [property: MaxLength(500)] List<InboundCustomerRequest>? Customers,
    [property: MaxLength(500)] List<InboundInvoiceRequest>? Invoices,
    [property: MaxLength(500)] List<InboundPaymentRequest>? Payments,
    [property: MaxLength(500)] List<InboundExpenseRequest>? Expenses,
    [property: MaxLength(500)] List<InboundProductRequest>? Products,
    [property: MaxLength(500)] List<InboundJournalRequest>? Journals,
    [property: MaxLength(500)] List<InboundCertificateInLieuRequest>? CertificatesInLieu = null,
    [property: MaxLength(500)] List<InboundPaymentVoucherRequest>? PaymentVouchers = null);

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
    string? DocumentNumber,
    /// <summary>IDs of FileAttachment rows created from Attachments[] in the request.
    /// Empty when no attachments were sent or all failed validation. Order matches
    /// the request's Attachments list — failed entries are reported via Warnings.</summary>
    List<Guid>? AttachmentIds = null,
    List<string>? Warnings = null);

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
    DateTime CreatedAt,
    string? BranchName,
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? CountryCode,
    string? ContactPerson,
    bool IsActive,
    string? Moo = null);

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
