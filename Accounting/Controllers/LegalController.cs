using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// ข้อมูลเชิงกฎหมายที่หน้าเว็บสาธารณะต้องใช้ — เวอร์ชันนโยบายที่บังคับใช้อยู่
/// และ **ตัวตนของผู้ควบคุมข้อมูลส่วนบุคคล** (PDPA ม.23(1)(6): ต้องแจ้งชื่อและ
/// ช่องทางติดต่อของผู้ควบคุมข้อมูลให้เจ้าของข้อมูลทราบก่อน/ขณะเก็บข้อมูล)
///
/// ทำไมต้องเป็น endpoint ไม่ใช่พิมพ์ลงหน้า HTML ตรง ๆ: ชื่อ/ที่อยู่/เลขผู้เสียภาษี
/// ของผู้ให้บริการตั้งไว้ที่ <c>/admin/site-settings.html</c> อยู่แล้ว
/// (<c>SiteSettings.PlatformSeller*</c> — ชุดเดียวกับที่ใช้ออกใบกำกับค่าบริการ)
/// ถ้าหน้า terms/privacy ฝัง literal ไว้เอง วันที่ผู้ให้บริการย้ายที่อยู่/
/// เปลี่ยนชื่อนิติบุคคล เอกสารทางกฎหมายจะค้างของเก่าเงียบ ๆ
/// (defect class "สอง renderer ห้าม drift" — ที่เดียวคือแหล่งความจริง)
///
/// <para>รอบ 214 (คำตัดสินข้อ 173): เนื้อความทั้งฉบับมาจาก <see cref="ILegalDocumentService"/> (ฉบับที่ SystemAdmin เผยแพร่ หรือฉบับตั้งต้น)
/// แปลงเป็น HTML ที่เซิร์ฟเวอร์ (<c>Helpers/LegalMarkdown</c> — encode ทุกตัว) · เวอร์ชันทุกจุดมาจาก resolver ตัวเดียว ·
/// ผู้ใช้ที่ล็อกอินเห็นแถบ "มีฉบับใหม่" แล้วกดรับทราบ (ไม่บล็อก)</para>
/// </summary>
[ApiController]
[Route("api/legal")]
// ฝ่ายค้านรอบ 214: ไม่ใส่ [AllowAnonymous] ระดับคลาส — มันลบล้าง [Authorize] ของ action ⇒ endpoint ยอมรับนโยบายกลายเป็นไม่ต้องล็อกอิน ·
// เปิดสาธารณะรายเมธอดแทน (policy · documents · history)
public class LegalController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ILegalDocumentService _legal;
    private readonly Accounting.Services.Implementations.Pdpa.IPdpaService _pdpa;

    public LegalController(AccountingDbContext db, ILegalDocumentService legal, Accounting.Services.Implementations.Pdpa.IPdpaService pdpa)
    {
        _db = db;
        _legal = legal;
        _pdpa = pdpa;
    }

    public sealed record PolicyInfo(
        string Version,              // เวอร์ชันความยินยอม (นโยบาย + ข้อกำหนด) — หน้าสมัครส่งกลับมาเป็นหลักฐาน "ฉบับที่เห็น"
        string EffectiveDate,        // ISO yyyy-MM-dd ของนโยบายความเป็นส่วนตัว (หน้าเว็บแปลงเป็น พ.ศ. เอง)
        string PrivacyVersion,
        string TermsVersion,
        string? ControllerName,
        string? ControllerTaxId,
        string? ControllerAddress,
        string? ControllerPhone,
        string? PrivacyContactEmail,
        string? SiteName,
        bool ControllerConfigured);  // false = แอดมินยังไม่ได้กรอก → หน้าเว็บเตือน

    /// <summary>เวอร์ชันนโยบาย + ตัวตนผู้ควบคุมข้อมูล (ไม่ต้อง login)</summary>
    [HttpGet("policy")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PolicyInfo>>> GetPolicy(CancellationToken ct)
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var versions = await _legal.GetConsentVersionAsync(ct);
        var privacy = await _legal.GetCurrentAsync(LegalDocumentDefaults.KindPrivacy, ct);

        // ชื่อผู้ควบคุมข้อมูล: ชื่อนิติบุคคลผู้ให้บริการ → ถ้ายังไม่ตั้ง ใช้ชื่อเว็บ
        // (ชื่อเว็บไม่ใช่ชื่อนิติบุคคล จึงยังนับว่า "ยังไม่ได้ตั้งค่า" ด้านล่าง)
        var name = FirstNonEmpty(s?.PlatformSellerName, s?.SiteName);
        // ช่องทางติดต่อเรื่องข้อมูลส่วนบุคคล: อีเมลผู้ขาย → อีเมลติดต่อทั่วไป
        var email = FirstNonEmpty(s?.PlatformSellerEmail, s?.ContactEmail);

        return Ok(new ApiResponse<PolicyInfo>(true, new PolicyInfo(
            Version: versions.ConsentVersion,
            EffectiveDate: privacy.EffectiveDate.ToString("yyyy-MM-dd"),
            PrivacyVersion: versions.PrivacyVersion,
            TermsVersion: versions.TermsVersion,
            ControllerName: name,
            ControllerTaxId: s?.PlatformSellerTaxId,
            ControllerAddress: s?.PlatformSellerAddress,
            ControllerPhone: FirstNonEmpty(s?.PlatformSellerPhone, s?.ContactPhone),
            PrivacyContactEmail: email,
            SiteName: s?.SiteName,
            ControllerConfigured: !string.IsNullOrWhiteSpace(s?.PlatformSellerName)
                                  && !string.IsNullOrWhiteSpace(email))));
    }

    public sealed record UpcomingInfo(string Version, string EffectiveDate, string? ChangeSummary);

    /// <summary><c>IsArchived</c> = ฉบับที่หมดผลแล้ว · <c>IsUpcoming</c> = ฉบับที่ประกาศแล้วแต่ยังไม่ถึงวันมีผล (ฝ่ายค้าน S2 — เดิมเหมาเป็น "ฉบับเก่า")</summary>
    public sealed record LegalDocumentDto(
        string Kind, string Title, string Version, string EffectiveDate, string Html, string Source,
        string? ChangeSummary, UpcomingInfo? Upcoming, string ConsentVersion, bool ControllerConfigured, bool IsArchived, bool IsUpcoming);

    /// <summary>เนื้อความเอกสาร (HTML ที่เซิร์ฟเวอร์แปลง + encode แล้ว) — ไม่ระบุ version = ฉบับที่มีผลวันนี้</summary>
    [HttpGet("documents/{kind}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LegalDocumentDto>>> GetDocument(string kind, [FromQuery] string? version, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<LegalDocumentDto>(false, null, "ไม่พบเอกสาร"));
        var current = await _legal.GetCurrentAsync(kind, ct);
        var doc = string.IsNullOrWhiteSpace(version) ? current : await _legal.GetPublishedVersionAsync(kind, version, ct);
        if (doc == null) return NotFound(new ApiResponse<LegalDocumentDto>(false, null, $"ไม่พบฉบับ {version}"));
        var upcoming = await _legal.GetUpcomingAsync(kind, ct);
        var consent = await _legal.GetConsentVersionAsync(ct);
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var html = await _legal.RenderPlatformAsync(doc.BodyMarkdown, ct);
        var isUpcoming = doc.EffectiveDate > LegalDocumentPolicy.TodayBangkok(DateTime.UtcNow);
        return Ok(new ApiResponse<LegalDocumentDto>(true, new LegalDocumentDto(
            doc.Kind, doc.Title, doc.Version, doc.EffectiveDate.ToString("yyyy-MM-dd"), html, doc.Source, doc.ChangeSummary,
            upcoming == null ? null : new UpcomingInfo(upcoming.Version, upcoming.EffectiveDate.ToString("yyyy-MM-dd"), upcoming.ChangeSummary),
            consent.ConsentVersion,
            ControllerConfigured: !string.IsNullOrWhiteSpace(s?.PlatformSellerName)
                                  && !string.IsNullOrWhiteSpace(FirstNonEmpty(s?.PlatformSellerEmail, s?.ContactEmail)),
            IsArchived: doc.Version != current.Version && !isUpcoming,
            IsUpcoming: isUpcoming)));
    }

    /// <summary>ประวัติฉบับที่เผยแพร่ (สาธารณะ — โปร่งใสว่าเคยเปลี่ยนอะไรเมื่อไร · ไม่มีร่าง · ไม่มีชื่อผู้เผยแพร่)</summary>
    [HttpGet("documents/{kind}/history")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> GetHistory(string kind, CancellationToken ct)
    {
        if (!LegalDocumentDefaults.IsKind(kind))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        var rows = await _legal.GetHistoryAsync(kind, ct);
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.Version, effectiveDate = r.EffectiveDate.ToString("yyyy-MM-dd"), r.ChangeSummary, r.IsCurrent, r.IsUpcoming, r.IsBuiltIn,
        }).ToList()));
    }

    /// <summary>ผู้ใช้ที่ล็อกอินยอมรับฉบับปัจจุบันแล้วหรือยัง (แถบแจ้งในระบบ)</summary>
    [HttpGet("acceptance")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<LegalAcceptanceStatus>>> GetAcceptance(CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);   // [Authorize] มีผลจริงแล้ว (ไม่มี AllowAnonymous ระดับคลาส)
        return Ok(new ApiResponse<LegalAcceptanceStatus>(true, await _legal.GetAcceptanceStatusAsync(uid, ct)));
    }

    /// <summary>ฝ่ายค้านรอบ 214 S3: เจ้าของข้อมูลดู/ดาวน์โหลดหลักฐานยอมรับนโยบายของตัวเองครบทุกช่อง (ม.30/31 · ระดับแพลตฟอร์ม —
    /// ผู้ใช้ที่ไม่มีบริษัทก็ใช้ได้) · DSR ของบริษัทไม่ส่ง IP/UA (ประวัติข้ามบริษัท) · ขอลบด้วยตัวเอง (ไม่มีบริษัท) = งานถัดไป</summary>
    [HttpGet("my-data")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("อ่านข้อมูลส่วนบุคคลแทนผู้ใช้")]
    public async Task<IActionResult> MyData([FromQuery] bool download = false, CancellationToken ct = default)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var rows = await _legal.GetMyAcceptancesAsync(uid, ct);
        // รอบ 214 ต่อ (ฐ): สถานะคำขอลบล่าสุดของผู้ใช้เอง (หน้า /my-data.html แสดง)
        var erase = await _pdpa.GetMyPlatformErasureRequestAsync(uid, ct);
        var payload = new
        {
            generatedAt = DateTime.UtcNow, policyAcceptances = rows,
            erasureRequest = erase == null ? null : new { erase.Status, erase.RequestedAt, erase.DueAt, erase.ResolvedAt, erase.ResolutionNote },
            // ฝ่ายค้านรอบ 214 B2: ต้องโอนความเป็นเจ้าของก่อน (ผู้ใช้เห็นก่อนยื่น)
            ownershipBlockers = await _pdpa.ErasureOwnershipBlockersAsync(uid, ct),
        };
        if (!download) return Ok(new ApiResponse<object>(true, payload));
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return File(json, "application/json", $"NextAcc-policy-acceptances-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    public sealed record EraseRequestBody(string? Reason);

    /// <summary>รอบ 214 ต่อ (คำตัดสินข้อ 173 ฐ): ผู้ใช้ยื่นคำขอลบข้อมูลของตัวเอง (ม.33) ระดับแพลตฟอร์ม — ใช้ได้แม้ไม่มีบริษัท ·
    /// ค้างอยู่แล้ว = คืนฉบับเดิม (idempotent) · ยื่นได้วันละครั้ง (429) · โหมดเข้าดูในนามลูกค้า = 403 · ผู้ดูแลระบบดำเนินการภายใน 30 วัน</summary>
    [HttpPost("my-data/erase-request")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ยื่นคำขอลบข้อมูลแทนผู้ใช้")]
    public async Task<ActionResult<ApiResponse<object>>> RequestErasure([FromBody] EraseRequestBody? body, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        if (User.FindFirst("imp")?.Value == "true")
            return StatusCode(403, new ApiResponse<object>(false, null, "โหมดเข้าดูในนามลูกค้ายื่นคำขอลบแทนเจ้าของบัญชีไม่ได้"));
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (row, created) = await _pdpa.RequestPlatformErasureAsync(uid, body?.Reason, ip, ct);
        return Ok(new ApiResponse<object>(true, new { row.Status, row.RequestedAt, row.DueAt, created },
            created ? "รับคำขอลบข้อมูลแล้ว — ผู้ดูแลระบบจะดำเนินการภายใน 30 วันและแจ้งผลทางอีเมล" : "มีคำขอลบที่กำลังดำเนินการอยู่แล้ว"));
    }

    public sealed record AcceptRequest(string? VersionShown);

    /// <summary>กด "รับทราบและยอมรับ" ฉบับปัจจุบัน — บันทึกหลักฐาน (PdpaConsentRecord + evidence hash)</summary>
    [HttpPost("acceptance")]
    [Authorize]
    [Accounting.Filters.RejectApiKey("ยอมรับนโยบายแทนผู้ใช้")]
    public async Task<ActionResult<ApiResponse<LegalAcceptanceStatus>>> Accept([FromBody] AcceptRequest? req, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);   // [Authorize] มีผลจริงแล้ว (ไม่มี AllowAnonymous ระดับคลาส)
        // โหมดเข้าดูในนามลูกค้า (claim imp=true) — ผู้ดูแลห้ามยอมรับนโยบายแทนเจ้าของบัญชี (หลักฐานต้องเป็นการกระทำของเจ้าของข้อมูลเอง)
        if (User.FindFirst("imp")?.Value == "true")
            return StatusCode(403, new ApiResponse<LegalAcceptanceStatus>(false, null, "โหมดเข้าดูในนามลูกค้ายอมรับนโยบายแทนเจ้าของบัญชีไม่ได้"));
        // X-Forwarded-For ตัวแรก = client จริงเมื่ออยู่หลัง reverse proxy (แบบเดียวกับ AuthService ตอนสมัคร)
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var status = await _legal.RecordAcceptanceAsync(uid, req?.VersionShown, ip, Request.Headers.UserAgent.ToString(), ct);
        return Ok(new ApiResponse<LegalAcceptanceStatus>(true, status, "บันทึกการยอมรับแล้ว"));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
