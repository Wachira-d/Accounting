using System.Diagnostics;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 250 (WP-02 · คำตัดสิน §3.3 Y7 · Y8 · N11): การ์ด "รายการตรวจ go-live" บนแดชบอร์ดแอดมิน (<c>/admin/index.html</c>) —
/// ข้อเท็จจริงจากฐานจริง → ตัวตัดสินเดียว <see cref="PlatformGoLiveChecklist.Evaluate"/> (หน้าแสดงอย่างเดียว) ·
/// ปุ่ม "ทดสอบการเชื่อมต่อขาออก" ยิง HEAD ไป <see cref="PlatformGoLiveChecklist.OutboundTestUrl"/> ผ่าน <see cref="OutboundUrlGuard"/>
/// (ตรวจ DNS/IP สาธารณะ + client ขาออกตัวเดียวของระบบ — ไม่ผ่าน proxy) แล้วบันทึกผลลง <c>SiteSettings</c> (การ์ดแดงเมื่อล้ม) + audit chain
/// </summary>
[ApiController]
[Route("api/admin/go-live")]
[Authorize(Roles = "SystemAdmin")]
public class AdminGoLiveController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly ILegalDocumentService _legal;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AdminGoLiveController> _logger;
    private readonly IConfiguration? _config;
    /// <summary>รอบ 257 ชุด 4 (E-H1): ค่าตั้ง LINE OA กลางที่ใช้จริง (ผู้อ่านตัวเดียว) · null = ถือว่ายังไม่ได้ตั้ง (ไม่ประทับ ✅)</summary>
    private readonly INextAccLineConfig? _nextAccLine;

    public AdminGoLiveController(AccountingDbContext db, IPlatformPaymentService platform, ILegalDocumentService legal,
        IHttpClientFactory http, ILogger<AdminGoLiveController> logger, IConfiguration? config = null, INextAccLineConfig? nextAccLine = null)
    {
        _db = db; _platform = platform; _legal = legal; _http = http; _logger = logger; _config = config; _nextAccLine = nextAccLine;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PlatformGoLiveChecklist.Report>>> Get(CancellationToken ct)
        => Ok(new ApiResponse<PlatformGoLiveChecklist.Report>(true, PlatformGoLiveChecklist.Evaluate(await FactsAsync(ct))));

    /// <summary>ข้อเท็จจริงทั้งหมดของรายการตรวจ — อ่านอย่างเดียว</summary>
    private async Task<PlatformGoLiveChecklist.Facts> FactsAsync(CancellationToken ct)
    {
        var s = await _db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var baseUrlConfigured = SystemBaseUrl.IsConfigured(s?.AppBaseUrl, _config?["App:BaseUrl"]);
        var cfg = await _platform.PlatformConfigAsync(ct);
        var sellable = await _db.PlanTemplates.AsNoTracking()
            .CountAsync(p => p.IsActive && (p.MonthlyPrice > 0m || p.QuarterlyPrice > 0m || p.SemiAnnualPrice > 0m || p.AnnualPrice > 0m), ct);
        var published = new Dictionary<string, (bool Published, bool HasEnglish)>();
        foreach (var kind in LegalDocumentDefaults.Kinds)
        {
            var v = await _legal.GetCurrentAsync(kind, ct);
            published[kind] = (v.Source == "published", !string.IsNullOrWhiteSpace(v.BodyMarkdownEn));
        }
        // รอบ 257 ชุด 4 (E-H1): บริษัทผู้ให้บริการ + LINE OA กลาง (ผู้อ่านเดียว INextAccLineConfig — ค่าในหน้าแอดมิน › config)
        var platformName = s?.PlatformCompanyId is Guid pcid
            ? await _db.Companies.AsNoTracking().Where(c => c.Id == pcid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        var centralOa = _nextAccLine == null ? null : await _nextAccLine.GetAsync(ct);
        return new PlatformGoLiveChecklist.Facts
        {
            PlatformCompanySet = platformName != null,
            PlatformCompanyName = platformName,
            CentralOaHasToken = centralOa?.HasToken == true,
            CentralOaHasSecret = centralOa?.HasSecret == true,
            CentralOaHasBasicId = !string.IsNullOrWhiteSpace(centralOa?.BasicId),
            CentralOaProblem = centralOa?.Problem,
            SystemBaseUrlConfigured = baseUrlConfigured,
            SystemBaseUrl = baseUrlConfigured ? SystemBaseUrl.Pick(s?.AppBaseUrl, _config?["App:BaseUrl"]) : null,
            PlatformGatewayConfigured = cfg != null,
            TestKeyPassed = cfg?.LastTestPassedAt != null,
            PublicCheckoutEnabled = s?.PublicCheckoutEnabled == true,
            SellablePlanCount = sellable,
            PrivacyPublished = published[LegalDocumentDefaults.KindPrivacy].Published,
            TermsPublished = published[LegalDocumentDefaults.KindTerms].Published,
            RefundPublished = published[LegalDocumentDefaults.KindRefund].Published,
            EnglishForAllPublished = published.Values.All(p => p.HasEnglish),
            ContactInfoSet = !string.IsNullOrWhiteSpace(s?.ContactPhone) && !string.IsNullOrWhiteSpace(s?.ContactEmail)
                && !string.IsNullOrWhiteSpace(s?.ContactAddress),
            SystemEmailConfigured = s?.SystemEmailConfigured == true,
            SelfTestPaid = cfg?.LastSelfTestPaidAt != null,
            LiveConfirmed = cfg?.PlatformLiveConfirmedAt != null,
            OutboundCheckAt = s?.OutboundCheckAt,
            OutboundCheckOk = s?.OutboundCheckOk,
            OutboundCheckMessage = s?.OutboundCheckMessage,
        };
    }

    /// <summary>Y8: ทดสอบการเชื่อมต่อขาออก — ผลบันทึกถาวร (การ์ดแดงเมื่อล้มจนกว่าจะทดสอบผ่าน) · ล้ม = คำตอบสำเร็จของ API (ผลการทดสอบคือข้อมูล) แต่สถานะในการ์ดแดง</summary>
    [HttpPost("outbound-check")]
    [Accounting.Filters.RejectApiKey("ทดสอบการเชื่อมต่อขาออก")]
    public async Task<ActionResult<ApiResponse<PlatformGoLiveChecklist.Report>>> OutboundCheck(CancellationToken ct)
    {
        var s = await _db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (s == null) return BadRequest(new ApiResponse<PlatformGoLiveChecklist.Report>(false, null, "ยังไม่มีค่าตั้งเว็บไซต์"));

        var sw = Stopwatch.StartNew();
        var guard = await OutboundUrlGuard.CheckResolvedAsync(PlatformGoLiveChecklist.OutboundTestUrl, ct);
        int? status = null;
        string? error = null;
        if (guard.Ok)
        {
            try
            {
                var client = _http.CreateClient(OutboundUrlGuard.WebhookClientName);
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = new HttpRequestMessage(HttpMethod.Head, PlatformGoLiveChecklist.OutboundTestUrl);
                using var resp = await client.SendAsync(req, ct);
                status = (int)resp.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // ไม่ใช่เส้นเงิน — ผลการทดสอบเก็บลงฐาน + การ์ดแดง + log (ล้มดัง 3 ที่)
                error = ex.Message;
                _logger.LogWarning(ex, "ทดสอบการเชื่อมต่อขาออกไป {Url} ไม่สำเร็จ", PlatformGoLiveChecklist.OutboundTestUrl);
            }
        }
        var (ok, message) = PlatformGoLiveChecklist.OutboundResult(status, guard.Ok ? null : guard.Reason ?? "ไม่ทราบเหตุ", error, sw.ElapsedMilliseconds);
        var before = new { s.OutboundCheckAt, s.OutboundCheckOk, s.OutboundCheckMessage };
        s.OutboundCheckAt = DateTime.UtcNow;
        s.OutboundCheckOk = ok;
        s.OutboundCheckMessage = message.Length > 500 ? message[..500] : message;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId,
            Action = AuditAction.Update,
            EntityType = nameof(SiteSettings),
            EntityId = s.Id.ToString(),
            UserEmail = User?.Identity?.Name,
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "OutboundCheck", ok, message = s.OutboundCheckMessage, target = PlatformGoLiveChecklist.OutboundTestUrl }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<PlatformGoLiveChecklist.Report>(true, PlatformGoLiveChecklist.Evaluate(await FactsAsync(ct)),
            ok ? "เชื่อมต่อขาออกได้" : "เชื่อมต่อขาออกไม่สำเร็จ — ดูรายละเอียดในการ์ด"));
    }
}
