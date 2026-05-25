using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

public record CreateTaxReportRequest(
    TaxType TaxType,
    int Year,
    int Month);

public record TaxReportResponse(
    Guid Id,
    TaxType TaxType,
    int Year,
    int Month,
    TaxReportStatus Status,
    DateTime? FiledDate,
    decimal OutputVat,
    decimal InputVat,
    decimal NetVat,
    decimal TotalIncome,
    decimal TotalTaxWithheld,
    List<TaxReportLineResponse> Lines,
    string? Notes = null);

public record UpdateTaxReportRequest(
    string? Notes,
    List<UpdateTaxReportLineRequest>? Lines);

public record UpdateTaxReportLineRequest(
    Guid Id,
    decimal? IncomeAmount,
    decimal? TaxRate,
    decimal? TaxAmount,
    string? Description,
    bool? Excluded = null);

public record PullableDocumentDto(
    Guid Id,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    string ContactName,
    decimal SubTotal,
    decimal VatAmount,
    bool IsInput);

public record PullDocumentRequest(Guid DocumentId);

public record TaxReportLineResponse(
    Guid Id,
    int LineOrder,
    string? TaxPayerId,
    string? TaxPayerName,
    DateTime TransactionDate,
    string? Description,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? IncomeTypeCode,
    bool IsExcluded = false);
