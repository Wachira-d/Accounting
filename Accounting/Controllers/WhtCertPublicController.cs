using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 ช่อง ① · §6.5 · §9 เฟส 244b) — <b>ฝั่งลูกค้า (ไม่ล็อกอิน)</b>: เปิดหน้า/ส่งหนังสือรับรอง 50 ทวิ ด้วย token ของลิงก์อัปโหลด
///
/// <para>สิทธิ์ = token (ค้นด้วย hash + เทียบเวลาคงที่ · ผูกบริษัทใน URL — token บริษัท A กับ URL บริษัท B = ไม่พบ) · จำกัดความถี่ต่อ IP และต่อ token ด้วย
/// <c>IChatRateLimiter</c> แบบ DB (หลายเครื่อง · กฎ #4 D) — <b>failClosed</b> (ตารางนับใช้ไม่ได้ = ปฏิเสธ) · ไบต์ไฟล์ตัดสินชนิด (MagicBytes ใน
/// <c>WhtCertUploadLinkPolicy.Inspect</c>) · ไฟล์เข้าประตูเดียว <c>WhtCertIntakeService.SubmitAsync</c> ผ่าน <see cref="IWhtCertUploadLinkService"/></para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/public-wht/{token}")]
[AllowAnonymous]
public class WhtCertPublicController : ControllerBase
{
    private readonly IWhtCertUploadLinkService _links;
    private readonly IChatRateLimiter _limiter;

    public WhtCertPublicController(IWhtCertUploadLinkService links, IChatRateLimiter limiter)
    { _links = links; _limiter = limiter; }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>หน้าอัปโหลด: ข้อมูลออกใบในนามเรา + รายการของลูกค้ารายนี้ที่รอใบ · ลิงก์หมดอายุ ⇒ ข้อความอย่างเดียว</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<WhtUploadPublicView>>> Show(Guid companyId, string token, CancellationToken ct)
    {
        if (!await _limiter.TryConsumeAsync($"wht-upload-view:{Ip}", WhtCertUploadLinkPolicy.ViewsPerMinute, WhtCertUploadLinkPolicy.ViewsPerDay,
                ct, failClosed: true))
            return StatusCode(429, new ApiResponse<WhtUploadPublicView>(false, null, WhtCertUploadLinkPolicy.RateLimitedMessage));
        var view = await _links.PublicViewAsync(companyId, token, ct);
        if (view == null) return NotFound(new ApiResponse<WhtUploadPublicView>(false, null, WhtCertUploadLinkPolicy.NotFoundMessage));
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new ApiResponse<WhtUploadPublicView>(true, view));
    }

    /// <summary>ส่งไฟล์หนังสือรับรอง (PDF/JPG/PNG ≤ 10 MB · ทีละไฟล์) · <paramref name="documentIds"/> = เอกสารที่ลูกค้าติ๊ก (คั่นด้วย ,) ·
    /// <paramref name="senderName"/> = ชื่อผู้ส่ง (ไม่บังคับ)</summary>
    [HttpPost]
    [RequestSizeLimit(WhtCertUploadLinkPolicy.MaxBytes + 256 * 1024)]
    public async Task<ActionResult<ApiResponse<WhtUploadPublicResult>>> Upload(Guid companyId, string token, IFormFile? file,
        [FromForm] string? documentIds, [FromForm] string? senderName, CancellationToken ct)
    {
        // RateLimit ต่อ IP + ต่อ token (ลิงก์รั่วถูกยิงไฟล์ขยะได้ไม่เกินเพดานวัน) — ก่อนอ่านไฟล์
        if (!await _limiter.TryConsumeAsync($"wht-upload:{Ip}", WhtCertUploadLinkPolicy.UploadsPerMinute, WhtCertUploadLinkPolicy.UploadsPerDay,
                ct, failClosed: true)
            || !await _limiter.TryConsumeAsync($"wht-upload-token:{WhtCertUploadLinkPolicy.TokenHash(token ?? "")[..16]}",
                WhtCertUploadLinkPolicy.UploadsPerMinute, WhtCertUploadLinkPolicy.UploadsPerTokenPerDay, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<WhtUploadPublicResult>(false, null, WhtCertUploadLinkPolicy.RateLimitedMessage));
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<WhtUploadPublicResult>(false, null, "กรุณาเลือกไฟล์หนังสือรับรอง"));
        if (file.Length > WhtCertUploadLinkPolicy.MaxBytes)
            return BadRequest(new ApiResponse<WhtUploadPublicResult>(false, null, "ไฟล์ใหญ่เกิน 10 MB — ย่อรูปหรือบันทึกเป็น PDF ขนาดเล็กลงแล้วลองใหม่"));
        byte[] content;
        await using (var s = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms, ct);
            content = ms.ToArray();
        }
        var ids = (documentIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Take(50).ToList();
        try
        {
            var r = await _links.PublicUploadAsync(companyId, token ?? "", content, ids, senderName, ct);
            if (r == null) return NotFound(new ApiResponse<WhtUploadPublicResult>(false, null, WhtCertUploadLinkPolicy.NotFoundMessage));
            return Ok(new ApiResponse<WhtUploadPublicResult>(true, r, r.Message));
        }
        catch (BusinessRuleException ex)
        {
            return StatusCode(ex.StatusCode, new ApiResponse<WhtUploadPublicResult>(false, null, ex.Message));
        }
    }
}
