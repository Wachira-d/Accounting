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

    public LineWebhookController(ILineBotService bot, ILogger<LineWebhookController> logger)
    {
        _bot = bot; _logger = logger;
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
            if (!doc.RootElement.TryGetProperty("events", out var events)) return Ok();
            foreach (var ev in events.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "message") continue;
                var msg = ev.GetProperty("message");
                if (msg.GetProperty("type").GetString() != "text") continue;
                var text = msg.GetProperty("text").GetString() ?? "";
                var src = ev.GetProperty("source");
                if (src.GetProperty("type").GetString() != "user") continue; // ignore group/room for now
                var userId = src.GetProperty("userId").GetString() ?? "";

                var reply = await _bot.HandleMessageAsync(userId, text);
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

    /// <summary>Authenticated user requests a one-shot 6-digit bind code
    /// they can send to the bot as "ผูก {code}".</summary>
    [HttpPost("issue-bind-code")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> IssueBindCode()
    {
        var userId = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);
        var code = await _bot.IssueBindCodeAsync(userId);
        return Ok(new ApiResponse<object>(true,
            new { code, expiresInMinutes = 10 },
            "ส่งข้อความ 'ผูก " + code + "' ให้บอทใน LINE ภายใน 10 นาที"));
    }
}
