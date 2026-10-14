using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รายงานตรวจข้อมูลที่อาจผิดจากบั๊กที่แก้แล้ว (คำตัดสินเจ้าของ 2026-10-08: "รายงานอ่านอย่างเดียวก่อน" — ผู้ทำบัญชีตรวจแล้วซ่อมรายตัว) ·
/// รายงานอ่านอย่างเดียว · สิทธิ์ระดับรายงานการเงิน · ปุ่ม "แก้" รายใบมีเฉพาะแถวที่แก้ได้โดยเงินไม่ขยับ (ทีม E 2026-10-13: ส่วนลดบรรทัดจากสแกน)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/data-integrity")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsFinancial)]
public class DataIntegrityController : ControllerBase
{
    private readonly DataIntegrityReportService _service;
    private readonly IPermissionService _permissions;
    public DataIntegrityController(DataIntegrityReportService service, IPermissionService permissions)
    {
        _service = service;
        _permissions = permissions;
    }

    [HttpGet("suspects")]
    public async Task<ActionResult<ApiResponse<DataIntegrityReport>>> Suspects(Guid companyId)
    {
        var r = await _service.GetAsync(companyId);
        var n = r.Total;   // นับทุกหมวดที่ record รู้จัก (รอบ 203: เพิ่ม PlainReceiptsWithVat) — ที่เดียว ไม่ลืมหมวดใหม่
        return Ok(new ApiResponse<DataIntegrityReport>(true, r, n == 0 ? "ไม่พบรายการที่ต้องตรวจ" : $"พบ {n} รายการที่ควรให้ผู้ทำบัญชีตรวจ"));
    }

    /// <summary>
    /// ทีม E 2026-10-13 — แก้ส่วนลดบรรทัดของเอกสารจากสแกนหนึ่งใบ (ตั้ง % = 0 · ส่วนลดบาท/ราคาให้บันทึกซ้ำได้ยอดเดิม) ·
    /// ยอด/VAT/ยอดรวม/JE ไม่เปลี่ยน · เงินบรรทัดใดจะขยับ ⇒ ปฏิเสธ (ไม่บันทึก) · audit <c>OCR-LINE-DISC-FIX</c> ·
    /// สิทธิ์: รายงานการเงิน (คลาส) + แก้เอกสารทิศนั้น (ฉบับร่าง = สร้าง · ออกแล้ว = อนุมัติ)
    /// </summary>
    [HttpPost("ocr-line-discount/{documentId:guid}/fix")]
    public async Task<ActionResult<ApiResponse<OcrLineDiscountFixResult>>> FixOcrLineDiscount(Guid companyId, Guid documentId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var target = await _service.DocumentTypeAsync(companyId, documentId);
        if (target is not { } t)
            return NotFound(new ApiResponse<OcrLineDiscountFixResult>(false, null, "ไม่พบเอกสาร"));
        var allowed = t.Status == DocumentStatus.Draft
            ? await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, t.Type)
            : await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, t.Type);
        if (!allowed)
            return StatusCode(403, new ApiResponse<OcrLineDiscountFixResult>(false, null,
                $"ไม่มีสิทธิ์แก้เอกสาร {t.Type} (ต้องการสิทธิ์{(t.Status == DocumentStatus.Draft ? "สร้าง" : "อนุมัติ")}เอกสารทิศนี้)"));
        var actor = userId == Guid.Empty ? (Guid?)null : userId;
        var result = await _service.FixOcrLineDiscountAsync(companyId, documentId, actor);
        return Ok(new ApiResponse<OcrLineDiscountFixResult>(true, result, result.Message));
    }
}
