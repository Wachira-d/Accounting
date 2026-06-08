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
    Task DeleteAsync(Guid companyId, Guid certId);
    Task DeleteAsync(Guid companyId, Guid certId, Guid? userId);
    Task<List<WithholdingTaxCertResponse>> GetByContactAsync(Guid companyId, Guid contactId, int? year = null);

    // Auto-generate from document/payment
    Task<WithholdingTaxCertResponse> AutoGenerateFromDocumentAsync(Guid companyId, Guid documentId, bool autoIssue, string createdBy);
    Task<List<PendingWhtDocumentResponse>> GetPendingDocumentsAsync(Guid companyId, int? year = null, int? month = null);
    Task<BulkGenerateWhtResponse> BulkGenerateAsync(Guid companyId, BulkGenerateWhtRequest request, string createdBy);
    /// <summary>Dismiss a document from the "waiting to issue cert" list — the
    /// source document stays exactly as-is, we just stop prompting. Set
    /// dismiss=false to re-add it.</summary>
    Task DismissPendingAsync(Guid companyId, Guid documentId, bool dismiss);
}
