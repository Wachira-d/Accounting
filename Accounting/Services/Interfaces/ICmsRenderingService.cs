using Accounting.Models.DTOs.Cms;

namespace Accounting.Services.Interfaces;

public interface ICmsRenderingService
{
    Task<string> GenerateThemeCssAsync(Guid companyId, Guid siteId);
    Task<RenderedPageResponse?> RenderPageAsync(Guid companyId, Guid siteId, string slug, string? languageCode = null);
    Task<StorefrontDataResponse> GetStorefrontDataAsync(Guid companyId, Guid siteId, string? languageCode = null);
    Task<string> RenderBlockHtmlAsync(Guid companyId, Guid siteId, Guid blockId, string? languageCode = null);
}
