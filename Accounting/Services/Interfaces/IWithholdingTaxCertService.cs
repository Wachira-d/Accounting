using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IWithholdingTaxCertService
{
    Task<WithholdingTaxCertResponse> CreateAsync(Guid companyId, CreateWithholdingTaxCertRequest request, string createdBy);
    Task<WithholdingTaxCertResponse> GetByIdAsync(Guid companyId, Guid certId);
    Task<PagedResponse<WithholdingTaxCertResponse>> GetAllAsync(Guid companyId, TaxType? taxFormType, int? year, int? month, PagedRequest request);
    Task<WithholdingTaxCertResponse> IssueAsync(Guid companyId, Guid certId);
    Task VoidAsync(Guid companyId, Guid certId);
    Task<List<WithholdingTaxCertResponse>> GetByContactAsync(Guid companyId, Guid contactId, int? year = null);
}
