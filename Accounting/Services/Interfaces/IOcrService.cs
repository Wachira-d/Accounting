using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;

namespace Accounting.Services.Interfaces;

public interface IOcrService
{
    Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId);
    Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId);
    Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request);
    Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy);
    Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId);
    Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction);
    Task DeleteScanAsync(Guid companyId, Guid scanResultId);
}
