using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — ฝั่ง<b>ผู้ซื้อ</b>: การแจ้ง "ผู้ขายคู่ค้ารับเงินผ่านลิงก์แล้ว" (หน้า partners.html) · ทุกคำสั่งผูก <c>companyId</c> ของผู้เรียก ·
/// ฝ่ายค้าน L3: บันทึกจ่าย/ปิด = สิทธิ์เดียวกับบันทึกการชำระที่หน้าเอกสาร (อนุมัติเอกสาร<b>ตามชนิดใบซื้อจริง</b> + ชั้นความลับของใบ) · รายการ = ต้องเห็นเอกสารฝั่งซื้อ ·
/// API key ทำไม่ได้ (การยืนยันจ่ายเงินต้องเป็นคนกด)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/partner-payment-notices")]
[Authorize]
public class PartnerPaymentNoticeController : ControllerBase
{
    private readonly PartnerPaymentNoticeService _service;
    private readonly IPermissionService _perms;
    private readonly Accounting.Data.AccountingDbContext _db;

    public PartnerPaymentNoticeController(PartnerPaymentNoticeService service, IPermissionService perms, Accounting.Data.AccountingDbContext db)
    { _service = service; _perms = perms; _db = db; }

    private ObjectResult Denied(string key)
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key.Replace("perm:", "") }, PermissionKeys.DeniedMessage(key)));

    /// <summary>ด่านเดียวกับ <c>DocumentController.CreatePayment</c>: อนุมัติเอกสารชนิดนั้น + ชั้นความลับ · null = ผ่าน</summary>
    private async Task<ObjectResult?> DenyPayAsync(Guid companyId, Guid userId, Guid noticeId, CancellationToken ct)
    {
        var target = await _service.TargetAsync(companyId, noticeId, ct);
        if (target is not { } t) return NotFound(new ApiResponse<object>(false, null, "ไม่พบการแจ้งนี้หรือใบซื้อปลายทาง"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, t.Type))
            return Denied(DocumentPermissionHelper.IsPurchase(t.Type) ? PermissionKeys.DocumentPurchaseApprove : PermissionKeys.DocumentApprove);
        var kind = await _db.Documents.AsNoTracking().Where(d => d.Id == t.DocumentId && d.CompanyId == companyId)
            .Select(d => d.Sensitivity).FirstOrDefaultAsync(ct);
        if (SensitivityAccess.NeedsCheck(kind)
            && HttpContext.RequestServices.GetService(typeof(ISensitivityService)) is ISensitivityService svc
            && !await svc.CanViewAsync(companyId, userId, kind))
            return StatusCode(403, new ApiResponse<object>(false, null, SensitivityAccess.DeniedMessage(kind, "บันทึกการชำระของ")));
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PartnerPaymentNoticeDto>>>> List(Guid companyId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, userId)).Purchase)
            return Denied(PermissionKeys.DocumentPurchaseView);
        return Ok(new ApiResponse<List<PartnerPaymentNoticeDto>>(true, await _service.ListAsync(companyId, ct)));
    }

    public record RecordNoticeRequest(Guid? BankAccountId, string? PaymentMethod);

    /// <summary>"บันทึกจ่ายตามที่ผู้ขายรับแล้ว" — บันทึกจ่ายใบซื้อของเราเองผ่านเส้นเดิม (ยอด/ภาษีหักตามกติกาของเรา · บัญชี/วิธีจ่ายที่ผู้กดเลือก) · ไม่ผ่าน = 4xx ข้อความบอกทางไปต่อ</summary>
    [HttpPost("{noticeId:guid}/record")]
    [Accounting.Filters.RejectApiKey("บันทึกจ่ายใบซื้อตามที่ผู้ขายคู่ค้ารับเงินแล้ว")]
    public async Task<ActionResult<ApiResponse<PartnerPaymentNoticeDto>>> Record(Guid companyId, Guid noticeId, [FromBody] RecordNoticeRequest? req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (await DenyPayAsync(companyId, userId, noticeId, ct) is { } deny) return deny;
        try
        {
            var r = await _service.RecordAsync(companyId, noticeId, userId, req?.BankAccountId, req?.PaymentMethod, ct);
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
        if (await DenyPayAsync(companyId, userId, noticeId, ct) is { } deny) return deny;
        try
        {
            await _service.DismissAsync(companyId, noticeId, userId, req?.Reason, ct);
            return Ok(new ApiResponse<object>(true, null, "ปิดการแจ้งแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
    }
}
