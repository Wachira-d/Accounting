using System.Text.Json;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// LINE Messaging API webhook receiver. LINE POSTs JSON events here when a
/// user sends a message to the bot. We verify the X-Line-Signature, route
/// each text event through LineBotService.HandleMessageAsync, and push the
/// reply back. Public endpoint by design (LINE has no auth).
/// </summary>
[ApiController]
[Route("api/line-webhook")]
[AllowAnonymous]
public class LineWebhookController : ControllerBase
{
    private readonly ILineBotService _bot;
    private readonly ILogger<LineWebhookController> _logger;
    private readonly ICustomerLineLinkService? _customerLinks;
    private readonly ICustomerRelinkInviteService? _relink;
    /// <summary>รอบ 256 (คำตัดสิน 256-2): กลุ่ม LINE ของผู้ติดต่อของบริษัทผู้ให้บริการผ่าน OA กลาง (#HeyNextAcc/#ByeNextAcc · leave · สมาชิก) + กัน webhook ซ้ำ</summary>
    private readonly IContactLineGroupService? _groups;

    public LineWebhookController(ILineBotService bot, ILogger<LineWebhookController> logger, ICustomerLineLinkService? customerLinks = null,
        ICustomerRelinkInviteService? relink = null, IContactLineGroupService? groups = null)
    {
        _bot = bot; _logger = logger; _customerLinks = customerLinks; _relink = relink; _groups = groups;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        // Read the raw body for signature verification — the signature is over
        // the EXACT bytes, not over re-serialized JSON.
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var sig = Request.Headers["X-Line-Signature"].FirstOrDefault();
        if (!await _bot.VerifySignatureAsync(body, sig))
        {
            _logger.LogWarning("LINE webhook signature mismatch");
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            // รอบ 211 ฝ่ายค้าน ข้อ 4: bot userId ของ OA กลาง (ลายเซ็นผ่านแล้ว) ⇒ ลิงก์ลูกค้าเก็บปลายทาง + ตรวจกับ bot userId ของ OA กลาง (INextAccLineConfig)
            var destination = doc.RootElement.TryGetProperty("destination", out var dst) ? dst.GetString() : null;
            // รอบ 212 แพ็ก B: bot ของ OA กลางเปลี่ยน ⇒ บันทึกการสลับต่อบริษัทที่ยังใช้ OA กลาง (ตรวจครั้งเดียวต่อ bot ต่อเครื่อง · ไม่โยน)
            if (_relink != null) await _relink.NoteWebhookDestinationAsync(Accounting.Helpers.CustomerNotifyChannel.ScopeNextAcc, null, destination);
            if (!doc.RootElement.TryGetProperty("events", out var events)) return Ok();
            foreach (var ev in events.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
                // รอบ 211: ลูกค้าปลายทางบล็อก/เพิ่มเพื่อน OA กลางกลับ ⇒ หยุด/เปิดส่งแจ้งเตือนลูกค้าที่ผูกไว้ (เงียบ — ผู้ใช้ ERP ก็ follow OA นี้)
                if (type is ("follow" or "unfollow") && _customerLinks != null
                    && ev.TryGetProperty("source", out var fs) && fs.TryGetProperty("userId", out var fu) && !string.IsNullOrEmpty(fu.GetString()))
                {
                    await _customerLinks.SetBlockedAsync(Accounting.Helpers.CustomerNotifyChannel.ScopeNextAcc, null, fu.GetString()!, blocked: type == "unfollow");
                    continue;
                }
                if (!ev.TryGetProperty("source", out var src)) continue;
                var srcType = src.TryGetProperty("type", out var stp) ? stp.GetString() : null;
                if (srcType is "group" or "room")
                {
                    // รอบ 256: เดิมทิ้ง event กลุ่มเงียบ ⇒ ผู้ใช้เชิญ OA กลางเข้ากลุ่มลูกค้าแล้วพิมพ์ #HeyNextAcc ไม่มีอะไรตอบ ·
                    // รอบ 256 (คำตัดสิน 256-2): ทุกชนิด event กลุ่ม (ข้อความ · leave · สมาชิกเข้า/ออก) เดินตัวจัดการเดียว — บริษัทผู้ให้บริการผูก/ปลดกลุ่มผ่าน OA กลาง ·
                    // ไม่ได้ตั้งบริษัทผู้ให้บริการ ⇒ ตอบเหตุ + ทางไปต่อเฉพาะคำสั่ง (เดิม) · ข้อความอื่นในกลุ่มไม่ประมวล/ไม่เก็บ
                    await HandleGroupEventAsync(ev, src, type, destination);
                    continue;
                }
                if (type is not ("message" or "postback")) continue;
                if (srcType != "user") continue;
                await HandleOneToOneEventAsync(ev, src, type, destination);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE webhook handler failed");
            // Always 200 — LINE retries aggressively on non-2xx and the event
            // would loop forever. Log and move on.
        }
        return Ok();
    }

    /// <summary>รอบ 258 (A-M2 · A-M3) — event 1:1 ของ OA กลาง (ข้อความ/รูป/ไฟล์/ปุ่มกด):
    /// <list type="bullet">
    /// <item>กันซ้ำด้วย <c>webhookEventId</c> (ตาราง <c>LineWebhookEventsSeen</c> เดียวกับเส้นกลุ่มและ webhook OA บริษัท — upsert ในฐาน · หลายเครื่องเห็นร่วมกัน) —
    /// เดิม event ที่ LINE ส่งซ้ำ (Redelivery/คำขอช้า) ทำให้ "จ่าย เซเว่น 250" ลงค่าใช้จ่ายอนุมัติแล้วสองใบ</item>
    /// <item>ตอบด้วย reply token (ไม่กินโควตา push) — <see cref="ILineBotService.ReplyAsync"/> ถอยไป push เฉพาะผู้ส่งที่ผูกบัญชีแล้ว</item>
    /// <item>try ต่อ event: event หนึ่งล้มไม่ลาก event ที่เหลือในชุดเดียวกัน · ล้ม ⇒ log Error + ถอนการประทับ (webhook ตอบ 200 เสมอ ⇒ LINE ไม่ส่งซ้ำเอง —
    /// ถอนประทับมีผลเมื่อสั่ง Redelivery เอง)</item>
    /// </list></summary>
    private async Task HandleOneToOneEventAsync(JsonElement ev, JsonElement src, string? type, string? destination)
    {
        var eventId = ev.TryGetProperty("webhookEventId", out var we) ? we.GetString() : null;
        var marked = false;
        try
        {
            if (_groups != null)
            {
                if (!await _groups.MarkEventSeenAsync(eventId)) return;   // เห็นแล้ว ⇒ ไม่ประมวลซ้ำ
                marked = true;
            }
            var userId = src.TryGetProperty("userId", out var uid) ? uid.GetString() ?? "" : "";
            if (userId.Length == 0) return;
            var replyToken = ev.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;

            string? reply = null;
            if (type == "postback")
            {
                // ปุ่มกดใน Flex card (เช่น "✅ อนุมัติเลย")
                var data = ev.TryGetProperty("postback", out var pb)
                    && pb.TryGetProperty("data", out var pd) ? pd.GetString() : null;
                if (!string.IsNullOrEmpty(data))
                    reply = await _bot.HandlePostbackAsync(userId, data);
            }
            else
            {
                var msg = ev.GetProperty("message");
                var msgType = msg.GetProperty("type").GetString();
                if (msgType == "text")
                {
                    reply = await _bot.HandleMessageAsync(userId, msg.GetProperty("text").GetString() ?? "", destination);
                }
                else if (msgType is "image" or "file")
                {
                    // "โยนบิลเข้าไลน์" — รูปถ่ายใบเสร็จ หรือไฟล์ PDF → OCR → สร้างเอกสารให้ทันที · อัลบั้มหลายรูป = LINE ส่งทีละ event
                    var messageId = msg.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                    var fileName = msg.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                    if (!string.IsNullOrEmpty(messageId))
                        reply = await _bot.HandleImageAsync(userId, messageId, fileName);
                }
            }
            if (!string.IsNullOrEmpty(reply))
                await _bot.ReplyAsync(userId, reply, replyToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "LINE webhook OA กลาง: ประมวลเหตุการณ์ 1:1 {EventId} ล้ม — ถอนการประทับ (ส่งซ้ำได้เฉพาะสั่ง Redelivery เอง)", eventId);
            if (marked && _groups != null) await _groups.UnmarkEventSeenAsync(eventId, CancellationToken.None);
        }
    }

    /// <summary>รอบ 256 (คำตัดสิน 256-2) — event กลุ่ม/ห้องของ OA กลาง:
    /// <list type="bullet">
    /// <item>เฉพาะ event ที่ต้องทำอะไร (<see cref="Accounting.Helpers.LineGroupBindRule.CentralGroupEventActionable"/>: คำสั่ง · leave · สมาชิกเข้า/ออก) —
    /// คุยกันในกลุ่มไม่แตะฐานเลย (ไม่ประทับกันซ้ำ ไม่อ่านบริษัทผู้ให้บริการ · ฝ่ายค้าน M2)</item>
    /// <item>ใน try ต่อ event: ประทับกันซ้ำ (ตารางเดียวกับ webhook OA บริษัท · G10) → ตัวจัดการกลุ่มเดียว → ตอบ (reply · นับ reply OA กลาง) → บอทออกหลังตอบ #ByeNextAcc (Q8)</item>
    /// <item>ล้ม ⇒ log Error + ถอนการประทับ · webhook กลางตอบ 200 เสมอ (event 1:1 ในชุดเดียวกันไม่มีตัวกันซ้ำ) ⇒ LINE <b>ไม่</b>ส่งซ้ำเอง —
    /// การถอนประทับมีผลเฉพาะเมื่อมีคนสั่งส่งซ้ำเอง (Redelivery ใน LINE Developers Console) เท่านั้น (ฝ่ายค้าน L1)</item>
    /// </list></summary>
    private async Task HandleGroupEventAsync(JsonElement ev, JsonElement src, string? type, string? destination)
    {
        var groupText = type == "message" && ev.TryGetProperty("message", out var gm) && gm.TryGetProperty("type", out var gmt)
            && gmt.GetString() == "text" && gm.TryGetProperty("text", out var gtx) ? gtx.GetString() : null;
        if (!Accounting.Helpers.LineGroupBindRule.CentralGroupEventActionable(type, groupText)) return;
        if (_groups == null)
        {
            // ไม่มีตัวจัดการกลุ่ม (เทสต์/ประกอบบางส่วน) ⇒ พฤติกรรมรอบ 256 เดิม: ตอบเหตุเฉพาะคำสั่ง
            var legacyReply = Accounting.Helpers.LineGroupBindRule.CentralOaGroupReply(type, groupText);
            var legacyToken = ev.TryGetProperty("replyToken", out var lrt) ? lrt.GetString() : null;
            if (legacyReply != null && !string.IsNullOrEmpty(legacyToken) && _customerLinks != null)
                await _customerLinks.ReplyAsync(Accounting.Helpers.CustomerNotifyChannel.ScopeNextAcc, null, legacyToken, legacyReply);
            return;
        }
        var eventId = ev.TryGetProperty("webhookEventId", out var we) ? we.GetString() : null;
        var marked = false;
        try
        {
            if (!await _groups.MarkEventSeenAsync(eventId)) return;
            marked = true;
            var outcome = await _groups.HandleCentralGroupEventAsync(destination, ev);
            var replyToken = ev.TryGetProperty("replyToken", out var grt) ? grt.GetString() : null;
            if (!string.IsNullOrEmpty(outcome.Reply) && !string.IsNullOrEmpty(replyToken) && _customerLinks != null)
                await _customerLinks.ReplyAsync(Accounting.Helpers.CustomerNotifyChannel.ScopeNextAcc, null, replyToken, outcome.Reply);
            if (outcome.LeaveAfterReply && src.TryGetProperty("groupId", out var leaveGroup))
                await _groups.LeaveCentralGroupIfUnboundAsync(destination, leaveGroup.GetString() ?? "");   // Q8 หลังตอบ
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "LINE webhook OA กลาง: ประมวลเหตุการณ์กลุ่ม {EventId} ล้ม — ถอนการประทับ (ส่งซ้ำได้เฉพาะสั่ง Redelivery เอง)", eventId);
            if (marked) await _groups.UnmarkEventSeenAsync(eventId, CancellationToken.None);
        }
    }

