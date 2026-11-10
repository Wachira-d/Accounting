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
    private readonly AccountingDbContext _db;

    public PartnerExchangeController(CrossTenantWorkflowService service, IPermissionService perms, IDocumentService docs, AccountingDbContext db)
    {
        _service = service; _perms = perms; _docs = docs; _db = db;
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

    /// <summary>ชนิดต้นฉบับที่ผู้รับยืนยัน — <c>Paper</c> (ตั้งต้น) = ใบกำกับกระดาษ ⇒ PaperConfirmed · <c>StampedEtax</c> = ไฟล์ e-Tax ที่ประทับเวลา ETDA ⇒ EtaxVerified</summary>
    public record ConfirmOriginalRequest(string? Kind);

    /// <summary>ฝ่ายค้าน M1 (รอบ 257 ชุด 3): คำขอ "ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ" — เลขที่ + วันที่ใบกำกับของผู้ขาย + ชนิดต้นฉบับ (Paper | StampedEtax)</summary>
    public record ExternalTaxInvoiceRequest(string? Kind, string? SupplierInvoiceNumber, DateTime? SupplierTaxInvoiceDate);

    /// <summary>ฝ่ายค้าน M1: ทางออกของคนจาก "รอใบกำกับจากผู้ขาย" — สิทธิ์อนุมัติเอกสารซื้อ · ไม่รับ API key · ยืนยัน + ย้าย VAT (ใบที่อนุมัติแล้ว) ในธุรกรรมเดียว
    /// ผ่าน <c>CompleteSupplierTaxInvoiceAsync</c> เดิม (ตัวตรวจ §86/4 ตัวเดียว)</summary>
    [HttpPost("partner-purchase-drafts/{documentId:guid}/confirm-external-tax-invoice")]
    [Accounting.Filters.RequirePermission(PermissionKeys.DocumentPurchaseApprove)]
    [Accounting.Filters.RejectApiKey("ยืนยันใบกำกับจากผู้ขายนอกระบบ")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmExternalTaxInvoice(Guid companyId, Guid documentId, [FromBody] ExternalTaxInvoiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var stampedEtax = string.Equals(request?.Kind, "StampedEtax", StringComparison.OrdinalIgnoreCase);
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var outcome = await _service.ConfirmExternalTaxInvoiceAsync(companyId, documentId, userId, stampedEtax,
                request?.SupplierInvoiceNumber, request?.SupplierTaxInvoiceDate);
            if (outcome.Approved)
                await _docs.CompleteSupplierTaxInvoiceAsync(companyId, documentId,
                    new Accounting.Models.DTOs.Document.CompleteSupplierTaxInvoiceRequest(
                        SupplierInvoiceNumber: outcome.SupplierInvoiceNumber, SupplierTaxInvoiceDate: outcome.SupplierTaxInvoiceDate),
                    userId.ToString());
            if (ownTx != null) await ownTx.CommitAsync();
            return Ok(new ApiResponse<object>(true, new { inputVatEvidence = outcome.Evidence, inputVatEvidenceLabel = PartnerInputVatEvidence.Label(outcome.Evidence) },
                outcome.Approved ? "บันทึกใบกำกับจากผู้ขายแล้ว — ระบบย้ายภาษีซื้อเข้างวดที่เคลมได้ (ถ้าใบครบ §86/4 และอยู่ในกรอบ 6 เดือน)"
                    : "บันทึกใบกำกับจากผู้ขายแล้ว — ภาษีซื้อจะลงตามปกติเมื่ออนุมัติใบนี้"));
        }
        catch (KeyNotFoundException ex) { if (ownTx != null) await ownTx.RollbackAsync(); return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex)
        {
            if (ownTx != null) await ownTx.RollbackAsync();
            if (ex.Message == PartnerInputVatEvidence.SourceVoidedMessage) await _service.MarkSourceVoidedAsync(companyId, documentId);
            return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message));
        }
        catch (InvalidOperationException ex) { if (ownTx != null) await ownTx.RollbackAsync(); return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        finally { if (ownTx != null) await ownTx.DisposeAsync(); }
    }

    /// <summary>ยืนยัน "ได้ต้นฉบับใบกำกับแล้ว" (N3 · ฝ่ายค้าน H2: ค่าที่ปลดด่านภาษีซื้อมาจากทางนี้ทางเดียว) — สิทธิ์อนุมัติเอกสารซื้อ ·
    /// ใบอนุมัติแล้วที่ VAT พัก 11640 ⇒ ย้ายเข้า 11610 ผ่านเส้นเติมใบกำกับเดิม (ตัวตรวจ §86/4 ตัวเดียว) ·
    /// ฝ่ายค้าน M3: ยืนยัน + ย้าย VAT ในธุรกรรมเดียว — ล้มทางใดไม่มีอะไรเปลี่ยน (ไม่มี "ยืนยันแล้วแต่ VAT ยังพัก")</summary>
    [HttpPost("partner-purchase-drafts/{documentId:guid}/confirm-original")]
    [Accounting.Filters.RequirePermission(PermissionKeys.DocumentPurchaseApprove)]
    [Accounting.Filters.RejectApiKey("ยืนยันต้นฉบับใบกำกับจากคู่ค้า")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmOriginal(Guid companyId, Guid documentId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ConfirmOriginalRequest? request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var stampedEtax = string.Equals(request?.Kind, "StampedEtax", StringComparison.OrdinalIgnoreCase);
        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var outcome = await _service.ConfirmPartnerOriginalAsync(companyId, documentId, userId, stampedEtax);
            if (outcome.Approved)
                // ใบกำกับที่มาทีหลังของการขายเดิม (H3) ⇒ เลข/วันที่ใบกำกับจริงเข้าทางเติมใบกำกับเดิม · ไม่มี = คำขอว่าง (ตรวจ §86/4 ใหม่ด้วยข้อมูลบนใบ)
                await _docs.CompleteSupplierTaxInvoiceAsync(companyId, documentId,
                    new Accounting.Models.DTOs.Document.CompleteSupplierTaxInvoiceRequest(
                        SupplierInvoiceNumber: outcome.SupplierInvoiceNumber, SupplierTaxInvoiceDate: outcome.SupplierTaxInvoiceDate),
                    userId.ToString());
            if (ownTx != null) await ownTx.CommitAsync();
            return Ok(new ApiResponse<object>(true, new { inputVatEvidence = outcome.Evidence, inputVatEvidenceLabel = PartnerInputVatEvidence.Label(outcome.Evidence) },
                outcome.Approved ? "บันทึกแล้ว — ระบบย้ายภาษีซื้อเข้างวดที่เคลมได้ (ถ้าใบครบ §86/4 และอยู่ในกรอบ 6 เดือน)" : "บันทึกแล้ว — ภาษีซื้อจะลงตามปกติเมื่ออนุมัติใบนี้"));
        }
        catch (KeyNotFoundException ex) { if (ownTx != null) await ownTx.RollbackAsync(); return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex)
        {
            if (ownTx != null) await ownTx.RollbackAsync();
            // ต้นทางถูกยกเลิก: ธงบนใบของผู้รับต้องอยู่รอดการย้อนธุรกรรม (ไม่งั้นรายการยังโชว์ "รอต้นฉบับ")
            if (ex.Message == PartnerInputVatEvidence.SourceVoidedMessage) await _service.MarkSourceVoidedAsync(companyId, documentId);
            return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
        catch (InvalidOperationException ex) { if (ownTx != null) await ownTx.RollbackAsync(); return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        finally { if (ownTx != null) await ownTx.DisposeAsync(); }
    }
}
