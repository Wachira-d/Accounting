namespace Accounting.Models.DTOs.Ocr;

public record OcrResultResponse(Guid Id, string OriginalFileName, string ScanStatus, string? DocumentType, decimal Confidence, string? ExtractedVendorName, string? ExtractedVendorTaxId, string? ExtractedDocumentNumber, DateTime? ExtractedDate, decimal? ExtractedSubTotal, decimal? ExtractedVatAmount, decimal? ExtractedTotalAmount, Guid? MatchedContactId, Guid? CreatedDocumentId, DateTime? ProcessedAt);
