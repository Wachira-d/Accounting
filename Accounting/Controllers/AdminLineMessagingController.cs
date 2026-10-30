using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>สถานะค่าตั้ง LINE OA กลาง — ความลับไม่ถูกส่งกลับ (บอกแค่ "ตั้งแล้ว")</summary>
/// <param name="EffectiveSource">ชุดที่ระบบใช้อยู่จริง: admin = ค่าในหน้านี้ · config = Line:* ของเซิร์ฟเวอร์ · none = ยังไม่มี token</param>
public record NextAccLineAdminDto(bool HasAccessToken, bool HasChannelSecret, string? BasicId, string? BotUserId, string? GroupId,
    string EffectiveSource, bool ConfigHasToken, string WebhookUrl, string? AddFriendUrl, IReadOnlyList<string> Warnings);

/// <summary>บันทึก — ช่องลับว่าง = คงค่าเดิม (ล้างต้องติ๊ก Clear*) · ช่องไม่ลับ null = คงค่าเดิม · "" = ล้าง</summary>
public record UpdateNextAccLineRequest(string? ChannelAccessToken, string? ChannelSecret, bool ClearAccessToken, bool ClearChannelSecret,
    string? BasicId, string? BotUserId, string? GroupId);

/// <summary>ผลทดสอบ token กับ LINE (<c>GET /v2/bot/info</c>) — ค่าจริงของบอทให้หน้าเว็บเสนอเติม Basic ID / Bot User ID</summary>
public record NextAccLineTestDto(bool Ok, string Message, string? BasicId, string? BotUserId, string? DisplayName, string? WebhookEndpoint, bool? WebhookActive);

