using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 276 — ส่งขออนุมัติ / ถอนคำขอ / ตีกลับ / สถานะบนใบ / คิวรออนุมัติ (ผู้ตัดสินทั้งหมดอยู่ที่ <see cref="DocumentApprovalRequestService"/>) ·
/// การ "อนุมัติ" ใช้ endpoint เดิม <c>POST document/{id}/approve</c> (ด่าน/เลขเอกสาร/JE เดิมทุกประการ) · ผู้ใช้ถูกจำกัดผ่าน <c>DocumentRestrictionFilter</c>
/// (รายการอนุญาต: GET/POST approval-request · withdraw — ไม่มี return/queue)
/// </summary>
[ApiController]
[Authorize]
[Accounting.Filters.RejectApiKey("ขออนุมัติ/พิจารณาเอกสาร")]
public class DocumentApprovalRequestController : ControllerBase
{
    private readonly IDocumentApprovalRequestService _svc;
    private readonly Accounting.Data.AccountingDbContext _db;
    private readonly Accounting.Services.Interfaces.IPermissionService _perms;
    public DocumentApprovalRequestController(IDocumentApprovalRequestService svc, Accounting.Data.AccountingDbContext db,
        Accounting.Services.Interfaces.IPermissionService perms)
    {
        _svc = svc; _db = db; _perms = perms;
    }

    /// <summary>ด่านสิทธิ์ชั้น controller (service ตรวจซ้ำ): ส่ง/ถอนคำขอ = สิทธิ์สร้างเอกสารชนิดนั้น · ตีกลับ = สิทธิ์อนุมัติ ·
    /// ใบที่มองไม่เห็น (ตัวกรองแถวของคำขอ) = 404 · null = ผ่าน</summary>
    private async Task<ActionResult?> DenyAsync<T>(Guid companyId, Guid documentId, bool approve)
    {
        var type = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId).Select(d => (Models.Enums.DocumentType?)d.DocumentType));
        if (type is not { } t) return NotFound(new ApiResponse<T>(false, default, DocumentRestrictionPolicy.NotFoundMessage));
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var ok = approve ? await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, uid, t)
                         : await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, uid, t);
        return ok ? null : StatusCode(403, new ApiResponse<T>(false, default,
            approve ? $"ไม่มีสิทธิ์พิจารณาเอกสาร {DocumentTypeNames.Title(t, "th")} (ต้องมีสิทธิ์อนุมัติเอกสารชนิดนี้)"
                    : $"ไม่มีสิทธิ์ส่งขออนุมัติเอกสาร {DocumentTypeNames.Title(t, "th")} (ต้องมีสิทธิ์สร้างเอกสารชนิดนี้)"));
    }

    [HttpGet("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> GetState(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.GetStateAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User))));

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalSubmitResult>>> Request(Guid companyId, Guid documentId,
        [FromBody] DocumentApprovalSubmitRequest? body)
    {
        if (await DenyAsync<DocumentApprovalSubmitResult>(companyId, documentId, approve: false) is { } deny) return deny;
        var result = await _svc.RequestAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User), body?.Note);
        return Ok(new ApiResponse<DocumentApprovalSubmitResult>(true, result, result.Message));
    }

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request/withdraw")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> Withdraw(Guid companyId, Guid documentId)
    {
        if (await DenyAsync<DocumentApprovalStateResponse>(companyId, documentId, approve: false) is { } deny) return deny;
        return Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.WithdrawAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User)), "ถอนคำขออนุมัติแล้ว — ใบกลับเป็นร่าง แก้ไขได้"));
    }

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request/return")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> Return(Guid companyId, Guid documentId,
        [FromBody] DocumentApprovalReturnRequest? body)
    {
        if (await DenyAsync<DocumentApprovalStateResponse>(companyId, documentId, approve: true) is { } deny) return deny;
        return Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.ReturnAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User), body?.Reason), "ตีกลับแล้ว — แจ้งผู้ขอพร้อมเหตุผล"));
    }

    [HttpGet("api/companies/{companyId:guid}/document-approval-queue")]
    public async Task<ActionResult<ApiResponse<List<DocumentApprovalQueueItem>>>> Queue(Guid companyId)
        => Ok(new ApiResponse<List<DocumentApprovalQueueItem>>(true,
            await _svc.QueueAsync(companyId, JwtHelper.GetUserIdFromClaims(User))));
}
