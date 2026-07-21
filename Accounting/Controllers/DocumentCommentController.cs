using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Accounting.Controllers;

/// <summary>
/// #23 — Comments + @mention. Frontend แสดง timeline ของ comments
/// บน document detail page. @mention อัตโนมัติ trigger notification.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/comments")]
[Authorize]
public class DocumentCommentController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public DocumentCommentController(AccountingDbContext db) { _db = db; }

    public sealed record PostCommentRequest(
        string EntityType, Guid EntityId, string Body, Guid? ParentCommentId);

    /// <summary>POST comment + extract @mentions ออกจาก body ก่อนเก็บ.
    /// @mentions syntax: "@username" หรือ "@email" — ระบบหา User
    /// ที่ FullName หรือ Email match แล้วเก็บ id ใน MentionedUserIdsJson.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Post(
        Guid companyId, [FromBody] PostCommentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new ApiResponse<object>(false, null, "ใส่ข้อความ comment"));
        var authorId = GetUserIdFromClaims();
        if (authorId == Guid.Empty)
            return Unauthorized(new ApiResponse<object>(false, null, "ไม่พบ user"));

        // Extract @mentions — match @name where name ≤ 50 chars no space
        var mentions = Regex.Matches(req.Body, @"@([\w\.\-]+)").Cast<Match>()
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var mentionedIds = new List<Guid>();
        if (mentions.Count > 0)
        {
            var users = await _db.Users.AsNoTracking()
                .Where(u => mentions.Contains(u.FullName) || mentions.Contains(u.Email)
                    || mentions.Contains(u.Email.Substring(0, u.Email.IndexOf('@') > 0 ? u.Email.IndexOf('@') : u.Email.Length)))
                .Select(u => u.Id)
                .ToListAsync();
            mentionedIds.AddRange(users);
        }

        var c = new DocumentComment
        {
            CompanyId = companyId,
            EntityType = req.EntityType,
            EntityId = req.EntityId,
            AuthorUserId = authorId,
            Body = req.Body.Trim(),
            ParentCommentId = req.ParentCommentId,
            MentionedUserIdsJson = System.Text.Json.JsonSerializer.Serialize(mentionedIds)
        };
        _db.DocumentComments.Add(c);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { c.Id, mentionedUsers = mentionedIds.Count },
            mentionedIds.Count > 0 ? $"โพสต์แล้ว — แจ้งเตือน {mentionedIds.Count} คน" : "โพสต์แล้ว"));
    }

    /// <summary>List comments + activity ของ entity. Sort by createdAt asc
    /// (timeline order).</summary>
    [HttpGet("entity/{entityType}/{entityId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> ListByEntity(
        Guid companyId, string entityType, Guid entityId)
    {
        var comments = await _db.DocumentComments.AsNoTracking()
            .Include(c => c.AuthorUser)
            .Where(c => c.CompanyId == companyId
                && c.EntityType == entityType && c.EntityId == entityId
                && !c.IsDeleted)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id, c.Body, c.CreatedAt, c.EditedAt, c.ParentCommentId,
                Author = new {
                    c.AuthorUser.Id,
                    c.AuthorUser.FullName,
                    c.AuthorUser.Email
                }
            })
            .ToListAsync();

        // Activity log (audit log)
        var activities = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId
                && a.EntityType == entityType
                && a.EntityId == entityId.ToString())
            .OrderByDescending(a => a.Timestamp)
            .Take(50)
            .Select(a => new {
                a.Timestamp, a.Action, a.UserEmail, type = "audit"
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new { comments, activities }));
    }

    [HttpDelete("{commentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid commentId)
    {
        var c = await _db.DocumentComments
            .FirstOrDefaultAsync(x => x.Id == commentId && x.CompanyId == companyId);
        if (c == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ"));
        var userId = GetUserIdFromClaims();
        if (c.AuthorUserId != userId)
            return Forbid();
        c.IsDeleted = true;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "ลบแล้ว"));
    }

    private Guid GetUserIdFromClaims()
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var g) ? g : Guid.Empty;
    }
}
