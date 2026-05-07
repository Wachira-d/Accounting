namespace Accounting.Models.DTOs.Ocr;

public record OcrResultResponse(
    Guid Id, string OriginalFileName, string ScanStatus, string? DocumentType, decimal Confidence,
    string? ExtractedVendorName, string? ExtractedVendorTaxId, string? ExtractedDocumentNumber,
    DateTime? ExtractedDate, decimal? ExtractedSubTotal, decimal? ExtractedVatAmount, decimal? ExtractedTotalAmount,
    Guid? MatchedContactId, Guid? CreatedDocumentId, DateTime? ProcessedAt,
    bool IsDuplicate = false, Guid? DuplicateOfScanId = null, string? FileHash = null, string? ProcessingNotes = null);

public record OcrCorrectionRequest(
    string? DocumentType = null,
    string? VendorName = null,
    string? VendorTaxId = null,
    string? DocumentNumber = null,
    DateTime? DocumentDate = null,
    decimal? SubTotal = null,
    decimal? VatAmount = null,
    decimal? TotalAmount = null);
