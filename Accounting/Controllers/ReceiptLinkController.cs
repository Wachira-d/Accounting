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

    public ReceiptLinkController(IReceiptLinkService links, IPermissionService permissions, AccountingDbContext db)
    {
        _links = links;
        _permissions = permissions;
        _db = db;
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
}
