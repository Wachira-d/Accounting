namespace Accounting.Models.DTOs.Consolidation;

public record ConsolidationReportSummary(
    Guid Id, string ReportType, DateTime AsOfDate, DateTime? FromDate,
    DateTime? ToDate, string Status, DateTime CreatedAt);
