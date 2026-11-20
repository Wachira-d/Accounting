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
    public DocumentApprovalRequestController(IDocumentApprovalRequestService svc) => _svc = svc;

    [HttpGet("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> GetState(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.GetStateAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User))));

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalSubmitResult>>> Request(Guid companyId, Guid documentId,
        [FromBody] DocumentApprovalSubmitRequest? body)
    {
        var result = await _svc.RequestAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User), body?.Note);
        return Ok(new ApiResponse<DocumentApprovalSubmitResult>(true, result, result.Message));
    }

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request/withdraw")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> Withdraw(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.WithdrawAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User)), "ถอนคำขออนุมัติแล้ว — ใบกลับเป็นร่าง แก้ไขได้"));

    [HttpPost("api/companies/{companyId:guid}/document/{documentId:guid}/approval-request/return")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalStateResponse>>> Return(Guid companyId, Guid documentId,
        [FromBody] DocumentApprovalReturnRequest? body)
        => Ok(new ApiResponse<DocumentApprovalStateResponse>(true,
            await _svc.ReturnAsync(companyId, documentId, JwtHelper.GetUserIdFromClaims(User), body?.Reason), "ตีกลับแล้ว — แจ้งผู้ขอพร้อมเหตุผล"));

    [HttpGet("api/companies/{companyId:guid}/document-approval-queue")]
    public async Task<ActionResult<ApiResponse<List<DocumentApprovalQueueItem>>>> Queue(Guid companyId)
        => Ok(new ApiResponse<List<DocumentApprovalQueueItem>>(true,
            await _svc.QueueAsync(companyId, JwtHelper.GetUserIdFromClaims(User))));
}
