using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>คอนโซล admin ของ chatbot — ดู/ตอบ/ปิดห้องสนทนา + refresh คลังความรู้.
/// ห้อง WaitingAgent เรียงขึ้นก่อนเสมอ (งานด่วนของทีม support).</summary>
[ApiController]
[Route("api/admin/chats")]
[Authorize(Roles = "SystemAdmin")]
public class AdminChatController : ControllerBase
{
    private readonly IChatbotService _chat;
    private readonly IKnowledgeBaseService _kb;

    public AdminChatController(IChatbotService chat, IKnowledgeBaseService kb)
    {
        _chat = chat; _kb = kb;
    }

    public record ReplyRequest(string Content);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ChatConversation>>>> List(
        [FromQuery] string? status, [FromQuery] string? channel,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        => Ok(new ApiResponse<List<ChatConversation>>(true,
            await _chat.ListConversationsAsync(status, channel, page, pageSize)));

    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<ApiResponse<List<ChatHistoryItem>>>> Messages(Guid conversationId)
        => Ok(new ApiResponse<List<ChatHistoryItem>>(true,
            await _chat.GetConversationMessagesAsync(conversationId)));

    [HttpPost("{conversationId:guid}/reply")]
    public async Task<ActionResult<ApiResponse<bool>>> Reply(Guid conversationId, [FromBody] ReplyRequest req)
    {
        var agent = User.Identity?.Name ?? "admin";
        var ok = await _chat.AgentReplyAsync(conversationId, agent, req.Content ?? "");
        return ok ? Ok(new ApiResponse<bool>(true, true, "ตอบแล้ว"))
                  : BadRequest(new ApiResponse<bool>(false, false, "ตอบไม่สำเร็จ — ตรวจห้อง/ข้อความ"));
    }

    [HttpPost("{conversationId:guid}/close")]
    public async Task<ActionResult<ApiResponse<bool>>> Close(Guid conversationId)
    {
        var ok = await _chat.CloseConversationAsync(conversationId, User.Identity?.Name ?? "admin");
        return Ok(new ApiResponse<bool>(ok, ok, ok ? "ปิดห้องแล้ว" : "ไม่พบห้อง"));
    }

    /// <summary>refresh คลังความรู้จากไฟล์ .md + seed FAQ (hash-diff — เร็ว)</summary>
    [HttpPost("kb/refresh")]
    public async Task<ActionResult<ApiResponse<object>>> RefreshKb()
    {
        var changed = await _kb.RefreshGlobalAsync();
        return Ok(new ApiResponse<object>(true, new { changed }, $"อัปเดตคลังความรู้ {changed} ชิ้น"));
    }
}
