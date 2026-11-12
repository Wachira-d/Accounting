using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 267 แพ็ก P1 — <b>หน้าเอกสารสำหรับลูกค้า (ไม่ล็อกอิน)</b>: ดูหัวใบ/รายการ/ยอด · ดาวน์โหลด PDF · ขอลิงก์ใหม่เมื่อลิงก์หมดอายุ
/// (DOCUMENT_FLOW §2.12n · คำตัดสิน 267-1-1) — route เดิมของลิงก์ชำระ (<c>public-pay/documents/{token}</c>): ลิงก์ชำระที่ลูกค้าถืออยู่
/// กลายเป็นหน้าเอกสารทันที · เส้นจ่าย/สลิป (<c>intents</c> · <c>slip</c>) ยังอยู่ที่ <see cref="PublicPaymentController"/> ตามเดิมทุกตัวอักษร
///
/// <para>สิทธิ์ = token ของใบ (สุ่ม 256 บิต · ฐานเก็บ hash + ฉบับเข้ารหัส · ตรวจเวลาคงที่ · ตัดสินสดทุกครั้ง) ผ่าน
/// <see cref="IDocumentPayLinkService"/> ตัวเดียว · ปุ่ม/ข้อความ/เหตุที่ทำไม่ได้ทั้งหมดมาจาก <see cref="CustomerDocPage"/> ·
/// ข้อความล้มเดียวทุกเหตุ (<see cref="CustomerDocPage.NotFoundMessage"/>) · เพดานต่อ IP + ต่อ token (นับในฐาน — หลายเครื่อง) ·
/// <c>Cache-Control: private, no-store</c> · <c>Referrer-Policy: no-referrer</c> (token อยู่ใน URL)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/public-pay")]
[AllowAnonymous]
public class PublicDocumentPageController : ControllerBase
{
    private readonly IDocumentPayLinkService _links;
    private readonly IPdfGenerationService _pdf;
    private readonly IChatRateLimiter _limiter;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<PublicDocumentPageController> _logger;

    public PublicDocumentPageController(IDocumentPayLinkService links, IPdfGenerationService pdf, IChatRateLimiter limiter,
        ILogger<PublicDocumentPageController> logger, INotificationEngine? notify = null)
    { _links = links; _pdf = pdf; _limiter = limiter; _logger = logger; _notify = notify; }

    private string Ip => HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private void NoStore()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private ObjectResult TooMany<T>()
    {
        Response.Headers.Append("Retry-After", "60");
        return StatusCode(429, new ApiResponse<T>(false, default, CustomerDocPage.RateMessage));
    }

    /// <summary>เพดานของเส้น — ต่อ (token + IP) (<see cref="CustomerDocPage.RateKey"/>) ·
    /// <paramref name="failClosed"/>: ตารางตัวนับใช้ไม่ได้ ⇒ ปฏิเสธไหม — เส้นดู = ไม่ (หน้านี้คือหน้าชำระเดิม: ตัวนับล่มต้องไม่ทำให้ลูกค้าจ่ายไม่ได้) ·
    /// PDF (เรนเดอร์หนัก) และขอลิงก์ใหม่ (เขียน) = ปฏิเสธ</summary>
    private async Task<bool> OverRateAsync(string bucket, string token, int perMinute, int perDay, bool failClosed, CancellationToken ct)
        => !await _limiter.TryConsumeAsync(CustomerDocPage.RateKey(bucket, token, Ip), perMinute, perDay, ct, failClosed: failClosed);

    private static string BasePath(Guid companyId, string token)
        => $"/api/companies/{companyId:D}/public-pay/documents/{Uri.EscapeDataString(token)}";

    /// <summary>view model เดียวของหน้า — หัวใบ · บรรทัด · ยอด · ปุ่ม (<c>page.actions</c>) · ใบที่เกี่ยวข้อง + ช่องจ่ายเดิม (ชื่อเดิม) ·
    /// ลิงก์เพิกถอน/ไม่พบ ⇒ 404 ข้อความเดียว · หมดอายุไม่นาน ⇒ 404 ข้อความเดียว + <c>page.canRequestLink</c> (ไม่มีข้อมูลของใบ)</summary>
    [HttpGet("documents/{token}")]
    public async Task<ActionResult<ApiResponse<PublicDocumentPayView>>> View(Guid companyId, string token, CancellationToken ct)
    {
        NoStore();
        if (await OverRateAsync("view", token, CustomerDocPage.Limits.ViewPerMinute, CustomerDocPage.Limits.ViewPerDay, failClosed: false, ct))
            return TooMany<PublicDocumentPayView>();
        var view = await _links.PublicViewAsync(companyId, token, ct);
        if (view == null)
            return NotFound(new ApiResponse<PublicDocumentPayView>(false, null, CustomerDocPage.NotFoundMessage));
        var basePath = BasePath(companyId, token);
        if (view.Page is not { Viewable: true })
        {
            // ลิงก์ใช้ไม่ได้: ไม่มีข้อมูลของใบ · หมดอายุไม่นาน ⇒ ทางไปต่อ = ขอลิงก์ใหม่ (ไม่ออก token ให้คนนอก)
            var stub = view.Page is { CanRequestLink: true } p ? view with { Page = p with { RequestLinkPath = basePath + "/request-link" } } : view;
            return NotFound(new ApiResponse<PublicDocumentPayView>(false, stub, CustomerDocPage.NotFoundMessage));
        }
        await _links.MarkOpenedAsync(companyId, token, ct);
        return Ok(new ApiResponse<PublicDocumentPayView>(true, view with
        {
            Page = view.Page! with { PdfPath = basePath + "/pdf", RelatedPdfBase = basePath + "/related/", RequestLinkPath = basePath + "/request-link" },
        }));
    }

