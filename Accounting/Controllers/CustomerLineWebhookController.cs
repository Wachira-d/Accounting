using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 211 (คำตัดสินข้อ 170) — webhook ของ LINE OA <b>ของบริษัท</b> (ร้านตั้งใน LINE Developers ว่า <c>{App:BaseUrl}/api/line/webhook/c/{companyId}</c>)
/// <list type="bullet">
/// <item>ด่าน: บริษัทต้องมี Channel Secret ที่ถอดได้ · ลายเซ็นตรวจด้วย <see cref="LineWebhookSignature"/> ตัวเดียวของระบบ · ไม่ได้ตั้ง/ลายเซ็นผิด ⇒ 401 แบบเดียวกัน · body ≤ 256 KB</item>
/// <item>ข้อความ <c>ผูก LN-…</c> ⇒ ผูก LINE ของลูกค้า (<see cref="ICustomerLineLinkService.TryRedeemAsync"/> · scope company + companyId จาก URL ที่ลายเซ็นผ่าน) · ข้อความอื่นไม่ตอบ (แชตของร้านเอง)</item>
/// <item>รอบ 260: รูปในแชต 1:1 / กลุ่มของผู้ติดต่อที่แสดงยอด ⇒ <c>ILineChatSlipService</c> (สลิป ⇒ ผูกเอกสาร) · postback ปุ่มเลือกใบ ⇒ บริการเดียวกัน</item>
/// <item><c>follow</c> ⇒ เปิดลิงก์ที่บล็อกไว้กลับ + ตอบวิธีผูก · <c>unfollow</c> ⇒ ประทับบล็อก (ไม่ส่งจนกว่าจะเพิ่มเพื่อนกลับ)</item>
/// <item>ลายเซ็นผ่านแล้ว ⇒ ตอบ 200 เสมอ (LINE ส่งซ้ำเมื่อไม่ใช่ 2xx) · ความล้มเหลวภายใน log ระดับ Error</item>
/// </list>
/// OA ของ NextAcc ใช้ <c>LineWebhookController</c> (<c>/api/line-webhook</c>) เดิม
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/line/webhook/c/{companyId:guid}")]
public class CustomerLineWebhookController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ICustomerLineLinkService _links;
    private readonly ILogger<CustomerLineWebhookController> _logger;
    private readonly ICustomerRelinkInviteService? _relink;
    /// <summary>รอบ 254 WP-11 L2: ผูก/หยุด/บล็อก LINE ของ<b>ผู้ติดต่อ</b> (ลิงก์/QR ผูก LINE ผ่าน OA ของบริษัท) — ก่อนเส้นลูกค้าเว็บ</summary>
    private readonly IContactLineBindingService? _contactLine;
    /// <summary>รอบ 254 WP-11 L3: กลุ่ม LINE ของผู้ติดต่อ + กัน webhook ซ้ำ</summary>
    private readonly IContactLineGroupService? _groups;

    /// <summary>รอบ 259 (A-M8): เพดานคำตอบต่อผู้ใช้ LINE (ตารางกลาง — หลายเครื่องใช้ตัวนับเดียวกัน)</summary>
    private readonly IChatRateLimiter? _limiter;

    /// <summary>รอบ 260 (คำตัดสิน 260-1-1): สลิปในแชต ⇒ ผูกเอกสาร (ใบเดียว = แจ้งโอนให้เอง · หลายใบ = ปุ่มเลือก · ไม่ตรง = คิวพนักงาน) · null = คำตอบเดิมของรอบ 259</summary>
    private readonly Accounting.Services.Implementations.ILineChatSlipService? _chatSlips;

    public CustomerLineWebhookController(AccountingDbContext db, ISecretProtector secrets, ICustomerLineLinkService links,
        ILogger<CustomerLineWebhookController> logger, ICustomerRelinkInviteService? relink = null, IContactLineBindingService? contactLine = null,
        IContactLineGroupService? groups = null, IChatRateLimiter? limiter = null,
        Accounting.Services.Implementations.ILineChatSlipService? chatSlips = null)
    {
        _db = db; _secrets = secrets; _links = links; _logger = logger; _relink = relink; _contactLine = contactLine; _groups = groups; _limiter = limiter;
        _chatSlips = chatSlips;
    }

    /// <summary>รอบ 260: เพดานเดียวกับคำตอบรูป/ไฟล์ของรอบ 259 (ต่อแหล่งแชต · ตารางกลาง · failClosed) — ผ่านแล้วจึงดึงรูป/อ่าน/ตอบ</summary>
    private async Task<bool> ConsumeMediaAsync(string? destination, string sourceId, CancellationToken ct)
        => !string.IsNullOrEmpty(sourceId) && _limiter != null
           && await _limiter.TryConsumeAsync(LineOaHealth.ChatMediaRateKey(destination, sourceId),
               LineOaHealth.ChatMediaPerMinute, LineOaHealth.ChatMediaPerDay, ct, failClosed: true);

    /// <summary>ฝ่ายค้าน 260-1 L3: ปุ่มเลือกใบของสลิปจากแชต — เพดานต่อแหล่งแชต (ตารางกลาง · failClosed) ก่อนตรวจลายเซ็น/ฐาน · postback อื่นไม่แตะตัวนับ</summary>
    private async Task<bool> ConsumeSlipPostbackAsync(string? destination, string sourceId, string? data, CancellationToken ct)
        => LineChatSlipRule.IsOurs(data) && !string.IsNullOrEmpty(sourceId) && _limiter != null
           && await _limiter.TryConsumeAsync(LineChatSlipRule.PostbackRateKey(NotifyEndpointKind.LineIdHash(
                   string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), sourceId.Trim())),
               LineChatSlipRule.PostbackPerMinute, LineChatSlipRule.PostbackPerDay, ct, failClosed: true);

    /// <summary>รอบ 260: เหตุการณ์ในกลุ่มที่เป็นงานของเส้นสลิป (รูป · ปุ่มเลือกใบ) ⇒ message ที่ตอบ · null = เส้นกลุ่มเดิม</summary>
    private async Task<IReadOnlyList<object>?> GroupSlipRepliesAsync(Guid companyId, string? destination, string? type, JsonElement ev, string groupId, CancellationToken ct)
    {
        if (_chatSlips == null || string.IsNullOrEmpty(groupId)) return null;
        var source = Accounting.Models.DTOs.Line.LineChatSource.Group(groupId);
        if (type == "postback" && ev.TryGetProperty("postback", out var pb) && pb.TryGetProperty("data", out var data)
            && LineChatSlipRule.IsOurs(data.GetString()))
            return await ConsumeSlipPostbackAsync(destination, groupId, data.GetString(), ct)
                ? await _chatSlips.HandlePostbackAsync(companyId, destination, source, data.GetString(), ct)
                : Array.Empty<object>();   // เกินเพดาน ⇒ ไม่ตอบ (ไม่ส่งต่อเส้นกลุ่มเดิม)
        // ฝ่ายค้าน L3: ตรวจว่ากลุ่มเป็นงานของเส้นสลิป (อ่านอย่างเดียว) ก่อนกินเพดาน — กลุ่มอื่นไม่เขียนตัวนับ
        if (type == "message" && ev.TryGetProperty("message", out var msg) && msg.TryGetProperty("type", out var mt) && mt.GetString() == "image"
            && msg.TryGetProperty("id", out var mid) && await _chatSlips.IsSlipGroupAsync(companyId, destination, groupId, ct)
            && await ConsumeMediaAsync(destination, groupId, ct))
            return await _chatSlips.HandleImageAsync(companyId, destination, source, mid.GetString() ?? "", ct);
        return null;
    }

    /// <summary>body ของ LINE webhook เล็ก (event ไม่กี่ตัว) — จำกัด 256 KB กันยิง body ใหญ่ใส่ทางเข้าสาธารณะ (ฝ่ายค้าน ข้อ 6)</summary>
    public const int MaxBodyBytes = 256 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(Guid companyId, CancellationToken ct)
    {
        // ฝ่ายค้าน ข้อ 6: ดูค่าตั้งก่อนอ่าน body · "ไม่ได้ตั้ง" กับ "ลายเซ็นผิด" ตอบ 401 แบบเดียวกัน (ไม่บอกคนนอกว่าบริษัทไหนตั้ง LINE ไว้)
        var enc = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.LineChannelSecret).FirstOrDefaultAsync(ct);
        var secret = _secrets.Unprotect(enc);
        if (string.IsNullOrEmpty(secret)) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!LineWebhookSignature.Verify(secret, body, Request.Headers["X-Line-Signature"].FirstOrDefault()))
        {
            _logger.LogWarning("LINE webhook ของบริษัท {CompanyId}: ลายเซ็นไม่ตรง", companyId);
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var destination = doc.RootElement.TryGetProperty("destination", out var d) ? d.GetString() : null;
            var destinationAccepted = await _links.LearnDestinationAsync(companyId, destination, ct);
            // รอบ 212 แพ็ก B: bot ใหม่ของ OA ร้าน (ร้านเปลี่ยน OA โดยใช้ช่อง/URL เดิม) ⇒ บันทึกการสลับ ให้งานเบื้องหลังเชิญลูกค้าที่ผูกกับ bot เดิมผูกใหม่ (ไม่โยน) ·
            // ฝ่ายค้าน (B+D) ข้อ 2: บอทไม่ตรงกับที่จำไว้ (secret ของ OA อื่น) ⇒ ไม่ใช่การสลับ — ไม่บันทึก
            if (destinationAccepted && _relink != null) await _relink.NoteWebhookDestinationAsync(CustomerNotifyChannel.ScopeCompany, companyId, destination, ct);
            // ปิดความเสี่ยงค้างของฝ่ายค้าน (B+D) ข้อ 2: เหตุการณ์จากบอทที่ไม่ตรง (secret ของ OA อื่น) ห้ามผูก/ปลดลูกค้า —
            // userId ของ OA นั้นส่งด้วย token ปัจจุบันไม่ได้ ⇒ ไม่ประมวลผล (หน้า LINE ตั้งค่าแสดงปัญหาให้ร้านแก้ secret แล้ว)
            if (!destinationAccepted) return Ok();
            // รอบ 259 (A-M5): หลักฐานว่า LINE เรียก webhook ของบริษัทถึงจริง (ลายเซ็นผ่าน + บอทตรง — รวมปุ่ม Verify ที่ส่ง events ว่าง) ⇒ ความพร้อมแท็บ LINE ·
            // ขยับไม่ถี่กว่า LineOaHealth.StampEvery (คำสั่งเดียวแบบมีเงื่อนไข) · ล้ม ⇒ LogError แต่ไม่ขวางการประมวลผล event
            try
            {
                var now = DateTime.UtcNow;
                var cutoff = now - LineOaHealth.StampEvery;
                await _db.CompanySettings
                    .Where(s => s.CompanyId == companyId && (s.LineLastWebhookAt == null || s.LineLastWebhookAt < cutoff))
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.LineLastWebhookAt, (DateTime?)now), ct);
            }
            catch (Exception stampEx) when (stampEx is not OperationCanceledException)
            {
                _logger.LogError(stampEx, "LINE webhook ของบริษัท {CompanyId}: ประทับเวลา webhook ล่าสุดไม่สำเร็จ", companyId);
            }
            if (!doc.RootElement.TryGetProperty("events", out var events)) return Ok();
            var anyFailed = false;
            foreach (var ev in events.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
                // รอบ 254 WP-11 L3 (G10): LINE ส่ง webhook ซ้ำได้ ⇒ event ที่เห็นแล้วข้าม (ผูก/ตอบไม่ซ้ำ · ตารางกลางหลายเครื่อง)
                var eventId = ev.TryGetProperty("webhookEventId", out var we) ? we.GetString() : null;
                if (_groups != null && !await _groups.MarkEventSeenAsync(eventId, ct)) continue;
                // ฝ่ายค้าน fix2 L1: ประทับ "เห็นแล้ว" ค้างไว้เฉพาะเมื่อประมวลผลสำเร็จ — ล้ม ⇒ ถอนการประทับ + ตอบ 500 ให้ LINE ส่งซ้ำ (เหตุการณ์ที่สำเร็จในชุดเดียวกันถูกข้าม)
                try
                {
                if (!ev.TryGetProperty("source", out var src) || !src.TryGetProperty("type", out var st)) continue;
                var sourceType = st.GetString();
                if (sourceType is "group" or "room")
                {
                    // รอบ 254 WP-11 L3: กลุ่มของผู้ติดต่อ (#HeyNextAcc/#ByeNextAcc · join/leave · สมาชิก) — ข้อความอื่นในกลุ่มไม่ประมวล/ไม่เก็บ
                    var groupReplyToken = ev.TryGetProperty("replyToken", out var grt) ? grt.GetString() : null;
                    // รอบ 260: สลิปในกลุ่มที่ผูกกับผู้ติดต่อและเปิดแสดงยอด · ปุ่มเลือกใบ — ไม่ใช่งานของเส้นนี้ (null) ⇒ เส้นกลุ่มเดิม
                    if (_chatSlips != null && sourceType == "group" && src.TryGetProperty("groupId", out var slipGroup)
                        && await GroupSlipRepliesAsync(companyId, destination, type, ev, slipGroup.GetString() ?? "", ct) is { } groupSlipReply)
                    {
                        if (groupSlipReply.Count > 0 && !string.IsNullOrEmpty(groupReplyToken))
                            await _links.ReplyMessagesAsync(CustomerNotifyChannel.ScopeCompany, companyId, groupReplyToken, groupSlipReply, ct);
                        continue;
                    }
                    if (_groups == null) continue;
                    var outcome = await _groups.HandleCompanyGroupEventAsync(companyId, destination, ev, ct);
                    if (!string.IsNullOrEmpty(outcome.Reply) && !string.IsNullOrEmpty(groupReplyToken))
                        await _links.ReplyAsync(CustomerNotifyChannel.ScopeCompany, companyId, groupReplyToken, outcome.Reply, ct);
                    if (outcome.LeaveAfterReply && src.TryGetProperty("groupId", out var leaveGroup))
                        await _groups.LeaveGroupIfUnboundAsync(companyId, destination, leaveGroup.GetString() ?? "", ct);   // Q8 หลังตอบ
                    continue;
                }
                if (sourceType != "user") continue;
                var userId = src.TryGetProperty("userId", out var u) ? u.GetString() ?? "" : "";
                var replyToken = ev.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;
                string? reply = null;
                IReadOnlyList<object>? replyMessages = null;
                switch (type)
                {
                    case "message":
                        if (ev.TryGetProperty("message", out var msg) && msg.TryGetProperty("type", out var mt) && mt.GetString() == "text")
                        {
                            var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                            // รอบ 254 WP-11 L2: รหัสของลิงก์ผูก LINE ผู้ติดต่อ / "หยุด" ก่อน — ไม่ใช่ของผู้ติดต่อ (null) ⇒ เส้นลูกค้าเว็บเดิม
                            if (_contactLine != null)
                                reply = await _contactLine.TryRedeemAsync(companyId, destination, userId, text, ct)
                                    ?? await _contactLine.TryStopAsync(companyId, destination, userId, text, ct);
                            reply ??= await _links.TryRedeemAsync(CustomerNotifyChannel.ScopeCompany, companyId, destination, userId, text, ct);
                        }
                        // รอบ 259 (A-M8): รูป/ไฟล์ (เช่นสลิป) — เดิมเงียบ ⇒ ตอบรับพร้อมทางไปต่อ · จำกัดต่อผู้ใช้ (ไม่มีตัวนับ = ไม่ตอบ — ทางเข้าสาธารณะ ปิดไว้ก่อน)
                        // รอบ 260: รูป ⇒ เส้นสลิปจากแชต (LineChatSlipService) ก่อน · ไม่ใช่งานของเส้นนั้น (ปิดสวิตช์/ไม่ใช่รูปที่รับ) ⇒ คำตอบเดิม
                        else if (ev.TryGetProperty("message", out var media) && media.TryGetProperty("type", out var mediaType)
                                 && LineOaHealth.IsChatMedia(mediaType.GetString()) && await ConsumeMediaAsync(destination, userId, ct))
                        {
                            if (_chatSlips != null && mediaType.GetString() == "image" && media.TryGetProperty("id", out var mid))
                                replyMessages = await _chatSlips.HandleImageAsync(companyId, destination,
                                    Accounting.Models.DTOs.Line.LineChatSource.User(userId), mid.GetString() ?? "", ct);
                            if (replyMessages == null) reply = LineOaHealth.ChatMediaReply;
                        }
                        break;
                    case "postback":
                        // รอบ 260: ปุ่มเลือกใบของสลิปจากแชต (ลายเซ็น + ตรวจซ้ำกับฐานในบริการ)
                        if (_chatSlips != null && ev.TryGetProperty("postback", out var pbu) && pbu.TryGetProperty("data", out var pbuData)
                            && await ConsumeSlipPostbackAsync(destination, userId, pbuData.GetString(), ct))
                            replyMessages = await _chatSlips.HandlePostbackAsync(companyId, destination,
                                Accounting.Models.DTOs.Line.LineChatSource.User(userId), pbuData.GetString(), ct);
                        break;
                    case "follow":
                        var restored = await _links.SetBlockedAsync(CustomerNotifyChannel.ScopeCompany, companyId, userId, blocked: false, ct);
                        if (_contactLine != null) restored += await _contactLine.SetBlockedAsync(companyId, destination, userId, blocked: false, ct);
                        reply = restored > 0 ? CustomerLineLinkRule.Replies.WelcomeBack : CustomerLineLinkRule.Replies.FollowHint;
                        break;
                    case "unfollow":
                        await _links.SetBlockedAsync(CustomerNotifyChannel.ScopeCompany, companyId, userId, blocked: true, ct);
                        if (_contactLine != null) await _contactLine.SetBlockedAsync(companyId, destination, userId, blocked: true, ct);
                        break;
                }
                if (replyMessages is { Count: > 0 } && !string.IsNullOrEmpty(replyToken))
                    await _links.ReplyMessagesAsync(CustomerNotifyChannel.ScopeCompany, companyId, replyToken, replyMessages, ct);
                else if (!string.IsNullOrEmpty(reply) && !string.IsNullOrEmpty(replyToken))
                    await _links.ReplyAsync(CustomerNotifyChannel.ScopeCompany, companyId, replyToken, reply, ct);
                }
                catch (Exception evEx) when (evEx is not OperationCanceledException)
                {
                    anyFailed = true;
                    _logger.LogError(evEx, "LINE webhook ของบริษัท {CompanyId}: ประมวลเหตุการณ์ {EventId} ล้ม — ถอนการประทับให้ส่งซ้ำได้", companyId, eventId);
                    if (_groups != null) await _groups.UnmarkEventSeenAsync(eventId, CancellationToken.None);
                }
            }
            if (anyFailed) return StatusCode(500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "LINE webhook ของบริษัท {CompanyId} ประมวลผลล้ม", companyId);
        }
        return Ok();
    }
}
