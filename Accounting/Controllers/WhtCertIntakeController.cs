using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §7.2) — กล่องรับ 50 ทวิ ขาเข้า (ใบที่ลูกค้าหักเราแล้วส่งให้) ·
/// ช่องทางพนักงานอัปโหลดเอง (ช่อง ②) เข้าประตูเดียว <see cref="IWhtCertIntakeService.SubmitAsync"/> ผ่าน OCR pipeline เดิม ·
/// สิทธิ์ = ภาษี (Tax.File) ทุกเส้น (ชุดเดียวกับไฟล์แนบ "WhtCredit" — <c>AttachmentPermissionScope</c>) · ทุก query ผูก CompanyId
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/wht-certs")]
[Authorize]
public class WhtCertIntakeController : ControllerBase
{
    private readonly IWhtCertIntakeService _intake;
    private readonly IOcrService _ocr;
    private readonly AccountingDbContext _db;

    public WhtCertIntakeController(IWhtCertIntakeService intake, IOcrService ocr, AccountingDbContext db)
    {
        _intake = intake; _ocr = ocr; _db = db;
    }

    /// <param name="EWithholding">รอบ 253 (WP-10 · N4): ไฟล์นี้เป็นหลักฐาน e-WHT — แนบเป็นใบ 50 ทวิ ธรรมดา (ด่านเดียวกัน · ไม่เสนออัตรา 1%)</param>
    /// <param name="EvidenceReference">เลขอ้างอิง e-WHT (ข้อความอิสระ · ไม่บังคับ)</param>
    public sealed record UploadBody(Guid FileAttachmentId, Guid? DocumentId = null, bool EWithholding = false, string? EvidenceReference = null);
    /// <summary>รอบ 253 (WP-10): ออกลิงก์อัปโหลด 50 ทวิ ให้ลูกค้า — ระบุเอกสาร (ผู้ติดต่อของเอกสาร) หรือผู้ติดต่อ</summary>
    public sealed record IssueLinkBody(Guid? ContactId = null, Guid? DocumentId = null);
    public sealed record FilterOption(string Value, string Label);

    /// <summary>ตัวเลือกตัวกรอง "50 ทวิ" ของหน้ารายการเอกสาร (ค่า + ป้ายจากตัวตัดสินเดียว — หน้าเว็บไม่มีสำเนา)</summary>
    [HttpGet("filter-options")]
    public ActionResult<ApiResponse<List<FilterOption>>> FilterOptions(Guid companyId)
        => Ok(new ApiResponse<List<FilterOption>>(true,
            WhtCertIntakeStatus.FilterOptions.Select(o => new FilterOption(o.Value, o.Label)).ToList()));

