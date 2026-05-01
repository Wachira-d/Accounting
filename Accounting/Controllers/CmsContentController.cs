using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/content")]
[Authorize]
public class CmsContentController : ControllerBase
{
    private readonly ICmsContentService _contentService;

    public CmsContentController(ICmsContentService contentService)
    {
        _contentService = contentService;
    }

    // ===== Pages =====

    [HttpPost("pages")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor, SiteStaffRole.ContentWriter)]
    public async Task<ActionResult<ApiResponse<PageResponse>>> CreatePage(Guid companyId, Guid siteId, [FromBody] CreatePageRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.CreatePageAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<PageResponse>(true, result, "สร้างหน้าเว็บสำเร็จ"));
    }

    [HttpGet("pages")]
    public async Task<ActionResult<ApiResponse<PagedResponse<PageListResponse>>>> GetPages(
        Guid companyId, Guid siteId, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _contentService.GetPagesAsync(companyId, siteId, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<PageListResponse>>(true, result));
    }

    [HttpGet("pages/{pageId:guid}")]
    public async Task<ActionResult<ApiResponse<PageResponse>>> GetPage(Guid companyId, Guid siteId, Guid pageId)
    {
        var result = await _contentService.GetPageAsync(companyId, siteId, pageId);
        if (result == null) return NotFound(new ApiResponse<PageResponse>(false, null, "ไม่พบหน้าเว็บ"));
        return Ok(new ApiResponse<PageResponse>(true, result));
    }

    [HttpGet("pages/by-slug/{slug}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PageResponse>>> GetPageBySlug(
        Guid companyId, Guid siteId, string slug, [FromQuery] string? lang = null)
    {
        var result = await _contentService.GetPageBySlugAsync(companyId, siteId, slug, lang);
        if (result == null) return NotFound(new ApiResponse<PageResponse>(false, null, "ไม่พบหน้าเว็บ"));
        return Ok(new ApiResponse<PageResponse>(true, result));
    }

    [HttpPut("pages/{pageId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor, SiteStaffRole.ContentWriter)]
    public async Task<ActionResult<ApiResponse<PageResponse>>> UpdatePage(Guid companyId, Guid siteId, Guid pageId, [FromBody] UpdatePageRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.UpdatePageAsync(companyId, siteId, pageId, request, userId);
        return Ok(new ApiResponse<PageResponse>(true, result, "อัปเดตหน้าเว็บสำเร็จ"));
    }

    [HttpDelete("pages/{pageId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<bool>>> DeletePage(Guid companyId, Guid siteId, Guid pageId)
    {
        var result = await _contentService.DeletePageAsync(companyId, siteId, pageId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบหน้าเว็บ"));
        return Ok(new ApiResponse<bool>(true, true, "ลบหน้าเว็บสำเร็จ"));
    }

    [HttpPost("pages/{pageId:guid}/publish")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.Editor)]
    public async Task<ActionResult<ApiResponse<PageResponse>>> PublishPage(Guid companyId, Guid siteId, Guid pageId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.PublishPageAsync(companyId, siteId, pageId, userId);
        return Ok(new ApiResponse<PageResponse>(true, result, "เผยแพร่หน้าเว็บสำเร็จ"));
    }

    // ===== Page Translations =====

    [HttpPut("pages/{pageId:guid}/translations")]
    public async Task<ActionResult<ApiResponse<PageTranslationResponse>>> UpsertTranslation(
        Guid companyId, Guid siteId, Guid pageId, [FromBody] UpsertPageTranslationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.UpsertPageTranslationAsync(companyId, siteId, pageId, request, userId);
        return Ok(new ApiResponse<PageTranslationResponse>(true, result, "บันทึกคำแปลสำเร็จ"));
    }

    [HttpDelete("pages/{pageId:guid}/translations/{languageCode}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTranslation(Guid companyId, Guid siteId, Guid pageId, string languageCode)
    {
        var result = await _contentService.DeletePageTranslationAsync(companyId, siteId, pageId, languageCode);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบคำแปล"));
        return Ok(new ApiResponse<bool>(true, true, "ลบคำแปลสำเร็จ"));
    }

    // ===== Blocks =====

    [HttpPost("pages/{pageId:guid}/blocks")]
    public async Task<ActionResult<ApiResponse<BlockResponse>>> CreateBlock(
        Guid companyId, Guid siteId, Guid pageId, [FromBody] CreateBlockRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.CreateBlockAsync(companyId, siteId, pageId, request, userId);
        return StatusCode(201, new ApiResponse<BlockResponse>(true, result, "เพิ่มบล็อกสำเร็จ"));
    }

    [HttpPut("pages/{pageId:guid}/blocks/{blockId:guid}")]
    public async Task<ActionResult<ApiResponse<BlockResponse>>> UpdateBlock(
        Guid companyId, Guid siteId, Guid pageId, Guid blockId, [FromBody] UpdateBlockRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.UpdateBlockAsync(companyId, siteId, pageId, blockId, request, userId);
        return Ok(new ApiResponse<BlockResponse>(true, result, "อัปเดตบล็อกสำเร็จ"));
    }

    [HttpDelete("pages/{pageId:guid}/blocks/{blockId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteBlock(Guid companyId, Guid siteId, Guid pageId, Guid blockId)
    {
        var result = await _contentService.DeleteBlockAsync(companyId, siteId, pageId, blockId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบบล็อก"));
        return Ok(new ApiResponse<bool>(true, true, "ลบบล็อกสำเร็จ"));
    }

    [HttpPost("pages/{pageId:guid}/blocks/reorder")]
    public async Task<ActionResult<ApiResponse<bool>>> ReorderBlocks(
        Guid companyId, Guid siteId, Guid pageId, [FromBody] ReorderBlocksRequest request)
    {
        var result = await _contentService.ReorderBlocksAsync(companyId, siteId, pageId, request);
        return Ok(new ApiResponse<bool>(true, result, "จัดเรียงบล็อกสำเร็จ"));
    }

    // ===== Navigation =====

    [HttpPost("navigations")]
    public async Task<ActionResult<ApiResponse<NavigationResponse>>> CreateNavigation(
        Guid companyId, Guid siteId, [FromBody] CreateNavigationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.CreateNavigationAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<NavigationResponse>(true, result, "สร้างเมนูสำเร็จ"));
    }

    [HttpGet("navigations")]
    public async Task<ActionResult<ApiResponse<List<NavigationResponse>>>> GetNavigations(Guid companyId, Guid siteId)
    {
        var result = await _contentService.GetNavigationsAsync(companyId, siteId);
        return Ok(new ApiResponse<List<NavigationResponse>>(true, result));
    }

    [HttpGet("navigations/{navId:guid}")]
    public async Task<ActionResult<ApiResponse<NavigationResponse>>> GetNavigation(Guid companyId, Guid siteId, Guid navId)
    {
        var result = await _contentService.GetNavigationAsync(companyId, siteId, navId);
        if (result == null) return NotFound(new ApiResponse<NavigationResponse>(false, null, "ไม่พบเมนู"));
        return Ok(new ApiResponse<NavigationResponse>(true, result));
    }

    [HttpDelete("navigations/{navId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteNavigation(Guid companyId, Guid siteId, Guid navId)
    {
        var result = await _contentService.DeleteNavigationAsync(companyId, siteId, navId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบเมนู"));
        return Ok(new ApiResponse<bool>(true, true, "ลบเมนูสำเร็จ"));
    }

    [HttpPost("navigations/{navId:guid}/items")]
    public async Task<ActionResult<ApiResponse<MenuItemResponse>>> AddMenuItem(
        Guid companyId, Guid siteId, Guid navId, [FromBody] CreateMenuItemRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.AddMenuItemAsync(companyId, siteId, navId, request, userId);
        return StatusCode(201, new ApiResponse<MenuItemResponse>(true, result, "เพิ่มรายการเมนูสำเร็จ"));
    }

    [HttpDelete("navigations/{navId:guid}/items/{itemId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteMenuItem(Guid companyId, Guid siteId, Guid navId, Guid itemId)
    {
        var result = await _contentService.DeleteMenuItemAsync(companyId, siteId, navId, itemId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบรายการเมนู"));
        return Ok(new ApiResponse<bool>(true, true, "ลบรายการเมนูสำเร็จ"));
    }

    // ===== Media =====

    [HttpPost("media")]
    [RequestSizeLimit(52_428_800)]
    public async Task<ActionResult<ApiResponse<MediaResponse>>> UploadMedia(
        Guid companyId, Guid siteId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<MediaResponse>(false, null, "ไม่พบไฟล์"));

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        using var stream = file.OpenReadStream();
        var result = await _contentService.UploadMediaAsync(companyId, siteId, file.FileName, file.ContentType, stream, userId);
        return StatusCode(201, new ApiResponse<MediaResponse>(true, result, "อัปโหลดไฟล์สำเร็จ"));
    }

    [HttpGet("media")]
    public async Task<ActionResult<ApiResponse<PagedResponse<MediaResponse>>>> GetMedia(
        Guid companyId, Guid siteId, [FromQuery] string? folder = null, [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _contentService.GetMediaAsync(companyId, siteId, folder, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<MediaResponse>>(true, result));
    }

    [HttpPut("media/{mediaId:guid}")]
    public async Task<ActionResult<ApiResponse<MediaResponse>>> UpdateMedia(
        Guid companyId, Guid siteId, Guid mediaId, [FromBody] UpdateMediaRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.UpdateMediaAsync(companyId, siteId, mediaId, request, userId);
        return Ok(new ApiResponse<MediaResponse>(true, result, "อัปเดตไฟล์สำเร็จ"));
    }

    [HttpDelete("media/{mediaId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteMedia(Guid companyId, Guid siteId, Guid mediaId)
    {
        var result = await _contentService.DeleteMediaAsync(companyId, siteId, mediaId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบไฟล์"));
        return Ok(new ApiResponse<bool>(true, true, "ลบไฟล์สำเร็จ"));
    }

    // ===== SEO Redirects =====

    [HttpPost("seo-redirects")]
    public async Task<ActionResult<ApiResponse<SeoRedirectResponse>>> CreateRedirect(
        Guid companyId, Guid siteId, [FromBody] CreateSeoRedirectRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _contentService.CreateSeoRedirectAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<SeoRedirectResponse>(true, result, "สร้าง Redirect สำเร็จ"));
    }

    [HttpGet("seo-redirects")]
    public async Task<ActionResult<ApiResponse<List<SeoRedirectResponse>>>> GetRedirects(Guid companyId, Guid siteId)
    {
        var result = await _contentService.GetSeoRedirectsAsync(companyId, siteId);
        return Ok(new ApiResponse<List<SeoRedirectResponse>>(true, result));
    }

    [HttpDelete("seo-redirects/{redirectId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteRedirect(Guid companyId, Guid siteId, Guid redirectId)
    {
        var result = await _contentService.DeleteSeoRedirectAsync(companyId, siteId, redirectId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบ Redirect"));
        return Ok(new ApiResponse<bool>(true, true, "ลบ Redirect สำเร็จ"));
    }

    // ===== Block Templates (global, no site context) =====

    [HttpGet("~/api/cms/block-templates")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<BlockTemplateResponse>>>> GetBlockTemplates()
    {
        var result = await _contentService.GetBlockTemplatesAsync();
        return Ok(new ApiResponse<List<BlockTemplateResponse>>(true, result));
    }

    // ===== Sitemap & JSON-LD (public) =====

    [HttpGet("sitemap.xml")]
    [AllowAnonymous]
    [Produces("application/xml")]
    public async Task<IActionResult> Sitemap(Guid companyId, Guid siteId)
    {
        var xml = await _contentService.GenerateSitemapXmlAsync(companyId, siteId);
        return Content(xml, "application/xml");
    }

    [HttpGet("pages/{pageId:guid}/json-ld")]
    [AllowAnonymous]
    [Produces("application/ld+json")]
    public async Task<IActionResult> JsonLd(Guid companyId, Guid siteId, Guid pageId)
    {
        var json = await _contentService.GenerateJsonLdAsync(companyId, siteId, pageId);
        return Content(json, "application/ld+json");
    }
}
