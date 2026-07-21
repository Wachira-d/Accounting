using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICmsContentService
{
    // Pages
    Task<PageResponse> CreatePageAsync(Guid companyId, Guid siteId, CreatePageRequest request, string userId);
    Task<PageResponse> UpdatePageAsync(Guid companyId, Guid siteId, Guid pageId, UpdatePageRequest request, string userId);
    Task<PageResponse?> GetPageAsync(Guid companyId, Guid siteId, Guid pageId);
    Task<PageResponse?> GetPageBySlugAsync(Guid companyId, Guid siteId, string slug, string? languageCode = null);
    Task<PagedResponse<PageListResponse>> GetPagesAsync(Guid companyId, Guid siteId, string? search = null, int page = 1, int pageSize = 20);
    Task<bool> DeletePageAsync(Guid companyId, Guid siteId, Guid pageId);
    Task<PageResponse> PublishPageAsync(Guid companyId, Guid siteId, Guid pageId, string userId);

    // Page Translations
    Task<PageTranslationResponse> UpsertPageTranslationAsync(Guid companyId, Guid siteId, Guid pageId, UpsertPageTranslationRequest request, string userId);
    Task<bool> DeletePageTranslationAsync(Guid companyId, Guid siteId, Guid pageId, string languageCode);

    // Blocks
    Task<BlockResponse> CreateBlockAsync(Guid companyId, Guid siteId, Guid pageId, CreateBlockRequest request, string userId);
    Task<BlockResponse> UpdateBlockAsync(Guid companyId, Guid siteId, Guid pageId, Guid blockId, UpdateBlockRequest request, string userId);
    Task<bool> DeleteBlockAsync(Guid companyId, Guid siteId, Guid pageId, Guid blockId);
    Task<bool> ReorderBlocksAsync(Guid companyId, Guid siteId, Guid pageId, ReorderBlocksRequest request);

    // Navigation
    Task<NavigationResponse> CreateNavigationAsync(Guid companyId, Guid siteId, CreateNavigationRequest request, string userId);
    Task<List<NavigationResponse>> GetNavigationsAsync(Guid companyId, Guid siteId);
    Task<NavigationResponse?> GetNavigationAsync(Guid companyId, Guid siteId, Guid navId);
    Task<bool> DeleteNavigationAsync(Guid companyId, Guid siteId, Guid navId);
    Task<MenuItemResponse> AddMenuItemAsync(Guid companyId, Guid siteId, Guid navId, CreateMenuItemRequest request, string userId);
    Task<bool> DeleteMenuItemAsync(Guid companyId, Guid siteId, Guid navId, Guid itemId);

    // Media
    Task<MediaResponse> UploadMediaAsync(Guid companyId, Guid siteId, string fileName, string contentType, Stream fileStream, string userId);
    Task<PagedResponse<MediaResponse>> GetMediaAsync(Guid companyId, Guid siteId, string? folder = null, string? search = null, int page = 1, int pageSize = 20);
    Task<MediaResponse> UpdateMediaAsync(Guid companyId, Guid siteId, Guid mediaId, UpdateMediaRequest request, string userId);
    Task<bool> DeleteMediaAsync(Guid companyId, Guid siteId, Guid mediaId);

    // SEO Redirects
    Task<SeoRedirectResponse> CreateSeoRedirectAsync(Guid companyId, Guid siteId, CreateSeoRedirectRequest request, string userId);
    Task<List<SeoRedirectResponse>> GetSeoRedirectsAsync(Guid companyId, Guid siteId);
    Task<bool> DeleteSeoRedirectAsync(Guid companyId, Guid siteId, Guid redirectId);

    // Block Templates
    Task<List<BlockTemplateResponse>> GetBlockTemplatesAsync();

    // Sitemap
    Task<string> GenerateSitemapXmlAsync(Guid companyId, Guid siteId);

    // JSON-LD
    Task<string> GenerateJsonLdAsync(Guid companyId, Guid siteId, Guid pageId);
}