/// <summary>
/// รอบ 212 แพ็ก D — ค่าตั้ง LINE OA กลางของ NextAcc (Messaging API) บนหน้าแอดมิน <c>/admin/site-settings.html</c> แท็บ "LINE OA กลาง" ·
/// เดิมอ่านจาก config <c>Line:*</c> อย่างเดียว (แอดมินมองไม่เห็น/แก้ไม่ได้) · ผู้อ่านค่าตัวเดียว <c>INextAccLineConfig</c> (หน้านี้ก่อน · config fallback) ·
/// เฉพาะผู้ดูแลแพลตฟอร์ม (SystemAdmin) แบบเดียวกับค่าตั้งอื่นของหน้านั้น
/// </summary>
[ApiController]
[Route("api/admin/line-messaging")]
[Authorize(Roles = "SystemAdmin")]
public class AdminLineMessagingController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly Accounting.Services.Interfaces.INextAccLineConfig _lineConfig;
    private readonly ILogger<AdminLineMessagingController> _logger;

    public AdminLineMessagingController(AccountingDbContext db, ISecretProtector secrets, IConfiguration config, IHttpClientFactory http,
        Accounting.Services.Interfaces.INextAccLineConfig lineConfig, ILogger<AdminLineMessagingController> logger)
    {
        _db = db; _secrets = secrets; _config = config; _http = http; _lineConfig = lineConfig; _logger = logger;
    }

    private async Task<SiteSettings> GetOrCreateAsync(CancellationToken ct)
    {
        var s = await _db.SiteSettings.FirstOrDefaultAsync(ct);
        if (s != null) return s;
        s = new SiteSettings();
        _db.SiteSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    private async Task<NextAccLineAdminDto> BuildAsync(SiteSettings s, CancellationToken ct)
    {
        // ชุดที่ใช้จริงคำนวณจากค่าที่เพิ่งบันทึก (ไม่ใช้ผลที่ resolver โหลดไว้ก่อนบันทึกในคำขอเดียวกัน)
        var admin = new NextAccLineValues(_secrets.Unprotect(s.LineMessagingChannelAccessToken), _secrets.Unprotect(s.LineMessagingChannelSecret),
            s.LineMessagingBasicId, s.LineMessagingBotUserId, s.LineMessagingGroupId);
        var cfg = Accounting.Services.Implementations.NextAccLineConfig.FromConfig(_config);
        var eff = NextAccLineConfigRule.Merge(admin, cfg);
        var hasToken = _secrets.IsUsable(s.LineMessagingChannelAccessToken);
        var hasSecret = _secrets.IsUsable(s.LineMessagingChannelSecret);
        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(s.LineMessagingChannelAccessToken) && !hasToken)
            warnings.Add("Channel Access Token ที่บันทึกไว้ถอดรหัสไม่ได้ (คีย์เข้ารหัสของระบบถูกเปลี่ยน) — กรอกใหม่");
        if (!string.IsNullOrEmpty(s.LineMessagingChannelSecret) && !hasSecret)
            warnings.Add("Channel Secret ที่บันทึกไว้ถอดรหัสไม่ได้ (คีย์เข้ารหัสของระบบถูกเปลี่ยน) — กรอกใหม่");
        if (hasToken && !hasSecret)
            warnings.Add("มี Channel Access Token แต่ยังไม่มี Channel Secret — webhook ของ OA กลางจะถูกปฏิเสธทุกครั้ง (ผูก LINE/คำสั่งบอทใช้ไม่ได้)");
        if (!hasToken && (hasSecret || s.LineMessagingBasicId != null || s.LineMessagingBotUserId != null))
            warnings.Add(eff.Source == NextAccLineConfigRule.SourceConfig
                ? "ยังไม่มี Channel Access Token ในหน้านี้ — ระบบใช้ค่าจาก config ของเซิร์ฟเวอร์ (Line:*) ทั้งชุด ค่าที่กรอกในหน้านี้ยังไม่มีผล"
                : "ยังไม่มี Channel Access Token — ส่ง/ตอบ LINE จาก OA กลางไม่ได้");
        if (eff.HasToken && eff.BasicId == null)
            warnings.Add("ยังไม่มี Basic ID — ปุ่มเพิ่มเพื่อน/ลิงก์ผูก LINE ของลูกค้าและพนักงานแสดงไม่ได้");
        if (!eff.HasToken) warnings.Add(NextAccLineConfigRule.NotConfiguredMessage);
        var baseUrl = await _db.SiteSettings.AsNoTracking().Select(x => x.AppBaseUrl).FirstOrDefaultAsync(ct);
        return new NextAccLineAdminDto(hasToken, hasSecret, s.LineMessagingBasicId, s.LineMessagingBotUserId, s.LineMessagingGroupId,
            eff.Source, !string.IsNullOrWhiteSpace(cfg.AccessToken), NextAccLineConfigRule.WebhookUrl(baseUrl ?? _config["App:BaseUrl"]),
            CustomerLineLinkRule.AddFriendUrl(eff.BasicId), warnings);
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<NextAccLineAdminDto>>> Get(CancellationToken ct = default)
    {
        var s = await GetOrCreateAsync(ct);
        return Ok(new ApiResponse<NextAccLineAdminDto>(true, await BuildAsync(s, ct)));
    }

    [HttpPut]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("ตั้งค่า LINE OA กลางของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<NextAccLineAdminDto>>> Update([FromBody] UpdateNextAccLineRequest req, CancellationToken ct = default)
    {
        var errBasic = NextAccLineConfigRule.ValidateBasicId(req.BasicId, out var basic);
        var errBot = NextAccLineConfigRule.ValidateBotUserId(req.BotUserId, out var bot);
        var errGroup = NextAccLineConfigRule.ValidateGroupId(req.GroupId, out var group);
        var err = errBasic ?? errBot ?? errGroup;
        if (err != null) return BadRequest(new ApiResponse<NextAccLineAdminDto>(false, null, err));

        var s = await GetOrCreateAsync(ct);
        s.LineMessagingChannelAccessToken = NextAccLineConfigRule.ApplySecret(s.LineMessagingChannelAccessToken, req.ChannelAccessToken, req.ClearAccessToken, v => _secrets.Protect(v));
        s.LineMessagingChannelSecret = NextAccLineConfigRule.ApplySecret(s.LineMessagingChannelSecret, req.ChannelSecret, req.ClearChannelSecret, v => _secrets.Protect(v));
        s.LineMessagingBasicId = NextAccLineConfigRule.ApplyText(s.LineMessagingBasicId, basic, req.BasicId != null);
        s.LineMessagingBotUserId = NextAccLineConfigRule.ApplyText(s.LineMessagingBotUserId, bot, req.BotUserId != null);
        s.LineMessagingGroupId = NextAccLineConfigRule.ApplyText(s.LineMessagingGroupId, group, req.GroupId != null);
        s.UpdatedAt = DateTime.UtcNow;
        s.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("ผู้ดูแลระบบ {User} แก้ค่าตั้ง LINE OA กลาง (token {Tok} · secret {Sec})", s.UpdatedBy,
            req.ClearAccessToken ? "ล้าง" : string.IsNullOrWhiteSpace(req.ChannelAccessToken) ? "คงเดิม" : "ใหม่",
            req.ClearChannelSecret ? "ล้าง" : string.IsNullOrWhiteSpace(req.ChannelSecret) ? "คงเดิม" : "ใหม่");
        return Ok(new ApiResponse<NextAccLineAdminDto>(true, await BuildAsync(s, ct), "บันทึกค่าตั้ง LINE OA กลางแล้ว — กด \"ทดสอบ\" เพื่อยืนยันกับ LINE"));
    }

    /// <summary>ทดสอบ token ที่ระบบใช้อยู่จริง (หน้านี้ก่อน · config fallback) กับ LINE: <c>GET /v2/bot/info</c> (+ ดู webhook endpoint ที่ตั้งใน LINE Developers) ·
    /// ไม่บันทึกอะไร — หน้าเว็บเสนอให้กดเติม Basic ID / Bot User ID จากผลนี้แล้วกดบันทึกเอง</summary>
    [HttpPost("test")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("ทดสอบ LINE OA กลางของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<NextAccLineTestDto>>> Test(CancellationToken ct = default)
    {
        var nx = await _lineConfig.GetAsync(ct);
        if (!nx.HasToken)
            return Ok(new ApiResponse<NextAccLineTestDto>(false, new NextAccLineTestDto(false, NextAccLineConfigRule.NotConfiguredMessage, null, null, null, null, null),
                NextAccLineConfigRule.NotConfiguredMessage));
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nx.AccessToken);
        try
        {
            using var resp = await client.GetAsync("https://api.line.me/v2/bot/info", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"LINE ปฏิเสธ token ({(int)resp.StatusCode}) — ตรวจ Channel Access Token ({(nx.Source == NextAccLineConfigRule.SourceAdmin ? "ค่าในหน้านี้" : "ค่าใน config Line:*")})";
                return Ok(new ApiResponse<NextAccLineTestDto>(false, new NextAccLineTestDto(false, msg, null, null, null, null, null), msg));
            }
            using var doc = JsonDocument.Parse(body);
            string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var root = doc.RootElement;
            var basicId = Str(root, "basicId");
            var userId = Str(root, "userId");
            var name = Str(root, "displayName");

            string? endpoint = null; bool? active = null;
            try
            {
                using var wr = await client.GetAsync("https://api.line.me/v2/bot/channel/webhook/endpoint", ct);
                if (wr.IsSuccessStatusCode)
                {
                    using var wd = JsonDocument.Parse(await wr.Content.ReadAsStringAsync(ct));
                    endpoint = Str(wd.RootElement, "endpoint");
                    active = wd.RootElement.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)a.GetBoolean() : null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "อ่าน webhook endpoint ของ OA กลางไม่สำเร็จ (ผลทดสอบ token ยังใช้ได้)");
            }

            var parts = new List<string> { $"เชื่อมต่อสำเร็จ — บอท \"{name}\" ({basicId})" };
            if (nx.BasicId != null && basicId != null && !string.Equals(nx.BasicId, CustomerLineLinkRule.NormalizeBasicId(basicId), StringComparison.OrdinalIgnoreCase))
                parts.Add($"⚠️ Basic ID ที่ตั้งไว้ ({nx.BasicId}) ไม่ตรงกับของบอทนี้ ({basicId})");
            if (nx.BotUserId != null && userId != null && !string.Equals(nx.BotUserId, userId, StringComparison.Ordinal))
                parts.Add("⚠️ Bot User ID ที่ตั้งไว้ไม่ตรงกับของบอทนี้ — การผูก LINE ของลูกค้าจะถูกปฏิเสธ");
            if (active == false) parts.Add("⚠️ ยังไม่ได้เปิด \"Use webhook\" ใน LINE Developers");
            var message = string.Join(" · ", parts);
            return Ok(new ApiResponse<NextAccLineTestDto>(true, new NextAccLineTestDto(true, message, basicId, userId, name, endpoint, active), message));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "ทดสอบ LINE OA กลางล้ม");
            var msg = $"เรียก LINE ไม่สำเร็จ ({ex.GetType().Name}) — ตรวจการเชื่อมต่ออินเทอร์เน็ตของเซิร์ฟเวอร์";
            return Ok(new ApiResponse<NextAccLineTestDto>(false, new NextAccLineTestDto(false, msg, null, null, null, null, null), msg));
        }
    }
}
