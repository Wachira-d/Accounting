using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICmsSiteService
{
    // Sites
    Task<SiteResponse> CreateSiteAsync(Guid companyId, CreateSiteRequest request, string userId);
    Task<SiteResponse> UpdateSiteAsync(Guid companyId, Guid siteId, UpdateSiteRequest request, string userId);
    Task<SiteResponse?> GetSiteAsync(Guid companyId, Guid siteId);
    Task<PagedResponse<SiteListResponse>> GetSitesAsync(Guid companyId, string? search = null, int page = 1, int pageSize = 20);
    Task<bool> DeleteSiteAsync(Guid companyId, Guid siteId);
    Task<SiteResponse> PublishSiteAsync(Guid companyId, Guid siteId, string userId);
    Task<ApplySiteTemplateResponse> ApplyTemplateAsync(Guid companyId, Guid siteId, ApplySiteTemplateRequest request, string userId);

    // Domains
    Task<DomainResponse> AddDomainAsync(Guid companyId, Guid siteId, CreateDomainRequest request, string userId);
    Task<List<DomainResponse>> GetDomainsAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteDomainAsync(Guid companyId, Guid siteId, Guid domainId);
    Task<DomainResponse> VerifyDomainAsync(Guid companyId, Guid siteId, Guid domainId);

    // Themes
    Task<ThemeResponse> CreateThemeAsync(Guid companyId, CreateThemeRequest request, string userId);
    Task<ThemeResponse> UpdateThemeAsync(Guid companyId, Guid themeId, UpdateThemeRequest request, string userId);
    Task<List<ThemeResponse>> GetThemesAsync(Guid companyId);
    Task<ThemeResponse?> GetThemeAsync(Guid companyId, Guid themeId);
    Task<bool> DeleteThemeAsync(Guid companyId, Guid themeId);

    // Locales
    Task<LocaleResponse> AddLocaleAsync(Guid companyId, Guid siteId, CreateLocaleRequest request, string userId);
    Task<List<LocaleResponse>> GetLocalesAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteLocaleAsync(Guid companyId, Guid siteId, Guid localeId);

    // Staff Access (RBAC)
    Task<StaffAccessResponse> GrantStaffAccessAsync(Guid companyId, Guid siteId, CreateStaffAccessRequest request, string userId);
    Task<List<StaffAccessResponse>> GetStaffAccessAsync(Guid companyId, Guid siteId);
    Task<bool> RevokeStaffAccessAsync(Guid companyId, Guid siteId, Guid accessId);

    // Public resolution
    Task<SiteResponse?> ResolveSiteBySubdomainAsync(string subdomain);
    Task<SiteResponse?> ResolveSiteByDomainAsync(string domain);
}
