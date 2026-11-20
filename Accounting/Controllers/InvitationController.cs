using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ฝั่ง "ผู้ถูกเชิญ" ของคำเชิญเข้าบริษัท — สองทาง:
///   1) <c>/by-token/{token}</c> — ไม่ต้องล็อกอิน: หน้ารับคำเชิญแสดง "{ผู้เชิญ} เชิญคุณเข้าร่วม {บริษัท}" + บอกว่ามีบัญชีแล้วหรือยัง
///   2) <c>/accept</c> — ล็อกอินแล้ว: ตอบรับในนามผู้ที่ล็อกอิน (อีเมลต้องตรงคำเชิญ)
/// รอบ 277: ตรรกะทั้งหมดอยู่ที่ <see cref="ICompanyInvitationService"/> (ตัวเดียวกับสมัครผ่านลิงก์ใน AuthService) · ฝั่งเจ้าของ
/// (เชิญ/รายการรอตอบรับ/ส่งซ้ำ/ยกเลิก) อยู่ที่ <c>CompanyController</c>
/// </summary>
[ApiController]
[Route("api/invitations")]
public class InvitationController : ControllerBase
{
    private readonly ICompanyInvitationService _invitations;
    public InvitationController(ICompanyInvitationService invitations) { _invitations = invitations; }

    [HttpGet("by-token/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<InvitationPreviewResponse>>> Preview(string token)
        => Ok(new ApiResponse<InvitationPreviewResponse>(true, await _invitations.PreviewAsync(token), null));

    public record AcceptRequest(string Token);

    [HttpPost("accept")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<AcceptInvitationResult>>> Accept([FromBody] AcceptRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _invitations.AcceptAsync(userId, req?.Token);
        return Ok(new ApiResponse<AcceptInvitationResult>(true, result, result.Message));
    }
}
