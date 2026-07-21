using Accounting.Models.DTOs;
using Accounting.Models.DTOs.ReportBuilder;

namespace Accounting.Services.Interfaces;

public interface IReportBuilderService
{
    Task<CustomReportResponse> CreateAsync(Guid companyId, CreateCustomReportRequest request, string userId);
    Task<CustomReportResponse> GetByIdAsync(Guid companyId, Guid reportId);
    Task<List<CustomReportListResponse>> GetAllAsync(Guid companyId, string? category = null);
    Task<CustomReportResponse> UpdateAsync(Guid companyId, Guid reportId, UpdateCustomReportRequest request);
    Task DeleteAsync(Guid companyId, Guid reportId);
    Task<CustomReportResponse> DuplicateAsync(Guid companyId, Guid reportId, string newName);

    // Execution
    Task<ReportExecutionResponse> ExecuteAsync(Guid companyId, Guid reportId, Dictionary<string, string>? parameters = null);
    Task<byte[]> ExportAsync(Guid companyId, Guid reportId, string format, Dictionary<string, string>? parameters = null);

    // Schema
    Task<List<ReportDataSourceResponse>> GetDataSourcesAsync();
    Task<List<ReportColumnDefinition>> GetColumnsForDataSourceAsync(string dataSourceType);
}
