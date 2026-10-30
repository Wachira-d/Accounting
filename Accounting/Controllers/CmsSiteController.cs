using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
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
    private readonly ICmsQuotaService _quotaService;
    private readonly ICmsCommerceService _commerceService;
    private readonly ICmsBookingService _bookingService;
    private readonly CmsLeadService _leadService;

    public CmsSiteController(ICmsSiteService siteService, ICmsQuotaService quotaService,
        ICmsCommerceService commerceService, ICmsBookingService bookingService, CmsLeadService leadService)
    {
        _siteService = siteService;
        _quotaService = quotaService;
        _commerceService = commerceService;
        _bookingService = bookingService;
        _leadService = leadService;
    }

    // ===== มุมมอง "ทุกไซต์" (R-45) — endpoint ระดับบริษัท · แบ่งหน้าจริง =====
    // ไม่มี {siteId} ใน route ⇒ CmsRbacMiddleware ไม่ตรวจ — ด่านอยู่ที่นี่: เว็บที่เห็น = Helpers/CmsSiteScope (เจ้าของ = ทุกเว็บ ·
    // คนอื่น = เว็บที่มี SiteStaffAccess ใช้งานอยู่ และบทบาทตรงกับ endpoint รายเว็บเดิม) · service กรอง CompanyId + siteIds เสมอ

    private bool IsCompanyOwner() => HttpContext.Items.TryGetValue("UserRole", out var r) && r is UserRole ur && ur == UserRole.Owner;

    [HttpGet("~/api/companies/{companyId:guid}/cms/overview/orders")]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderListResponse>>>> OverviewOrders(
        Guid companyId, [FromQuery] string? status = null, [FromQuery] string? search = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var siteIds = await _siteService.GetAccessibleSiteIdsAsync(companyId, JwtHelper.GetUserIdFromClaims(User), IsCompanyOwner(), CmsSiteScope.OrderRoles);
        var result = await _commerceService.GetOrdersAcrossSitesAsync(companyId, siteIds, status, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<OrderListResponse>>(true, result));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/overview/bookings")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BookingListResponse>>>> OverviewBookings(
        Guid companyId, [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var siteIds = await _siteService.GetAccessibleSiteIdsAsync(companyId, JwtHelper.GetUserIdFromClaims(User), IsCompanyOwner(), CmsSiteScope.AnyRole);
        var result = await _bookingService.GetBookingsAcrossSitesAsync(companyId, siteIds, status, fromDate, toDate, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<BookingListResponse>>(true, result));
    }

    [HttpGet("~/api/companies/{companyId:guid}/cms/overview/leads")]
    public async Task<ActionResult<ApiResponse<PagedResponse<LeadListResponse>>>> OverviewLeads(
        Guid companyId, [FromQuery] LeadType? type = null, [FromQuery] LeadStatus? status = null,
        [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var siteIds = await _siteService.GetAccessibleSiteIdsAsync(companyId, JwtHelper.GetUserIdFromClaims(User), IsCompanyOwner(), CmsSiteScope.AnyRole);
        var result = await _leadService.ListAcrossSitesAsync(companyId, siteIds, type, status, search, page, pageSize);
        return Ok(new ApiResponse<PagedResponse<LeadListResponse>>(true, result));
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

    public sealed record LegalPreviewRequest(string? Markdown);

    /// <summary>รอบ 214 (คำตัดสินข้อ 173 B): ตัวอย่างหน้านโยบาย/ข้อกำหนดของร้าน + markdown ที่ใช้จริง (ว่าง = แม่แบบ — ใช้ตั้งต้นการแก้) ·
    /// อ่านอย่างเดียว ไม่เขียนอะไร (เก็บผ่าน PUT {siteId} ตามเดิม)</summary>
    [HttpPost("{siteId:guid}/legal/{kind}/preview")]
    public async Task<ActionResult<ApiResponse<object>>> LegalPreview(Guid companyId, Guid siteId, string kind,
        [FromBody] LegalPreviewRequest request, [FromServices] ICmsRenderingService rendering)
    {
        if ((request?.Markdown?.Length ?? 0) > LegalMarkdown.MaxLength)   // ฝ่ายค้าน S1: เพดานเท่าที่เก็บได้จริง
            return BadRequest(new ApiResponse<object>(false, null, "เนื้อความยาวเกินไป"));
        var r = await rendering.PreviewLegalPageAsync(companyId, siteId, kind, request?.Markdown);
        if (r == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเว็บไซต์หรือชนิดเอกสาร"));
        return Ok(new ApiResponse<object>(true, new { html = r.Value.Html, markdown = r.Value.Markdown, warnings = r.Value.Warnings }));
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

    /// <summary>เติม/เปลี่ยนเทมเพลตให้เว็บที่มีอยู่ — ทางซ่อมเว็บที่สร้างผิดประเภท (ไม่ต้องลบสร้างใหม่)
    ///
    /// <para>เป็น endpoint เดียวในคอนโทรลเลอร์นี้ที่ **ทำลายเนื้อหาของเจ้าของได้** (ติ๊ก "แทนที่หน้าเดิม"
    /// = ปลดหน้าที่ slug ซ้ำออกจากเว็บ) จึงบังคับสิทธิ์ระดับ Admin ของเว็บนั้น ไม่ใช่แค่ `[Authorize]`
    /// ที่ตอบเพียง "ล็อกอินหรือยัง" (เจ้าของบริษัทผ่านอัตโนมัติใน `CmsRbacMiddleware`)</para></summary>
    [HttpPost("{siteId:guid}/apply-template")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<ApplySiteTemplateResponse>>> ApplyTemplate(Guid companyId, Guid siteId, [FromBody] ApplySiteTemplateRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        try
        {
            var result = await _siteService.ApplyTemplateAsync(companyId, siteId, request, userId);
            return Ok(new ApiResponse<ApplySiteTemplateResponse>(true, result, "เติมเทมเพลตสำเร็จ"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ApiResponse<ApplySiteTemplateResponse>(false, null, "ไม่พบเว็บไซต์"));
        }
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

    // ===== Quotas =====

    [HttpGet("{siteId:guid}/quotas")]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<CmsQuotaStatus>>> GetQuotas(Guid companyId, Guid siteId)
    {
        var result = await _quotaService.GetQuotaStatusAsync(companyId, siteId);
        return Ok(new ApiResponse<CmsQuotaStatus>(true, result));
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

    /// <summary>รายการโทเคนที่ใช้ได้ในเนื้อหาหน้าเว็บ — **หน้าเว็บสร้าง UI จากที่นี่**
    /// ห้ามพิมพ์รายชื่อโทเคนซ้ำในหน้าจอ (defect class "สำเนามือฝั่ง JS" ที่เคยเกิดกับ
    /// MENU_SECTIONS/complianceIssues — เพิ่มโทเคนใหม่แล้วหน้าช่วยเหลือตามทันที)</summary>
    [HttpGet("~/api/cms/content-tokens")]
    public ActionResult<ApiResponse<object>> GetContentTokens()
        => Ok(new ApiResponse<object>(true,
            Helpers.CmsContentTokens.Supported.Select(t => new { token = t.Token, label = t.Label })));

    [HttpGet("~/api/cms/resolve/domain/{domain}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteResponse>>> ResolveByDomain(string domain)
    {
        var result = await _siteService.ResolveSiteByDomainAsync(domain);
        if (result == null) return NotFound(new ApiResponse<SiteResponse>(false, null, "ไม่พบเว็บไซต์"));
        return Ok(new ApiResponse<SiteResponse>(true, result));
    }
}
