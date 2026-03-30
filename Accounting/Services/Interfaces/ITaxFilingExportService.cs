using Accounting.Models.DTOs.Tax;

namespace Accounting.Services.Interfaces;

public interface ITaxFilingExportService
{
    // ภ.ง.ด.1 - Monthly Salary WHT (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd1Async(Guid companyId, int year, int month);

    // ภ.ง.ด.3 - Service WHT for individuals (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd3Async(Guid companyId, int year, int month);

    // ภ.ง.ด.53 - Service WHT for companies (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd53Async(Guid companyId, int year, int month);

    // ภ.ง.ด.1ก - Annual salary summary
    Task<TaxFilingExportResult> ExportPnd1kAsync(Guid companyId, int year);

    // ภ.พ.30 - VAT filing
    Task<TaxFilingExportResult> ExportPp30Async(Guid companyId, int year, int month);

    // สปส.1-10 - SSO monthly contribution report
    Task<TaxFilingExportResult> ExportSso110Async(Guid companyId, int year, int month);
}
