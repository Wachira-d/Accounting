using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Admin matrix CRUD + per-user preferences + LINE binding for the
/// notification engine. The separate NotificationController in the
/// repo handles the in-app bell (list / mark-read) — these endpoints
/// are for the configuration UI.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/notifications/config")]
[Authorize]
public class NotificationConfigController : ControllerBase
{
    private readonly INotificationConfigService _service;

    public NotificationConfigController(INotificationConfigService service) => _service = service;

    [HttpGet("catalog")]
    public ActionResult<ApiResponse<NotificationCatalogResponse>> GetCatalog()
        => Ok(new ApiResponse<NotificationCatalogResponse>(true, _service.GetCatalog()));

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<List<NotificationSettingDto>>>> GetCompanySettings(Guid companyId)
        => Ok(new ApiResponse<List<NotificationSettingDto>>(true,
            await _service.GetCompanySettingsAsync(companyId)));

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<string>>> BulkUpsertSettings(
        Guid companyId, [FromBody] BulkUpdateNotificationSettingsRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _service.BulkUpsertCompanySettingsAsync(companyId, request, userId);
        return Ok(new ApiResponse<string>(true, null, "บันทึกการตั้งค่าแจ้งเตือนเรียบร้อย"));
    }

    [HttpGet("preferences/me")]
    public async Task<ActionResult<ApiResponse<List<NotificationPreferenceDto>>>> GetMyPreferences(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<List<NotificationPreferenceDto>>(true,
            await _service.GetUserPreferencesAsync(companyId, userId)));
    }

    [HttpPut("preferences/me")]
    public async Task<ActionResult<ApiResponse<string>>> UpsertMyPreferences(
        Guid companyId, [FromBody] BulkUpdateNotificationPreferencesRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _service.BulkUpsertUserPreferencesAsync(companyId, userId, request);
        return Ok(new ApiResponse<string>(true, null, "บันทึกการตั้งค่าส่วนตัวเรียบร้อย"));
    }

    // ===== LINE binding (per-user; company route kept for consistency) =====

    [HttpGet("line-binding/me")]
    public async Task<ActionResult<ApiResponse<LineBindingResponse>>> GetMyLineBinding(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<LineBindingResponse>(true,
            await _service.GetLineBindingAsync(userId)));
    }

    [HttpPut("line-binding/me")]
    public async Task<ActionResult<ApiResponse<LineBindingResponse>>> SetMyLineBinding(
        Guid companyId, [FromBody] LineBindingRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _service.SetLineBindingAsync(userId, request);
        return Ok(new ApiResponse<LineBindingResponse>(true, result, "บันทึกการเชื่อมต่อ LINE สำเร็จ"));
    }

    [HttpDelete("line-binding/me")]
    public async Task<ActionResult<ApiResponse<LineBindingResponse>>> ClearMyLineBinding(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _service.ClearLineBindingAsync(userId);
        return Ok(new ApiResponse<LineBindingResponse>(true, result, "ยกเลิกการเชื่อมต่อ LINE สำเร็จ"));
    }
}