    /// <summary>PDF ของใบนี้ — ไฟล์เดียวกับที่ผู้ขายดาวน์โหลด/ส่งอีเมล (renderer เดิม · ภาษาเอกสารผ่าน resolver กลาง · ใบยกเลิกพิมพ์ "ยกเลิก")</summary>
    [HttpGet("documents/{token}/pdf")]
    public Task<IActionResult> Pdf(Guid companyId, string token, CancellationToken ct) => PdfCoreAsync(companyId, token, null, ct);

    /// <summary>PDF ของใบที่เกี่ยวข้อง — <paramref name="relId"/> ต้องอยู่ในชุดที่หน้าแสดงของ token นี้ (ตัวโหลดเดียว · บริษัท/ผู้ติดต่อเดียวกัน ·
    /// ลูกค้าเห็นได้ · ออกแล้ว) · นอกชุด = 404 ข้อความเดียว</summary>
    [HttpGet("documents/{token}/related/{relId:guid}/pdf")]
    public Task<IActionResult> RelatedPdf(Guid companyId, string token, Guid relId, CancellationToken ct) => PdfCoreAsync(companyId, token, relId, ct);

    private async Task<IActionResult> PdfCoreAsync(Guid companyId, string token, Guid? relId, CancellationToken ct)
    {
        NoStore();
        if (await OverRateAsync("pdf", token, CustomerDocPage.Limits.PdfPerMinute, CustomerDocPage.Limits.PdfPerDay, failClosed: true, ct))
            return TooMany<object>();
        IActionResult Missing() => NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        var documentId = await _links.ResolvePdfDocumentAsync(companyId, token, relId, ct);
        if (documentId is not Guid id) return Missing();
        var pdf = await _pdf.GenerateDocumentPdfAsync(companyId, new Models.DTOs.DocumentTemplate.GeneratePdfRequest(id, null, null, null, null));
        if (pdf?.PdfData is not { Length: > 0 }) return Missing();
        Response.Headers.ContentDisposition = $"inline; filename=\"{SafeFileName(pdf.FileName)}\"";
        return File(pdf.PdfData, "application/pdf");
    }

    /// <summary>ผลของการขอลิงก์ — ไม่มีรหัสภายใน</summary>
    public sealed record RequestLinkResponse(bool Recorded, string Message);

    /// <summary>"ขอให้ผู้ขายส่งลิงก์ใหม่" — ใช้ได้กับลิงก์ที่หมดอายุไม่เกิน <see cref="CustomerDocPage.ExpiredRequestWindowDays"/> วัน
    /// (และลิงก์ที่ยังใช้ได้แต่ปุ่มตอบรับปิด) · <b>ไม่ออก token ให้คนนอก</b> — บันทึก audit แล้วแจ้งพนักงาน (กระดิ่ง) · ≤ 1 ครั้ง/ใบ/วัน</summary>
    [HttpPost("documents/{token}/request-link")]
    public async Task<ActionResult<ApiResponse<RequestLinkResponse>>> RequestLink(Guid companyId, string token, CancellationToken ct)
    {
        NoStore();
        if (await OverRateAsync("reqlink", token, CustomerDocPage.Limits.ViewPerMinute, CustomerDocPage.Limits.ViewPerDay, failClosed: true, ct))
            return TooMany<RequestLinkResponse>();
        var r = await _links.RequestNewLinkAsync(companyId, token, ct);
        if (!r.Found) return NotFound(new ApiResponse<RequestLinkResponse>(false, null, CustomerDocPage.NotFoundMessage));
        if (r.Recorded) await NotifyLinkRequestedAsync(_notify, _logger, companyId, r);
        return Ok(new ApiResponse<RequestLinkResponse>(true, new RequestLinkResponse(r.Recorded, r.Message), r.Message));
    }

    /// <summary>แจ้งพนักงานว่าลูกค้าขอลิงก์ใหม่ (กระดิ่ง — ค่าตั้งต้น <c>NotificationAudiencePolicy.BellOnlyKeys</c>) · ผู้เรียก: เส้นลิงก์ + เส้นการ์ด LINE ·
    /// คำขอถูกบันทึกลง audit chain แล้วก่อนถึงตรงนี้ ⇒ แจ้งล้ม = log ระดับ error (หลักฐานคำขอไม่หาย · ไม่ตอบลูกค้าว่าล้ม)</summary>
    internal static async Task NotifyLinkRequestedAsync(INotificationEngine? notify, ILogger logger, Guid companyId, CustomerLinkRequestResult r)
    {
        if (notify == null) return;
        try
        {
            await notify.DispatchAsync(companyId, NotificationEvents.DocumentLinkRequested, new NotificationContext
            {
                Title = $"🔗 ลูกค้าขอลิงก์เอกสารใหม่ — {r.DocumentNumber}",
                Message = $"ผู้ถือลิงก์ของเอกสาร {r.DocumentNumber} ขอให้ส่งลิงก์ใหม่ (ลิงก์เดิมหมดอายุหรือพ้นกำหนดทำรายการ) — เปิดเอกสารแล้วออก/ส่งลิงก์ให้ลูกค้า",
                ActionUrl = $"/pages/documents.html?id={r.DocumentId}",
                EntityType = nameof(Models.Entities.Document),
                EntityId = r.DocumentId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "แจ้งพนักงานเรื่องลูกค้าขอลิงก์เอกสารใหม่ไม่สำเร็จ doc={DocId} company={CompanyId}", r.DocumentId, companyId);
        }
    }

    internal static string SafeFileName(string? name)
    {
        var n = new string((name ?? "document.pdf").Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.').ToArray());
        return n.Length == 0 ? "document.pdf" : n;
    }
}
