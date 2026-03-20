using Accounting.Models.DTOs;

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

public record CreateCustomReportRequest(string Name, string? Description, string ReportType, string Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, string? ChartConfigJson, bool ShowTotals, bool IsPublic, bool IsScheduled, string? ScheduleFrequency, string? SendToEmails, string? ExportFormat);
public record UpdateCustomReportRequest(string? Name, string? Description, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, bool? ShowTotals, bool? IsPublic, bool? IsScheduled, string? ScheduleFrequency);
public record CustomReportResponse(Guid Id, string Name, string? Description, string ReportType, string Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? GroupingJson, string? ChartType, bool IsPublic, bool IsScheduled, DateTime CreatedAt);
public record CustomReportListResponse(Guid Id, string Name, string ReportType, string Category, bool IsPublic, DateTime CreatedAt);
public record ReportExecutionResponse(string ReportName, string ReportType, List<Dictionary<string, object>> Data, Dictionary<string, object>? Totals, int TotalRows, DateTime ExecutedAt);
public record ReportDataSourceResponse(string Type, string DisplayName, string Description);
public record ReportColumnDefinition(string Name, string DisplayName, string DataType, bool IsSortable, bool IsFilterable, bool IsGroupable);
