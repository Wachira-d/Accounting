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
    List<TaxReportLineResponse> Lines);

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
    string? IncomeTypeCode);
