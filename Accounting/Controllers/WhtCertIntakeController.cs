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

    public sealed record UploadBody(Guid FileAttachmentId, Guid? DocumentId = null);
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
        [FromServices] IAttachmentAccessGate gate, CancellationToken ct)
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
        var scan = await _ocr.ScanAsync(companyId, body.FileAttachmentId, actingUserId: uid);
        var r = await _intake.SubmitFromScanAsync(companyId, scan.Id, WhtCertIntakeChannel.Manual,
            body.DocumentId is Guid d ? new[] { d } : null, name, uid, ct);
        return StatusCode(r.Duplicate ? 200 : 201, new ApiResponse<WhtCertSubmitResult>(true, r, r.Message));
    }

    [HttpPost("inbox/{id:guid}/confirm")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertIntakeDto>>> Confirm(Guid companyId, Guid id, [FromBody] WhtCertConfirmRequest? body, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await _intake.ConfirmAsync(companyId, id, body ?? new WhtCertConfirmRequest(), uid, User?.Identity?.Name ?? "user", holdsTaxFile: true, ct);
        return Ok(new ApiResponse<WhtCertIntakeDto>(true, r, "ยืนยันหนังสือรับรองแล้ว — ใช้เป็นเครดิตได้ตามยอดที่จัดสรร"));
    }

    [HttpPost("inbox/{id:guid}/reject")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<WhtCertIntakeDto>>> Reject(Guid companyId, Guid id, [FromBody] WhtCertRejectRequest body, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var r = await _intake.RejectAsync(companyId, id, body, uid, User?.Identity?.Name ?? "user", ct);
        return Ok(new ApiResponse<WhtCertIntakeDto>(true, r, "ตีกลับแล้ว"));
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
