using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront")]
[AllowAnonymous]
public class CmsStorefrontController : ControllerBase
{
    private readonly ICmsRenderingService _renderingService;
    private readonly ICmsContentService _contentService;
    private readonly ICmsCommerceService _commerceService;
    private readonly ICmsBookingService _bookingService;

    public CmsStorefrontController(
        ICmsRenderingService renderingService,
        ICmsContentService contentService,
        ICmsCommerceService commerceService,
        ICmsBookingService bookingService)
    {
        _renderingService = renderingService;
        _contentService = contentService;
        _commerceService = commerceService;
        _bookingService = bookingService;
    }

    // ===== Storefront Bootstrap Data =====

    [HttpGet("init")]
    public async Task<ActionResult<ApiResponse<StorefrontDataResponse>>> Init(
        Guid companyId, Guid siteId, [FromQuery] string? lang = null)
    {
        var result = await _renderingService.GetStorefrontDataAsync(companyId, siteId, lang);
        return Ok(new ApiResponse<StorefrontDataResponse>(true, result));
    }

    // ===== Theme CSS =====

    [HttpGet("theme.css")]
    [Produces("text/css")]
    public async Task<IActionResult> ThemeCss(Guid companyId, Guid siteId)
    {
        var css = await _renderingService.GenerateThemeCssAsync(companyId, siteId);
        return Content(css, "text/css");
    }

    // ===== Page Rendering =====

    [HttpGet("pages/{*slug}")]
    public async Task<ActionResult<ApiResponse<RenderedPageResponse>>> RenderPage(
        Guid companyId, Guid siteId, string slug, [FromQuery] string? lang = null)
    {
        var result = await _renderingService.RenderPageAsync(companyId, siteId, slug, lang);
        if (result == null) return NotFound(new ApiResponse<RenderedPageResponse>(false, null, "ไม่พบหน้าเว็บ"));
        return Ok(new ApiResponse<RenderedPageResponse>(true, result));
    }

    // ===== Product Catalog (public) =====

    [HttpGet("products")]
    public async Task<ActionResult<ApiResponse<PagedResponse<SiteProductResponse>>>> Products(
        Guid companyId, Guid siteId,
        [FromQuery] Guid? categoryId = null, [FromQuery] string? search = null,
        [FromQuery] bool? featured = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _commerceService.GetProductsAsync(companyId, siteId, categoryId, search, featured, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<SiteProductResponse>>(true, result));
    }

    [HttpGet("products/{slug}")]
    public async Task<ActionResult<ApiResponse<SiteProductResponse>>> ProductBySlug(Guid companyId, Guid siteId, string slug)
    {
        var result = await _commerceService.GetProductBySlugAsync(companyId, siteId, slug);
        if (result == null) return NotFound(new ApiResponse<SiteProductResponse>(false, null, "ไม่พบสินค้า"));
        return Ok(new ApiResponse<SiteProductResponse>(true, result));
    }

    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<List<CategoryResponse>>>> Categories(Guid companyId, Guid siteId)
    {
        var result = await _commerceService.GetCategoriesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<CategoryResponse>>(true, result));
    }

    // ===== Booking (public) =====

    [HttpGet("booking-services")]
    public async Task<ActionResult<ApiResponse<List<BookingServiceResponse>>>> BookingServices(Guid companyId, Guid siteId)
    {
        var result = await _bookingService.GetServicesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<BookingServiceResponse>>(true, result));
    }

    [HttpPost("booking-services/available-slots")]
    public async Task<ActionResult<ApiResponse<List<AvailableSlotResponse>>>> AvailableSlots(
        Guid companyId, Guid siteId, [FromBody] AvailableSlotsRequest request)
    {
        var result = await _bookingService.GetAvailableSlotsAsync(companyId, siteId, request);
        return Ok(new ApiResponse<List<AvailableSlotResponse>>(true, result));
    }

    // ===== SEO =====

    [HttpGet("sitemap.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> Sitemap(Guid companyId, Guid siteId)
    {
        var xml = await _contentService.GenerateSitemapXmlAsync(companyId, siteId);
        return Content(xml, "application/xml");
    }

    [HttpGet("pages/{slug}/json-ld")]
    [Produces("application/ld+json")]
    public async Task<IActionResult> PageJsonLd(
        Guid companyId, Guid siteId, string slug, [FromQuery] string? lang = null)
    {
        var page = await _contentService.GetPageBySlugAsync(companyId, siteId, slug, lang);
        if (page == null) return NotFound();
        var json = await _contentService.GenerateJsonLdAsync(companyId, siteId, page.Id);
        return Content(json, "application/ld+json");
    }
}
