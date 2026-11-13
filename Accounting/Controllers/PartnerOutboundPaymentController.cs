using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11/12) — ฝั่ง<b>ผู้ซื้อ</b>: "จ่ายจากระบบของเรา" + "แจ้งโอนให้ผู้ขาย" บนใบซื้อที่มาจากคู่ค้า NextAcc ·
/// ทุกคำสั่งผูก <c>companyId</c> ของผู้เรียก · เขียน = สิทธิ์เดียวกับบันทึกการชำระที่หน้าเอกสาร (อนุมัติเอกสาร<b>ตามชนิดใบซื้อจริง</b> + ชั้นความลับ) ·
/// ดู = เห็นเอกสารฝั่งซื้อ · API key ทำไม่ได้ (การจ่ายเงินต้องเป็นคนกด)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/partner-outbound-payments")]
[Authorize]
public class PartnerOutboundPaymentController : ControllerBase
{
    private readonly PartnerOutboundPaymentService _service;
    private readonly IPermissionService _perms;
    private readonly Accounting.Data.AccountingDbContext _db;

    public PartnerOutboundPaymentController(PartnerOutboundPaymentService service, IPermissionService perms, Accounting.Data.AccountingDbContext db)
    { _service = service; _perms = perms; _db = db; }

    private ObjectResult Denied(string key)
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key.Replace("perm:", "") }, PermissionKeys.DeniedMessage(key)));

    /// <summary>ด่านเดียวกับ <c>PartnerPaymentNoticeController.DenyDocAsync</c>: อนุมัติเอกสารชนิดนั้น + ชั้นความลับ · null = ผ่าน</summary>
    private async Task<ObjectResult?> DenyAsync(Guid companyId, Guid userId, Guid purchaseId, bool write, CancellationToken ct)
    {
        var d = await _db.Documents.AsNoTracking().Where(x => x.Id == purchaseId && x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => new { x.DocumentType, x.Sensitivity }).FirstOrDefaultAsync(ct);
        if (d == null || !DocumentPermissionHelper.IsPurchase(d.DocumentType)) return NotFound(new ApiResponse<object>(false, null, "ไม่พบใบซื้อ"));
        if (write && !await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, userId, d.DocumentType))
            return Denied(PermissionKeys.DocumentPurchaseApprove);
        if (!write && !(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, userId)).Purchase)
            return Denied(PermissionKeys.DocumentPurchaseView);
        if (SensitivityAccess.NeedsCheck(d.Sensitivity)
            && HttpContext.RequestServices.GetService(typeof(ISensitivityService)) is ISensitivityService svc
            && !await svc.CanViewAsync(companyId, userId, d.Sensitivity))
            return StatusCode(403, new ApiResponse<object>(false, null, SensitivityAccess.DeniedMessage(d.Sensitivity, "บันทึกการชำระของ")));
        return null;
    }

    [HttpGet("purchases/{purchaseId:guid}")]
    public async Task<ActionResult<ApiResponse<PartnerOutboundOptionsDto>>> Options(Guid companyId, Guid purchaseId, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (await DenyAsync(companyId, userId, purchaseId, false, ct) is { } deny) return deny;
        try { return Ok(new ApiResponse<PartnerOutboundOptionsDto>(true, await _service.OptionsAsync(companyId, purchaseId, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<PartnerOutboundOptionsDto>(false, null, ex.Message)); }
    }

    public record GatewayRequest(Guid? BankAccountId, string? Method, string? ReturnUrl);

    /// <summary>"จ่ายจากระบบของเรา" — เปิดรายการชำระของเอกสารผู้ขายผ่านช่องทางของผู้ขาย · ผลยังไม่ใช่ "ชำระแล้ว"</summary>
    [HttpPost("purchases/{purchaseId:guid}/gateway")]
    [Accounting.Filters.RejectApiKey("จ่ายใบซื้อจากคู่ค้าผ่านช่องทางของผู้ขาย")]
    public async Task<ActionResult<ApiResponse<PartnerGatewayStartResult>>> Gateway(Guid companyId, Guid purchaseId, [FromBody] GatewayRequest? req, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (await DenyAsync(companyId, userId, purchaseId, true, ct) is { } deny) return deny;
        try
        {
            var r = await _service.StartGatewayAsync(companyId, userId, purchaseId, req?.BankAccountId, req?.Method, req?.ReturnUrl, ct);
            return Ok(new ApiResponse<PartnerGatewayStartResult>(true, r, r.Message));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<PartnerGatewayStartResult>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<PartnerGatewayStartResult>(false, null, ex.Message)); }
    }

    /// <summary>"แจ้งโอนให้ผู้ขาย" — แนบสลิปของเรา + บัญชีที่โอน + ยอด + วันที่ ⇒ บันทึกจ่ายของเรา + สำเนาสลิปเข้าคิวของผู้ขาย (ถ้าผู้ขายเปิดรับ)</summary>
    [HttpPost("purchases/{purchaseId:guid}/transfer")]
    [RequestSizeLimit(SlipEvidenceRule.PayLinkMaxBytes + 64 * 1024)]
    [Accounting.Filters.RejectApiKey("แจ้งโอนใบซื้อจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<PartnerTransferResult>>> Transfer(Guid companyId, Guid purchaseId, IFormFile? file, [FromForm] Guid? bankAccountId,
        [FromForm] string? method, [FromForm] decimal? amount, [FromForm] DateTime? transferredAt, [FromForm] string? bankRef, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (await DenyAsync(companyId, userId, purchaseId, true, ct) is { } deny) return deny;
        if (file == null || file.Length == 0) return BadRequest(new ApiResponse<PartnerTransferResult>(false, null, "กรุณาแนบสลิปการโอน"));
        if (file.Length > SlipEvidenceRule.PayLinkMaxBytes) return BadRequest(new ApiResponse<PartnerTransferResult>(false, null, "ไฟล์ใหญ่เกิน 10 MB"));
        if (amount is not decimal amt) return BadRequest(new ApiResponse<PartnerTransferResult>(false, null, "กรอกยอดเงินที่โอนจริง"));
        if (transferredAt is not DateTime local) return BadRequest(new ApiResponse<PartnerTransferResult>(false, null, "กรอกวันที่โอน"));
        byte[] bytes;
        await using (var hs = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await hs.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        try
        {
            // วันที่จากหน้าเป็นวันตามปฏิทินไทย ⇒ เก็บเป็นวันที่ (ไม่มีเวลา) แบบเดียวกับวันที่การชำระของหน้าเอกสาร
            var r = await _service.NotifyTransferAsync(companyId, userId, purchaseId, new SlipFile(bytes, file.ContentType ?? "", Path.GetFileName(file.FileName ?? "")),
                bankAccountId, method, amt, DateTime.SpecifyKind(local.Date, DateTimeKind.Utc), bankRef, ct);
            return Ok(new ApiResponse<PartnerTransferResult>(true, r, r.Message));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<PartnerTransferResult>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<PartnerTransferResult>(false, null, ex.Message)); }
    }
}
