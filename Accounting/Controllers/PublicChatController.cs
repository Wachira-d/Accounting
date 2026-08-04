using System.Security.Cryptography;
using System.Text;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Chatbot หน้าแรก (public) — anonymous. rate limit หลายชั้นอยู่ใน
/// ChatbotService (ต่อ IP-hash/session/ห้อง/งบ AI) — controller เก็บแค่
/// การแปลง IP → SHA-256 (ไม่ส่ง IP ดิบลึกเข้าไปในระบบ — PDPA minimization).
/// </summary>
[ApiController]
[Route("api/public/chat")]
[AllowAnonymous]
public class PublicChatController : ControllerBase
{
    private readonly IChatbotService _chat;
    public PublicChatController(IChatbotService chat) => _chat = chat;

    public record AskRequest(string? SessionToken, string Message, string? Name, string? Email);
    public record AgentRequest(Guid ConversationId, string? SessionToken, string? Name, string? Email);
    public record VoteRequest(Guid MessageId, string? SessionToken, int Vote);
    public record RateRequest(Guid ConversationId, string? SessionToken, int Score);

    private string IpHash()
    {
        // ใช้ X-Forwarded-For ตัวแรกเมื่ออยู่หลัง proxy (ค่านี้ปลอมได้ —
        // จึงเป็นแค่ 1 ในหลายชั้น ไม่ใช่ด่านเดียว)
        var ip = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(ip)) ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip))).ToLowerInvariant();
    }

    [HttpPost("ask")]
    public async Task<ActionResult<ApiResponse<ChatAskResult>>> Ask([FromBody] AskRequest req)
    {
        var result = await _chat.AskPublicAsync(req.SessionToken, IpHash(),
            req.Message ?? "", req.Name, req.Email);
        // rate limited → 429 พร้อมข้อความสุภาพ (widget โชว์ตรง ๆ ได้)
        if (result.RateLimited)
            return StatusCode(429, new ApiResponse<ChatAskResult>(false, result, result.Answer));
        return Ok(new ApiResponse<ChatAskResult>(true, result));
    }

    /// <summary>ปุ่ม "ติดต่อเจ้าหน้าที่" — เข้าคิวรอ admin ตอบ</summary>
    [HttpPost("request-agent")]
    public async Task<ActionResult<ApiResponse<bool>>> RequestAgent([FromBody] AgentRequest req)
    {
        var ok = await _chat.RequestAgentAsync(req.ConversationId, req.SessionToken, req.Name, req.Email);
        return ok ? Ok(new ApiResponse<bool>(true, true, "ส่งคำขอแล้ว — เจ้าหน้าที่จะเข้ามาตอบโดยเร็ว"))
                  : NotFound(new ApiResponse<bool>(false, false, "ไม่พบห้องสนทนา"));
    }

    /// <summary>polling ระหว่างรอ/คุยกับเจ้าหน้าที่ (after = ISO timestamp
    /// ของข้อความล่าสุดที่ widget มีแล้ว)</summary>
    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<ApiResponse<List<ChatHistoryItem>>>> Poll(
        Guid conversationId, [FromQuery] string sessionToken, [FromQuery] DateTime? after)
    {
        var msgs = await _chat.GetMessagesAsync(conversationId, sessionToken, null, null, after);
        return Ok(new ApiResponse<List<ChatHistoryItem>>(true, msgs));
    }

    /// <summary>👍/👎 บนคำตอบ — ปิดลูป distillation (คำตอบที่คนชอบกลายเป็น
    /// ความจำ local ตอบฟรีครั้งหน้า)</summary>
    [HttpPost("vote")]
    public async Task<ActionResult<ApiResponse<bool>>> Vote([FromBody] VoteRequest req)
    {
        var ok = await _chat.VoteAsync(req.MessageId, req.SessionToken, null, req.Vote);
        return Ok(new ApiResponse<bool>(ok, ok));
    }

    /// <summary>ให้ดาว 1-5 หลังจบสนทนา — วัดคุณภาพบอท/เจ้าหน้าที่</summary>
    [HttpPost("rate")]
    public async Task<ActionResult<ApiResponse<bool>>> Rate([FromBody] RateRequest req)
    {
        var ok = await _chat.RateConversationAsync(req.ConversationId, req.SessionToken, null, req.Score);
        return Ok(new ApiResponse<bool>(ok, ok, ok ? "ขอบคุณสำหรับคะแนนครับ" : "บันทึกคะแนนไม่สำเร็จ"));
    }
}
