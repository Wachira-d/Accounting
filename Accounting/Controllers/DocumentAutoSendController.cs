using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 241 — "ส่งเอกสารอัตโนมัติ": ค่าตั้งบริษัท (ลิงก์ชำระเงิน + ช่องทาง · ส่งใบเสร็จหลังรับชำระ) · ลิงก์ชำระเงินต่อเอกสาร (ดู/ออก/ยกเลิก) ·
/// งานหลังรับชำระ (ลองใหม่) — DOCUMENT_FLOW §2.12g · คำตัดสิน 241-1..241-9
/// <para>ค่าตั้งเขียนได้เฉพาะเจ้าของบริษัท (สั่งให้ระบบส่งอีเมลถึงลูกค้าในนามบริษัท + เปิดช่องรับเงิน — ระดับเดียวกับค่าตั้งลิงก์ให้ลูกค้าเซ็น รอบ 240) ·
/// ต่อเอกสาร = สิทธิ์ของชนิดเอกสารนั้น (สร้าง/ยกเลิก/อนุมัติ) · ทุก query ผูก CompanyId</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/document-auto-send")]
[Authorize]
public class DocumentAutoSendController : ControllerBase
{
    private readonly IDocumentPayLinkService _links;
    private readonly IPostPaymentAutomationService _postPay;
    private readonly ICompanyService _companies;
    private readonly IPermissionService _perms;
    private readonly AccountingDbContext _db;

    public DocumentAutoSendController(IDocumentPayLinkService links, IPostPaymentAutomationService postPay,
        ICompanyService companies, IPermissionService perms, AccountingDbContext db)
    {
        _links = links;
        _postPay = postPay;
        _companies = companies;
        _perms = perms;
        _db = db;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<DocumentAutoSendSettingsDto>>> GetSettings(Guid companyId)
    {
        // รอบ 259-3 ฝ่ายค้าน L5: บอกหน้าว่าแก้ได้ไหม (ด่านเดียวกับ PUT — EnsureOwnerAccessAsync) · ไม่ใช่เจ้าของ/คีย์ API = อ่านอย่างเดียว
        var canEdit = true;
        try { await _companies.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User)); }
        catch (BusinessRuleException) { canEdit = false; }
        var dto = await _links.GetSettingsAsync(companyId);
        return Ok(new ApiResponse<DocumentAutoSendSettingsDto>(true, dto with { CanEdit = canEdit }));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<DocumentAutoSendSettingsDto>>> UpdateSettings(Guid companyId,
        [FromBody] UpdateDocumentAutoSendSettings request)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        await _companies.EnsureOwnerAccessAsync(companyId, uid);
        return Ok(new ApiResponse<DocumentAutoSendSettingsDto>(true,
            await _links.UpdateSettingsAsync(companyId, request ?? new UpdateDocumentAutoSendSettings(), uid.ToString()),
            "บันทึกค่าตั้งส่งเอกสารอัตโนมัติแล้ว"));
    }

    private async Task<DocumentType?> DocTypeAsync(Guid companyId, Guid documentId)
        => await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync();

    [HttpGet("documents/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentPayLinkStatusDto>>> Status(Guid companyId, Guid documentId)
    {
        var t = await DocTypeAsync(companyId, documentId);
        if (t == null) return NotFound(new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User), t.Value))
            return StatusCode(403, new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่มีสิทธิ์ดูลิงก์ชำระเงินของเอกสารชนิดนี้"));
        return Ok(new ApiResponse<DocumentPayLinkStatusDto>(true, await _links.StatusAsync(companyId, documentId)));
    }

    /// <summary>ออกลิงก์ชำระเงินเดี๋ยวนี้ (หรือคืนลิงก์เดิมที่ยังใช้ได้) — ช่องทางตามค่าบริษัท · ใบที่ออกลิงก์ไม่ได้ = 400 พร้อมเหตุผล</summary>
    [HttpPost("documents/{documentId:guid}/pay-link")]
    public async Task<ActionResult<ApiResponse<DocumentPayLinkStatusDto>>> IssueLink(Guid companyId, Guid documentId)
    {
        var t = await DocTypeAsync(companyId, documentId);
        if (t == null) return NotFound(new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanCreateAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User), t.Value))
            return StatusCode(403, new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่มีสิทธิ์ออกลิงก์ชำระเงินของเอกสารชนิดนี้"));
        var r = await _links.EnsureLinkAsync(companyId, documentId, null);
        if (!r.Ok) return BadRequest(new ApiResponse<DocumentPayLinkStatusDto>(false, null, r.Refusal));
        return Ok(new ApiResponse<DocumentPayLinkStatusDto>(true, await _links.StatusAsync(companyId, documentId), "ลิงก์ชำระเงินพร้อมใช้"));
    }

    [HttpPost("documents/{documentId:guid}/pay-link/revoke")]
    public async Task<ActionResult<ApiResponse<DocumentPayLinkStatusDto>>> RevokeLink(Guid companyId, Guid documentId)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var t = await DocTypeAsync(companyId, documentId);
        if (t == null) return NotFound(new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_perms, companyId, uid, t.Value))
            return StatusCode(403, new ApiResponse<DocumentPayLinkStatusDto>(false, null, "ไม่มีสิทธิ์ยกเลิกลิงก์ชำระเงินของเอกสารชนิดนี้"));
        await _links.RevokeAsync(companyId, documentId, uid.ToString());
        return Ok(new ApiResponse<DocumentPayLinkStatusDto>(true, await _links.StatusAsync(companyId, documentId),
            "ยกเลิกลิงก์แล้ว — ลิงก์เดิมใช้จ่ายไม่ได้ทันที · อีเมลรอบถัดไปจะได้ลิงก์ใหม่"));
    }

    /// <summary>ลองงานหลังรับชำระใหม่ (หลังเติมอีเมล/ข้อมูลผู้ซื้อ/อนุมัติใบเสร็จ) — สิทธิ์อนุมัติของชนิดเอกสารต้นทาง (ระดับเดียวกับบันทึกรับชำระ)</summary>
    [HttpPost("post-payment-runs/{runId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<object>>> Retry(Guid companyId, Guid runId)
    {
        var docId = await _db.PostPaymentAutomationRuns.AsNoTracking().Where(r => r.Id == runId && r.CompanyId == companyId)
            .Select(r => (Guid?)r.DocumentId).FirstOrDefaultAsync();
        var t = docId == null ? null : await DocTypeAsync(companyId, docId.Value);
        if (t == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบงานส่งเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, JwtHelper.GetUserIdFromClaims(User), t.Value))
            return StatusCode(403, new ApiResponse<object>(false, null, "ไม่มีสิทธิ์สั่งงานส่งเอกสารของเอกสารชนิดนี้"));
        await _postPay.RetryAsync(companyId, runId);
        return Ok(new ApiResponse<object>(true, null, "ตั้งให้ลองใหม่แล้ว — ระบบจะทำภายใน ~1 นาที"));
    }
}
