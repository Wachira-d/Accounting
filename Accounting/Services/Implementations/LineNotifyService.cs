using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ตัวยิง LINE push ตัวเดียวของระบบ — ดูสัญญาที่ <see cref="ILineNotifyService"/> ·
/// รอบ 232 (คำตัดสิน 232-Notify): ทุกเมธอดตัดสินเส้นทางด้วย <see cref="NotificationAudiencePolicy.LineRoute"/> ก่อนยิง · เส้นของบริษัทอ่าน <b>OA ของบริษัทเท่านั้น</b>
/// (<see cref="ResolveCompanyOaAsync"/>) · เส้นกลางอ่าน <see cref="INextAccLineConfig"/> เท่านั้น — ไม่มีเมธอดไหน "ลองอันหนึ่งแล้วถอยไปอีกอัน"</summary>
public class LineNotifyService : ILineNotifyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineNotifyService> _logger;
    private readonly AccountingDbContext? _db;
    private readonly ISecretProtector _secrets;
    /// <summary>รอบ 212 D: ค่าตั้ง OA กลาง (หน้าแอดมินก่อน · config Line:* fallback) — ผู้อ่านตัวเดียว</summary>
    private readonly INextAccLineConfig _nextAcc;

    public LineNotifyService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineNotifyService> logger,
        AccountingDbContext db,
        ISecretProtector secrets,
        INextAccLineConfig? nextAcc = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _db = db;
        _secrets = secrets;
        // ไม่ได้ฉีดมา (เทสต์ที่สร้างตรง) ⇒ resolver ตัวเดียวกันจาก dependency เดิม (db null = config อย่างเดียว)
        _nextAcc = nextAcc ?? new NextAccLineConfig(db, configuration, secrets);
    }

    /// <summary>OA ของบริษัท (ถอดรหัสแล้ว) — <see cref="Configured"/> = เปิด LINE + token ถอดได้ · <see cref="GroupId"/> = "LINE กลุ่มของทีม"</summary>
    private sealed record CompanyOa(bool Configured, string? Token, string? GroupId)
    {
        public static CompanyOa None { get; } = new(false, null, null);
    }

    /// <summary>ค่าตั้ง OA ของ<b>บริษัท</b>เท่านั้น — รอบ 232: ไม่มี ⇒ <see cref="CompanyOa.None"/> (เดิมถอยไป token + GroupId ของ OA กลาง = ต้นตอที่เหตุการณ์ของผู้เช่าเด้งเข้าห้องแอดมินแพลตฟอร์ม) ·
    /// db null (เทสต์ที่สร้างตรง) = ไม่มี OA ของบริษัท</summary>
    private async Task<CompanyOa> ResolveCompanyOaAsync(Guid companyId)
    {
        if (_db == null) return CompanyOa.None;
        var s = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineEnabled, x.LineChannelAccessToken, x.LineDefaultGroupId })
            .FirstOrDefaultAsync();
        if (s == null || !s.LineEnabled || string.IsNullOrWhiteSpace(s.LineChannelAccessToken)) return CompanyOa.None;
        // ฝ่ายค้าน 232 S2: token ที่เก็บไว้แต่ถอดไม่ได้ (คีย์เปลี่ยน/ค่าเสีย) = ไม่มี OA (IsUsable ก่อน Unprotect — Unprotect คืนค่าว่าง/ค่าดิบเมื่อถอดไม่ออก)
        if (!_secrets.IsUsable(s.LineChannelAccessToken)) return CompanyOa.None;
        var token = _secrets.Unprotect(s.LineChannelAccessToken);
        if (string.IsNullOrWhiteSpace(token)) return CompanyOa.None;
        return new CompanyOa(true, token, string.IsNullOrWhiteSpace(s.LineDefaultGroupId) ? null : s.LineDefaultGroupId.Trim());
    }

    private async Task<bool> PushRawAsync(string? token, object payload, string context)
        => (await PushCoreAsync(token, payload, context)).Ok;

    /// <summary>ตัวยิง push จริงตัวเดียว — คืนผล + <b>เหตุ</b> (รอบ 212 C: ผู้เรียกที่ต้องประทับเหตุบนแถว เช่น สรุปประจำวัน) · log ปิดบัง userId ·
    /// รอบ 254 WP-11 (NOTIFY §5 ข้อ 8 · LINE_OA §7 ข้อ 3): <b>ทุก push</b> ส่ง <c>X-Line-Retry-Key</c> — ผู้เรียกที่มีเหตุการณ์ (คิว LINE) ส่ง key คงที่ต่อ
    /// (เหตุการณ์ + ผู้รับ) ⇒ ลองใหม่หลัง timeout ไม่เกิดสองข้อความ (LINE ตอบ 409 = รับไปแล้ว) · ผู้เรียกอื่นได้ UUID ใหม่ต่อการเรียก (ข้อความเดิมซ้ำโดยตั้งใจยังส่งได้) ·
    /// คืนสถานะ HTTP + <c>x-line-request-id</c> ให้ตัวจำแนก <see cref="LinePushOutcome"/></summary>
    private async Task<LinePushResult> PushCoreAsync(string? token, object payload, string context, Guid? retryKey = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("LINE skipped ({Ctx}) — no token configured", context);
            return LinePushResult.Fail("ยังไม่ได้ตั้งค่า Channel Access Token ของ LINE OA");
        }
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = PushTimeout;   // ฝ่ายค้าน M2: เพดานต่อคำขอ ⇒ แบตช์คิวจบก่อนเกณฑ์ "ค้างกำลังส่ง" เสมอ
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation(LineRetryKeyHeader, (retryKey ?? Guid.NewGuid()).ToString("D"));
            using var resp = await client.SendAsync(req);
            var requestId = resp.Headers.TryGetValues("x-line-request-id", out var ids) ? ids.FirstOrDefault() : null;
            // ฝ่ายค้าน L2: 409 (retry key เดิม) ⇒ หลักฐานคือคำขอที่ LINE รับไว้ก่อน (x-line-accepted-request-id) ไม่ใช่คำขอซ้ำนี้
            if ((int)resp.StatusCode == 409 && resp.Headers.TryGetValues("x-line-accepted-request-id", out var accepted))
                requestId = accepted.FirstOrDefault() ?? requestId;
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var redacted = CustomerLineLinkRule.RedactForLog(body);
                _logger.LogError("LINE push failed ({Ctx}): {Status} {Body}", context, resp.StatusCode, redacted);   // รอบ 211 ฝ่ายค้าน: ห้าม log userId เต็ม
                return new LinePushResult(false, $"LINE ตอบ {(int)resp.StatusCode}: {LineMessageLimits.Truncate(redacted, 200)}",
                    HttpStatus: (int)resp.StatusCode, LineRequestId: requestId);
            }
            return new LinePushResult(true, null, HttpStatus: (int)resp.StatusCode, LineRequestId: requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE push threw ({Ctx})", context);
            return new LinePushResult(false, $"เรียก LINE ไม่สำเร็จ ({ex.GetType().Name})", TransportError: true);
        }
    }

    /// <summary>ชื่อ header กันส่งซ้ำของ LINE Messaging API (UUID · ⚠ DESIGN §7.4 ยืนยันกับเอกสารทางการ)</summary>
    internal const string LineRetryKeyHeader = "X-Line-Retry-Key";

    /// <summary>เพดานเวลาต่อคำขอ LINE (ฝ่ายค้าน M2) — <c>ContactLineQueueService.StaleSending</c> ต้องมากกว่า BatchSize × ค่านี้</summary>
    internal static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(15);

    private static object TextPayload(string to, string text)
        => new { to, messages = new[] { new { type = "text", text } } };

    private static object FlexPayload(string to, string altText, object flexContents)
        => new
        {
            to,
            messages = new[]
            {
                new
                {
                    type = "flex",
                    altText = LineMessageLimits.Truncate(string.IsNullOrEmpty(altText) ? "การแจ้งเตือนจาก NextAcc" : altText, 400),
                    contents = flexContents
                }
            }
        };

    // ───────── เส้นของผู้เช่า — OA ของบริษัทเท่านั้น ─────────

    public async Task<LinePushResult> TryPushCompanyGroupAsync(Guid companyId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return LinePushResult.Fail("ไม่มีข้อความให้ส่ง");
        var oa = await ResolveCompanyOaAsync(companyId);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.CompanyGroup, oa.Configured);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        if (oa.GroupId == null) return LinePushResult.Fail("ยังไม่ได้ใส่ \"LINE กลุ่มของทีม\" (Group ID) ในหน้าตั้งค่า LINE ของบริษัท");
        return await PushCoreAsync(oa.Token, TextPayload(oa.GroupId, LineMessageLimits.Truncate(message, 5000)), $"company-group {companyId}");
    }

    public async Task<LinePushResult> TryPushToUserAsync(Guid companyId, string lineUserId, string message)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return LinePushResult.Fail("ผู้รับยังไม่ผูก LINE");
        var oa = await ResolveCompanyOaAsync(companyId);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.TenantMember, oa.Configured);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        return await PushCoreAsync(oa.Token, TextPayload(lineUserId, message), $"user {CustomerLineLinkRule.Mask(lineUserId)}");
    }

    public async Task<LinePushResult> TryPushFlexToUserAsync(Guid companyId, string lineUserId, string altText, object flexContents)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return LinePushResult.Fail("ผู้รับยังไม่ผูก LINE");
        var oa = await ResolveCompanyOaAsync(companyId);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.TenantMember, oa.Configured);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        return await PushCoreAsync(oa.Token, FlexPayload(lineUserId, altText, flexContents), $"flex user {CustomerLineLinkRule.Mask(lineUserId)}");
    }

    public async Task<LinePushResult> TryPushContactAsync(Guid companyId, string oaScope, LineTargetKind target, string to,
        string altText, object flexContents, Guid retryKey)
    {
        var (denied, token, central) = await ContactRouteAsync(companyId, oaScope, target, to);
        if (denied != null) return denied;
        var result = await PushCoreAsync(token, FlexPayload(to.Trim(), altText, flexContents),
            $"contact {(target == LineTargetKind.ContactGroup ? "group" : "user")} {CustomerLineLinkRule.Mask(to)}", retryKey);
        if (result.Ok && central) await CountCentralGroupPushAsync();
        return result;
    }

    public async Task<LinePushResult> TryPushContactTextAsync(Guid companyId, string oaScope, LineTargetKind target, string to, string text, Guid retryKey)
    {
        if (string.IsNullOrWhiteSpace(text)) return LinePushResult.Fail("ไม่มีข้อความให้ส่ง");
        var (denied, token, central) = await ContactRouteAsync(companyId, oaScope, target, to);
        if (denied != null) return denied;
        var result = await PushCoreAsync(token, TextPayload(to.Trim(), LineMessageLimits.Truncate(text, 5000)),
            $"contact-text {CustomerLineLinkRule.Mask(to)}", retryKey);
        if (result.Ok && central) await CountCentralGroupPushAsync();
        return result;
    }

    /// <summary>รอบ 256-2: push เข้ากลุ่มของผู้ติดต่อผ่าน OA กลางสำเร็จ ⇒ +1 ในตัวนับต้นทุน OA กลาง (ล้ม ⇒ log Error ไม่ล้มการส่ง)</summary>
    private async Task CountCentralGroupPushAsync()
    {
        if (_db == null) return;
        await NextAccOaUsage.IncrementAsync(_db, NextAccOaQuotaRule.KindGroupPush, _logger);
    }

    /// <summary>ด่านเดียวของปลายทางผู้ติดต่อ (flex + text): กลุ่มกลางของแอดมินปฏิเสธเสมอ (Q1(b)) · OA กลางยังปิด (N8) ยกเว้นกลุ่มของบริษัทผู้ให้บริการ
    /// (<see cref="CentralGroupRouteAsync"/> · คำตัดสิน 256-2) · นอกนั้น OA ของบริษัทเท่านั้น</summary>
    private async Task<(LinePushResult? Denied, string? Token, bool Central)> ContactRouteAsync(Guid companyId, string oaScope, LineTargetKind target, string to)
    {
        if (string.IsNullOrWhiteSpace(to)) return (LinePushResult.Fail("ปลายทาง LINE ไม่มีค่า (ถอดรหัสไม่ได้หรือถูกล้างแล้ว)"), null, false);
        if (target is not (LineTargetKind.TenantMember or LineTargetKind.ContactGroup))
            return (LinePushResult.Denied(NotificationAudiencePolicy.RuleTenantNotPlatform, NotificationAudiencePolicy.TenantNotPlatformMessage), null, false);
        // คำตอบเจ้าของ Q1(b): groupId กลางของแอดมิน (ทุกแหล่ง — หน้าแอดมิน + config) ห้ามเป็นปลายทางของผู้ติดต่อ <b>ไม่ว่าผ่าน OA ใด</b> — ด่านชั้นที่สอง
        // (ชั้นแรกคือ ContactRecipientRule.GroupProblem ตอนหาผู้รับ) · กันค่าที่หลุดมาทางอื่น (แถวที่ถูกแก้ในฐาน/ผูกก่อนแอดมินตั้งกลุ่มกลาง)
        if (ContactRecipientRule.IsPlatformCentralGroup(to, await _nextAcc.PlatformGroupIdsAsync()))
            return (LinePushResult.Denied(ContactRecipientRule.RuleCodePlatformGroup,
                "ปลายทางนี้คือกลุ่ม LINE กลางของระบบ NextAcc (รับเฉพาะเรื่องของระบบ) — ไม่ส่งเรื่องของบริษัท/ผู้ติดต่อเข้ากลุ่มนี้ (คำตัดสินเจ้าของ Q1(b))"), null, false);
        if (oaScope == NotifyEndpointKind.ScopeNextAcc && target == LineTargetKind.ContactGroup)
        {
            var (centralDenied, centralToken) = await CentralGroupRouteAsync(companyId);
            return (centralDenied, centralToken, centralDenied == null);
        }
        if (oaScope != NotifyEndpointKind.ScopeCompany)
            return (oaScope == NotifyEndpointKind.ScopeNextAcc
                ? LinePushResult.Denied(ContactRecipientRule.RuleCodeNextAccNotOpen, ContactRecipientRule.NextAccNotOpenReason)
                : LinePushResult.Fail("ไม่รู้ว่าปลายทางนี้ผูกกับ LINE OA ใด — ไม่ส่ง (ให้ผู้รับผูกใหม่)"), null, false);
        var oa = await ResolveCompanyOaAsync(companyId);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, target, oa.Configured);
        if (!route.Allowed) return (LinePushResult.Denied(route.RuleCode, route.Reason!), null, false);
        return (null, oa.Token, false);
    }

    /// <summary>รอบ 256 (คำตัดสินเจ้าของ 256-2) — กลุ่มของผู้ติดต่อผ่าน <b>OA กลาง</b>: ตรวจบริษัทผู้ให้บริการ<b>ใหม่ตอนส่ง</b> (เปลี่ยนแล้ว ⇒ ปฏิเสธพร้อมเหตุ) ผ่านด่านเดียว
    /// <see cref="LineGroupBindRule.CentralScopeAllowed"/> → นโยบายเส้นทาง (สวิตช์กลุ่มผ่าน OA กลาง) → token ของ OA กลาง · db null (เทสต์ที่สร้างตรง) = ไม่มีบริษัทผู้ให้บริการ</summary>
    private async Task<(LinePushResult? Denied, string? Token)> CentralGroupRouteAsync(Guid companyId)
    {
        var platformId = _db == null ? null : await PlatformCompanyLookup.IdAsync(_db);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.ContactGroup, companyOaConfigured: false,
            centralOaGroupDeliveryEnabled: LineGroupBindRule.CentralScopeAllowed(companyId, platformId));
        if (!route.Allowed)
            return (LinePushResult.Denied(ContactRecipientRule.RuleCodeNextAccNotOpen, ContactRecipientRule.CentralGroupNotPlatformReason), null);
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return (LinePushResult.Fail(NextAccLineConfigRule.NotConfiguredMessage), null);
        return (null, token);
    }

    public async Task<LineGroupSummary?> TryGetCompanyGroupSummaryAsync(Guid companyId, string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return null;
        var oa = await ResolveCompanyOaAsync(companyId);
        if (!oa.Configured || string.IsNullOrWhiteSpace(oa.Token)) return null;
        return await GroupSummaryCoreAsync(oa.Token!, groupId);
    }

    public async Task<LineGroupSummary?> TryGetNextAccGroupSummaryAsync(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return null;
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return null;
        return await GroupSummaryCoreAsync(token, groupId);
    }

    /// <summary>ตัวอ่านชื่อ/จำนวนสมาชิกกลุ่มตัวเดียว (OA ตาม token ที่ผู้เรียกเลือก)</summary>
    private async Task<LineGroupSummary?> GroupSummaryCoreAsync(string token, string groupId)
    {
        var id = Uri.EscapeDataString(groupId.Trim());
        string? name = null;
        int? count = null;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = PushTimeout;
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.line.me/v2/bot/group/{id}/summary"))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    name = doc.RootElement.TryGetProperty("groupName", out var n) ? n.GetString() : null;
                }
            }
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.line.me/v2/bot/group/{id}/members/count"))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var v)) count = v;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
        {
            _logger.LogWarning(ex, "อ่านข้อมูลกลุ่ม LINE ({Group}) ไม่สำเร็จ", CustomerLineLinkRule.Mask(groupId));
        }
        return new LineGroupSummary(name, count);
    }

    public async Task<bool> TryLeaveCompanyGroupAsync(Guid companyId, string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        if (ContactRecipientRule.IsPlatformCentralGroup(groupId, await _nextAcc.PlatformGroupIdsAsync())) return false;
        var oa = await ResolveCompanyOaAsync(companyId);
        if (!oa.Configured || string.IsNullOrWhiteSpace(oa.Token)) return false;
        return await LeaveGroupCoreAsync(oa.Token!, groupId);
    }

    public async Task<bool> TryLeaveNextAccGroupAsync(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        // Q1(b): บอทกลางไม่มีวันออกจากกลุ่มกลางของแอดมิน (ทุกแหล่ง)
        if (ContactRecipientRule.IsPlatformCentralGroup(groupId, await _nextAcc.PlatformGroupIdsAsync())) return false;
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return false;
        return await LeaveGroupCoreAsync(token, groupId);
    }

    /// <summary>ตัวสั่งบอทออกจากกลุ่มตัวเดียว (OA ตาม token ที่ผู้เรียกเลือก — ผู้เรียกตรวจกลุ่มกลางของแอดมินแล้ว)</summary>
    private async Task<bool> LeaveGroupCoreAsync(string token, string groupId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = PushTimeout;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.line.me/v2/bot/group/{Uri.EscapeDataString(groupId.Trim())}/leave");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("บอทออกจากกลุ่ม LINE ({Group}) ไม่สำเร็จ: {Status}", CustomerLineLinkRule.Mask(groupId), (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
        {
            _logger.LogWarning(ex, "บอทออกจากกลุ่ม LINE ({Group}) ไม่สำเร็จ", CustomerLineLinkRule.Mask(groupId));
            return false;
        }
    }

    public async Task<string?> TryGetCompanyOaProfileNameAsync(Guid companyId, string lineUserId)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return null;
        var oa = await ResolveCompanyOaAsync(companyId);
        if (!oa.Configured || string.IsNullOrWhiteSpace(oa.Token)) return null;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = PushTimeout;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.line.me/v2/bot/profile/" + Uri.EscapeDataString(lineUserId.Trim()));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oa.Token);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("LINE profile ({User}) ตอบ {Status}", CustomerLineLinkRule.Mask(lineUserId), (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
        {
            _logger.LogWarning(ex, "อ่านโปรไฟล์ LINE ({User}) ไม่สำเร็จ", CustomerLineLinkRule.Mask(lineUserId));
            return null;
        }
    }

    public async Task<(bool ok, string message)> TestConfigAsync(Guid companyId, string toLineId)
    {
        if (string.IsNullOrWhiteSpace(toLineId))
            return (false, "กรุณาระบุ LINE User ID หรือ Group ID ปลายทาง");
        var oa = await ResolveCompanyOaAsync(companyId);
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.CompanyOa, LineTargetKind.TenantMember, oa.Configured);
        if (!route.Allowed)
            return (false, "ยังไม่ได้ตั้งค่า Channel Access Token ของ LINE OA ของร้าน (ติ๊ก \"เปิดใช้งาน\" + ใส่ Token แล้วบันทึกก่อน) — " + route.Reason);
        var ok = await PushRawAsync(oa.Token, TextPayload(toLineId.Trim(),
            $"✅ ทดสอบ LINE Messaging API สำเร็จ\nบริษัท: {companyId}\nเวลา: {NowIct()}"), "test");
        return ok
            ? (true, "ส่งข้อความทดสอบสำเร็จ")
            : (false, "ส่งไม่สำเร็จ ตรวจสอบ Token และ ID ปลายทาง (ดู log)");
    }

    // ───────── เส้น OA กลาง — ระบุชัด ─────────

    /// <summary>รอบ 212 C — push ถึง<b>พนักงาน/ผู้ใช้ ERP/ลูกค้า</b>ที่ผูกกับบอทกลางเองผ่าน OA กลางของ NextAcc <b>แบบระบุชัด</b> (ไม่อ่านค่าตั้งบริษัท):
    /// userId ของ LINE แยกตาม provider ⇒ ส่งผ่าน OA ของร้านไม่มีทางถึง · ข้อความผ่าน <see cref="LineMessageLimits.Normalize"/> (≤ 5 ก้อน × ≤ 5,000 ตัว) ·
    /// ไม่ตั้ง token กลาง ⇒ ล้มพร้อมเหตุ (ไม่ถอยไป OA ร้าน) · รอบ 232: ปลายทาง = <see cref="LineTargetKind.TenantMember"/> (ช่องส่วนตัวของสมาชิก — ไม่ใช่ห้องของแพลตฟอร์ม)</summary>
    public async Task<LinePushResult> TryPushViaNextAccAsync(string lineUserId, IReadOnlyList<string> messages,
        NotificationAudience audience = NotificationAudience.TenantBusiness)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return LinePushResult.Fail("ผู้รับยังไม่ผูก LINE");
        var texts = LineMessageLimits.Normalize(messages);
        if (texts.Count == 0) return LinePushResult.Fail("ไม่มีข้อความให้ส่ง");
        // ฝ่ายค้าน 232 N1: ผู้เรียกประกาศผู้รับของเหตุการณ์ (TenantBusiness ตั้งต้น · PlatformBillingNotifier = PlatformAccount) — ปลายทางเดียวกัน = สมาชิกของบริษัทนั้น
        var route = NotificationAudiencePolicy.LineRoute(audience, LineOaKind.NextAccOa, LineTargetKind.TenantMember, companyOaConfigured: false);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return LinePushResult.Fail(NextAccLineConfigRule.NotConfiguredMessage);
        return await PushCoreAsync(token, new
        {
            to = lineUserId,
            messages = texts.Select(t => new { type = "text", text = t }).ToArray()
        }, $"staff {CustomerLineLinkRule.Mask(lineUserId)}");
    }

    /// <summary>รอบ 212 D — Flex ถึง<b>พนักงาน</b>ผ่าน OA กลางแบบระบุชัด (คู่ของ <see cref="TryPushViaNextAccAsync"/>) · ใช้กับสลิปเงินเดือนที่ผูกผ่าน OA กลาง · คืนเหตุเมื่อล้ม</summary>
    public async Task<LinePushResult> TryPushFlexViaNextAccAsync(string lineUserId, string altText, object flexContents)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return LinePushResult.Fail("ผู้รับยังไม่ผูก LINE");
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.TenantBusiness, LineOaKind.NextAccOa, LineTargetKind.TenantMember, companyOaConfigured: false);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return LinePushResult.Fail(NextAccLineConfigRule.NotConfiguredMessage);
        return await PushCoreAsync(token, FlexPayload(lineUserId, altText, flexContents), $"staff flex {CustomerLineLinkRule.Mask(lineUserId)}");
    }

    /// <summary>รอบ 232 — เรื่องของ<b>ผู้ดูแลแพลตฟอร์ม</b>เข้ากลุ่ม LINE กลาง (<c>SiteSettings.LineMessagingGroupId</c>) · แอดมินปิดได้ (<c>OpsAlertsToGroup</c>) ·
    /// ไม่มี token/กลุ่มกลาง ⇒ ล้มพร้อมเหตุ (ไม่ถอยไป OA ของบริษัทใด)</summary>
    public async Task<LinePushResult> TryPushPlatformOpsAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return LinePushResult.Fail("ไม่มีข้อความให้ส่ง");
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudience.PlatformOps, LineOaKind.NextAccOa, LineTargetKind.PlatformGroup, companyOaConfigured: false);
        if (!route.Allowed) return LinePushResult.Denied(route.RuleCode, route.Reason!);
        var nx = await _nextAcc.GetAsync();
        if (!nx.OpsAlertsToGroup) return LinePushResult.Fail("ผู้ดูแลระบบปิด \"ส่งเรื่องของระบบเข้ากลุ่ม LINE กลาง\" ไว้");
        if (!nx.HasToken) return LinePushResult.Fail(nx.Problem ?? NextAccLineConfigRule.NotConfiguredMessage);
        if (string.IsNullOrWhiteSpace(nx.GroupId)) return LinePushResult.Fail("ยังไม่ได้ตั้ง Group ID ของกลุ่ม LINE กลาง (ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง)");
        return await PushCoreAsync(nx.AccessToken, TextPayload(nx.GroupId, LineMessageLimits.Truncate(message, 5000)), "platform-ops");
    }

    public async Task<NextAccOaQuotaReading?> TryReadNextAccQuotaAsync(CancellationToken ct = default)
    {
        var token = await NextAccTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = PushTimeout;
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var quotaResp = await client.GetAsync("https://api.line.me/v2/bot/message/quota", ct);
            using var usedResp = await client.GetAsync("https://api.line.me/v2/bot/message/quota/consumption", ct);
            if (!quotaResp.IsSuccessStatusCode || !usedResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("อ่านโควตา OA กลางไม่สำเร็จ (quota {Q} · consumption {C})", (int)quotaResp.StatusCode, (int)usedResp.StatusCode);
                return null;
            }
            var used = NextAccOaQuotaRule.ParseConsumption(await usedResp.Content.ReadAsStringAsync(ct));
            if (used is not long u) return null;
            return new NextAccOaQuotaReading(NextAccOaQuotaRule.ParseQuota(await quotaResp.Content.ReadAsStringAsync(ct)), u);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "อ่านโควตา OA กลางไม่สำเร็จ");
            return null;
        }
    }

    /// <summary>token ของ OA กลาง (NextAcc) — ผ่าน resolver ตัวเดียว (รอบ 212 D)</summary>
    private async Task<string?> NextAccTokenAsync() => (await _nextAcc.GetAsync()).AccessToken;

    // Servers run UTC; recipients are Thai-based. Render times in ICT (UTC+7)
    // so the LINE message matches what the user sees on the wall clock.
    private static string NowIct() => DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm");
}
