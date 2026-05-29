using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Document;

public record CreateDocumentRequest(
    DocumentType DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    Guid ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest> Lines,
    Guid? ProjectId = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    // ===== ใบรับรองแทนใบเสร็จ =====
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ===== สกุลเงิน + อัตราแลกเปลี่ยน =====
    // Currency defaults to THB; ExchangeRate to 1. For non-THB docs the
    // service auto-fetches the BoT mid-rate at DocumentDate if ExchangeRate
    // is omitted; callers can override with a contracted rate.
    string Currency = "THB",
    decimal? ExchangeRate = null,
    // ===== Sensitivity classification (optional) =====
    // Internal callers (e.g. PayrollService) pass Sensitivity to gate the
    // resulting document behind the matching role. External clients leave it
    // None (the default) and the document is publicly visible within the company.
    SensitivityKind Sensitivity = SensitivityKind.None,
    // CreditNote reason — required when DocumentType=CreditNote. Determines
    // whether stock restocks (Return only) vs pure financial adjustment.
    CreditNoteReason? CreditNoteReason = null);

public record DocumentLineRequest(
    string Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    // Optional per-line project override (null → inherits Document.ProjectId)
    Guid? ProjectId = null,
    // Optional product linkage — set when the user picked a product via
    // the line-item typeahead. Stored on DocumentLine.ProductCode so reports
    // can group revenue/cost by product without re-parsing descriptions.
    string? ProductCode = null,
    // Traceability link for flexible/partial conversion — set by the
    // conversion engine, and round-tripped by the edit form so editing a
    // converted document never loses its link to the source line.
    Guid? SourceLineId = null);

public record UpdateDocumentRequest(
    DateTime? DocumentDate,
    DateTime? DueDate,
    Guid? ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest>? Lines,
    Guid? ProjectId = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null);

public record DocumentResponse(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    DateTime? DueDate,
    ContactBrief Contact,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    string? Reference,
    string? Notes,
    List<DocumentLineResponse> Lines,
    DateTime CreatedAt,
    // e-Tax info — set when this document has been processed via the e-Tax pipeline.
    // The UI uses these to surface the "Download PDF/A-3 (with embedded XML)" action,
    // which is required for e-Tax by Email compliance — printing strips the XML payload.
    Guid? EtaxInvoiceId = null,
    EtaxStatus? EtaxStatus = null,
    Guid? ProjectId = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    Guid? BankAccountId = null,
    string? BankAccountName = null,
    Guid? PaymentAccountId = null,
    string? PaymentAccountName = null,
    Guid? ExpenseCategoryId = null,
    string? ExpenseCategoryName = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    Guid? RelatedDocumentId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ERP upgrade — OCR RD compliance + aging cache
    decimal? OcrConfidenceScore = null,
    RdComplianceStatus RdComplianceStatus = RdComplianceStatus.Pending,
    string? RdComplianceIssuesJson = null,
    bool OcrTenantMismatchFlag = false,
    int? AgingDays = null,
    // Days a document has sat in a non-terminal status (Draft/WaitingApproval/
    // Approved/Sent/Partially-paid) beyond the stale threshold — null when not
    // stale. Surfaces "forgotten" documents (e.g. a PO left 3 months).
    int? StaleDays = null,
    // Multi-currency — Currency is doc's denomination; ExchangeRate is THB per
    // 1 unit of Currency captured at Create. Both default to ("THB", 1).
    string Currency = "THB",
    decimal ExchangeRate = 1m,
    // Sensitivity — None for the regular sales/purchase stream. Payroll vouchers
    // and other restricted records stamp this. When the requesting user lacks
    // the matching permission the API returns a stub with IsRedacted=true and
    // amounts/contact/notes blanked out so integration targets know the record
    // exists but is hidden — they should not 404 or pretend it isn't there.
    SensitivityKind Sensitivity = SensitivityKind.None,
    bool IsRedacted = false,
    string? RedactedReason = null,
    // CreditNote reason — set when DocumentType=CreditNote so the UI can
    // display "ลดราคา" / "คืนสินค้า" etc. Drives whether ApplyStockMovements
    // restocks on approval (only Return does).
    CreditNoteReason? CreditNoteReason = null);

public record DocumentLineResponse(
    Guid Id,
    int LineOrder,
    string Description,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount,
    Guid? AccountId = null,
    Guid? ProjectId = null,
    string? ProductCode = null,
    Guid? SourceLineId = null);

// ===== Flexible / partial document conversion =====

