using System.Net;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Pdpa;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฐ) — คำขอลบข้อมูล (PDPA ม.33) ที่ผู้ใช้ยื่นเองระดับแพลตฟอร์ม: SystemAdmin ดูรายการ (เรียงค้าง → ครบกำหนดก่อน) ·
/// "ดำเนินการลบ" = แกนลบตัวเดียวกับ DSR ของบริษัท (<c>PdpaService.AnonymiseUserCoreAsync</c> — legal hold ของหลักฐานยอมรับนโยบาย) ·
/// ปฏิเสธต้องมีเหตุผล · แจ้งผลทางอีเมล (อีเมล ณ ตอนยื่น — เก็บไว้ก่อนลบ) · หน้า <c>/admin/legal.html</c> แท็บคำขอลบ
/// </summary>
[ApiController]
[Route("api/admin/platform-erasure-requests")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformErasureController : ControllerBase
{
    private readonly IPdpaService _pdpa;
    private readonly IEmailService _email;
    private readonly ILogger<AdminPlatformErasureController> _logger;

    public AdminPlatformErasureController(IPdpaService pdpa, IEmailService email, ILogger<AdminPlatformErasureController> logger)
    {
        _pdpa = pdpa;
        _email = email;
        _logger = logger;
    }

    private static object Dto(PlatformErasureRequest r) => new
    {
        r.Id, r.UserId, r.RequesterEmail, r.RequestedAt, r.DueAt, r.Status, r.Reason, r.ResolvedAt, r.ResolutionNote,
        overdue = r.Status == PlatformErasureRequestPolicy.StatusPending && r.DueAt < DateTime.UtcNow,
    };

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List([FromQuery] string? status, CancellationToken ct)
    {
        var rows = await _pdpa.ListPlatformErasureRequestsAsync(string.IsNullOrWhiteSpace(status) ? null : status, ct);
        return Ok(new ApiResponse<object>(true, rows.Select(Dto).ToList()));
    }

    public sealed record ResolveRequest(string? Note);

    [HttpPost("{id:guid}/complete")]
    [Accounting.Filters.RejectApiKey("ลบข้อมูลผู้ใช้ตามคำขอ")]
    public async Task<ActionResult<ApiResponse<object>>> Complete(Guid id, [FromBody] ResolveRequest? req, CancellationToken ct)
    {
        var admin = JwtHelper.GetUserIdFromClaims(User);
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (row, notify) = await _pdpa.CompletePlatformErasureAsync(id, admin, req?.Note, ip, ct);
        var sent = await NotifyAsync(notify, "ดำเนินการลบข้อมูลส่วนบุคคลตามคำขอแล้ว",
            "ระบบได้ลบ/ทำให้ไม่สามารถระบุตัวตนได้สำหรับข้อมูลบัญชีผู้ใช้ของท่านตามคำขอ (พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.33) แล้ว · "
            + "ข้อมูลที่กฎหมายกำหนดให้ต้องเก็บ (เช่น หลักฐานการยอมรับข้อกำหนด เอกสารบัญชี/ภาษี) ยังคงเก็บไว้ในรูปที่ไม่ระบุตัวตนตามระยะเวลาที่กฎหมายกำหนด");
        return Ok(new ApiResponse<object>(true, Dto(row), sent ? "ลบข้อมูลแล้ว · แจ้งผู้ใช้ทางอีเมลแล้ว" : "ลบข้อมูลแล้ว · ส่งอีเมลแจ้งผลไม่สำเร็จ (ตรวจอีเมลระบบ)"));
    }

    [HttpPost("{id:guid}/reject")]
    [Accounting.Filters.RejectApiKey("ปฏิเสธคำขอลบข้อมูล")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(Guid id, [FromBody] ResolveRequest? req, CancellationToken ct)
    {
        var admin = JwtHelper.GetUserIdFromClaims(User);
        var row = await _pdpa.RejectPlatformErasureAsync(id, admin, req?.Note ?? "", ct);
        var sent = await NotifyAsync(row.RequesterEmail, "ผลคำขอลบข้อมูลส่วนบุคคล",
            "คำขอลบข้อมูลของท่านไม่สามารถดำเนินการได้ เหตุผล: " + row.ResolutionNote);
        return Ok(new ApiResponse<object>(true, Dto(row), sent ? "ปฏิเสธแล้ว · แจ้งผู้ใช้ทางอีเมลแล้ว" : "ปฏิเสธแล้ว · ส่งอีเมลแจ้งผลไม่สำเร็จ"));
    }

    /// <summary>แจ้งผล — ส่งไม่สำเร็จไม่ทำให้การลบล้ม (ลบไปแล้ว) แต่บอกแอดมินในข้อความตอบ (ล้มดัง ไม่เงียบ)</summary>
    private async Task<bool> NotifyAsync(string to, string subject, string text)
    {
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@') || to.EndsWith("@deleted.local")) return false;
        try { return await _email.TrySendAsync(to, subject, $"<p>{WebUtility.HtmlEncode(text)}</p>"); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "แจ้งผลคำขอลบข้อมูลทางอีเมลไม่สำเร็จ");
            return false;
        }
    }
}
