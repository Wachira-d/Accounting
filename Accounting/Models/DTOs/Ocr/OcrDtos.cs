namespace Accounting.Models.DTOs.Ocr;

public record OcrResultResponse(
    Guid Id, string OriginalFileName, string ScanStatus, string? DocumentType, decimal Confidence,
    string? ExtractedVendorName, string? ExtractedVendorTaxId, string? ExtractedDocumentNumber,
    DateTime? ExtractedDate, decimal? ExtractedSubTotal, decimal? ExtractedVatAmount, decimal? ExtractedTotalAmount,
    Guid? MatchedContactId, Guid? CreatedDocumentId, DateTime? ProcessedAt,
    bool IsDuplicate = false, Guid? DuplicateOfScanId = null, string? FileHash = null, string? ProcessingNotes = null,
    string? ExpenseCategory = null,
    OcrSuggestedAccountsDto? SuggestedAccounts = null,
    bool HasWht = false, decimal? WhtRate = null,
    int? PaymentTermsDays = null,
    List<OcrLineItemDto>? ExtractedItems = null,
    string? RawTextContent = null,
    Dictionary<string, double>? FieldConfidence = null,
    string? BuyerName = null,
    string? BuyerTaxId = null,
    OcrDbdInfo? DbdInfo = null,
    // ─── Role inference (Phase 1) ───
    // ScannedDocumentType is the paper that the user actually scanned.
    // TargetDocumentType is what we should create in our books — these differ
    // for the common case of a supplier receipt (scanned=Receipt,
    // target=PaymentVoucher). OurRole is "Buyer" or "Seller". The legacy
    // DocumentType field mirrors ScannedDocumentType for back-compat.
    string? ScannedDocumentType = null,
    string? OurRole = null,
    string? TargetDocumentType = null);

public record OcrDbdInfo(
    bool LookupAttempted,
    bool Matched,
    string? CanonicalName = null,
    string? Address = null,
    string? JuristicType = null,
    string? Status = null);

public record OcrSuggestedAccountsDto(
    string? DebitAccountCode, string? DebitAccountName,
    string? CreditAccountCode, string? CreditAccountName,
    string? VatAccountCode = null, string? VatAccountName = null);

public record OcrLineItemDto(
    string? Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount,
    string? SuggestedAccountCode = null);

public record OcrCreditPurchaseRequest(int Pages);

public record OcrCorrectionRequest(
    string? DocumentType = null,
    string? VendorName = null,
    string? VendorTaxId = null,
    string? DocumentNumber = null,
    DateTime? DocumentDate = null,
    decimal? SubTotal = null,
    decimal? VatAmount = null,
    decimal? TotalAmount = null,
    string? ExpenseCategory = null,
    string? DebitAccountCode = null,
    string? CreditAccountCode = null,
    bool? HasWht = null,
    decimal? WhtRate = null,
    // Phase-1 role-inference correction: when user changes the inferred
    // "เอกสารที่จะสร้าง" dropdown, this string carries the new value so the
    // backend can both update the scan record AND train VendorIntelligence
    // to suggest the same target for this vendor next time.
    string? TargetDocumentType = null);