    [HttpGet("inbox")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<List<WhtCertIntakeDto>>>> Inbox(Guid companyId, [FromQuery] string? status, CancellationToken ct)
        => Ok(new ApiResponse<List<WhtCertIntakeDto>>(true, await _intake.ListInboxAsync(companyId, status, ct)));

    [HttpGet("inbox/{id:guid}/candidates")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<List<WhtCertCandidateDto>>>> Candidates(Guid companyId, Guid id, CancellationToken ct)
        => Ok(new ApiResponse<List<WhtCertCandidateDto>>(true, await _intake.CandidatesAsync(companyId, id, ct)));

    /// <summary>ช่อง ② — อัปโหลดใบ 50 ทวิ (ไฟล์ที่อัปโหลดไว้ในถัง WhtCredit ก่อนบันทึก) ⇒ OCR ⇒ ประตูเดียว · ไฟล์เดิมซ้ำ = แถวเดิม (ไม่สแกนซ้ำ)</summary>
    [HttpPost("inbox")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertSubmitResult>>> Upload(Guid companyId, [FromBody] UploadBody body,
        [FromServices] IAttachmentAccessGate gate, [FromServices] ILogger<WhtCertIntakeController> log, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        // ด่านไฟล์แนบกลาง — ไฟล์ในถัง "ภาษีถูกหัก" ก่อนบันทึก: ผู้อัปโหลดเอง หรือผู้ถือ Tax.File (ก่อนอ่าน/สแกนไฟล์ใด ๆ)
        var deny = await gate.DenyAttachmentAsync(companyId, uid, "WhtCredit", Guid.Empty, AttachmentAccess.Read,
            "ส่งใบ 50 ทวิ เข้ากล่องรับ", body.FileAttachmentId);
        if (deny is { } d0) return StatusCode(d0.Status, new ApiResponse<WhtCertSubmitResult>(false, null, d0.Message));
        var file = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.Id == body.FileAttachmentId && f.CompanyId == companyId)
            .Select(f => new { f.EntityType, f.EntityId, f.UploadedByUserId }).FirstOrDefaultAsync(ct);
        if (file == null)
            return NotFound(new ApiResponse<WhtCertSubmitResult>(false, null, "ไม่พบไฟล์ในบริษัทนี้ — อัปโหลดไฟล์ใหม่แล้วลองอีกครั้ง"));
        // ไฟล์ต้องอยู่ในถัง "ภาษีถูกหัก" ก่อนบันทึก (ไม่หยิบไฟล์ของรายการ/เอกสารอื่นมาเป็นหลักฐาน — Q4 รอบ 193)
        if (AttachmentPermissionScope.WhtCreditFileLink(file.EntityType, file.EntityId, Guid.NewGuid()) != WhtCreditFileLinkKind.AdoptFromBucket)
            return BadRequest(new ApiResponse<WhtCertSubmitResult>(false, null,
                "ไฟล์นี้เป็นไฟล์ของรายการอื่น — อัปโหลดไฟล์ใบ 50 ทวิ จากหน้า \"ภาษีถูกหัก ณ ที่จ่าย\" แล้วลองอีกครั้ง"));
        if (body.DocumentId is Guid docId
            && !await _db.Documents.AsNoTracking().AnyAsync(d => d.Id == docId && d.CompanyId == companyId, ct))
            return NotFound(new ApiResponse<WhtCertSubmitResult>(false, null, "ไม่พบเอกสารที่ระบุในบริษัทนี้"));

        var name = User?.Identity?.Name ?? "user";
        // ไฟล์เดิมเคยส่งแล้ว ⇒ คืนแถวเดิมก่อนเสียโควตา OCR (SubmitAsync กันซ้ำอีกชั้นด้วย hash เดียวกัน)
        if (await _intake.FindDuplicateFileAsync(companyId, body.FileAttachmentId, ct) is { } dup)
            return Ok(new ApiResponse<WhtCertSubmitResult>(true, dup, dup.Message));
        var hints = body.DocumentId is Guid d ? new[] { d } : null;
        var kind = WhtEvidenceReference.KindFor(body.EWithholding);
        Guid? scanId = null;
        try { scanId = (await _ocr.ScanAsync(companyId, body.FileAttachmentId, actingUserId: uid)).Id; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // รอบ 257 ชุด 2 (B-L3): OCR ล้ม (ผู้ให้บริการล่ม · ไฟล์อ่านไม่ได้ · โควตา) — เดิม error ดิบถึงผู้ใช้และไฟล์ค้างในถัง ⇒ เข้ากล่องรับแบบรอตรวจจากไฟล์
            log.LogWarning(ex, "สแกนใบ 50 ทวิ ที่อัปโหลดเองไม่สำเร็จ ไฟล์ {File} บริษัท {Company} — เข้ากล่องรับแบบรอตรวจ", body.FileAttachmentId, companyId);
        }
        var r = scanId is Guid sid
            ? await _intake.SubmitFromScanAsync(companyId, sid, WhtCertIntakeChannel.Manual, hints, name, uid, ct, kind, body.EvidenceReference)
            : await _intake.SubmitUnreadableAsync(companyId, WhtCertIntakeChannel.Manual, body.FileAttachmentId, null, hints, name, uid, ct,
                evidenceKind: kind, evidenceReference: body.EvidenceReference);
        return StatusCode(r.Duplicate ? 200 : 201, new ApiResponse<WhtCertSubmitResult>(true, r, r.Message));
    }

    [HttpPost("inbox/{id:guid}/confirm")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertIntakeDto>>> Confirm(Guid companyId, Guid id, [FromBody] WhtCertConfirmRequest? body,
        [FromServices] IPermissionService perms, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await _intake.ConfirmAsync(companyId, id, body ?? new WhtCertConfirmRequest(), uid, User?.Identity?.Name ?? "user", holdsTaxFile: true, ct,
            await JournalActorAsync(companyId, uid, perms));
        return Ok(new ApiResponse<WhtCertIntakeDto>(true, r,
            r.FollowUpNote is string note ? "ยืนยันหนังสือรับรองแล้ว — " + note : "ยืนยันหนังสือรับรองแล้ว — ใช้เป็นเครดิตได้ตามยอดที่จัดสรร"));
    }

    /// <summary>รอบ 257 ชุด 2 (B-L3/B-H1): ผู้ลงบัญชี — ส่งเฉพาะผู้ถือ Journal.Manage (ไม่ถือ + ต้องกลับรายการตัดสูญ ⇒ service ปฏิเสธ 403 พร้อมเหตุ)</summary>
    private async Task<Accounting.Services.Implementations.WhtCreditService.WriteOffActor?> JournalActorAsync(Guid companyId, Guid uid, IPermissionService perms)
    {
        return await perms.HasPermissionAsync(companyId, uid, PermissionKeys.JournalManage)
            ? new Accounting.Services.Implementations.WhtCreditService.WriteOffActor(uid, User?.Identity?.Name ?? "user") : null;
    }

    /// <summary>รอบ 257 ชุด 2 (B-H2): ถอนการยืนยันใบที่ลงเครดิตแล้ว (ยืนยันผิด · ผู้จ่ายยกเลิกใบ) — ใช้เครดิตในแบบที่ยื่นแล้ว ⇒ 409 · ต้องกลับตัดสูญ ⇒ ต้องมี Journal.Manage</summary>
    [HttpPost("inbox/{id:guid}/unconfirm")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertIntakeDto>>> Unconfirm(Guid companyId, Guid id, [FromBody] WhtCertUnconfirmRequest body,
        [FromServices] IPermissionService perms, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await _intake.UnconfirmAsync(companyId, id, body, uid, User?.Identity?.Name ?? "user", ct, await JournalActorAsync(companyId, uid, perms));
        return Ok(new ApiResponse<WhtCertIntakeDto>(true, r, "ถอนการยืนยันแล้ว — ยอดเครดิตจากใบนี้ถูกถอนออกจากทะเบียน (รายการกลับไปรอใบ)"));
    }

    [HttpPost("inbox/{id:guid}/reject")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertIntakeDto>>> Reject(Guid companyId, Guid id, [FromBody] WhtCertRejectRequest body, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await _intake.RejectAsync(companyId, id, body, uid, User?.Identity?.Name ?? "user", ct);
        return Ok(new ApiResponse<WhtCertIntakeDto>(true, r, "ตีกลับแล้ว"));
    }

    // ═══════════ รอบ 253 (WP-10 · AUTO_SEND §3.2 ช่อง ①): ลิงก์อัปโหลด 50 ทวิ ให้ลูกค้า (ไม่ต้องล็อกอิน) — สิทธิ์ Tax.File ═══════════

    /// <summary>ออกลิงก์ใหม่ (URL มี token — แสดงครั้งเดียว · ฐานเก็บเฉพาะ hash) · ผู้ขายคัดลอกส่งทาง LINE/อีเมลเอง</summary>
    [HttpPost("upload-links")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertUploadLinkIssued>>> IssueUploadLink(Guid companyId, [FromBody] IssueLinkBody body,
        [FromServices] IWhtCertUploadLinkService links, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await links.IssueAsync(companyId, body?.ContactId, body?.DocumentId, uid, User?.Identity?.Name ?? "user", ct);
        return Ok(new ApiResponse<WhtCertUploadLinkIssued>(true, r, r.Problem ?? "ออกลิงก์อัปโหลดแล้ว — คัดลอกส่งให้ลูกค้า (ลิงก์นี้แสดงครั้งเดียว)"));
    }

    [HttpGet("upload-links")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<List<WhtCertUploadLinkDto>>>> UploadLinks(Guid companyId, [FromQuery] Guid contactId,
        [FromServices] IWhtCertUploadLinkService links, CancellationToken ct)
        => Ok(new ApiResponse<List<WhtCertUploadLinkDto>>(true, await links.ListAsync(companyId, contactId, ct)));

    [HttpPost("upload-links/{linkId:guid}/revoke")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<bool>>> RevokeUploadLink(Guid companyId, Guid linkId,
        [FromServices] IWhtCertUploadLinkService links, CancellationToken ct)
    {
        await links.RevokeAsync(companyId, linkId, User?.Identity?.Name ?? "user", ct);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกลิงก์แล้ว — ลูกค้าใช้ลิงก์นี้ส่งไฟล์ไม่ได้อีก"));
    }

    /// <summary>การ์ด "50 ทวิ ที่ต้องตาม" — ทั้งบริษัท (แดชบอร์ด) หรือรายผู้ติดต่อ (<paramref name="contactId"/>)</summary>
    [HttpGet("tracking")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertTrackingDto>>> Tracking(Guid companyId, [FromQuery] Guid? contactId,
        [FromQuery] int? taxYear, CancellationToken ct)
        => Ok(new ApiResponse<WhtCertTrackingDto>(true, await _intake.TrackingAsync(companyId, contactId, taxYear, ct)));

    /// <summary>รายงานปลายปี "50 ทวิ ที่ยังขาด" (AUTO_SEND §4.5)</summary>
    [HttpGet("missing-report")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertMissingReportDto>>> MissingReport(Guid companyId, [FromQuery] int taxYear, CancellationToken ct)
        => Ok(new ApiResponse<WhtCertMissingReportDto>(true, await _intake.MissingReportAsync(companyId, taxYear, ct)));

    /// <summary>ส่งออก Excel ของรายงานปลายปี (MiniExcel — ตัวเลขชุดเดียวกับหน้าจอ)</summary>
    [HttpGet("missing-report/export")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<IActionResult> MissingReportExport(Guid companyId, [FromQuery] int taxYear, CancellationToken ct)
    {
        var rep = await _intake.MissingReportAsync(companyId, taxYear, ct);
        var rows = rep.Rows.Select(r => new
        {
            ลูกค้า = r.PayerName,
            เลขผู้เสียภาษี = r.PayerTaxId ?? "",
            เอกสาร = r.DocumentNumber ?? "",
            วันที่ถูกหัก = r.WithheldSince.HasValue ? ThaiDate.YyyyMmDd(r.WithheldSince.Value) : "",
            ยอดเงินได้ = r.IncomeAmount,
            ภาษีถูกหัก = r.WhtAmount,
            ได้รับใบแล้ว = r.ReceivedAmount,
            ยังขาด = r.Missing,
            สถานะ = r.Label,
            วันค้าง = r.DaysWaiting ?? 0,
            เกินกำหนด = r.IsOverdue ? "ใช่" : "",
            ช่องทางล่าสุด = r.LastChannel ?? "",
        }).ToList();
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, rows);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"wht-cert-missing-{taxYear + 543}.xlsx");
    }
}
