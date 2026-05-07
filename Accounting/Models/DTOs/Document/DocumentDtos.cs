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
    Guid? PerformanceObligationId = null);

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
    Guid? ProjectId = null);

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
    Guid? PerformanceObligationId = null);

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
    Guid? RelatedDocumentId = null);

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
    Guid? ProjectId = null);

public record ContactBrief(Guid Id, string Name, string? TaxId);

public record ApproveDocumentRequest(string? Notes);

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
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = "TH");

/// <summary>Request body for the smart-parse endpoint — paste address text, get structured fields.</summary>
public record ParseAddressRequest(string Address);

public record ParsedAddressResponse(
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode);

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
    string? Notes);

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
