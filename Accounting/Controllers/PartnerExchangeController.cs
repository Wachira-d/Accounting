using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 255 WP-12B ข้อ 6–7 — ปลายทางของ "คู่ค้า NextAcc" ส่วนต่อยอด (หน้า <c>/pages/partners.html</c>): สถานะการเชื่อมของผู้ติดต่อ ·
/// เพดานยอดใบซื้อร่างต่อคู่ · ใบซื้อร่างจากคู่ค้า · ยืนยัน "ได้ต้นฉบับใบกำกับแล้ว" · ทุกเส้นมีด่านสิทธิ์ · การผูก/ยอมรับ/เชิญอยู่ที่ <c>CrossTenantWorkflowController</c> เดิม
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}")]
[Authorize]
public class PartnerExchangeController : ControllerBase
{
    private readonly CrossTenantWorkflowService _service;
    private readonly IPermissionService _perms;
    private readonly IDocumentService _docs;

    public PartnerExchangeController(CrossTenantWorkflowService service, IPermissionService perms, IDocumentService docs)
    {
        _service = service; _perms = perms; _docs = docs;
    }

    private ObjectResult Denied(string key)
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key.Replace("perm:", "") }, PermissionKeys.DeniedMessage(key)));

    /// <summary>สถานะการเชื่อมกับผู้ติดต่อ — ไม่บอกว่าเลขภาษีนั้นใช้ NextAcc เว้นปลายทางเปิดให้ค้นเจอเอง (P-1 · PartnerDiscoverable ตั้งต้นปิด)</summary>
    [HttpGet("contacts/{contactId:guid}/partner-status")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<CrossTenantWorkflowService.PartnerStatusView>>> PartnerStatus(Guid companyId, Guid contactId, CancellationToken ct)
    {
        try { return Ok(new ApiResponse<CrossTenantWorkflowService.PartnerStatusView>(true, await _service.PartnerStatusForContactAsync(companyId, contactId, ct))); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<CrossTenantWorkflowService.PartnerStatusView>(false, null, ex.Message)); }
    }

    public record PurchaseDraftLimitRequest(decimal? Limit);

    /// <summary>เพดานยอดใบซื้อร่างที่เรารับจากคู่นี้ (null = ไม่จำกัด) · ค่าตั้งด่านควบคุมภายใน ⇒ สิทธิ์ตั้งค่าบริษัท + ไม่รับ API key · audit</summary>
    [HttpPut("trading-partners/{partnershipId:guid}/purchase-draft-limit")]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งเพดานใบซื้อร่างจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> SetPurchaseDraftLimit(Guid companyId, Guid partnershipId, [FromBody] PurchaseDraftLimitRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        try
        {
            var v = await _service.SetPurchaseDraftLimitAsync(companyId, partnershipId, req?.Limit, userId);
            return Ok(new ApiResponse<object>(true, new { purchaseDraftLimit = v }, "บันทึกเพดานแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>ใบซื้อร่างจากคู่ค้า (สำเนาของเรา) — เอกสารฝั่งซื้อ ⇒ ผู้ที่เห็นฝั่งซื้อเท่านั้น (ตัวตัดสินเดียว DocumentPermissionHelper)</summary>
    [HttpGet("partner-purchase-drafts")]
    public async Task<ActionResult<ApiResponse<List<object>>>> PurchaseDrafts(Guid companyId, CancellationToken ct)
    {
        if (!(await DocumentPermissionHelper.VisibleDirectionsAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User))).Purchase)
            return Denied(PermissionKeys.DocumentPurchaseView);
        return Ok(new ApiResponse<List<object>>(true, await _service.IncomingPurchaseDraftsAsync(companyId, ct)));
    }

    /// <summary>ยืนยัน "ได้ต้นฉบับใบกำกับแล้ว" (N3) — สิทธิ์อนุมัติเอกสารซื้อ · ใบอนุมัติแล้วที่ VAT พัก 11640 ⇒ ย้ายเข้า 11610 ผ่านเส้นเติมใบกำกับเดิม (ตัวตรวจ §86/4 ตัวเดียว)</summary>
    [HttpPost("partner-purchase-drafts/{documentId:guid}/confirm-original")]
    [Accounting.Filters.RequirePermission(PermissionKeys.DocumentPurchaseApprove)]
    [Accounting.Filters.RejectApiKey("ยืนยันต้นฉบับใบกำกับจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmOriginal(Guid companyId, Guid documentId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        try
        {
            var approved = await _service.ConfirmPartnerOriginalAsync(companyId, documentId, userId);
            if (approved)
                await _docs.CompleteSupplierTaxInvoiceAsync(companyId, documentId,
                    new Accounting.Models.DTOs.Document.CompleteSupplierTaxInvoiceRequest(), userId.ToString());
            return Ok(new ApiResponse<object>(true, new { inputVatEvidence = PartnerInputVatEvidence.PaperConfirmed },
                approved ? "บันทึกแล้ว — ระบบย้ายภาษีซื้อเข้างวดที่เคลมได้ (ถ้าใบครบ §86/4 และอยู่ในกรอบ 6 เดือน)" : "บันทึกแล้ว — ภาษีซื้อจะลงตามปกติเมื่ออนุมัติใบนี้"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }
}
