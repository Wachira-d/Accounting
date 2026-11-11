using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — ฝั่ง<b>ผู้ซื้อ</b>: การแจ้ง "ผู้ขายคู่ค้ารับเงินผ่านลิงก์แล้ว" (หน้า partners.html) · ทุกคำสั่งผูก <c>companyId</c> ของผู้เรียก
/// (แถวแจ้งอยู่ในบริษัทผู้ซื้อ · ใบซื้อเป็นของผู้ซื้อ) · บันทึกจ่าย = สิทธิ์เดียวกับหน้าเอกสาร (อนุมัติใบซื้อ — <see cref="DocumentPermissionHelper.CanApproveAsync"/>) ·
/// API key ทำไม่ได้ (การยืนยันจ่ายเงินต้องเป็นคนกด)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/partner-payment-notices")]
[Authorize]
public class PartnerPaymentNoticeController : ControllerBase
{
    private readonly PartnerPaymentNoticeService _service;
    private readonly IPermissionService _perms;

    public PartnerPaymentNoticeController(PartnerPaymentNoticeService service, IPermissionService perms)
    { _service = service; _perms = perms; }

    private ObjectResult Denied(string key)
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key.Replace("perm:", "") }, PermissionKeys.DeniedMessage(key)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PartnerPaymentNoticeDto>>>> List(Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        return Ok(new ApiResponse<List<PartnerPaymentNoticeDto>>(true, await _service.ListAsync(companyId, ct)));
    }

    public record RecordNoticeRequest(Guid? BankAccountId);

    /// <summary>"บันทึกจ่ายตามที่ผู้ขายรับแล้ว" — บันทึกจ่ายใบซื้อของเราเองผ่านเส้นเดิม (ยอด/ภาษีหักตามกติกาของเรา) · ไม่ผ่าน = 409 ข้อความบอกทางไปต่อ</summary>
    [HttpPost("{noticeId:guid}/record")]
    [Accounting.Filters.RejectApiKey("บันทึกจ่ายใบซื้อตามที่ผู้ขายคู่ค้ารับเงินแล้ว")]
    public async Task<ActionResult<ApiResponse<PartnerPaymentNoticeDto>>> Record(Guid companyId, Guid noticeId, [FromBody] RecordNoticeRequest? req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PurchaseInvoice))
            return Denied(PermissionKeys.DocumentPurchaseApprove);
        try
        {
            var r = await _service.RecordAsync(companyId, noticeId, userId, req?.BankAccountId, ct);
            return Ok(new ApiResponse<PartnerPaymentNoticeDto>(true, r, "บันทึกจ่ายใบซื้อแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<PartnerPaymentNoticeDto>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<PartnerPaymentNoticeDto>(false, null, ex.Message)); }
    }

    public record DismissNoticeRequest(string? Reason);

    /// <summary>ปิดการแจ้ง (บันทึกเองแล้ว/ไม่ใช่ของเรา) — ไม่แตะบัญชี · สิทธิ์เดียวกับการบันทึกจ่าย</summary>
    [HttpPost("{noticeId:guid}/dismiss")]
    [Accounting.Filters.RejectApiKey("ปิดการแจ้งผู้ขายคู่ค้ารับเงินแล้ว")]
    public async Task<ActionResult<ApiResponse<object>>> Dismiss(Guid companyId, Guid noticeId, [FromBody] DismissNoticeRequest? req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, DocumentType.PurchaseInvoice))
            return Denied(PermissionKeys.DocumentPurchaseApprove);
        try
        {
            await _service.DismissAsync(companyId, noticeId, userId, req?.Reason, ct);
            return Ok(new ApiResponse<object>(true, null, "ปิดการแจ้งแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
    }
}
