using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;

namespace Accounting.Services.Interfaces;

public interface IOcrService
{
    /// <summary>
    /// Run the OCR cascade on an uploaded file.
    /// <paramref name="preferredEngine"/>: "auto" | "azure" | "local" (case-
    /// insensitive). Null/empty/auto = full cascade (Tier 0 e-Tax XML →
    /// Tier 1 Azure DI → Tier 2 Local Python → Tier 3 Embedded Tesseract).
    /// "azure" skips Tier 2 / Tier 3 (no silent local fallback when the user
    /// explicitly asked for Azure-grade accuracy). "local" skips Tier 1 so
    /// no Azure cost is incurred. Tier 0 always runs regardless — it's free,
    /// 100% accurate, and consumes no engine quota.
    ///
    /// <paramref name="externalMetadataJson"/>: optional structured payload an
    /// external system uploads alongside the file (order/project line info).
    /// When present, OcrMetadataProjectMatcher links each extracted line back
    /// to its originating project so created DocumentLines get ProjectId
    /// pre-selected for automatic cost allocation.
    /// </summary>
    Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId, string? preferredEngine = null, string? externalMetadataJson = null);
    Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId);
    Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request);
    Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride = null);
    /// <summary>Rebuild a document's lines from its source OCR scan when it
    /// was created empty (pre line-building fix). Looked up by documentId.</summary>
    Task<OcrResultResponse> RepopulateDocumentLinesFromScanAsync(Guid companyId, Guid documentId, string performedBy);

    /// <summary>Record a balanced Journal Entry directly from a scan (the
    /// "JE only" path — no business document). Returns the created JE id.</summary>
    Task<Guid> CreateJournalEntryFromScanAsync(Guid companyId, Guid scanResultId,
        Models.DTOs.Ocr.CreateJeFromScanRequest request, string performedBy);
    Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId);

    /// <summary>Persist a per-line project assignment into the scan's
    /// ExtractedItemsJson so CreateDocumentFromScanAsync can flow it
    /// to DocumentLine.ProjectId.</summary>
    Task SetExtractedLineProjectAsync(Guid companyId, Guid scanResultId,
        int lineIndex, Guid? projectId, string? projectName);

    /// <summary>Bulk assign — set the same project on EVERY extracted
    /// line. Used by the OCR review UI's "main project" picker:
    /// user picks one project, every row inherits it, then user only
    /// has to touch the rows that should override. When onlyEmpty=true,
    /// only lines that don't already have a project get updated
    /// (preserves the user's prior overrides).</summary>
    Task SetAllExtractedLineProjectsAsync(Guid companyId, Guid scanResultId,
        Guid? projectId, string? projectName, bool onlyEmpty);

    Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction);
    Task DeleteScanAsync(Guid companyId, Guid scanResultId, bool cascadeCreatedDocument = false);
    Task<object> RegisterAssetFromScanAsync(Guid companyId, Guid scanResultId,
        Controllers.OcrController.RegisterAssetFromScanRequest req,
        IFixedAssetService assetService, string createdBy);
}
