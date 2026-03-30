namespace Accounting.Models.DTOs.Tax;

public record TaxFilingExportResult(
    string FormCode,
    string FormName,
    string FileName,
    string ContentType,
    byte[] FileData,
    int RecordCount,
    decimal TotalIncome,
    decimal TotalTax,
    string? Summary);
