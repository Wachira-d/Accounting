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

    public LineWebhookController(ILineBotService bot, ILogger<LineWebhookController> logger, ICustomerLineLinkService? customerLinks = null)
    {
        _bot = bot; _logger = logger; _customerLinks = customerLinks;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        // Read the raw body for signature verification — the signature is over
        // the EXACT bytes, not over re-serialized JSON.
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var sig = Request.Headers["X-Line-Signature"].FirstOrDefault();
        if (!_bot.VerifySignature(body, sig))
        {
            _logger.LogWarning("LINE webhook signature mismatch");
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            // รอบ 211 ฝ่ายค้าน ข้อ 4: bot userId ของ OA กลาง (ลายเซ็นผ่านแล้ว) ⇒ ลิงก์ลูกค้าเก็บปลายทาง + ตรวจกับ Line:BotUserId
            var destination = doc.RootElement.TryGetProperty("destination", out var dst) ? dst.GetString() : null;
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
                if (type is not ("message" or "postback")) continue;
                var src = ev.GetProperty("source");
                if (src.GetProperty("type").GetString() != "user") continue; // ignore group/room for now
                var userId = src.GetProperty("userId").GetString() ?? "";

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
                        // "โยนบิลเข้าไลน์" — รูปถ่ายใบเสร็จ หรือไฟล์ PDF → OCR →
                        // สร้างเอกสารให้ทันที (เดิม path นี้ถูก drop เงียบ ๆ ผู้ใช้
                        // ส่งรูปมาแล้วไม่มีอะไรตอบกลับเลย). อัลบั้มหลายรูป = LINE
                        // ส่งทีละ event → วน loop นี้ประมวลผลครบทุกใบอยู่แล้ว
                        var messageId = msg.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                        var fileName = msg.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                        if (!string.IsNullOrEmpty(messageId))
                            reply = await _bot.HandleImageAsync(userId, messageId, fileName);
                    }
                }
                if (!string.IsNullOrEmpty(reply))
                    await _bot.ReplyAsync(userId, reply);
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
    public LineBotBindController(ILineBotService bot) => _bot = bot;

    [HttpPost("issue-bind-code")]
    public async Task<ActionResult<ApiResponse<object>>> IssueBindCode()
    {
        var userId = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);
        var code = await _bot.IssueBindCodeAsync(userId);
        return Ok(new ApiResponse<object>(true,
            new { code, expiresInMinutes = 10 },
            "ส่งข้อความ 'ผูก " + code + "' ให้บอทใน LINE ภายใน 10 นาที"));
    }
}
