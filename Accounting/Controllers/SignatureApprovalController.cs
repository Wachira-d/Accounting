using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Signature;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

// ===================================================================
// User Signature Controller (ไม่ผูก company)
// ===================================================================
[ApiController]
[Route("api/signatures")]
[Authorize]
public class UserSignatureController : ControllerBase
{
    private readonly ISignatureApprovalService _svc;
    public UserSignatureController(ISignatureApprovalService svc) => _svc = svc;

    private Guid UserId => JwtHelper.GetUserIdFromClaims(User);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserSignatureResponse>>> Upload([FromBody] UploadSignatureRequest req)
        => Ok(new ApiResponse<UserSignatureResponse>(true, await _svc.UploadSignatureAsync(UserId, req), "อัพโหลดลายเซ็นสำเร็จ"));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<UserSignatureResponse>>>> GetAll()
        => Ok(new ApiResponse<List<UserSignatureResponse>>(true, await _svc.GetUserSignaturesAsync(UserId)));

    [HttpGet("default")]
    public async Task<ActionResult<ApiResponse<UserSignatureResponse?>>> GetDefault()
        => Ok(new ApiResponse<UserSignatureResponse?>(true, await _svc.GetDefaultSignatureAsync(UserId)));

    [HttpPost("{signatureId:guid}/set-default")]
    public async Task<ActionResult<ApiResponse<object>>> SetDefault(Guid signatureId)
    {
        await _svc.SetDefaultSignatureAsync(UserId, signatureId);
        return Ok(new ApiResponse<object>(true, null, "ตั้งเป็นลายเซ็นหลักแล้ว"));
    }

    [HttpDelete("{signatureId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid signatureId)
    {
        await _svc.DeleteSignatureAsync(UserId, signatureId);
        return Ok(new ApiResponse<object>(true, null, "ลบลายเซ็นแล้ว"));
    }
}

// ===================================================================
// Document Approval Controller (ผูก company)
// ===================================================================
[ApiController]
[Route("api/companies/{companyId:guid}/approvals")]
[Authorize]
public class DocumentApprovalController : ControllerBase
{
    private readonly ISignatureApprovalService _svc;
    public DocumentApprovalController(ISignatureApprovalService svc) => _svc = svc;

    private string UserId => JwtHelper.GetUserIdFromClaims(User).ToString();

    // ===== Setup Approval Workflow =====
    [HttpPost("setup")]
    public async Task<ActionResult<ApiResponse<List<DocumentApprovalResponse>>>> SetupApproval(
        Guid companyId, [FromBody] SetupDocumentApprovalRequest req)
        => Ok(new ApiResponse<List<DocumentApprovalResponse>>(true,
            await _svc.SetupApprovalAsync(companyId, req, UserId), "ตั้งค่าการอนุมัติสำเร็จ"));

    // ===== Get Document Approvals =====
    [HttpGet("document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentWithApprovalsResponse>>> GetDocumentApprovals(
        Guid companyId, Guid documentId)
        => Ok(new ApiResponse<DocumentWithApprovalsResponse>(true,
            await _svc.GetDocumentWithApprovalsAsync(companyId, documentId)));

    // ===== Get My Pending Approvals =====
    [HttpGet("pending")]
    public async Task<ActionResult<ApiResponse<List<DocumentApprovalResponse>>>> GetPending(Guid companyId)
        => Ok(new ApiResponse<List<DocumentApprovalResponse>>(true,
            await _svc.GetPendingApprovalsAsync(companyId, JwtHelper.GetUserIdFromClaims(User))));

    // ===== Approve (Internal) =====
    [HttpPost("{approvalId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalResponse>>> Approve(
        Guid companyId, Guid approvalId, [FromBody] ApproveDocumentRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        try
        {
            var result = await _svc.ApproveAsync(companyId, approvalId, req, UserId, ip);
            return Ok(new ApiResponse<DocumentApprovalResponse>(true, result, "อนุมัติสำเร็จ"));
        }
        catch (Accounting.Services.Implementations.DocumentApprovalWarningsException ex)
        {
            // รอบ 193 (ฝ่ายค้านรอบสอง N6): ลายเซ็นขั้นสุดท้ายเจอคำเตือน — ยังไม่มีอะไรถูกบันทึก · 422 รูปเดียวกับหน้าเอกสาร/workflow
            return StatusCode(422, new ApiResponse<object>(false,
                new Accounting.Models.DTOs.Document.ApprovalWarningsResponse(ex.Warnings, null),
                "เอกสารมีคำเตือนที่ต้องกด \"รับทราบ\" ก่อนลายเซ็นขั้นสุดท้าย (ยังไม่ได้บันทึกลายเซ็น): " + string.Join(" · ", ex.Warnings)));
        }
    }

    // ===== Reject =====
    [HttpPost("{approvalId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<DocumentApprovalResponse>>> Reject(
        Guid companyId, Guid approvalId, [FromBody] RejectDocumentRequest req)
        => Ok(new ApiResponse<DocumentApprovalResponse>(true,
            await _svc.RejectAsync(companyId, approvalId, req, UserId), "ปฏิเสธแล้ว"));

    // ===== Get Document Signatures =====
    [HttpGet("document/{documentId:guid}/signatures")]
    public async Task<ActionResult<ApiResponse<List<DocumentSignatureResponse>>>> GetSignatures(
        Guid companyId, Guid documentId)
        => Ok(new ApiResponse<List<DocumentSignatureResponse>>(true,
            await _svc.GetDocumentSignaturesAsync(companyId, documentId)));
}

// ===================================================================
// External API Controller (สำหรับระบบภายนอกยิง approve)
// ===================================================================
[ApiController]
[Route("api/companies/{companyId:guid}/external")]
[Authorize]
public class ExternalApprovalController : ControllerBase
{
    private readonly ISignatureApprovalService _svc;
    public ExternalApprovalController(ISignatureApprovalService svc) => _svc = svc;

    /// <summary>
    /// External system approves a quotation with customer signature.
    /// ระบบภายนอกส่ง approve ใบเสนอราคา พร้อมลายเซ็นลูกค้า
    /// After approval, auto-converts to Invoice (if autoConvert=true).
    /// </summary>
    [HttpPost("quotations/{documentId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<QuotationApprovalResult>>> ApproveQuotation(
        Guid companyId, Guid documentId, [FromBody] ExternalApproveRequest req)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        try
        {
            var result = await _svc.ExternalApproveQuotationAsync(companyId, documentId, req, ip,
                JwtHelper.GetUserIdFromClaims(User).ToString());
            return Ok(new ApiResponse<QuotationApprovalResult>(true, result, result.Message));
        }
        catch (Accounting.Services.Implementations.DocumentApprovalWarningsException ex)
        {
            // รอบ 193 (ฝ่ายค้านรอบสอง N6): ยังไม่บันทึกลายเซ็นลูกค้า — เรียกซ้ำพร้อม acknowledgeWarnings=true หลังตรวจรายการ
            return StatusCode(422, new ApiResponse<object>(false,
                new Accounting.Models.DTOs.Document.ApprovalWarningsResponse(ex.Warnings, null),
                "ใบเสนอราคามีคำเตือนที่ต้องรับทราบก่อนบันทึกลายเซ็นลูกค้า (ยังไม่ได้บันทึก) — ส่งซ้ำพร้อม acknowledgeWarnings=true: "
                + string.Join(" · ", ex.Warnings)));
        }
    }
}
