using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Team-invitation endpoints. Two surfaces:
///   1) /by-token/{token} — anonymous preview so the accept page can show
///      "{Inviter} เชิญคุณเข้าร่วม {Company}" before the user signs in.
///   2) /accept — authenticated; consumes the invitation for the
///      currently-signed-in user. Requires the signed-in user's email to
///      match the invitation's email (case-insensitive).
/// Creation lives on CompanyController.AddUser (with the existing team
/// management flow); the consumption flow is here so the auth-required
/// accept doesn't need the company id in the URL.
/// </summary>
[ApiController]
[Route("api/invitations")]
public class InvitationController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public InvitationController(AccountingDbContext db) { _db = db; }

    public record InvitationPreview(
        string CompanyName,
        string InviterName,
        string Email,
        UserRole Role,
        InvitationStatus Status,
        DateTime ExpiresAt);

    [HttpGet("by-token/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<InvitationPreview>>> Preview(string token)
    {
        // Includes are minimal — preview returns just enough to render the
        // page, not full company / inviter records.
        var inv = await _db.CompanyInvitations.AsNoTracking()
            .Include(i => i.Company)
            .Include(i => i.InvitedBy)
            .FirstOrDefaultAsync(i => i.Token == token && !i.IsDeleted);
        if (inv == null)
            return NotFound(new ApiResponse<InvitationPreview>(false, null!, "ไม่พบคำเชิญ หรือลิงก์ไม่ถูกต้อง"));

        // We don't auto-flip Status to Expired here — the next access path
        // (register / accept) will reject expired tokens. Surfacing Expired
        // in the preview lets the page show a clear "expired" message
        // instead of a generic error.
        var status = inv.Status == InvitationStatus.Pending && inv.ExpiresAt < DateTime.UtcNow
            ? InvitationStatus.Expired
            : inv.Status;

        return Ok(new ApiResponse<InvitationPreview>(true, new InvitationPreview(
            CompanyName: inv.Company.Name,
            InviterName: inv.InvitedBy.FullName,
            Email: inv.Email,
            Role: inv.Role,
            Status: status,
            ExpiresAt: inv.ExpiresAt), null));
    }

    public record AcceptRequest(string Token);

    [HttpPost("accept")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> Accept([FromBody] AcceptRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new UnauthorizedAccessException("ไม่พบผู้ใช้");

        var inv = await _db.CompanyInvitations
            .FirstOrDefaultAsync(i => i.Token == req.Token && !i.IsDeleted);
        if (inv == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำเชิญ"));
        if (inv.Status != InvitationStatus.Pending)
            return BadRequest(new ApiResponse<object>(false, null, $"คำเชิญนี้ไม่สามารถตอบรับได้ (สถานะ: {inv.Status})"));
        if (inv.ExpiresAt < DateTime.UtcNow)
        {
            inv.Status = InvitationStatus.Expired;
            inv.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return BadRequest(new ApiResponse<object>(false, null, "คำเชิญหมดอายุแล้ว — กรุณาขอคำเชิญใหม่จากเจ้าของบริษัท"));
        }

        // Enforce email match (case-insensitive). Without this any signed-in
        // user could swipe an invitation meant for someone else just by
        // knowing the token.
        if (!string.Equals(user.Email.Trim(), inv.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<object>(false, null,
                $"คำเชิญนี้ส่งถึง {inv.Email} แต่คุณเข้าสู่ระบบในนาม {user.Email} — กรุณาเข้าสู่ระบบด้วยอีเมลที่ถูกเชิญ"));

        // Idempotent: if the user is already a member of the company, mark
        // the invitation accepted and return success rather than throwing.
        if (!await _db.CompanyUsers.AnyAsync(cu => cu.CompanyId == inv.CompanyId && cu.UserId == userId))
        {
            _db.CompanyUsers.Add(new CompanyUser
            {
                CompanyId = inv.CompanyId,
                UserId = userId,
                Role = inv.Role,
            });
        }
        inv.Status = InvitationStatus.Accepted;
        inv.AcceptedAt = DateTime.UtcNow;
        inv.AcceptedByUserId = userId;
        inv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object>(true, new { companyId = inv.CompanyId, role = inv.Role }, "ตอบรับคำเชิญสำเร็จ"));
    }
}
