using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites")]
[Authorize]
public class CmsSiteController : ControllerBase
{
    private readonly ICmsSiteService _siteService;

    public CmsSiteController(ICmsSiteService siteService)
    {
        _siteService = siteService;
    }

    // ===== Sites =====

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> Create(Guid companyId, [FromBody] CreateSiteRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.CreateSiteAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<SiteResponse>(true, result, "สร้างเว็บไซต์สำเร็จ"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<SiteListResponse>>>> GetAll(
        Guid companyId, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _siteService.GetSitesAsync(companyId, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<SiteListResponse>>(true, result));
    }

    [HttpGet("{siteId:guid}")]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> Get(Guid companyId, Guid siteId)
    {
        var result = await _siteService.GetSiteAsync(companyId, siteId);
        if (result == null) return NotFound(new ApiResponse<SiteResponse>(false, null, "ไม่พบเว็บไซต์"));
        return Ok(new ApiResponse<SiteResponse>(true, result));
    }

    [HttpPut("{siteId:guid}")]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> Update(Guid companyId, Guid siteId, [FromBody] UpdateSiteRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.UpdateSiteAsync(companyId, siteId, request, userId);
        return Ok(new ApiResponse<SiteResponse>(true, result, "อัปเดตเว็บไซต์สำเร็จ"));
    }

    [HttpDelete("{siteId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid siteId)
    {
        var result = await _siteService.DeleteSiteAsync(companyId, siteId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบเว็บไซต์"));
        return Ok(new ApiResponse<bool>(true, true, "ลบเว็บไซต์สำเร็จ"));
    }

    [HttpPost("{siteId:guid}/publish")]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> Publish(Guid companyId, Guid siteId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.PublishSiteAsync(companyId, siteId, userId);
        return Ok(new ApiResponse<SiteResponse>(true, result, "เผยแพร่เว็บไซต์สำเร็จ"));
    }

    // ===== Domains =====

    [HttpPost("{siteId:guid}/domains")]
    public async Task<ActionResult<ApiResponse<DomainResponse>>> AddDomain(Guid companyId, Guid siteId, [FromBody] CreateDomainRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.AddDomainAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<DomainResponse>(true, result, "เพิ่มโดเมนสำเร็จ"));
    }

    [HttpGet("{siteId:guid}/domains")]
    public async Task<ActionResult<ApiResponse<List<DomainResponse>>>> GetDomains(Guid companyId, Guid siteId)
    {
        var result = await _siteService.GetDomainsAsync(companyId, siteId);
        return Ok(new ApiResponse<List<DomainResponse>>(true, result));
    }

    [HttpDelete("{siteId:guid}/domains/{domainId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteDomain(Guid companyId, Guid siteId, Guid domainId)
    {
        var result = await _siteService.DeleteDomainAsync(companyId, siteId, domainId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบโดเมน"));
        return Ok(new ApiResponse<bool>(true, true, "ลบโดเมนสำเร็จ"));
    }

    [HttpPost("{siteId:guid}/domains/{domainId:guid}/verify")]
    public async Task<ActionResult<ApiResponse<DomainResponse>>> VerifyDomain(Guid companyId, Guid siteId, Guid domainId)
    {
        var result = await _siteService.VerifyDomainAsync(companyId, siteId, domainId);
        return Ok(new ApiResponse<DomainResponse>(true, result, "ตรวจสอบโดเมนสำเร็จ"));
    }

    // ===== Themes =====

    [HttpPost("~/api/companies/{companyId:guid}/cms/themes")]
    public async Task<ActionResult<ApiResponse<ThemeResponse>>> CreateTheme(Guid companyId, [FromBody] CreateThemeRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.CreateThemeAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<ThemeResponse>(true, result, "สร้างธีมสำเร็จ"));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/themes")]
    public async Task<ActionResult<ApiResponse<List<ThemeResponse>>>> GetThemes(Guid companyId)
    {
        var result = await _siteService.GetThemesAsync(companyId);
        return Ok(new ApiResponse<List<ThemeResponse>>(true, result));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/themes/{themeId:guid}")]
    public async Task<ActionResult<ApiResponse<ThemeResponse>>> GetTheme(Guid companyId, Guid themeId)
    {
        var result = await _siteService.GetThemeAsync(companyId, themeId);
        if (result == null) return NotFound(new ApiResponse<ThemeResponse>(false, null, "ไม่พบธีม"));
        return Ok(new ApiResponse<ThemeResponse>(true, result));
    }

    [HttpPut("~/api/companies/{companyId:guid}/cms/themes/{themeId:guid}")]
    public async Task<ActionResult<ApiResponse<ThemeResponse>>> UpdateTheme(Guid companyId, Guid themeId, [FromBody] UpdateThemeRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.UpdateThemeAsync(companyId, themeId, request, userId);
        return Ok(new ApiResponse<ThemeResponse>(true, result, "อัปเดตธีมสำเร็จ"));
    }

    [HttpDelete("~/api/companies/{companyId:guid}/cms/themes/{themeId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTheme(Guid companyId, Guid themeId)
    {
        var result = await _siteService.DeleteThemeAsync(companyId, themeId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบธีม"));
        return Ok(new ApiResponse<bool>(true, true, "ลบธีมสำเร็จ"));
    }

    // ===== Locales =====

    [HttpPost("{siteId:guid}/locales")]
    public async Task<ActionResult<ApiResponse<LocaleResponse>>> AddLocale(Guid companyId, Guid siteId, [FromBody] CreateLocaleRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.AddLocaleAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<LocaleResponse>(true, result, "เพิ่มภาษาสำเร็จ"));
    }

    [HttpGet("{siteId:guid}/locales")]
    public async Task<ActionResult<ApiResponse<List<LocaleResponse>>>> GetLocales(Guid companyId, Guid siteId)
    {
        var result = await _siteService.GetLocalesAsync(companyId, siteId);
        return Ok(new ApiResponse<List<LocaleResponse>>(true, result));
    }

    [HttpDelete("{siteId:guid}/locales/{localeId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteLocale(Guid companyId, Guid siteId, Guid localeId)
    {
        var result = await _siteService.DeleteLocaleAsync(companyId, siteId, localeId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบภาษา"));
        return Ok(new ApiResponse<bool>(true, true, "ลบภาษาสำเร็จ"));
    }

    // ===== Staff Access (RBAC) =====

    [HttpPost("{siteId:guid}/staff-access")]
    public async Task<ActionResult<ApiResponse<StaffAccessResponse>>> GrantAccess(Guid companyId, Guid siteId, [FromBody] CreateStaffAccessRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _siteService.GrantStaffAccessAsync(companyId, siteId, request, userId);
        return StatusCode(201, new ApiResponse<StaffAccessResponse>(true, result, "เพิ่มสิทธิ์เข้าถึงสำเร็จ"));
    }

    [HttpGet("{siteId:guid}/staff-access")]
    public async Task<ActionResult<ApiResponse<List<StaffAccessResponse>>>> GetStaffAccess(Guid companyId, Guid siteId)
    {
        var result = await _siteService.GetStaffAccessAsync(companyId, siteId);
        return Ok(new ApiResponse<List<StaffAccessResponse>>(true, result));
    }

    [HttpDelete("{siteId:guid}/staff-access/{accessId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> RevokeAccess(Guid companyId, Guid siteId, Guid accessId)
    {
        var result = await _siteService.RevokeStaffAccessAsync(companyId, siteId, accessId);
        if (!result) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบสิทธิ์เข้าถึง"));
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสิทธิ์เข้าถึงสำเร็จ"));
    }

    // ===== Public Resolution (no auth required) =====

    [HttpGet("~/api/cms/resolve/subdomain/{subdomain}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> ResolveBySubdomain(string subdomain)
    {
        var result = await _siteService.ResolveSiteBySubdomainAsync(subdomain);
        if (result == null) return NotFound(new ApiResponse<SiteResponse>(false, null, "ไม่พบเว็บไซต์"));
        return Ok(new ApiResponse<SiteResponse>(true, result));
    }

    [HttpGet("~/api/cms/resolve/domain/{domain}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> ResolveByDomain(string domain)
    {
        var result = await _siteService.ResolveSiteByDomainAsync(domain);
        if (result == null) return NotFound(new ApiResponse<SiteResponse>(false, null, "ไม่พบเว็บไซต์"));
        return Ok(new ApiResponse<SiteResponse>(true, result));
    }
}
