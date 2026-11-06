using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173 · เจ้าของ: "นโยบายความเป็นส่วนตัว/ข้อกำหนด ... admin ปรับแต่ง เปลี่ยนได้ เห็น url") —
/// หน้า <c>/admin/legal.html</c>: แก้ร่าง → ดูตัวอย่าง → เผยแพร่ (append-only · ฉบับเผยแพร่แล้วแก้ไม่ได้) · ประวัติฉบับ · URL สาธารณะ ·
/// กติกาอยู่ที่ <c>Helpers/LegalDocumentPolicy</c> · ร่องรอย: audit อัตโนมัติของ BaseEntity + แถว audit ตอนเผยแพร่ (ใน service)
/// </summary>
[ApiController]
[Route("api/admin/legal-documents")]
[Authorize(Roles = "SystemAdmin")]
public class AdminLegalDocumentController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILegalDocumentService _legal;

    public AdminLegalDocumentController(AccountingDbContext db, ILegalDocumentService legal)
    {
        _db = db;
        _legal = legal;
    }

    /// <summary>base URL สาธารณะ — "URL ของระบบ" (SiteSettings.AppBaseUrl) ก่อน · ยังไม่ตั้ง ⇒ โฮสต์ของคำขอนี้ (บอกหน้าเว็บว่ามาจากไหน)</summary>
    private async Task<(string BaseUrl, bool FromSettings)> PublicBaseAsync(CancellationToken ct)
    {
        var configured = await _db.SiteSettings.AsNoTracking().Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            return (configured.Trim().TrimEnd('/'), true);
        return ($"{Request.Scheme}://{Request.Host}", false);
    }

    private static object HistoryDto(LegalHistoryItem h) => new
    {
        h.Version, effectiveDate = h.EffectiveDate.ToString("yyyy-MM-dd"), h.PublishedAt, h.PublishedBy, h.ChangeSummary,
        h.IsCurrent, h.IsUpcoming, h.IsBuiltIn,
    };

    private static object? DraftDto(LegalDraftView? d) => d == null ? null : new
    {
        d.Id, d.Version, effectiveDate = d.EffectiveDate.ToString("yyyy-MM-dd"), d.BodyMarkdown, d.ChangeSummary,
        d.CreatedAt, d.UpdatedAt, d.UpdatedBy,
        d.BodyMarkdownEn,   // รอบ 232: เก็บแล้วต้อง echo กลับ (กฎ #4 A) — hydrate ช่องอังกฤษตอนเปิดแก้
    };

    /// <summary>ภาพรวม/รายละเอียดเอกสารหนึ่งชนิด: ฉบับปัจจุบัน (พร้อม markdown ให้ตั้งต้นร่างใหม่) · ร่าง · ประวัติ · URL สาธารณะ · โทเคนที่ใช้ได้</summary>
    [HttpGet("{kind}")]
    public async Task<ActionResult<ApiResponse<object>>> Get(string kind, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var current = await _legal.GetCurrentAsync(kind, ct);
        var upcoming = await _legal.GetUpcomingAsync(kind, ct);
        var (baseUrl, fromSettings) = await PublicBaseAsync(ct);
        var draftView = await _legal.GetDraftAsync(kind, ct);
        // รอบ 250 WP-03 (A H-01 · คำตัดสิน Y6): ข้อของฉบับตั้งต้นที่ฉบับเผยแพร่ล่าสุด (ประกาศล่วงหน้า › ปัจจุบัน) ยังไม่มี — แบนเนอร์ให้เจ้าของกดเผยแพร่เอง
        // (ระบบประทับการยอมรับแทนไม่ได้) · ปัจจุบันเป็นฉบับตั้งต้น (ยังไม่เคยเผยแพร่) = ไม่มีส่วนต่าง
        var builtInBody = LegalDocumentDefaults.PlatformBuiltIn(kind);
        var latestPublishedBody = upcoming?.BodyMarkdown ?? (current.Source == "builtin" ? null : current.BodyMarkdown);
        var builtInNewClauses = LegalDocumentClauseDiff.MissingFromPublished(builtInBody, latestPublishedBody);
        return Ok(new ApiResponse<object>(true, new
        {
            kind,
            builtInNewClauses,
            draftCoversBuiltInNewClauses = draftView != null && builtInNewClauses.Count > 0
                && LegalDocumentClauseDiff.MissingFromPublished(builtInBody, draftView.BodyMarkdown).Count == 0,
            title = LegalDocumentDefaults.Title(kind),
            publicUrl = baseUrl + LegalDocumentDefaults.PlatformPath(kind),
            publicUrlEn = baseUrl + LegalDocumentDefaults.PlatformPath(kind) + "?lang=" + LegalDocumentLanguage.English,   // รอบ 232: ฉบับเดียว ภาษาเป็น query
            baseUrlFromSettings = fromSettings,
            current = new
            {
                current.Version, effectiveDate = current.EffectiveDate.ToString("yyyy-MM-dd"), current.Source,
                current.PublishedAt, current.ChangeSummary, current.BodyMarkdown,
                current.BodyMarkdownEn, englishAvailable = !string.IsNullOrWhiteSpace(current.BodyMarkdownEn),   // รอบ 232
            },
            upcoming = upcoming == null ? null : new
            {
                upcoming.Version, effectiveDate = upcoming.EffectiveDate.ToString("yyyy-MM-dd"), upcoming.ChangeSummary, upcoming.BodyMarkdown,
                upcoming.BodyMarkdownEn,
            },
            draft = DraftDto(draftView),
            history = (await _legal.GetHistoryAsync(kind, ct)).Select(HistoryDto).ToList(),
            tokens = LegalDocumentDefaults.PlatformTokens.Select(t => new { token = "{{" + t.Token + "}}", label = t.Label }).ToList(),
            consentVersion = (await _legal.GetConsentVersionAsync(ct)).ConsentVersion,
            // ฝ่ายค้านรอบ 214: วันนี้ตามเวลาไทยจากเซิร์ฟเวอร์ — หน้าแอดมินเทียบวันมีผลของร่างที่ค้าง (ไม่คำนวณเวลาไทยเอง)
            today = LegalDocumentPolicy.TodayBangkok(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        }));
    }

    /// <summary>รอบ 232: <c>BodyMarkdownEn</c> = ฉบับอังกฤษของร่างเดียวกัน (null = ไม่เปลี่ยน · "" = ล้าง)</summary>
    public sealed record DraftRequest(string? Version, DateTime? EffectiveDate, string? BodyMarkdown, string? ChangeSummary, string? BodyMarkdownEn = null);

    /// <summary>บันทึกร่าง (สร้างหรือแก้ร่างเดียวของชนิดนี้) — ไม่มีผลกับหน้าสาธารณะจนกว่าจะเผยแพร่</summary>
    [HttpPut("{kind}/draft")]
    [Accounting.Filters.RejectApiKey("แก้ร่างนโยบาย/ข้อกำหนดของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<object>>> SaveDraft(string kind, [FromBody] DraftRequest req, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var eff = req.EffectiveDate is DateTime d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        var draft = await _legal.SaveDraftAsync(kind, new LegalDraftInput(req.Version, eff, req.BodyMarkdown, req.ChangeSummary, req.BodyMarkdownEn), actor, ct);
        return Ok(new ApiResponse<object>(true, DraftDto(draft), "บันทึกร่างแล้ว (ยังไม่เผยแพร่)"));
    }

    public sealed record PreviewRequest(string? BodyMarkdown, string? Language = null);   // รอบ 232 ต่อ: "en" = ตัวอย่างฉบับอังกฤษ (ค่า "ยังไม่ได้ตั้งค่า" เป็นอังกฤษ)

    /// <summary>ตัวอย่าง HTML ของข้อความที่กำลังแก้ (ตัวแปลงเดียวกับหน้าสาธารณะ) + คำเตือน (ลิงก์ที่ไม่รับ · แท็ก HTML · โทเคนที่ไม่รู้จัก) — ไม่เขียนอะไร</summary>
    [HttpPost("{kind}/preview")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(string kind, [FromBody] PreviewRequest req, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var md = req.BodyMarkdown ?? "";
        if (md.Length > LegalMarkdown.MaxLength)   // ฝ่ายค้าน S1: เพดานเท่าที่เก็บได้จริง
            return BadRequest(new ApiResponse<object>(false, null, "เนื้อความยาวเกินไป"));
        var html = await _legal.RenderPlatformAsync(md, ct, req.Language);
        var warnings = LegalMarkdown.Warnings(md, LegalDocumentDefaults.PlatformTokens.Select(t => t.Token));
        return Ok(new ApiResponse<object>(true, new { html, warnings }));
    }

    /// <summary>เผยแพร่ร่าง — หลังจากนี้แก้ไม่ได้ (ต้องออกฉบับใหม่) · ผู้ใช้ที่ล็อกอินจะเห็นแถบ "มีฉบับใหม่" เมื่อถึงวันมีผล</summary>
    [HttpPost("{kind}/publish")]
    [Accounting.Filters.RejectApiKey("เผยแพร่นโยบาย/ข้อกำหนดของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<object>>> Publish(string kind, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var item = await _legal.PublishDraftAsync(kind, actor, ip, ct);
        var msg = item.IsUpcoming
            ? $"เผยแพร่ฉบับ {item.Version} แล้ว — ประกาศล่วงหน้า มีผล {item.EffectiveDate:yyyy-MM-dd}"
            : $"เผยแพร่ฉบับ {item.Version} แล้ว — มีผลทันที";
        return Ok(new ApiResponse<object>(true, HistoryDto(item), msg));
    }

    /// <summary>รอบ 232 ต่อ (เจ้าของ: "แปลนโยบายเป็นภาษาอังกฤษให้เลย"): คำแปลอังกฤษของ<b>ฉบับตั้งต้นของระบบ</b> — ปุ่ม "ใส่คำแปลตั้งต้น (อังกฤษ)" ดึงไปเติมช่อง
    /// อังกฤษของร่าง/ฉบับปัจจุบัน (หน้าเว็บเติมเฉพาะช่องว่างหรือเมื่อผู้ใช้ยืนยันทับ) · ไม่เขียนอะไร ·
    /// <c>markdown</c> = ข้อความ<b>ยังมีโทเคน</b> <c>{{controller.*}}</c> (ตัวตนผู้ให้บริการไม่ฝังลงข้อความที่เก็บ — แก้หน้าตั้งค่าแล้วทุกฉบับตาม) ·
    /// <c>html</c> = ตัวอย่างหลังแทนโทเคนด้วยค่าปัจจุบัน (ตัวแปลงเดียวกับหน้าสาธารณะ) · ตัวตั้งเดียว <see cref="LegalDocumentDefaults.PlatformBuiltInEn"/></summary>
    [HttpGet("{kind}/builtin-en")]
    public async Task<ActionResult<ApiResponse<object>>> BuiltInEnglish(string kind, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy · terms หรือ refund"));
        var md = LegalDocumentDefaults.PlatformBuiltInEn(kind);
        var html = await _legal.RenderPlatformAsync(md, ct, LegalDocumentLanguage.English);
        return Ok(new ApiResponse<object>(true, new
        {
            kind, basedOnVersion = LegalDocumentDefaults.BuiltInVersion, markdown = md, html,
            warning = "คำแปลนี้แปลจากฉบับตั้งต้นของระบบ — ถ้าแก้ฉบับไทยไปแล้วให้ตรวจให้ตรงก่อนบันทึก (ฉบับไทยเป็นฉบับผูกพัน)",
        }));
    }

    /// <summary>รอบ 250 WP-03 (A H-01 · คำตัดสิน Y6): ฉบับตั้งต้นของระบบ (ไทย + คำแปลตั้งต้น · ยังมีโทเคน) ให้แอดมินคัดลอกเป็นร่างฉบับใหม่
    /// เมื่อแบนเนอร์ฟ้องว่าฉบับเผยแพร่ขาดข้อ · อ่านอย่างเดียว — การเผยแพร่ยังเป็นการกดของแอดมิน (เส้นเดิม draft → publish)</summary>
    [HttpGet("{kind}/builtin")]
    public ActionResult<ApiResponse<object>> BuiltIn(string kind)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy · terms หรือ refund"));
        return Ok(new ApiResponse<object>(true, new
        {
            kind, basedOnVersion = LegalDocumentDefaults.BuiltInVersion,
            bodyMarkdown = LegalDocumentDefaults.PlatformBuiltIn(kind),
            bodyMarkdownEn = LegalDocumentDefaults.PlatformBuiltInEn(kind),
        }));
    }

    public sealed record EnglishRequest(string? BodyMarkdownEn);

    /// <summary>รอบ 232 (ฝ่ายค้าน N3 · คำตัดสิน 232-Checkout-1): เติม/แก้ฉบับอังกฤษของฉบับที่เผยแพร่แล้ว <b>โดยไม่ขยับเลขฉบับ</b> (ไทยเป็นฉบับผูกพัน) ·
    /// ว่าง = ล้างฉบับอังกฤษ (หน้าสาธารณะกลับไปแสดงไทย+ประกาศ) · audit chain ทุกครั้งที่เปลี่ยน</summary>
    [HttpPut("{kind}/versions/{version}/english")]
    [Accounting.Filters.RejectApiKey("แก้ข้อความอังกฤษของนโยบาย/ข้อกำหนดของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<object>>> SetPublishedEnglish(string kind, string version, [FromBody] EnglishRequest req, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var view = await _legal.SetPublishedEnglishAsync(kind, version, req.BodyMarkdownEn, actor, ip, ct);
        return Ok(new ApiResponse<object>(true, new { view.Version, view.BodyMarkdownEn, englishAvailable = view.BodyMarkdownEn != null },
            view.BodyMarkdownEn == null ? $"ล้างข้อความอังกฤษของฉบับ {view.Version} แล้ว (เลขฉบับคงเดิม)" : $"บันทึกข้อความอังกฤษของฉบับ {view.Version} แล้ว (เลขฉบับคงเดิม · ไทยเป็นฉบับผูกพัน)"));
    }

    [HttpDelete("{kind}/draft")]
    [Accounting.Filters.RejectApiKey("ทิ้งร่างนโยบาย/ข้อกำหนดของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<bool>>> DiscardDraft(string kind, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<bool>(false, false, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var ok = await _legal.DiscardDraftAsync(kind, actor, ct);
        return Ok(new ApiResponse<bool>(true, ok, ok ? "ทิ้งร่างแล้ว" : "ไม่มีร่างให้ทิ้ง"));
    }
}
