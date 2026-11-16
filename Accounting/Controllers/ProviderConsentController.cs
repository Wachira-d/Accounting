using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 272 แพ็ก P2 (คำตัดสินเจ้าของ 272-0-2 · ทาง B) — การ์ด "สำนักงานบัญชี NextAcc ทำแทนบริษัทนี้" ในศูนย์บิล (<c>billing.html#provider-consent</c>) ·
/// ยืนยันบน<b>เว็บ</b>เท่านั้น (272-1-9) · ผู้ให้ = เจ้าของ หรือ Tax.File + CompanySettings.Edit (272-1-7 · ตรวจใน <see cref="ProviderConsentService.GrantProblemForAsync"/>) ·
/// ไม่รับ API key · กติกาอยู่ใน <c>Helpers/ProviderConsent</c>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/provider-consent")]
[Authorize]
public class ProviderConsentController : ControllerBase
{
    private readonly ProviderConsentService _service;
    public ProviderConsentController(ProviderConsentService service) => _service = service;

    private ObjectResult Fail(BusinessRuleException ex) => StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message));

    /// <summary>การ์ดของบริษัทนี้ (สถานะ · แบบ · สลิปที่พัก · ข้อความ · ผู้มีสิทธิ์ให้) — สมาชิกทุกคนเห็นได้ ปุ่มให้แสดงตามผล <c>CallerCannotGrantReason</c></summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProviderConsentService.CardView>>> Card(Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        return Ok(new ApiResponse<ProviderConsentService.CardView>(true, await _service.CardAsync(companyId, userId, ct)));
    }

    /// <summary>ขั้นที่หนึ่ง — hash ของเนื้อที่จะยินยอม (ไม่เขียนอะไร)</summary>
    [HttpPost("preview")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ดูข้อความยินยอมของสำนักงานบัญชี")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(Guid companyId, [FromBody] ProviderConsentService.GrantInput req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var (hash, text, version, types) = await _service.PreviewAsync(companyId, userId, req, ct);
            return Ok(new ApiResponse<object>(true, new { hash, text, version, taxTypes = types, confirmPhrase = ProviderConsent.ConfirmPhrase }));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ขั้นที่สอง — อนุญาต (hash จากขั้นแรก + พิมพ์ "ยินยอม") · หลักฐาน: ผู้ให้ · เวลา · IP · UA · hash</summary>
    [HttpPost]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("อนุญาตให้สำนักงานบัญชีบันทึกการชำระภาษีจากสลิป")]
    public async Task<ActionResult<ApiResponse<object>>> Grant(Guid companyId, [FromBody] ProviderConsentService.GrantInput req, [FromServices] TaxSlipChannelService channel,
        CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var row = await _service.GrantAsync(companyId, userId, req, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
            // ข้อความในกลุ่ม (ดีไซน์ §5.6 "อนุญาตแล้ว") — งานเบื้องหลังบันทึกสลิปที่พักไว้แล้วแจ้งผลทีละใบ · ส่งไม่ได้ไม่ย้อนการให้ แต่บอกผู้กด
            var held = row.IncludesHeldSlips ? await _service.HeldCountAsync(companyId, row.ProviderCompanyId, ct) : 0;
            var sent = await channel.PostToClientAsync(row.ProviderCompanyId, companyId,
                _ => TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.ConsentGranted, new(HeldCount: held, RecordedAt: row.GrantedAt), false), ct);
            return Ok(new ApiResponse<object>(true, new { id = row.Id, grantedAt = row.GrantedAt, notice = sent },
                "อนุญาตแล้ว — สลิปที่ส่งต่อจากนี้ระบบจะบันทึกให้เมื่อผ่านทุกด่าน" + (row.IncludesHeldSlips ? " · สลิปที่พักไว้จะถูกประมวลภายในไม่กี่นาที" : "")));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }

    /// <summary>ถอน (มีผลทันทีกับสลิปที่ยังไม่บันทึก · รายการที่บันทึกแล้วไม่ถูกแตะ)</summary>
    [HttpDelete("{consentId:guid}")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ถอนการอนุญาตของสำนักงานบัญชี")]
    public async Task<ActionResult<ApiResponse<object>>> Withdraw(Guid companyId, Guid consentId, [FromQuery] string? reason, [FromServices] TaxSlipChannelService channel,
        CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        try
        {
            var row = await _service.WithdrawAsync(companyId, userId, consentId, reason, ct);
            // ข้อความในกลุ่ม (ดีไซน์ §5.6 "ถอน") — ส่งไม่ได้ไม่ย้อนการถอน แต่บอกผู้กด
            var sent = await channel.PostToClientAsync(row.ProviderCompanyId, companyId, _ => TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.ConsentWithdrawn, new(), false), ct);
            return Ok(new ApiResponse<object>(true, new { id = row.Id, withdrawnAt = row.WithdrawnAt, notice = sent }, "ถอนการอนุญาตแล้ว — มีผลทันที"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return Fail(ex); }
    }
}
