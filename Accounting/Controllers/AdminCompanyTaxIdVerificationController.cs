using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 285 — คิวตรวจหลักฐานเลขผู้เสียภาษีของบริษัท (ผู้ดูแลแพลตฟอร์ม · SystemAdmin) · อนุมัติ/ปฏิเสธ/ถอน ผ่าน
/// <see cref="CompanyTaxIdVerificationService"/> เท่านั้น (ตัวตัดสิน <see cref="CompanyTaxIdVerification"/> · audit ทุกครั้ง) · หน้า <c>/admin/taxid-verifications.html</c>
/// </summary>
[ApiController]
[Route("api/admin/tax-id-verifications")]
[Authorize(Roles = "SystemAdmin")]
public class AdminCompanyTaxIdVerificationController : ControllerBase
{
    private readonly CompanyTaxIdVerificationService _service;

    public AdminCompanyTaxIdVerificationController(CompanyTaxIdVerificationService service) => _service = service;

    public record ReviewRequest(string? Note);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List([FromQuery] string? status, CancellationToken ct)
        => Ok(new ApiResponse<object>(true, new
        {
            Rows = await _service.ListForAdminAsync(status, ct),
            Guidance = "เปิดไฟล์หลักฐานทุกครั้งก่อนอนุมัติ · ตรวจว่าเลขผู้เสียภาษี + ชื่อบนเอกสารตรงกับคำขอ · ชื่อตรงทะเบียนเพียงอย่างเดียวไม่ใช่หลักฐานความเป็นเจ้าของ (ทะเบียนเป็นข้อมูลสาธารณะ) · " +
                       "อนุมัติแล้วบริษัทเห็นคำขอเชื่อมคู่ค้าที่ส่งถึงเลขนี้ก่อนสมัคร และคู่ค้าเห็นป้าย \"ยืนยันแล้ว\"",
        }));

    [HttpGet("{requestId:guid}/evidence")]
    public async Task<IActionResult> Evidence(Guid requestId, CancellationToken ct)
    {
        var f = await _service.ReadEvidenceAsync(requestId, ct);
        if (f == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบไฟล์หลักฐาน"));
        return File(f.Value.Bytes, f.Value.ContentType, f.Value.FileName);
    }

    [HttpPost("{requestId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<object>>> Approve(Guid requestId, [FromBody] ReviewRequest? req, CancellationToken ct)
    {
        var adminId = JwtHelper.GetUserIdFromClaims(User);
        if (adminId == Guid.Empty) return Unauthorized();
        await _service.ApproveAsync(requestId, adminId, req?.Note, DateTime.UtcNow, ct);
        return Ok(new ApiResponse<object>(true, null, "อนุมัติแล้ว — บริษัทยืนยันเลขผู้เสียภาษีแล้ว"));
    }

    [HttpPost("{requestId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid requestId, [FromBody] ReviewRequest? req, CancellationToken ct)
    {
        var adminId = JwtHelper.GetUserIdFromClaims(User);
        if (adminId == Guid.Empty) return Unauthorized();
        await _service.RejectAsync(requestId, adminId, req?.Note, DateTime.UtcNow, ct);
        return Ok(new ApiResponse<object>(true, null, "ปฏิเสธแล้ว"));
    }

    [HttpPost("{requestId:guid}/revoke")]
    public async Task<ActionResult<ApiResponse<object>>> Revoke(Guid requestId, [FromBody] ReviewRequest? req, CancellationToken ct)
    {
        var adminId = JwtHelper.GetUserIdFromClaims(User);
        if (adminId == Guid.Empty) return Unauthorized();
        await _service.RevokeAsync(requestId, adminId, req?.Note, DateTime.UtcNow, ct);
        return Ok(new ApiResponse<object>(true, null, "ถอนการยืนยันแล้ว"));
    }
}