    // NOTE: the authenticated "issue-bind-code" endpoint lives in
    // LineBotBindController below — not on this controller — because the
    // class-level [AllowAnonymous] on the webhook receiver would override
    // any method-level [Authorize] (Microsoft docs: [AllowAnonymous]
    // short-circuits all authorization).
}

/// <summary>
/// Authenticated companion endpoint. Separate controller so the
/// class-level [Authorize] sticks — keeping IssueBindCode inside
/// the [AllowAnonymous] LineWebhookController would silently
/// short-circuit the authorization check.
/// </summary>
[ApiController]
[Route("api/line-bot")]
[Authorize]
public class LineBotBindController : ControllerBase
{
    private readonly ILineBotService _bot;
    /// <summary>รอบ 258 (A-M9): ค่าตั้ง OA กลาง (ผู้อ่านตัวเดียว) — ชื่อบอท/ลิงก์เพิ่มเพื่อนบนหน้าผูกมาจากที่นี่ ไม่ฝังในหน้าเว็บ</summary>
    private readonly INextAccLineConfig? _nextAcc;
    public LineBotBindController(ILineBotService bot, INextAccLineConfig? nextAcc = null)
    {
        _bot = bot; _nextAcc = nextAcc;
    }

    [HttpPost("issue-bind-code")]
    public async Task<ActionResult<ApiResponse<object>>> IssueBindCode(CancellationToken ct = default)
    {
        var userId = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);
        // รอบ 258 (A-M9): บอทกลางยังไม่พร้อมรับข้อความ (ไม่มี token/secret) ⇒ บอกเหตุแทนการออกรหัสที่ส่งไปแล้วเงียบ
        var central = _nextAcc == null ? null : await _nextAcc.GetAsync(ct);
        var view = Accounting.Helpers.StaffLineBindView.From(central?.HasToken == true && central.HasSecret, central?.BasicId);
        if (!view.BotReady)
            return Ok(new ApiResponse<object>(false, new { view.BotReady, view.BasicId, view.AddFriendUrl, view.Notice }, view.Notice));
        var code = await _bot.IssueBindCodeAsync(userId);
        return Ok(new ApiResponse<object>(true,
            new { code, expiresInMinutes = 10, view.BotReady, view.BasicId, view.AddFriendUrl, view.Notice },
            "ส่งข้อความ 'ผูก " + code + "' ให้บอทใน LINE ภายใน 10 นาที"));
    }
}
