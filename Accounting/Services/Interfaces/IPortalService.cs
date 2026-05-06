using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;

namespace Accounting.Services.Interfaces;

public interface IPortalService
{
    // Portal access management (by company)
    Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request);
    Task<List<PortalAccessResponse>> GetAccessesAsync(Guid companyId);
    Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request);
    Task DeactivateAccessAsync(Guid companyId, Guid accessId);

    // Portal auth (by customer/supplier)
    Task<PortalLoginResponse> LoginAsync(PortalLoginRequest request);
    Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken);

    Guid? ExtractContactIdFromToken(string token);

    // Portal data (by customer/supplier)
    Task<List<PortalDocumentResponse>> GetMyDocumentsAsync(Guid companyId, Guid contactId, string? documentType = null);
    Task<PortalDocumentResponse> GetDocumentAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<byte[]> DownloadDocumentPdfAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<PortalStatementResponse> GetMyStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate);
    Task<List<PortalPaymentResponse>> GetMyPaymentsAsync(Guid companyId, Guid contactId);
}
