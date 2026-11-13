using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 266 แพ็ก A — ผูกใบเสร็จ/เงินที่รับไว้แล้วกับใบแจ้งหนี้ (เจ้าเดียวกัน) ทั้งจากหน้าใบแจ้งหนี้และหน้าใบเสร็จ · ตัวตัดสิน <c>Helpers/ReceiptLinkPolicy</c>
/// <para><b>สิทธิ์ (266-1-10):</b> อ่าน/พรีวิว = เห็นเอกสารฝั่งขาย · ผูก = อนุมัติ (Approve) ของชนิดใบปลายทาง (ระดับเดียวกับจัดสรรเงินรอจัดสรร) + สร้างใบเสร็จ
/// เมื่อเส้นคือแก้ใบเสร็จร่าง · ถอด = ยกเลิก (Void) ของชนิดใบปลายทาง / สร้างใบเสร็จ (ใบร่าง) · <b>API key ใช้ไม่ได้ในรุ่นนี้</b> — เฝ้าโดย
/// <c>tools/write_permission_gate_check.py</c></para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/receipt-links")]
[Authorize]
public class ReceiptLinkController : ControllerBase
{
    private readonly IReceiptLinkService _links;
    private readonly IPermissionService _permissions;
    private readonly AccountingDbContext _db;
    private readonly IDuplicateSaleService _duplicates;
    private readonly IPdfGenerationService _pdf;
    private readonly ISensitivityService _sensitivity;

    public ReceiptLinkController(IReceiptLinkService links, IPermissionService permissions, AccountingDbContext db,
        IDuplicateSaleService duplicates, IPdfGenerationService pdf, ISensitivityService sensitivity)
    {
        _links = links;
        _permissions = permissions;
        _db = db;
        _duplicates = duplicates;
        _pdf = pdf;
        _sensitivity = sensitivity;
    }

    private ActionResult<ApiResponse<T>> Forbid403<T>(string th) => StatusCode(403, new ApiResponse<T>(false, default, th));

    private const string ApiKeyDenied = "ผูกใบเสร็จกับใบแจ้งหนี้ทำด้วย API key ไม่ได้ในรุ่นนี้ (คำตัดสิน 266-1-10) — ทำที่หน้าเอกสารบนเว็บ";

