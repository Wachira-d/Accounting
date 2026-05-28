using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Portal;
using Microsoft.AspNetCore.Http;

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

    // Customer-self-service payments
    /// <summary>Surface the company's first active PromptPay / bank
    /// gateway so the portal can render QR + account info for the
    /// customer to pay an outstanding invoice.</summary>
    Task<StorefrontPaymentOptions> GetCompanyPaymentOptionsAsync(Guid companyId);
    /// <summary>Record a slip uploaded by the portal customer against
    /// a specific Document (invoice). Creates a Payment row in
    /// Pending status; owner reviews + marks Confirmed.</summary>
    Task<PortalSlipUploadResponse> UploadDocumentSlipAsync(
        Guid companyId, Guid contactId, Guid documentId, IFormFile file, decimal? amount);
}
