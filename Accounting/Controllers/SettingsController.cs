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

    /// <summary>
    /// อัปโหลด "ตราประทับบริษัท" สำหรับเทมเพลตเอกสาร — คืน URL ให้หน้าเทมเพลตเก็บ
    ///
    /// ⚠️ ทำไมต้องมี endpoint นี้: หน้า `/pages/document-templates.html` (หน้าของ
    /// **ลูกค้า**) เคยยิงไปที่ `/api/admin/upload-image` ซึ่งอยู่ใต้ `AdminController`
    /// ที่บังคับ `[Authorize(Roles = "SystemAdmin")]` ⇒ ลูกค้าอัปโหลดตราประทับ
    /// **ไม่ได้เลย (403)** ทั้งที่เป็นฟีเจอร์ของเขาเอง — คนละคลาสกับปัญหาความปลอดภัย
    /// แต่รากเดียวกัน: หน้าจอฝั่งลูกค้ากับฝั่งแพลตฟอร์มถูกปนกัน
    ///
    /// เก็บแยกโฟลเดอร์ต่อบริษัท (tenant isolation) และไม่รับ SVG ด้วยเหตุผลเดียวกับ
    /// โลโก้แบรนด์ (SVG ฝัง &lt;script&gt; = stored XSS เพราะไฟล์ถูก serve จาก origin
    /// เดียวกับแอปที่เก็บ JWT ไว้ใน localStorage)
    /// </summary>
    [HttpPost("stamp")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> UploadStamp(
        Guid companyId, IFormFile file,
        [FromServices] IImageProcessingService images,
        [FromServices] IWebHostEnvironment env)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "กรุณาเลือกไฟล์ตราประทับ"));

        var allowed = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" };
        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        var allowedExt = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        if (!allowed.Contains((file.ContentType ?? "").ToLowerInvariant()) || !allowedExt.Contains(ext))
            return BadRequest(new ApiResponse<object>(false, null,
                "รองรับเฉพาะไฟล์ PNG, JPEG, GIF, WebP เท่านั้น (ไม่รับ SVG ด้วยเหตุผลด้านความปลอดภัย)"));

        var webRoot = env.WebRootPath
            ?? Path.Combine(env.ContentRootPath ?? Directory.GetCurrentDirectory(), "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "stamps", companyId.ToString());
        var web = $"/uploads/stamps/{companyId}";
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? $"stamp{ext}" : file.FileName;

        try
        {
            await using var s = file.OpenReadStream();
            // profile Logo = ไม่ crop สี่เหลี่ยม (ตราประทับโปร่งใสต้องคงสัดส่วนเดิม)
            var processed = await images.ProcessAndSaveAsync(
                s, file.ContentType!, fileName, dir, web, ImageProfile.Logo);
            return Ok(new ApiResponse<object>(true, new { url = processed.RelativeUrl },
                "อัพโหลดตราประทับสำเร็จ"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null,
                $"ระบบไม่มีสิทธิ์เขียนไฟล์ลงโฟลเดอร์ uploads ({dir}) — โปรดติดต่อผู้ดูแลระบบ: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, $"บันทึกไฟล์ไม่สำเร็จ: {ex.Message}"));
        }
    }

    [HttpDelete("logo")]
    public async Task<IActionResult> DeleteLogo(Guid companyId)
    {
        await _settingsService.DeleteLogoAsync(companyId);
        return NoContent();
    }

    // ===== Company Stamp (ตราประทับบริษัท) Upload =====

    [HttpPost("stamp")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB max
    public async Task<ActionResult<ApiResponse<CompanySettingsResponse>>> UploadStamp(Guid companyId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาเลือกไฟล์ตราประทับ"));

        using var stream = file.OpenReadStream();
        var result = await _settingsService.UploadStampAsync(companyId, stream, file.FileName, file.ContentType);
        return Ok(new ApiResponse<CompanySettingsResponse>(true, result, "อัพโหลดตราประทับสำเร็จ"));
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
