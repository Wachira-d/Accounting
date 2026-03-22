namespace Accounting.Models.DTOs.ReportBuilder;

public record CreateCustomReportRequest(string Name, string? Description, string ReportType, string Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, string? ChartConfigJson, bool ShowTotals, bool IsPublic, bool IsScheduled, string? ScheduleFrequency, string? SendToEmails, string? ExportFormat);
public record UpdateCustomReportRequest(string? Name, string? Description, string? FilterJson, string? ColumnsJson, string? SortingJson, string? GroupingJson, string? AggregationJson, string? ChartType, bool? ShowTotals, bool? IsPublic, bool? IsScheduled, string? ScheduleFrequency);
public record CustomReportResponse(Guid Id, string Name, string? Description, string ReportType, string Category, string DataSourceType, string? FilterJson, string? ColumnsJson, string? GroupingJson, string? ChartType, bool IsPublic, bool IsScheduled, DateTime CreatedAt);
public record CustomReportListResponse(Guid Id, string Name, string ReportType, string Category, bool IsPublic, DateTime CreatedAt);
public record ReportExecutionResponse(string ReportName, string ReportType, List<Dictionary<string, object>> Data, Dictionary<string, object>? Totals, int TotalRows, DateTime ExecutedAt);
public record ReportDataSourceResponse(string Type, string DisplayName, string Description);
public record ReportColumnDefinition(string Name, string DisplayName, string DataType, bool IsSortable, bool IsFilterable, bool IsGroupable);
