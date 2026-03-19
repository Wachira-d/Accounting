using Accounting.Models.DTOs;
using Accounting.Models.DTOs.DocumentTemplate;

namespace Accounting.Services.Interfaces;

public interface IEtaxInvoiceService
{
    Task<EtaxInvoiceResponse> GenerateAsync(Guid companyId, GenerateEtaxRequest request);
    Task<EtaxInvoiceResponse> GetByIdAsync(Guid companyId, Guid etaxId);
    Task<EtaxInvoiceResponse> GetByDocumentIdAsync(Guid companyId, Guid documentId);
    Task<PagedResponse<EtaxInvoiceResponse>> GetAllAsync(Guid companyId, EtaxStatus? status, PagedRequest request);
    Task<EtaxInvoiceResponse> SignAsync(Guid companyId, Guid etaxId);
    Task<EtaxInvoiceResponse> SubmitToRevenueAsync(Guid companyId, Guid etaxId);
    Task<string> GetXmlAsync(Guid companyId, Guid etaxId);
    Task VoidAsync(Guid companyId, Guid etaxId);
}
