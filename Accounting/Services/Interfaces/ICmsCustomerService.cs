using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICmsCustomerService
{
    // Customers
    Task<SiteCustomerResponse> CreateCustomerAsync(Guid companyId, Guid siteId, CreateSiteCustomerRequest request, string userId);
    Task<SiteCustomerResponse> UpdateCustomerAsync(Guid companyId, Guid siteId, Guid customerId, UpdateSiteCustomerRequest request, string userId);
    Task<SiteCustomerResponse?> GetCustomerAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<PagedResponse<SiteCustomerListResponse>> GetCustomersAsync(Guid companyId, Guid siteId, string? search = null, string? group = null, int page = 1, int pageSize = 20);
    Task<bool> DeleteCustomerAsync(Guid companyId, Guid siteId, Guid customerId);

    // Customer Portal Auth
    Task<CustomerLoginResponse> CustomerLoginAsync(Guid companyId, Guid siteId, CustomerLoginRequest request);
    Task<SiteCustomerResponse> CustomerRegisterAsync(Guid companyId, Guid siteId, CustomerRegisterRequest request);

    // Customer Addresses
    Task<CustomerAddressResponse> AddAddressAsync(Guid companyId, Guid siteId, Guid customerId, CreateCustomerAddressRequest request);
    Task<List<CustomerAddressResponse>> GetAddressesAsync(Guid companyId, Guid siteId, Guid customerId);
    Task<bool> DeleteAddressAsync(Guid companyId, Guid siteId, Guid customerId, Guid addressId);

    // PDPA / Right to be Forgotten
    Task<bool> UpdateConsentAsync(Guid companyId, Guid siteId, Guid customerId, ConsentUpdateRequest request);
    Task<bool> ProcessDataDeletionAsync(Guid companyId, Guid siteId, DataDeletionRequest request, string userId);

    // CRM: cross-site identity merge
    Task<bool> MergeCustomersAsync(Guid companyId, MergeCustomersRequest request, string userId);
    Task<bool> AutoLinkToErpContactAsync(Guid companyId, Guid siteId, Guid customerId);

    // Forms
    Task<FormResponse> CreateFormAsync(Guid companyId, Guid siteId, CreateFormRequest request, string userId);
    Task<FormResponse?> GetFormAsync(Guid companyId, Guid siteId, Guid formId);
    Task<List<FormResponse>> GetFormsAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteFormAsync(Guid companyId, Guid siteId, Guid formId);

    // Form Submissions
    Task<FormSubmissionResponse> SubmitFormAsync(Guid companyId, Guid siteId, Guid formId, SubmitFormRequest request, string? ipAddress = null, string? userAgent = null);
    Task<PagedResponse<FormSubmissionResponse>> GetSubmissionsAsync(Guid companyId, Guid siteId, Guid formId, string? status = null, int page = 1, int pageSize = 20);
    Task<bool> UpdateSubmissionStatusAsync(Guid companyId, Guid siteId, Guid formId, Guid submissionId, string status, string? notes = null);
}
