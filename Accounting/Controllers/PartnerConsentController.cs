using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 264 P1 — หน้า "งานอัตโนมัติกับคู่ค้า" (<c>/pages/partner-automation.html</c>) · ความยินยอมต่อคู่ × ขอบเขต (คำตัดสิน 264-2-1…3) ·
/// ทุกเส้นเป็นค่าตั้งด่านควบคุมภายใน ⇒ สิทธิ์ตั้งค่าบริษัท + ไม่รับ API key · ตอนให้ตรวจสิทธิ์ของขอบเขตกับผู้กดจริง (<c>PartnerConsent.GrantProblem</c>) ·
/// กติกาอยู่ใน <c>Helpers/PartnerConsent</c> · ผู้เขียนเดียว <see cref="PartnerConsentService"/>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/partner-consents")]
[Authorize]
public class PartnerConsentController : ControllerBase
{
    private readonly PartnerConsentService _service;
    private readonly IChatRateLimiter _limiter;

    public PartnerConsentController(PartnerConsentService service, IChatRateLimiter limiter)
    { _service = service; _limiter = limiter; }

    private ObjectResult Fail(BusinessRuleException ex) => StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message));

    [HttpGet]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<PartnerConsentService.PageView>>> List(Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        return Ok(new ApiResponse<PartnerConsentService.PageView>(true, await _service.ListAsync(companyId, userId, ct)));
    }

    /// <summary>ขั้นที่หนึ่ง — ข้อความยินยอม + hash (ไม่เขียนอะไร)</summary>
    [HttpPost("preview")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ดูข้อความยินยอมงานอัตโนมัติกับคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(Guid companyId, [FromBody] PartnerConsentService.PreviewRequest req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try { return Ok(new ApiResponse<object>(true, await _service.PreviewAsync(companyId, userId, req, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ขั้นที่สอง — ยินยอม (hash จากขั้นแรก + พิมพ์ยืนยันสำหรับขอบเขตที่ลงบัญชีอัตโนมัติ)</summary>
    [HttpPost]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ให้ความยินยอมงานอัตโนมัติกับคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Grant(Guid companyId, [FromBody] PartnerConsentService.GrantRequest req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var row = await _service.GrantAsync(companyId, userId, req, ct);
            return Ok(new ApiResponse<object>(true, new { id = row.Id, scope = row.Scope, grantedAt = row.GrantedAt }, "บันทึกความยินยอมแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ถอนความยินยอม (มีผลทันที) · เหตุไม่บังคับ (?reason=)</summary>
    [HttpDelete("{consentId:guid}")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ถอนความยินยอมงานอัตโนมัติกับคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> Withdraw(Guid companyId, Guid consentId, [FromQuery] string? reason, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var row = await _service.WithdrawAsync(companyId, userId, consentId, reason, ct);
            return Ok(new ApiResponse<object>(true, new { id = row.Id, withdrawnAt = row.WithdrawnAt }, "ถอนความยินยอมแล้ว — มีผลทันที"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    public sealed record RequestBody(Guid PartnershipId, List<string>? Scopes);

    /// <summary>ขอให้คู่ค้าเปิดงานอัตโนมัติ (F9 — ผู้ขอได้แค่ "ขอ") · เพดานต่อคู่ต่อวัน (นับในฐาน · ตารางนับพัง = ปฏิเสธ)</summary>
    [HttpPost("request")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ขอให้คู่ค้าเปิดงานอัตโนมัติ")]
    public async Task<ActionResult<ApiResponse<object>>> RequestFromPartner(Guid companyId, [FromBody] RequestBody req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            // ฝ่ายค้าน L8(d): โควตานับหลังคำขอผ่านการตรวจ (service เรียกตัวนับเอง)
            await _service.RequestFromPartnerAsync(companyId, userId, req.PartnershipId, req.Scopes,
                () => _limiter.TryConsumeAsync($"xt-consent-req:{companyId:N}:{req.PartnershipId:N}", PartnerConsentService.RequestsPerPartnerPerDay,
                    PartnerConsentService.RequestsPerPartnerPerDay, ct, failClosed: true), ct);
            return Ok(new ApiResponse<object>(true, null, "ส่งคำขอถึงคู่ค้าแล้ว — ระบบของคู่ค้าจะไม่ทำอะไรจนกว่าเขาจะยินยอมเอง"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }
}
