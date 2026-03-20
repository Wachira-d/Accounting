using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IOcrService
{
    Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId);
    Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId);
    Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request);
    Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy);
    Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId);
}

public record OcrResultResponse(Guid Id, string OriginalFileName, string ScanStatus, string? DocumentType, decimal Confidence, string? ExtractedVendorName, string? ExtractedVendorTaxId, string? ExtractedDocumentNumber, DateTime? ExtractedDate, decimal? ExtractedSubTotal, decimal? ExtractedVatAmount, decimal? ExtractedTotalAmount, Guid? MatchedContactId, Guid? CreatedDocumentId, DateTime? ProcessedAt);
