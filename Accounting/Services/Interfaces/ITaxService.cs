using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ITaxService
{
    Task<TaxReportResponse> GenerateTaxReportAsync(Guid companyId, CreateTaxReportRequest request);
    Task<TaxReportResponse> GetTaxReportAsync(Guid companyId, Guid reportId);
    Task<List<TaxReportResponse>> GetTaxReportsAsync(Guid companyId, TaxType? taxType = null, int? year = null);
    Task<TaxReportResponse> FileTaxReportAsync(Guid companyId, Guid reportId);
    Task<TaxReportResponse> UpdateTaxReportAsync(Guid companyId, Guid reportId, UpdateTaxReportRequest request);
    Task<TaxReportResponse> RegenerateTaxReportAsync(Guid companyId, Guid reportId);
    Task DeleteTaxReportAsync(Guid companyId, Guid reportId);
    Task<object> GetVatDebugAsync(Guid companyId, int year, int month);
}