    private async Task<string?> DenyViewAsync(Guid companyId)
        => (await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, JwtHelper.GetUserIdFromClaims(User))).Revenue
            ? null
            : "ไม่มีสิทธิ์ดูเอกสารฝั่งขาย (Document.Revenue.View)";

    private Task<DocumentType?> TypeAsync(Guid companyId, Guid? id)
        => id is Guid g
            ? _db.Documents.AsNoTracking().Where(d => d.Id == g && d.CompanyId == companyId).Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync()
            : Task.FromResult<DocumentType?>(null);

    /// <summary>ผู้สมัครของใบที่เปิดอยู่ (ใบแจ้งหนี้ หรือ ใบเสร็จ)</summary>
    [HttpGet("candidates")]
    public async Task<ActionResult<ApiResponse<ReceiptLinkCandidatesResponse>>> Candidates(Guid companyId, [FromQuery] Guid fromDocumentId,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<ReceiptLinkCandidatesResponse>(deny);
        var r = await _links.CandidatesAsync(companyId, fromDocumentId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<ReceiptLinkCandidatesResponse>(true, r));
    }

    /// <summary>พรีวิวผล (อ่านอย่างเดียว — ไม่เขียนอะไร · เส้นเขียนจริงคือ POST ด้านล่าง)</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<ApiResponse<ReceiptLinkPreviewResponse>>> Preview(Guid companyId, [FromBody] ReceiptLinkRequest request,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<ReceiptLinkPreviewResponse>(deny);
        var r = await _links.PreviewAsync(companyId, request, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<ReceiptLinkPreviewResponse>(true, r));
    }

    /// <summary>ผูก — สิทธิ์อนุมัติของชนิดใบปลายทาง (+ สร้างใบเสร็จ เมื่อแก้ใบเสร็จร่าง)</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ReceiptLinkResult>>> Link(Guid companyId, [FromBody] ReceiptLinkRequest request,
        CancellationToken ct = default)
    {
        if (OwnerActionGuard.IsApiKeyRequest(HttpContext)) return Forbid403<ReceiptLinkResult>(ApiKeyDenied);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var invType = await TypeAsync(companyId, request.InvoiceId);
        if (invType == null) return NotFound(new ApiResponse<ReceiptLinkResult>(false, null, "ไม่พบใบแจ้งหนี้"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, invType.Value))
            return Forbid403<ReceiptLinkResult>($"ไม่มีสิทธิ์ผูกการรับเงินเข้าเอกสาร {invType.Value} (ต้องการ Document.Approve หรือ Document.Revenue.Approve)");
        if (await TypeAsync(companyId, request.ReceiptDocumentId) is DocumentType rType
            && !await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, rType))
            return Forbid403<ReceiptLinkResult>($"ไม่มีสิทธิ์แก้ไขเอกสาร {rType} (ต้องการ Document.Create ของชนิดนี้)");
        // แพ็ก B (266-1-27 · ฝ่ายค้าน B-2): สิทธิ์เฉพาะของเส้นย้าย (ยกเลิกชนิดของใบที่เงินถูกถอนออก · ยกเลิก/สร้าง/อนุมัติใบเสร็จเมื่อเส้นแตะกระดาษ) ตัดสินใน
        // ข้อเท็จจริงของการย้าย (DocumentService.BuildSettlementMoveFactsAsync) ซึ่งอ่านซ้ำใต้ล็อก — ไม่ตัดสินจากพรีวิวนอกล็อกที่นี่ (TOCTOU) · ไม่มีสิทธิ์ = 403 RL-MOVE-PERMISSION / RL-MOVE-RECEIPT
        var r = await _links.LinkAsync(companyId, request, userId, ct);
        return Ok(new ApiResponse<ReceiptLinkResult>(true, r, string.Join(" · ", r.Notices.DefaultIfEmpty($"ผูกกับ {r.InvoiceNumber} แล้ว"))));
    }

    /// <summary>ถอดการผูก (ใบเสร็จร่าง · เงินรอจัดสรร)</summary>
    [HttpPost("undo")]
    public async Task<ActionResult<ApiResponse<ReceiptLinkResult>>> Undo(Guid companyId, [FromBody] ReceiptLinkUndoRequest request,
        CancellationToken ct = default)
    {
        if (OwnerActionGuard.IsApiKeyRequest(HttpContext)) return Forbid403<ReceiptLinkResult>(ApiKeyDenied);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await TypeAsync(companyId, request.ReceiptDocumentId) is DocumentType rType
            && !await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, rType))
            return Forbid403<ReceiptLinkResult>($"ไม่มีสิทธิ์แก้ไขเอกสาร {rType}");
        // แพ็ก B: ถอดการย้ายการรับชำระ = ย้ายกลับ (ลงบัญชีใหม่) — ต้องระบุแถวที่มีผล เพื่อให้ด่านสิทธิ์ยกเลิกด้านล่างทำงานเสมอ
        if (request.AllocationId == null
            && (request.Route == nameof(ReceiptLinkRoute.MoveSettlement) || request.Route == nameof(ReceiptLinkRoute.MoveSettlementWithReissue)))
            return BadRequest(new ApiResponse<ReceiptLinkResult>(false, null, "ถอดการย้ายการรับชำระต้องระบุแถวจัดสรรที่มีผล (allocationId)"));
        if (request.AllocationId is Guid aid)
        {
            var t = await _db.PaymentAllocations.AsNoTracking()
                .Where(a => a.Id == aid && a.CompanyId == companyId).Select(a => (DocumentType?)a.Document.DocumentType).FirstOrDefaultAsync(ct);
            if (t == null) return NotFound(new ApiResponse<ReceiptLinkResult>(false, null, "ไม่พบการจัดสรร"));
            if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, t.Value))
                return Forbid403<ReceiptLinkResult>($"ไม่มีสิทธิ์ยกเลิกการจัดสรรของเอกสาร {t.Value} (ต้องการ Document.Void หรือ Document.Revenue.Void)");
        }
        var r = await _links.UndoAsync(companyId, request, userId, ct);
        return Ok(new ApiResponse<ReceiptLinkResult>(true, r, string.Join(" · ", r.Notices.DefaultIfEmpty("ถอดการผูกแล้ว"))));
    }

    // ── รอบ 266 แพ็ก C: ตัวช่วย "ใบซ้ำ" (Helpers/DuplicateSalePolicy · คำตัดสิน 266-1-5 · 266-1-36…38) ──

    /// <summary>คู่ใบที่อาจซ้ำของใบที่เปิดอยู่ (ใบขายสด ⇒ ใบแจ้งหนี้ · ใบแจ้งหนี้ ⇒ ใบขายสด) — อ่านอย่างเดียว</summary>
    [HttpGet("duplicate-sale/candidates")]
    public async Task<ActionResult<ApiResponse<DuplicateSaleCandidatesResponse>>> DuplicateSaleCandidates(Guid companyId, [FromQuery] Guid documentId,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<DuplicateSaleCandidatesResponse>(deny);
        var r = await _duplicates.CandidatesAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<DuplicateSaleCandidatesResponse>(true, r));
    }

    /// <summary>พรีวิวผลของทั้งสองตัวเลือก (อ่านอย่างเดียว — ไม่เขียนอะไร)</summary>
    [HttpGet("duplicate-sale/preview")]
    public async Task<ActionResult<ApiResponse<DuplicateSalePreviewResponse>>> DuplicateSalePreview(Guid companyId, [FromQuery] Guid cashSaleId,
        [FromQuery] Guid invoiceId, CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<DuplicateSalePreviewResponse>(deny);
        var r = await _duplicates.PreviewAsync(companyId, cashSaleId, invoiceId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<DuplicateSalePreviewResponse>(true, r));
    }

    /// <summary>ทำตามตัวเลือก — ระดับอนุมัติของใบแจ้งหนี้ (ระดับเดียวกับปุ่มผูก 266-1-10) · สิทธิ์ของเส้นที่เดินจริง (ยกเลิก/สร้างใบลดหนี้/รับชำระ)
    /// ตัดสินในบริการจากผลตัดสินล่าสุด · API key ใช้ไม่ได้</summary>
    [HttpPost("duplicate-sale")]
    public async Task<ActionResult<ApiResponse<DuplicateSaleResult>>> DuplicateSaleExecute(Guid companyId, [FromBody] DuplicateSaleRequest request,
        CancellationToken ct = default)
    {
        if (OwnerActionGuard.IsApiKeyRequest(HttpContext)) return Forbid403<DuplicateSaleResult>(ApiKeyDenied);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var invType = await TypeAsync(companyId, request.InvoiceId);
        if (invType == null) return NotFound(new ApiResponse<DuplicateSaleResult>(false, null, "ไม่พบใบแจ้งหนี้"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, invType.Value))
            return Forbid403<DuplicateSaleResult>($"ไม่มีสิทธิ์จัดการใบซ้ำของเอกสาร {invType.Value} (ต้องการ Document.Approve หรือ Document.Revenue.Approve)");
        var r = await _duplicates.ExecuteAsync(companyId, request, userId, ct);
        return Ok(new ApiResponse<DuplicateSaleResult>(true, r, string.Join(" · ", r.Notices.DefaultIfEmpty("ทำรายการแล้ว"))));
    }

    /// <summary>"ใบแจ้งการนำเงินไปตัดชำระ" (266-1-6) ของแถวจัดสรรที่เกิดหลังรับเงิน — format=html (พรีวิว) / pdf · อ่านอย่างเดียว · ไม่ใช่ใบรับ/ใบกำกับภาษี</summary>
    [HttpGet("advice/{allocationId:guid}")]
    public async Task<ActionResult> Advice(Guid companyId, Guid allocationId, [FromQuery] string? format = "pdf",
        [FromQuery] string? lang = null)
    {
        if (await DenyViewAsync(companyId) is { } deny) return StatusCode(403, new ApiResponse<string>(false, null, deny));
        // ใบลับที่ผู้ใช้ไม่เห็น = ไม่พบ (ชุดเดียวกับทางอ่านเอกสารอื่น) · tenant
        var sensitivity = await _db.PaymentAllocations.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.Id == allocationId && a.CompanyId == companyId).Select(a => (SensitivityKind?)a.Document.Sensitivity).FirstOrDefaultAsync();
        if (sensitivity == null || !await _sensitivity.CanViewAsync(companyId, JwtHelper.GetUserIdFromClaims(User), sensitivity.Value))
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบการตัดชำระ"));
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            return Content(await _pdf.GeneratePaymentApplicationAdviceHtmlAsync(companyId, allocationId, lang), "text/html; charset=utf-8");
        var file = await _pdf.GeneratePaymentApplicationAdvicePdfAsync(companyId, allocationId, lang);
        return File(file.PdfData, file.ContentType, file.FileName);
    }
}
