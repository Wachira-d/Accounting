using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 240 — ค่าตั้งของ "ลิงก์ให้ลูกค้าเซ็น" ระดับบริษัท: อีเมลขอยอมรับอัตโนมัติเมื่ออนุมัติใบเสนอราคา · อีเมลเตือน · รหัสลับลงนาม webhook ต่อเอกสาร ·
/// โฮสต์ที่อนุญาต · ประวัติเหตุการณ์ทั้งบริษัท (DOCUMENT_FLOW §2.12f · คำตัดสิน 240-2/240-7)
/// <para>เขียน = <b>เจ้าของบริษัทเท่านั้น</b> (<c>EnsureOwnerAccessAsync</c> — ปฏิเสธ API key ด้วย) เหมือนการลงทะเบียน webhook: ค่าตั้งนี้ส่งข้อมูลออกนอกระบบ
/// และสั่งให้ระบบส่งอีเมลถึงลูกค้าในนามบริษัท</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/sign-requests")]
[Authorize]
public class SignRequestController : ControllerBase
{
    private readonly ISignRequestService _signs;
    private readonly ICompanyService _companies;

    public SignRequestController(ISignRequestService signs, ICompanyService companies)
    {
        _signs = signs;
        _companies = companies;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<SignRequestSettingsDto>>> GetSettings(Guid companyId)
        => Ok(new ApiResponse<SignRequestSettingsDto>(true, await _signs.GetSettingsAsync(companyId)));

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<SignRequestSettingsDto>>> UpdateSettings(Guid companyId, [FromBody] UpdateSignRequestSettings request)
    {
        await _companies.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        return Ok(new ApiResponse<SignRequestSettingsDto>(true, await _signs.UpdateSettingsAsync(companyId, request ?? new UpdateSignRequestSettings()),
            "บันทึกค่าตั้งลิงก์ให้ลูกค้าเซ็นแล้ว"));
    }

    /// <summary>ออกรหัสลับลงนาม webhook ใหม่ — คืนค่าเต็มครั้งเดียว (รหัสเดิมใช้ไม่ได้ทันที · ปลายทางต้องเปลี่ยนตาม)</summary>
    [HttpPost("settings/rotate-secret")]
    public async Task<ActionResult<ApiResponse<object>>> RotateSecret(Guid companyId)
    {
        await _companies.EnsureOwnerAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User));
        var secret = await _signs.RotateCallbackSecretAsync(companyId);
        return Ok(new ApiResponse<object>(true, new { secret },
            "ออกรหัสลับใหม่แล้ว — คัดลอกไปตั้งที่ระบบปลายทางตอนนี้ (จะไม่แสดงเต็มอีก) · รหัสเดิมใช้ตรวจลายเซ็นไม่ได้แล้ว"));
    }

    [HttpGet("events")]
    public async Task<ActionResult<ApiResponse<List<SignEventDto>>>> GetEvents(Guid companyId, [FromQuery] int take = 50)
        => Ok(new ApiResponse<List<SignEventDto>>(true, await _signs.GetEventsAsync(companyId, null, take)));
}
