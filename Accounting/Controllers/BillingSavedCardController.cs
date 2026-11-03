using System.Security.Claims;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — <b>บัตรที่บันทึกไว้ + ตัดบัตรอัตโนมัติ</b> ในศูนย์บิลของผู้เช่า (ดู · ลบ · ตั้งบัตรตั้งต้น · เปิด/ปิดตัดอัตโนมัติ)
/// <para>route ใต้ <c>api/subscription/{companyId}/billing-center</c> เหมือนศูนย์บิล (สมาชิกตรวจจาก route · ผู้ถูกระงับยังเข้ามาจัดการ/ปิดได้) ·
/// สิทธิ์ = <c>Billing.Manage</c> · ทุกค่าที่หน้าแสดง (ป้ายบัตร · เหตุที่ตัดไม่ได้ · ข้อความยินยอม/ฉบับ) มาจากเซิร์ฟเวอร์ ·
/// <b>บันทึกบัตรใหม่ทำได้เฉพาะตอนชำระด้วยบัตร</b> (ติ๊กในหน้าชำระ — <c>PlatformPaymentController</c>) — ไม่มี endpoint รับโทเคนลอย ๆ ที่นี่</para>
/// </summary>
[ApiController]
[Route("api/subscription/{companyId:guid}/billing-center/saved-cards")]
[Authorize]
public class BillingSavedCardController : ControllerBase
{
    private readonly IPermissionService _permissions;
    private readonly IPlatformSavedCardService _cards;

    public BillingSavedCardController(IPermissionService permissions, IPlatformSavedCardService cards)
    { _permissions = permissions; _cards = cards; }

    private async Task<ActionResult?> RequireBillingAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BillingManage)) return null;
        return StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = PermissionKeys.BillingManage.Replace("perm:", "") },
            "ไม่มีสิทธิ์จัดการบัตร/การตัดอัตโนมัติ — ต้องได้รับสิทธิ์ \"จัดการค่าใช้จ่าย/ส่วนเสริม\" จากเจ้าของบริษัทก่อน"));
    }

    private PlatformCardActor Actor() => new(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(ClaimTypes.Email),
        HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());

    [HttpGet]
    public async Task<ActionResult<ApiResponse<SavedCardsSummary>>> Get(Guid companyId, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId) is { } deny) return deny;
        return Ok(new ApiResponse<SavedCardsSummary>(true, await _cards.SummaryAsync(companyId, ct)));
    }

    [HttpDelete("{cardId:guid}")]
    [Accounting.Filters.RejectApiKey("ลบบัตรที่บันทึกไว้")]
    public async Task<ActionResult<ApiResponse<SavedCardsSummary>>> Remove(Guid companyId, Guid cardId, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId) is { } deny) return deny;
        var s = await _cards.RemoveAsync(companyId, cardId, Actor(), ct);
        return Ok(new ApiResponse<SavedCardsSummary>(true, s, "ลบบัตรแล้ว"));
    }

    [HttpPost("{cardId:guid}/default")]
    [Accounting.Filters.RejectApiKey("ตั้งบัตรตั้งต้น")]
    public async Task<ActionResult<ApiResponse<SavedCardsSummary>>> SetDefault(Guid companyId, Guid cardId, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId) is { } deny) return deny;
        return Ok(new ApiResponse<SavedCardsSummary>(true, await _cards.SetDefaultAsync(companyId, cardId, Actor(), ct), "ตั้งเป็นบัตรตั้งต้นแล้ว"));
    }

    /// <param name="WhtAcknowledged">ฝ่ายค้านรอบ 230 S1: ผู้จ่ายนิติบุคคลรับทราบเรื่องภาษีหัก ณ ที่จ่าย (บังคับเมื่อเปิด)</param>
    /// <param name="RemoveCards">ฝ่ายค้านรอบ 230 S6: ปิดแล้วลบบัตรทุกใบด้วย (หน้าเว็บติ๊กไว้เป็นค่าตั้งต้น)</param>
    public sealed record AutoChargeRequest(bool Enabled, bool ConsentAccepted = false, string? ConsentVersionShown = null,
        bool WhtAcknowledged = false, bool RemoveCards = false);

    /// <summary>เปิด (ยินยอมใหม่ — ติ๊กบังคับ · ฉบับต้องตรง · ต้องมีบัตรยืนยันแล้ว) / ปิด (ถอนความยินยอม · มีผลรอบถัดไป · ลบบัตรด้วยถ้าติ๊ก)</summary>
    [HttpPost("auto-charge")]
    [Accounting.Filters.RejectApiKey("เปิด/ปิดตัดบัตรอัตโนมัติ")]
    public async Task<ActionResult<ApiResponse<SavedCardsSummary>>> SetAutoCharge(Guid companyId, [FromBody] AutoChargeRequest req, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId) is { } deny) return deny;
        var s = await _cards.SetAutoChargeAsync(companyId, req.Enabled, req.ConsentAccepted, req.ConsentVersionShown, req.WhtAcknowledged,
            req.RemoveCards, Actor(), ct);
        return Ok(new ApiResponse<SavedCardsSummary>(true, s, req.Enabled ? "เปิดตัดบัตรอัตโนมัติแล้ว" : "ปิดตัดบัตรอัตโนมัติแล้ว (ถอนความยินยอมแล้ว)"));
    }
}
