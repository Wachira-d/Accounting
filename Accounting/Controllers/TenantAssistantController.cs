using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>ผู้ช่วย AI ในระบบ (tenant assistant) — ถามวิธีลงบัญชี/เลือกหมวด
/// ในบริบทบริษัทตัวเอง (RAG ย่อยต่อบริษัท refresh อัตโนมัติ).
/// tenant guard: ต้องเป็นสมาชิกบริษัทใน route เสมอ.</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/assistant")]
[Authorize]
public class TenantAssistantController : ControllerBase
{
    private readonly IChatbotService _chat;
    private readonly AccountingDbContext _db;

    public TenantAssistantController(IChatbotService chat, AccountingDbContext db)
    {
        _chat = chat; _db = db;
    }

    /// <summary>DocumentId = ถามจากหน้าเอกสาร/OCR ใบใดใบหนึ่ง → ผู้ช่วยเห็น
    /// สรุปใบนั้นเป็น context (ตรวจสิทธิ์บริษัทก่อนเสมอในฝั่ง service)</summary>
    public record AskRequest(string Message, Guid? DocumentId = null);
    public record VoteRequest(Guid MessageId, int Vote);

    private async Task<(Guid UserId, string Email)?> ResolveMemberAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isMember = await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!isMember) return null;
        var email = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync() ?? "";
        return (userId, email);
    }

    [HttpPost("ask")]
    public async Task<ActionResult<ApiResponse<ChatAskResult>>> Ask(
        Guid companyId, [FromBody] AskRequest req)
    {
        var member = await ResolveMemberAsync(companyId);
        if (member == null)
            return StatusCode(403, new ApiResponse<ChatAskResult>(false, null, "ไม่มีสิทธิ์ในบริษัทนี้"));
        var result = await _chat.AskTenantAsync(companyId, member.Value.UserId, member.Value.Email,
            req.Message ?? "", req.DocumentId);
        if (result.RateLimited)
            return StatusCode(429, new ApiResponse<ChatAskResult>(false, result, result.Answer));
        return Ok(new ApiResponse<ChatAskResult>(true, result));
    }

    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<ApiResponse<List<ChatHistoryItem>>>> History(
        Guid companyId, Guid conversationId, [FromQuery] DateTime? after)
    {
        var member = await ResolveMemberAsync(companyId);
        if (member == null)
            return StatusCode(403, new ApiResponse<List<ChatHistoryItem>>(false, null, "ไม่มีสิทธิ์ในบริษัทนี้"));
        var msgs = await _chat.GetMessagesAsync(conversationId, null, companyId, member.Value.UserId, after);
        return Ok(new ApiResponse<List<ChatHistoryItem>>(true, msgs));
    }

    [HttpPost("vote")]
    public async Task<ActionResult<ApiResponse<bool>>> Vote(Guid companyId, [FromBody] VoteRequest req)
    {
        var member = await ResolveMemberAsync(companyId);
        if (member == null)
            return StatusCode(403, new ApiResponse<bool>(false, false, "ไม่มีสิทธิ์ในบริษัทนี้"));
        var ok = await _chat.VoteAsync(req.MessageId, null, companyId, req.Vote);
        return Ok(new ApiResponse<bool>(ok, ok));
    }
}
