using Accounting.Helpers;
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

    public SettingsController(ISettingsService settingsService, ICompanyService companyService)
    {
        _settingsService = settingsService;
        _companyService = companyService;
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
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<CompanySettingsResponse>>> UpdateSettings(Guid companyId, [FromBody] UpdateCompanySettingsRequest request)
    {
        var result = await _settingsService.UpdateSettingsAsync(companyId, request);
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result, "อัพเดทการตั้งค่าสำเร็จ"));
    }

    // ===== Logo Upload =====

    [HttpPost("logo")]
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
    public async Task<ActionResult<ApiResponse<NumberSeriesResponse>>> CreateNumberSeries(Guid companyId, [FromBody] CreateNumberSeriesRequest request)
    {
        var result = await _settingsService.CreateNumberSeriesAsync(companyId, request);
        return StatusCode(201, new ApiResponse<NumberSeriesResponse>(true, result, "สร้าง number series สำเร็จ"));
    }

    [HttpPut("number-series/{seriesId:guid}")]
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
