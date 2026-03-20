using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Interfaces;

public interface IImportExportService
{
    // Import
    Task<ImportResult> ImportAsync(Guid companyId, ImportRequest request, string performedBy);
    Task<ImportTemplateResponse> GetImportTemplateAsync(string entityType);
    Task<ImportResult> ValidateImportAsync(Guid companyId, ImportRequest request);

    // Export
    Task<ExportResult> ExportAsync(Guid companyId, ExportRequest request);
    Task<List<string>> GetExportableEntitiesAsync();

    // Smart Import - AI Column Matching
    Task<SmartImportSessionResponse> UploadAndAnalyzeAsync(Guid companyId, SmartImportUploadRequest request, string performedBy);
    Task<SmartImportSessionResponse> GetSessionAsync(Guid companyId, Guid sessionId);
    Task<SmartImportSessionResponse> SubmitManualMappingAsync(Guid companyId, ManualMappingRequest request, string performedBy);
    Task<SmartImportResult> ConfirmAndImportAsync(Guid companyId, SmartImportConfirmRequest request, string performedBy);

    // Template Downloads
    Task<ImportTemplateDownloadResponse> DownloadTemplateAsync(string entityType, string format);
    Task<List<ImportableEntityInfo>> GetImportableEntitiesAsync();
}
