using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 258-3 (F-H1 · A-L8 · คำตัดสิน 258-3-2) — <b>ฝั่งลูกค้า (ไม่ล็อกอิน)</b>: เปิด PDF ของเอกสารจากปุ่ม "ดู/ดาวน์โหลด PDF" บนการ์ด LINE
/// <para>สิทธิ์ = token HMAC ของแถวคิว LINE (<see cref="LineDocumentViewLink"/>) ผูกบริษัทใน URL · แถวต้องเป็นของบริษัทนี้ มีปลายทาง ไม่ถูกยกเลิก/ลบ ยังไม่หมดอายุ ·
/// เอกสารยังออกแล้วและไม่ถูกยกเลิก · เพดานต่อ IP แบบ DB (<see cref="IChatRateLimiter"/> · failClosed) · ข้อความล้มเดียวทุกเหตุ · อ่านอย่างเดียว</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/line-doc/{queueId:guid}/{token}")]
[AllowAnonymous]
public class LineDocumentViewController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService _pdf;
    private readonly IChatRateLimiter _limiter;
    private readonly IConfiguration _config;

    /// <summary>รอบ 267 P1: view model ของหน้าเอกสาร (โหมด LINE-ดู) + คำขอลิงก์ทำรายการ · null (สร้างนอก DI) = มีแต่เส้น PDF เดิม</summary>
    private readonly IDocumentPayLinkService? _docPage;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<LineDocumentViewController>? _logger;

    public LineDocumentViewController(AccountingDbContext db, IPdfGenerationService pdf, IChatRateLimiter limiter, IConfiguration config,
        IDocumentPayLinkService? docPage = null, INotificationEngine? notify = null, ILogger<LineDocumentViewController>? logger = null)
    { _db = db; _pdf = pdf; _limiter = limiter; _config = config; _docPage = docPage; _notify = notify; _logger = logger; }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private IActionResult Missing() => NotFound(new ApiResponse<object>(false, null, LineDocumentViewLink.NotFoundMessage));

    /// <summary>
    /// ด่านเดียวของทุกเส้นใต้ลิงก์การ์ด LINE (PDF · หน้าเอกสารโหมดดู · ขอลิงก์ทำรายการ — รอบ 267 P1 แยกจาก <see cref="Pdf"/> เดิมโดยไม่เปลี่ยนกติกา):
    /// เพดานต่อ IP/token → รูป token → HMAC ของแถวคิว → แถวของบริษัทนี้ที่ยังใช้ได้/ยังไม่เก่าเกิน → ปลายทางยังผูกอยู่และ<b>ไม่ซ่อนยอด</b>
    /// (<see cref="CustomerDocPage.PageLinkAllowedForLineEndpoint"/> = ตัวตัดสินเดิม <c>LineDocumentViewLink.EndpointAllows</c>) → ใบออกแล้วและไม่ถูกยกเลิก ·
    /// คืนรหัสเอกสาร หรือผลตอบกลับที่ต้องส่ง (429 / ข้อความล้มเดียว)
    /// </summary>
    private async Task<(Guid? DocumentId, IActionResult? Problem)> ResolveAsync(Guid companyId, Guid queueId, string token, CancellationToken ct)
    {
        if (!await _limiter.TryConsumeAsync(LineDocumentViewLink.RateKey(token, Ip), LineDocumentViewLink.ViewsPerMinute, LineDocumentViewLink.ViewsPerDay, ct, failClosed: true))
            return (null, StatusCode(429, new ApiResponse<object>(false, null, "เปิดเอกสารถี่เกินไป — รอสักครู่แล้วลองใหม่")));
        if (!LineDocumentViewLink.LooksValid(token)) return (null, Missing());
        byte[] key;
        try { key = LineDocumentViewLink.DeriveKey(_config["Jwt:Secret"]); }
        catch (InvalidOperationException) { return (null, Missing()); }
        if (!LineDocumentViewLink.Matches(key, companyId, queueId, token)) return (null, Missing());

        var row = await _db.LineQueues.AsNoTracking()
            .Where(q => q.Id == queueId && q.CompanyId == companyId && !q.IsDeleted && q.EndpointId != null && q.EntityType == "Document")
            .Select(q => new { q.EntityId, q.Status, q.CreatedAt, q.EndpointId, q.ContactId })
            .FirstOrDefaultAsync(ct);
        if (row == null || !LineDocumentViewLink.RowStatusAllows(row.Status) || !LineDocumentViewLink.Fresh(row.CreatedAt, DateTime.UtcNow)) return (null, Missing());
        // ฝ่ายค้าน L4: ตรวจปลายทางตอนเปิด — ยกเลิกผูก/บล็อก/กลุ่มเปลี่ยนเป็นซ่อนยอด ⇒ ลิงก์ที่ส่งไปแล้วใช้ไม่ได้ทันที
        var ep = await _db.ContactNotifyEndpoints.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.Id == row.EndpointId && e.CompanyId == companyId && e.ContactId == row.ContactId && !e.IsDeleted)
            .Select(e => new { e.Kind, e.ShowAmounts, e.UnlinkedAt, e.BlockedAt })
            .FirstOrDefaultAsync(ct);
        if (ep == null || !CustomerDocPage.PageLinkAllowedForLineEndpoint(ep.Kind, ep.ShowAmounts, ep.UnlinkedAt, ep.BlockedAt)) return (null, Missing());
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == row.EntityId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.Status })
            .FirstOrDefaultAsync(ct);
        if (doc == null || doc.Status == DocumentStatus.Voided || !DocumentStatusRules.IsIssued(doc.Status)) return (null, Missing());
        return (row.EntityId, null);
    }

    private static string BasePath(Guid companyId, Guid queueId, string token)
        => $"/api/companies/{companyId:D}/line-doc/{queueId:D}/{Uri.EscapeDataString(token)}";

    [HttpGet]
    public async Task<IActionResult> Pdf(Guid companyId, Guid queueId, string token, CancellationToken ct)
    {
        var (documentId, problem) = await ResolveAsync(companyId, queueId, token, ct);
        if (documentId is not Guid id) return problem!;

        var pdf = await _pdf.GenerateDocumentPdfAsync(companyId,
            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(id, null, null, null, null));
        if (pdf?.PdfData is not { Length: > 0 }) return Missing();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
        // inline = เปิดในเบราว์เซอร์ของ LINE ได้ทันที · ผู้ใช้กดดาวน์โหลดจากตัวแสดง PDF
        Response.Headers.ContentDisposition = $"inline; filename=\"{SafeFileName(pdf.FileName)}\"";
        return File(pdf.PdfData, "application/pdf");
    }

    /// <summary>รอบ 267 P1 — หน้าเอกสารในโหมด "LINE-ดู": หัวใบ/รายการ/ยอด + ดาวน์โหลด PDF (เส้นเดิมของลิงก์นี้) ·
    /// จ่าย/ตอบรับทำไม่ได้ในโหมดนี้ (ต้องใช้ลิงก์หน้าเอกสาร) — ปุ่มจากตัวตัดสินเดียว <c>CustomerDocPage.Actions</c> มีทางไปต่อ "ขอลิงก์ทำรายการ"</summary>
    [HttpGet("page")]
    public async Task<IActionResult> Page(Guid companyId, Guid queueId, string token, CancellationToken ct)
    {
        var (documentId, problem) = await ResolveAsync(companyId, queueId, token, ct);
        if (documentId is not Guid id) return problem!;
        var view = _docPage == null ? null : await _docPage.PageForDocumentAsync(companyId, id, CustomerDocPage.Mode.LineView, ct);
        if (view?.Page == null) return Missing();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var basePath = BasePath(companyId, queueId, token);
        return Ok(new ApiResponse<PublicDocumentPayView>(true, view with
        {
            Page = view.Page with { PdfPath = basePath, RequestLinkPath = basePath + "/request-link" },
        }));
    }

    /// <summary>รอบ 267 P1 — "ขอให้ผู้ขายส่งลิงก์ทำรายการ" จากโหมด LINE-ดู: ไม่ออก token ให้คนนอก · บันทึก audit + แจ้งพนักงาน (กระดิ่ง) · ≤ 1 ครั้ง/ใบ/วัน</summary>
    [HttpPost("request-link")]
    public async Task<IActionResult> RequestLink(Guid companyId, Guid queueId, string token, CancellationToken ct)
    {
        var (documentId, problem) = await ResolveAsync(companyId, queueId, token, ct);
        if (documentId is not Guid id) return problem!;
        if (_docPage == null) return Missing();
        var r = await _docPage.RequestLinkForDocumentAsync(companyId, id, "line-view", ct);
        if (!r.Found) return Missing();
        if (r.Recorded && _logger != null) await PublicDocumentPageController.NotifyLinkRequestedAsync(_notify, _logger, companyId, r);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new ApiResponse<PublicDocumentPageController.RequestLinkResponse>(true,
            new PublicDocumentPageController.RequestLinkResponse(r.Recorded, r.Message), r.Message));
    }

    private static string SafeFileName(string? name)
    {
        var n = new string((name ?? "document.pdf").Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.').ToArray());
        return n.Length == 0 ? "document.pdf" : n;
    }
}
