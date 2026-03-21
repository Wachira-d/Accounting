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

    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
        var result = await _settingsService.CreateApiKeyAsync(companyId, userId, request);
        return StatusCode(201, new ApiResponse<ApiKeyCreatedResponse>(true, result, "สร้าง API key สำเร็จ (เก็บ key นี้ไว้ จะแสดงครั้งเดียว)"));
    }

    [HttpDelete("api-keys/{apiKeyId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> RevokeApiKey(Guid companyId, Guid apiKeyId)
    {
        await _settingsService.RevokeApiKeyAsync(companyId, apiKeyId);
        return NoContent();
    }
}
