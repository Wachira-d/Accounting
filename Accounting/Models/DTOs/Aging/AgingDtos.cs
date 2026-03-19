using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Aging;

public record AgingReportResponse(
    AgingReportType ReportType,
    DateTime AsOfDate,
    List<AgingBucket> Buckets,
    List<AgingContactDetail> Details,
    AgingTotals Totals);

public record AgingBucket(
    string Label,
    int FromDays,
    int? ToDays,
    decimal TotalAmount,
    int DocumentCount);

public record AgingContactDetail(
    Guid ContactId,
    string ContactName,
    string? TaxId,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal TotalBalance,
    List<AgingDocumentDetail> Documents);

public record AgingDocumentDetail(
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    decimal TotalAmount,
    decimal BalanceDue,
    int AgingDays,
    string AgingBucket);

public record AgingTotals(
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal GrandTotal);

public record AgingReportRequest(
    DateTime? AsOfDate = null,
    Guid? ContactId = null,
    int[]? CustomBuckets = null);

public enum AgingReportType
{
    AccountsReceivable = 1,
    AccountsPayable = 2
}
