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

    public LineDocumentViewController(AccountingDbContext db, IPdfGenerationService pdf, IChatRateLimiter limiter, IConfiguration config)
    { _db = db; _pdf = pdf; _limiter = limiter; _config = config; }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> Pdf(Guid companyId, Guid queueId, string token, CancellationToken ct)
    {
        if (!await _limiter.TryConsumeAsync(LineDocumentViewLink.RateKey(token, Ip), LineDocumentViewLink.ViewsPerMinute, LineDocumentViewLink.ViewsPerDay, ct, failClosed: true))
            return StatusCode(429, new ApiResponse<object>(false, null, "เปิดเอกสารถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        IActionResult Missing() => NotFound(new ApiResponse<object>(false, null, LineDocumentViewLink.NotFoundMessage));
        if (!LineDocumentViewLink.LooksValid(token)) return Missing();
        byte[] key;
        try { key = LineDocumentViewLink.DeriveKey(_config["Jwt:Secret"]); }
        catch (InvalidOperationException) { return Missing(); }
        if (!LineDocumentViewLink.Matches(key, companyId, queueId, token)) return Missing();

        var row = await _db.LineQueues.AsNoTracking()
            .Where(q => q.Id == queueId && q.CompanyId == companyId && !q.IsDeleted && q.EndpointId != null && q.EntityType == "Document")
            .Select(q => new { q.EntityId, q.Status, q.CreatedAt, q.EndpointId, q.ContactId })
            .FirstOrDefaultAsync(ct);
        if (row == null || !LineDocumentViewLink.RowStatusAllows(row.Status) || !LineDocumentViewLink.Fresh(row.CreatedAt, DateTime.UtcNow)) return Missing();
        // ฝ่ายค้าน L4: ตรวจปลายทางตอนเปิด — ยกเลิกผูก/บล็อก/กลุ่มเปลี่ยนเป็นซ่อนยอด ⇒ ลิงก์ตาย (ตัวตัดสินเดียวกับตอนส่ง)
        var ep = await _db.ContactNotifyEndpoints.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.Id == row.EndpointId && e.CompanyId == companyId && e.ContactId == row.ContactId && !e.IsDeleted)
            .Select(e => new { e.Kind, e.ShowAmounts, e.UnlinkedAt, e.BlockedAt })
            .FirstOrDefaultAsync(ct);
        if (ep == null || !LineDocumentViewLink.EndpointAllows(ep.Kind, ep.ShowAmounts, ep.UnlinkedAt, ep.BlockedAt)) return Missing();
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == row.EntityId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.Status })
            .FirstOrDefaultAsync(ct);
        if (doc == null || doc.Status == DocumentStatus.Voided || !DocumentStatusRules.IsIssued(doc.Status)) return Missing();

        var pdf = await _pdf.GenerateDocumentPdfAsync(companyId,
            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(row.EntityId, null, null, null, null));
        if (pdf?.PdfData is not { Length: > 0 }) return Missing();
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
        // inline = เปิดในเบราว์เซอร์ของ LINE ได้ทันที · ผู้ใช้กดดาวน์โหลดจากตัวแสดง PDF
        Response.Headers.ContentDisposition = $"inline; filename=\"{SafeFileName(pdf.FileName)}\"";
        return File(pdf.PdfData, "application/pdf");
    }

    private static string SafeFileName(string? name)
    {
        var n = new string((name ?? "document.pdf").Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.').ToArray());
        return n.Length == 0 ? "document.pdf" : n;
    }
}
