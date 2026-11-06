using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด · LINE_MULTICHANNEL_DESIGN §3.b/§7.1 public) — หน้า "ผูก LINE รับเอกสาร" ของผู้รับฝั่งผู้ติดต่อ
/// (<c>wwwroot/pages/contact-line-join.html</c>) · ไม่ต้องล็อกอิน · <b>สิทธิ์ = token</b> ในลิงก์ (สุ่ม 32 ไบต์ · เก็บ hash · ใช้ครั้งเดียว · หมดอายุ 7 วัน) ·
/// token ส่งใน body (POST) ไม่ใช่ใน URL ของ API (ไม่ค้างใน access log) · กดยินยอมจำกัดความถี่ต่อ IP (DB) · ลิงก์ใช้ไม่ได้ ⇒ ข้อความเดียว (ไม่บอกคนนอกว่าเพราะอะไร)
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/contact-line-join/{companyId:guid}")]
public class ContactLineJoinController : ControllerBase
{
    private readonly IContactLineBindingService _binding;

    public ContactLineJoinController(IContactLineBindingService binding) => _binding = binding;

    public sealed record JoinRequest(string? Token);

    [HttpPost("view")]
    public async Task<ActionResult<ApiResponse<ContactLineJoinView>>> View(Guid companyId, [FromBody] JoinRequest request, CancellationToken ct)
    {
        var v = await _binding.GetJoinViewAsync(companyId, request?.Token, ct);
        return v.Problem != null
            ? NotFound(new ApiResponse<ContactLineJoinView>(false, v, v.Problem))
            : Ok(new ApiResponse<ContactLineJoinView>(true, v));
    }

    [HttpPost("consent")]
    public async Task<ActionResult<ApiResponse<ContactLineJoinConsentResponse>>> Consent(Guid companyId, [FromBody] JoinRequest request, CancellationToken ct)
    {
        var r = await _binding.ConsentAsync(companyId, request?.Token, HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), ct);
        return Ok(new ApiResponse<ContactLineJoinConsentResponse>(true, r, "บันทึกความยินยอมแล้ว — ส่งรหัสในแชต LINE ของบริษัทเพื่อผูก"));
    }
}
