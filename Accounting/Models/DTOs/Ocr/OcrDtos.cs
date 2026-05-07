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
    OcrDbdInfo? DbdInfo = null);

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
    decimal? WhtRate = null);
