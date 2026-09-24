using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Settings;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly ICompanyService _companyService;
    private readonly IPermissionService? _permissions;

    public SettingsController(ISettingsService settingsService, ICompanyService companyService,
        IPermissionService? permissions = null)
    {
        _settingsService = settingsService;
        _companyService = companyService;
        _permissions = permissions;
    }

    // ===== Public: Landing Page Services =====

    [HttpGet("/api/landing/services")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LandingServicesResponse>>> GetLandingServices()
    {
        var result = await _settingsService.GetLandingServicesAsync();
        return Ok(new ApiResponse<LandingServicesResponse>(true, result));
    }

    // ===== Company Settings =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<CompanySettingsResponse>>> GetSettings(Guid companyId)
    {
        var result = await _settingsService.GetSettingsAsync(companyId);
        // W-C7: บอกหน้าเว็บก่อนให้กรอกว่าบันทึกได้ไหม + ข้อความเดียวกับที่ PUT จะตอบ (server ตัดสิน · หน้าแสดง)
        if (OwnerActionGuard.IsApiKeyRequest(HttpContext))
            result = result with { CanEdit = false, EditDeniedMessage = OwnerActionGuard.DeniedMessage("บันทึกการตั้งค่าบริษัท") };
        else if (_permissions != null)
        {
            var can = await _permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User),
                PermissionKeys.CompanySettingsEdit);
            result = result with
            {
                CanEdit = can,
                EditDeniedMessage = can ? null : PermissionKeys.DeniedMessage(PermissionKeys.CompanySettingsEdit),
            };
        }
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result));
    }

    /// <summary>บันทึกค่าตั้งบริษัท — ต้องมีสิทธิ์ <c>CompanySettings.Edit</c> (เจ้าของผ่านอัตโนมัติ)
    ///
    /// <para>ฝ่ายค้านรอบ 193 C1: เดิมไม่มีด่านเลย (มีแค่ <c>[Authorize]</c> = ล็อกอินไหม) ⇒ สมาชิกคนไหน/คีย์ไหนก็เปิด
    /// <c>EnableApiAccess</c> · ปิดการอนุมัติ · เปลี่ยนนโยบายภาษีได้ · และสวิตช์ที่คุม "คีย์" (<c>EnableApiAccess</c> ·
    /// <c>MaxApiKeys</c>) ห้ามถูกเปลี่ยนด้วยคีย์เอง แม้คีย์นั้นสวมเป็นเจ้าของ — ส่งค่าเดิมกลับมา (GET แล้ว PUT ทั้งก้อน) ยังผ่าน</para></summary>
    [HttpPut]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("บันทึกการตั้งค่าบริษัท")]
    public async Task<ActionResult<ApiResponse<CompanySettingsResponse>>> UpdateSettings(Guid companyId, [FromBody] UpdateCompanySettingsRequest request)
    {
        // ฝ่ายค้านรอบ 193 W-C2: เดิมกันคีย์เฉพาะ EnableApiAccess/MaxApiKeys ⇒ คีย์ที่ถือตัวตนเจ้าของยังปิดการอนุมัติ/SoD/
        // เปลี่ยนนโยบายภาษีและข้อมูลรับรอง e-Tax ได้ (RequirePermission ปล่อยเจ้าของผ่านเสมอ) ⇒ ทั้งเส้นเป็นงานของคน
        var result = await _settingsService.UpdateSettingsAsync(companyId, request);
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result, "อัพเดทการตั้งค่าสำเร็จ"));
    }

    // ===== Logo Upload =====

    [HttpPost("logo")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("อัปโหลดโลโก้บริษัท")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB max
    public async Task<ActionResult<ApiResponse<CompanySettingsResponse>>> UploadLogo(Guid companyId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาเลือกไฟล์โลโก้"));

        using var stream = file.OpenReadStream();
        var result = await _settingsService.UploadLogoAsync(companyId, stream, file.FileName, file.ContentType);
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result, "อัพโหลดโลโก้สำเร็จ"));
    }

    [HttpDelete("logo")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ลบโลโก้บริษัท")]
    public async Task<IActionResult> DeleteLogo(Guid companyId)
    {
        await _settingsService.DeleteLogoAsync(companyId);
        return NoContent();
    }

    // ===== Company Stamp (ตราประทับบริษัท) Upload =====

    /// <summary>
    /// อัปโหลดตราประทับบริษัท — **endpoint เดียวของ route นี้**
    ///
    /// ⚠️ ที่มา (ฟีเจอร์ซ้อนที่ทำให้ระบบพัง): คอนโทรลเลอร์นี้เคยมี
    /// `[HttpPost("stamp")]` **สองเมธอด** — ตัวหนึ่งเขียนเพิ่มมาแก้ปัญหา
    /// "หน้า document-templates ของลูกค้ายิงไป endpoint แอดมินแล้วโดน 403"
    /// (เข้มความปลอดภัย กัน SVG แต่**ไม่ persist** StampPath ⇒ PDF ไม่เห็นตรา)
    /// อีกตัวเป็นของเดิมของหน้า settings (persist ครบ แต่**ยังรับ SVG** ซึ่ง
    /// ฝัง &lt;script&gt; ได้ = stored XSS). route ชนกัน ⇒ ASP.NET Core โยน
    /// AmbiguousMatchException = **อัปโหลดตราประทับได้ 500 ทุกครั้ง**
    /// จึงยุบเหลือตัวเดียวที่ได้ทั้งสองคุณสมบัติ: persist ผ่าน SettingsService
    /// (ตัวถือกฎ validation ที่เดียว — กัน SVG แล้ว) + ตอบครบสัญญาของ**ทั้งสอง
    /// หน้า**ที่เรียก: settings.html อ่าน `stampUrl` · document-templates.html
    /// อ่าน `url`/`relativeUrl`
    /// </summary>
    [HttpPost("stamp")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("อัปโหลดตราประทับบริษัท")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB max
    public async Task<ActionResult<ApiResponse<object>>> UploadStamp(Guid companyId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์ตราประทับ"));

        using var stream = file.OpenReadStream();
        var result = await _settingsService.UploadStampAsync(companyId, stream, file.FileName, file.ContentType);
        return Ok(new ApiResponse<object>(true, new
        {
            url = result.StampUrl,
            relativeUrl = result.StampUrl,
            stampUrl = result.StampUrl,
        }, "อัพโหลดตราประทับสำเร็จ"));
    }

    [HttpDelete("stamp")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ลบตราประทับบริษัท")]
    public async Task<IActionResult> DeleteStamp(Guid companyId)
    {
        await _settingsService.DeleteStampAsync(companyId);
        return NoContent();
    }

    // ===== Number Series =====

    [HttpGet("number-series")]
    public async Task<ActionResult<ApiResponse<List<NumberSeriesResponse>>>> GetNumberSeries(Guid companyId)
    {
        var result = await _settingsService.GetNumberSeriesAsync(companyId);
        return Ok(new ApiResponse<List<NumberSeriesResponse>>(true, result));
    }

    [HttpPost("number-series")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งตัวย่อเลขที่เอกสาร")]
    public async Task<ActionResult<ApiResponse<NumberSeriesResponse>>> CreateNumberSeries(Guid companyId, [FromBody] CreateNumberSeriesRequest request)
    {
        var result = await _settingsService.CreateNumberSeriesAsync(companyId, request);
        return StatusCode(201, new ApiResponse<NumberSeriesResponse>(true, result, "สร้าง number series สำเร็จ"));
    }

    [HttpPut("number-series/{seriesId:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("แก้ตัวย่อเลขที่เอกสาร")]
    public async Task<ActionResult<ApiResponse<NumberSeriesResponse>>> UpdateNumberSeries(Guid companyId, Guid seriesId, [FromBody] UpdateNumberSeriesRequest request)
    {
        var result = await _settingsService.UpdateNumberSeriesAsync(companyId, seriesId, request);
        return Ok(new ApiResponse<NumberSeriesResponse>(true, result));
    }

    // ===== API Keys =====

    [HttpGet("api-keys")]
    public async Task<ActionResult<ApiResponse<List<ApiKeyResponse>>>> GetApiKeys(Guid companyId)
    {
        var result = await _settingsService.GetApiKeysAsync(companyId);
        return Ok(new ApiResponse<List<ApiKeyResponse>>(true, result));
    }

    [HttpPost("api-keys")]
    public async Task<ActionResult<ApiResponse<ApiKeyCreatedResponse>>> CreateApiKey(Guid companyId, [FromBody] CreateApiKeyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        // API key creation grants full programmatic access — restrict to Owner.
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        var result = await _settingsService.CreateApiKeyAsync(companyId, userId, request);
        return StatusCode(201, new ApiResponse<ApiKeyCreatedResponse>(true, result, "สร้าง API key สำเร็จ (เก็บ key นี้ไว้ จะแสดงครั้งเดียว)"));
    }

    [HttpDelete("api-keys/{apiKeyId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RevokeApiKey(Guid companyId, Guid apiKeyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        await _settingsService.RevokeApiKeyAsync(companyId, apiKeyId);
        return NoContent();
    }
}
