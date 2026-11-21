using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 285 — บริษัทยื่นหลักฐานยืนยันเลขผู้เสียภาษี (คำตัดสิน 285-1…285-6 · ตัวตัดสิน <see cref="CompanyTaxIdVerification"/>) ·
/// ดูสถานะ = สมาชิกบริษัท · ยื่น = สิทธิ์ตั้งค่าบริษัท (ทางเดียวกับการแก้เลขภาษี) · ผู้อนุมัติ = ผู้ดูแลแพลตฟอร์ม (<see cref="AdminCompanyTaxIdVerificationController"/>)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/tax-id-verification")]
[Authorize]
public class CompanyTaxIdVerificationController : ControllerBase
{
    private readonly CompanyTaxIdVerificationService _service;

    public CompanyTaxIdVerificationController(CompanyTaxIdVerificationService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, CancellationToken ct)
    {
        var s = await _service.GetStatusAsync(companyId, ct);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));
        return Ok(new ApiResponse<object>(true, s));
    }

    /// <summary>ยื่นหลักฐาน (multipart: <c>file</c> + <c>evidenceKind</c>) — ข้อผิดพลาดที่ผู้ใช้แก้ได้ = 400/409 ข้อความไทย (ExceptionMiddleware)</summary>
    [HttpPost]
    [RequestSizeLimit(CompanyTaxIdVerification.MaxEvidenceBytes + 1024 * 1024)]
    [Accounting.Filters.RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ยื่นหลักฐานยืนยันเลขผู้เสียภาษี")]
    public async Task<ActionResult<ApiResponse<object>>> Submit(Guid companyId, IFormFile? file, [FromForm] string? evidenceKind, CancellationToken ct)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (userId == Guid.Empty) return Unauthorized();
        if (file == null || file.Length == 0) return BadRequest(new ApiResponse<object>(false, null, "แนบไฟล์หลักฐาน (PDF หรือรูปภาพ)"));
        if (file.Length > CompanyTaxIdVerification.MaxEvidenceBytes) return BadRequest(new ApiResponse<object>(false, null, "ไฟล์หลักฐานใหญ่เกิน 10 MB"));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var r = await _service.SubmitAsync(companyId, userId, evidenceKind, ms.ToArray(), file.FileName, DateTime.UtcNow, ct);
        return Ok(new ApiResponse<object>(true, r, "ยื่นหลักฐานแล้ว — รอผู้ดูแลระบบ NextAcc ตรวจ"));
    }
}
