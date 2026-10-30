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
        return Ok(new ApiResponse<object>(true, new
        {
            kind,
            title = LegalDocumentDefaults.Title(kind),
            publicUrl = baseUrl + LegalDocumentDefaults.PlatformPath(kind),
            baseUrlFromSettings = fromSettings,
            current = new
            {
                current.Version, effectiveDate = current.EffectiveDate.ToString("yyyy-MM-dd"), current.Source,
                current.PublishedAt, current.ChangeSummary, current.BodyMarkdown,
            },
            upcoming = upcoming == null ? null : new
            {
                upcoming.Version, effectiveDate = upcoming.EffectiveDate.ToString("yyyy-MM-dd"), upcoming.ChangeSummary, upcoming.BodyMarkdown,
            },
            draft = DraftDto(await _legal.GetDraftAsync(kind, ct)),
            history = (await _legal.GetHistoryAsync(kind, ct)).Select(HistoryDto).ToList(),
            tokens = LegalDocumentDefaults.PlatformTokens.Select(t => new { token = "{{" + t.Token + "}}", label = t.Label }).ToList(),
            consentVersion = (await _legal.GetConsentVersionAsync(ct)).ConsentVersion,
        }));
    }

    public sealed record DraftRequest(string? Version, DateTime? EffectiveDate, string? BodyMarkdown, string? ChangeSummary);

    /// <summary>บันทึกร่าง (สร้างหรือแก้ร่างเดียวของชนิดนี้) — ไม่มีผลกับหน้าสาธารณะจนกว่าจะเผยแพร่</summary>
    [HttpPut("{kind}/draft")]
    [Accounting.Filters.RejectApiKey("แก้ร่างนโยบาย/ข้อกำหนดของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<object>>> SaveDraft(string kind, [FromBody] DraftRequest req, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var eff = req.EffectiveDate is DateTime d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        var draft = await _legal.SaveDraftAsync(kind, new LegalDraftInput(req.Version, eff, req.BodyMarkdown, req.ChangeSummary), actor, ct);
        return Ok(new ApiResponse<object>(true, DraftDto(draft), "บันทึกร่างแล้ว (ยังไม่เผยแพร่)"));
    }

    public sealed record PreviewRequest(string? BodyMarkdown);

    /// <summary>ตัวอย่าง HTML ของข้อความที่กำลังแก้ (ตัวแปลงเดียวกับหน้าสาธารณะ) + คำเตือน (ลิงก์ที่ไม่รับ · แท็ก HTML · โทเคนที่ไม่รู้จัก) — ไม่เขียนอะไร</summary>
    [HttpPost("{kind}/preview")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(string kind, [FromBody] PreviewRequest req, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ชนิดเอกสารต้องเป็น privacy หรือ terms"));
        var md = req.BodyMarkdown ?? "";
        if (md.Length > LegalMarkdown.MaxLength * 2)
            return BadRequest(new ApiResponse<object>(false, null, "เนื้อความยาวเกินไป"));
        var html = await _legal.RenderPlatformAsync(md, ct);
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
        var item = await _legal.PublishDraftAsync(kind, actor, ct);
        var msg = item.IsUpcoming
            ? $"เผยแพร่ฉบับ {item.Version} แล้ว — ประกาศล่วงหน้า มีผล {item.EffectiveDate:yyyy-MM-dd}"
            : $"เผยแพร่ฉบับ {item.Version} แล้ว — มีผลทันที";
        return Ok(new ApiResponse<object>(true, HistoryDto(item), msg));
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
