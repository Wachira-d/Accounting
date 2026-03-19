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
}
