using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 267 แพ็ก P4 — เส้น "หลังจ่าย" ของหน้าเอกสารสำหรับลูกค้า (ไม่ล็อกอิน · สิทธิ์ = token ของใบ · DOCUMENT_FLOW §2.12n-P4):
/// <list type="bullet">
/// <item><c>POST documents/{token}/wht-cert</c> — ส่งหนังสือรับรอง 50 ทวิ ของใบนี้ (แกนเดียวกับลิงก์อัปโหลด 50 ทวิ เดิม —
/// <see cref="IWhtCertUploadLinkService.UploadForDocumentAsync"/> · ไม่มีระบบอัปโหลดที่สอง · 267-1-48)</item>
/// <item><c>POST documents/{token}/related/{relId}/page-link</c> — เปิดหน้าเอกสารของใบที่เกี่ยวข้องเพื่อชำระรายใบ (ใบวางบิล → ใบแจ้งหนี้ในบิล ·
/// 267-1-9 v1 · 267-1-47) — คืนเฉพาะลิงก์ที่ผู้ขายออกไว้และยังใช้ได้ · ไม่มี = ขอลิงก์ใหม่ถึงผู้ขาย (ไม่ออก token เอง)</item>
/// </list>
/// ไม่มีเส้นออกใบเสร็จ/ยืนยันสลิป/บันทึกรับชำระที่นี่ (R267d) · เพดานต่อ IP ก่อนแตะฐาน + ต่อ token หลังยืนยันว่า token มีจริง (failClosed)
/// </summary>
public partial class PublicDocumentPageController
{
    /// <summary>ที่อยู่ของเส้นหลังจ่ายที่หน้าเรียกได้ในโหมดลิงก์ — เติมเฉพาะเมื่อหน้าเสนอสิ่งนั้นจริง (ไม่มีที่อยู่ = หน้าไม่วาดปุ่ม)</summary>
    private static CustomerDocPageView WithAfterPayPaths(CustomerDocPageView page, string basePath) => page with
    {
        WhtCertPath = page.AfterPay?.WhtCert is { CanUpload: true } ? basePath + "/wht-cert" : null,
        RelatedLinkBase = page.Related.Any(r => r.PayOffered) ? basePath + "/related/" : null,
    };

    /// <summary><paramref name="Path"/> = หน้าเอกสารของใบนั้น · null = ยังไม่มีลิงก์ที่ใช้ได้ (บันทึกคำขอถึงผู้ขายแล้ว — ข้อความอยู่ใน message)</summary>
    public sealed record RelatedPageLinkResponse(string? Path, bool Requested);

    /// <summary>ปุ่ม "ชำระใบนี้" ของใบที่เกี่ยวข้อง — คืนที่อยู่ภายในเว็บของหน้าเอกสารใบนั้น <b>เฉพาะลิงก์ที่ผู้ขายออกไว้และยังใช้ได้</b> (ไม่ออก token ให้ผู้ถือลิงก์ ·
    /// ฝ่ายค้าน MED-2 · 267-1-47) · ยังไม่มี/หมดอายุ = บันทึกคำขอ + กระดิ่งพนักงาน (200 · Path null) · ใบนอกชุด/ไม่มีปุ่มจ่าย = 404 ข้อความเดียว ·
    /// ผู้ขายยกเลิกลิงก์ของใบนั้นไว้ = 409 พร้อมข้อความ</summary>
    [HttpPost("documents/{token}/related/{relId:guid}/page-link")]
    public async Task<ActionResult<ApiResponse<RelatedPageLinkResponse>>> RelatedPageLink(Guid companyId, string token, Guid relId, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("rellink", failClosed: true, ct)) return TooMany<RelatedPageLinkResponse>();
        var r = await _links.RelatedPageLinkAsync(companyId, token, relId, ct);
        if (!r.Found) return NotFound(new ApiResponse<RelatedPageLinkResponse>(false, null, CustomerDocPage.NotFoundMessage));
        if (await OverTokenIpAsync("rellink", token, CustomerDocPage.Limits.ViewPerMinute, CustomerDocPage.Limits.ViewPerDay, failClosed: true, ct))
            return TooMany<RelatedPageLinkResponse>();
        if (r.Path != null) return Ok(new ApiResponse<RelatedPageLinkResponse>(true, new RelatedPageLinkResponse(r.Path, false)));
        if (r.Request == null) return Conflict(new ApiResponse<RelatedPageLinkResponse>(false, null, r.Message));
        // คำขอถูกบันทึกลง audit chain แล้ว — แจ้งพนักงานด้วยตัวแจ้งเดียวกับปุ่ม "ขอลิงก์ใหม่"
        if (r.Request.Recorded) await NotifyLinkRequestedAsync(_notify, _logger, companyId, r.Request);
        return Ok(new ApiResponse<RelatedPageLinkResponse>(true, new RelatedPageLinkResponse(null, true), r.Message));
    }

    /// <summary>ส่งหนังสือรับรอง 50 ทวิ ของใบนี้ (PDF/JPG/PNG ≤ 10 MB · ทีละไฟล์) — ด่านไฟล์/เพดาน/ประตูเดียวของลิงก์อัปโหลด 50 ทวิ เดิม ·
    /// ใบที่ไม่ได้รอหนังสือรับรอง = 404 ข้อความเดียว · ผล = รอผู้ขายตรวจ (ไม่ประทับ "ได้รับใบแล้ว" เอง)</summary>
    [HttpPost("documents/{token}/wht-cert")]
    [RequestSizeLimit(WhtCertUploadLinkPolicy.MaxBytes + 256 * 1024)]
    public async Task<ActionResult<ApiResponse<WhtUploadPublicResult>>> UploadWhtCert(Guid companyId, string token, IFormFile? file,
        [FromForm] string? senderName, [FromServices] IWhtCertUploadLinkService wht, CancellationToken ct)
    {
        NoStore();
        // เพดานต่อ IP + ต่อ token (ตัวเลขเดียวกับลิงก์อัปโหลด 50 ทวิ) — ก่อนอ่านไฟล์ · ตารางนับใช้ไม่ได้ = ปฏิเสธ
        if (!await _limiter.TryConsumeAsync(CustomerDocPage.RateKeyIp("whtcert", Ip), WhtCertUploadLinkPolicy.UploadsPerMinute,
                WhtCertUploadLinkPolicy.UploadsPerDay, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<WhtUploadPublicResult>(false, null, WhtCertUploadLinkPolicy.RateLimitedMessage));
        var target = await _links.WhtCertTargetAsync(companyId, token, ct);
        if (target is not { } t) return NotFound(new ApiResponse<WhtUploadPublicResult>(false, null, CustomerDocPage.NotFoundMessage));
        if (!await _limiter.TryConsumeAsync(CustomerDocPage.RateKeyToken("whtcert", token), WhtCertUploadLinkPolicy.UploadsPerMinute,
                WhtCertUploadLinkPolicy.UploadsPerTokenPerDay, ct, failClosed: true))
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
        try
        {
            var r = await wht.UploadForDocumentAsync(companyId, t.ContactId, t.DocumentId, content, senderName, ct);
            return Ok(new ApiResponse<WhtUploadPublicResult>(true, r, r.Message));
        }
        catch (BusinessRuleException ex)
        {
            return StatusCode(ex.StatusCode, new ApiResponse<WhtUploadPublicResult>(false, null, ex.Message));
        }
    }
}
