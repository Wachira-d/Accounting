using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly AccountingDbContext _db;

    public AdminChatController(IChatbotService chat, IKnowledgeBaseService kb, AccountingDbContext db)
    {
        _chat = chat; _kb = kb; _db = db;
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

    /// <summary>ตัวชี้วัดคุณภาพ — อัตราการใช้ AI (ยิ่งลดยิ่งดี), โหวต,
    /// คำถามที่ตอบไม่ได้ (ควรเขียนบทความเพิ่ม)</summary>
    [HttpGet("metrics")]
    public async Task<ActionResult<ApiResponse<ChatMetricsDto>>> Metrics([FromQuery] int days = 30)
        => Ok(new ApiResponse<ChatMetricsDto>(true, await _chat.GetMetricsAsync(days)));

    // ═══════════ จัดการคลังความรู้ (บทความที่ admin เขียนเอง) ═══════════

    public record KbUpsertRequest(Guid? Id, string Title, string Content, string Audience);

    /// <summary>ลิสต์ชิ้นความรู้กลาง (ไม่รวม snapshot ของ tenant — นั่น
    /// สร้างอัตโนมัติจากข้อมูลบริษัทและ admin ไม่ควรแก้มือ)</summary>
    [HttpGet("kb")]
    public async Task<ActionResult<ApiResponse<object>>> ListKb(
        [FromQuery] string? audience, [FromQuery] string? search)
    {
        var q = _db.KnowledgeChunks.AsNoTracking().Where(k => k.CompanyId == null && !k.IsDeleted);
        if (!string.IsNullOrEmpty(audience)) q = q.Where(k => k.Audience == audience);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(k => k.Title.Contains(search) || k.Content.Contains(search));
        var rows = await q.OrderBy(k => k.Audience).ThenBy(k => k.SourceKey)
            .Take(400)
            .Select(k => new
            {
                k.Id, k.SourceType, k.SourceKey, k.Title, k.Audience, k.IsActive,
                k.UpdatedAt, k.CreatedAt,
                Preview = k.Content.Length > 200 ? k.Content.Substring(0, 200) + "…" : k.Content,
                Length = k.Content.Length,
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpGet("kb/{id:guid}")]
    public async Task<ActionResult<ApiResponse<KnowledgeChunk>>> GetKb(Guid id)
    {
        var row = await _db.KnowledgeChunks.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == id && k.CompanyId == null && !k.IsDeleted);
        return row == null
            ? NotFound(new ApiResponse<KnowledgeChunk>(false, null, "ไม่พบบทความ"))
            : Ok(new ApiResponse<KnowledgeChunk>(true, row));
    }

    /// <summary>เพิ่ม/แก้บทความที่เขียนเอง. ชิ้นที่มาจากไฟล์ .md (SourceType
    /// = "File") **แก้ที่นี่ไม่ได้** — จะถูกเขียนทับตอน refresh รอบถัดไป
    /// ต้องไปแก้ที่ไฟล์ต้นทางแทน (บอกผู้ใช้ตรง ๆ แทนที่จะให้แก้แล้วหาย)</summary>
    [HttpPost("kb")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertKb([FromBody] KbUpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new ApiResponse<object>(false, null, "ต้องมีหัวข้อและเนื้อหา"));
        if (req.Audience is not ("Public" or "Tenant" or "Internal"))
            return BadRequest(new ApiResponse<object>(false, null, "Audience ต้องเป็น Public / Tenant / Internal"));

        var id = await _kb.UpsertManualChunkAsync(req.Id, req.Title.Trim(), req.Content.Trim(), req.Audience);
        if (id == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "บทความนี้มาจากไฟล์คู่มือ (.md) — แก้ที่ไฟล์ต้นทางแล้วกด \"อัปเดตคลังความรู้\" แทน"));
        return Ok(new ApiResponse<object>(true, new { id }, "บันทึกบทความแล้ว"));
    }

    [HttpPost("kb/{id:guid}/toggle")]
    public async Task<ActionResult<ApiResponse<bool>>> ToggleKb(Guid id, [FromQuery] bool active)
    {
        var ok = await _kb.SetChunkActiveAsync(id, active);
        return Ok(new ApiResponse<bool>(ok, ok, ok ? (active ? "เปิดใช้แล้ว" : "ปิดแล้ว") : "ไม่พบบทความ"));
    }

    /// <summary>ทดลองค้น — เห็นว่าคำถามหนึ่งจะดึงชิ้นไหนมาตอบ พร้อมคะแนน
    /// (ใช้ตรวจว่าบทความที่เพิ่งเขียนถูกหยิบจริงไหมก่อนปล่อยให้ลูกค้าถาม)</summary>
    [HttpGet("kb/preview")]
    public async Task<ActionResult<ApiResponse<object>>> PreviewKb(
        [FromQuery] string q, [FromQuery] string audience = "Public")
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new ApiResponse<object>(false, null, "ใส่คำถามที่จะทดลองค้น"));
        var hits = await _kb.SearchAsync(q, audience, null, 6);
        return Ok(new ApiResponse<object>(true, hits.Select(h => new
        {
            h.Chunk.Id, h.Chunk.Title, h.Chunk.Audience,
            Score = Math.Round(h.Score, 3),
            Preview = h.Chunk.Content.Length > 300 ? h.Chunk.Content[..300] + "…" : h.Chunk.Content,
        })));
    }
}
