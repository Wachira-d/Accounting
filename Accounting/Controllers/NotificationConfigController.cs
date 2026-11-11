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
    /// <summary>รอบ 258 (A-H3): ด่านเขียนของตารางแจ้งเตือนทั้งบริษัท (CompanySettings.Edit) · ไม่มีบริการ = ปฏิเสธ (ปิดทางปลอดภัย)</summary>
    private readonly IPermissionService? _permissions;

    public NotificationConfigController(INotificationConfigService service, IPermissionService? permissions = null)
    {
        _service = service; _permissions = permissions;
    }

    /// <summary>รอบ 258 (A-H3): ด่านเขียนของตารางแจ้งเตือนทั้งบริษัท — ตัวตัดสินเดียว <see cref="SettingsWriteGate"/> · null = ผ่าน</summary>
    private async Task<ActionResult?> RequireSettingsAsync(Guid companyId, string verb)
    {
        var decision = await SettingsWriteGate.EvaluateAsync(_permissions, HttpContext, companyId, verb, rejectApiKey: true);
        return decision.Allowed ? null
            : StatusCode(403, new ApiResponse<object>(false, new { ruleCode = decision.RuleCode }, decision.Message));
    }

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
        // รอบ 258 (A-H3): ตารางนี้กำหนดว่าเหตุการณ์ของทั้งบริษัทแจ้งใคร ทางไหน — เดิมสมาชิกคนไหน/คีย์ไหนก็เขียนทับได้
        if (await RequireSettingsAsync(companyId, "แก้ตารางแจ้งเตือนของบริษัท") is { } deny) return deny;
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

    // รอบ 258 (A-H2): PUT line-binding/me (ผูกด้วยการพิมพ์ LINE userId ดิบ) ถูกถอด — ทางผูกเดียว = รหัส 6 หลักที่หน้า line-bind.html
    // (POST api/line-bot/issue-bind-code → ส่ง "ผูก 123456" ให้บอท) ซึ่งมีจำกัดความถี่ · audit · อีเมลแจ้งเจ้าของบัญชี

    [HttpDelete("line-binding/me")]
    public async Task<ActionResult<ApiResponse<LineBindingResponse>>> ClearMyLineBinding(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _service.ClearLineBindingAsync(userId, companyId, HttpContext?.Connection?.RemoteIpAddress?.ToString());
        return Ok(new ApiResponse<LineBindingResponse>(true, result, "ยกเลิกการเชื่อมต่อ LINE สำเร็จ"));
    }
}