/// <summary>Convert only a chosen subset of a source document's lines, each
/// at a chosen quantity — e.g. split one PO into several delivery notes /
/// invoices. <see cref="Lines"/> with quantity 0 are ignored.</summary>
public record PartialConvertRequest(
    List<PartialConvertLineRequest> Lines,
    DateTime? DocumentDate = null,
    DateTime? DueDate = null);

public record PartialConvertLineRequest(Guid SourceLineId, decimal Quantity);

/// <summary>Per-line fulfilment snapshot of a source document — how much of
/// each line has already been carried forward into delivery notes vs.
/// billing documents, and how much remains.</summary>
public record DocumentFulfillmentResponse(
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    bool SupportsDelivery,
    bool SupportsBilling,
    List<DocumentLineFulfillmentResponse> Lines);

public record DocumentLineFulfillmentResponse(
    Guid LineId,
    int LineOrder,
    string Description,
    string Unit,
    decimal OrderedQuantity,
    decimal DeliveredQuantity,
    decimal DeliveryRemaining,
    decimal BilledQuantity,
    decimal BillingRemaining,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    Guid? ProjectId,
    string? ProductCode);

public record ContactBrief(Guid Id, string Name, string? TaxId);

public record ApproveDocumentRequest(string? Notes, bool AcknowledgeWarnings = false);

/// <summary>Returned on the first approve attempt when pre-approval checks
/// produced soft warnings (legal/correct but unusual). Operator reviews the
/// list and retries with AcknowledgeWarnings=true to proceed. Hard errors
/// (data corruption / illegal state) still throw inline — they're never
/// surfaced as warnings.</summary>
public record ApprovalWarningsResponse(IReadOnlyList<string> Warnings);

// ===== Contact =====
public record CreateContactRequest(
    [property: Required, StringLength(200)] string Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    // Structured address (optional — recommended for e-Tax compliance)
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null);

public record UpdateContactRequest(
    [property: StringLength(200)] string? Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool? IsCustomer,
    bool? IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool? IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null);

/// <summary>
/// Result of attempting to delete a contact. May be a hard delete or
/// a soft deactivation if the contact has linked accounting records.
/// </summary>
public record ContactDeleteResult(
    bool Deleted,            // true = removed; false = deactivated only
    bool Deactivated,        // true if the contact was set to inactive
    int LinkedDocumentsCount,
    int LinkedWhtCount,
    string Message);

public record ContactResponse(
    Guid Id,
    string Name,
    string? TaxId,
    string? BranchCode,
    ContactType ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = "TH",
    int LoyaltyPoints = 0,
    DateTime? LastVisitAt = null,
    int TotalVisitCount = 0);

/// <summary>Request body for the smart-parse endpoint — paste address text, get structured fields.</summary>
public record ParseAddressRequest(string Address);

public record ParsedAddressResponse(
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Moo = null);

/// <summary>ค่าเริ่มต้นอัตโนมัติ ระบบวิเคราะห์จากข้อมูลผู้ติดต่อ</summary>
public record ContactSmartDefaults(
    ContactType ContactType,
    string ContactTypeLabel,
    TaxType SuggestedTaxFormType,
    string SuggestedTaxFormLabel,
    DocumentType? SuggestedDocumentType,
    string? SuggestedDocumentTypeLabel,
    decimal DefaultWhtRate,
    string DefaultIncomeTypeCode,
    string DefaultIncomeTypeLabel);

// ===== Payment =====
public record CreatePaymentRequest(
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    string? Notes,
    /// <summary>Optional — overrides the source document's BankAccountId for
    /// THIS payment only. Use when the cheque actually cleared through a
    /// different bank than the invoice originally targeted; the GL hit and
    /// bank-balance update follow this override, not doc.BankAccountId.</summary>
    Guid? OverrideBankAccountId = null,
    /// <summary>Optional — WHT withheld on THIS installment. Null = the
    /// service computes a proportional default: Amount / Document.TotalAmount
    /// × Document.WithholdingTaxAmount. Use the override when the customer's
    /// WHT certificate shows a different amount than the proportional split
    /// (e.g. they withhold the full amount on the first installment).
    /// Cumulative WHT across all payments must not exceed the source's
    /// WithholdingTaxAmount.</summary>
    decimal? WithholdingTaxAmount = null);

public record PaymentResponse(
    Guid Id,
    string PaymentNumber,
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    Guid? BankAccountId,
    string? Notes,
    DateTime CreatedAt);


public record WriteOffBadDebtRequest(string? Reason);
public record BatchConvertRequest(List<Guid> DocumentIds);
