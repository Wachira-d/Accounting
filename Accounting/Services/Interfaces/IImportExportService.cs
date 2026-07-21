using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Interfaces;

public interface IImportExportService
{
    // Import
    Task<ImportResult> ImportAsync(Guid companyId, ImportRequest request, string performedBy);
    Task<ImportTemplateResponse> GetImportTemplateAsync(string entityType);
    Task<ImportResult> ValidateImportAsync(Guid companyId, ImportRequest request);
    /// <summary>Scan request.Data against the DB and return rows whose
    /// natural key (TaxId/Code/AccountCode) already exists with different
    /// field values. Supported for entityTypes: contacts, products,
    /// chartofaccounts. Returns an empty list for other entity types.</summary>
    Task<ConflictPreviewResponse> PreviewConflictsAsync(Guid companyId, ImportRequest request);

    // Export
    Task<ExportResult> ExportAsync(Guid companyId, ExportRequest request);
    Task<List<string>> GetExportableEntitiesAsync();

    // Smart Import - AI Column Matching
    Task<SmartImportSessionResponse> UploadAndAnalyzeAsync(Guid companyId, SmartImportUploadRequest request, string performedBy);
    Task<SmartImportSessionResponse> GetSessionAsync(Guid companyId, Guid sessionId);
    Task<SmartImportSessionResponse> SubmitManualMappingAsync(Guid companyId, ManualMappingRequest request, string performedBy);
    Task<SmartImportResult> ConfirmAndImportAsync(Guid companyId, SmartImportConfirmRequest request, string performedBy);
    /// <summary>Scan a Smart Import session's mapped data against the DB
    /// for conflicts, same shape as PreviewConflictsAsync but works off the
    /// stored session + column mappings (so the UI can ask BEFORE Confirm).</summary>
    Task<ConflictPreviewResponse> PreviewSmartConflictsAsync(Guid companyId, Guid sessionId);

    // AI Review — single DeepSeek call covering type normalizations + fuzzy dups
    // + per-row quality flags + semantic field validation + batch patterns.
    // Caller invokes after mapping is saved, before commit.
    Task<ImportAiReviewResponse> AiReviewSessionAsync(Guid companyId, Guid sessionId);

    // Template Downloads
    Task<ImportTemplateDownloadResponse> DownloadTemplateAsync(string entityType, string format);
    Task<List<ImportableEntityInfo>> GetImportableEntitiesAsync();
}
