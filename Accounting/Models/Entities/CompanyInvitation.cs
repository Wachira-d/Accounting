using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// A pending invitation for an email address to join a company as a member.
/// Created when an Owner adds an email that doesn't yet have a User account.
/// Resolved when the invitee either:
///   1) signs up using the link in the invitation email (RegisterAsync sees
///      the inviteToken in the payload and links the new User to the company
///      in the same transaction), or
///   2) is already a member of the system and clicks "ตอบรับ" on the accept
///      page while signed in (POST /api/invitations/accept).
///
/// Email is stored lowercase so a case-typo on either the inviter or
/// invitee side doesn't break matching.
/// </summary>
public class CompanyInvitation : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>Normalized to lowercase on save.</summary>
    public string Email { get; set; } = null!;

    public UserRole Role { get; set; } = UserRole.Viewer;

    public Guid InvitedByUserId { get; set; }
    public User InvitedBy { get; set; } = null!;

    /// <summary>Opaque random token shipped in the email link.</summary>
    public string Token { get; set; } = null!;

    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    /// <summary>When the invitation is consumed, the User row that consumed
    /// it. Useful for audit when the invitee signed up with a slightly
    /// different cased / aliased email and we need to trace the link.</summary>
    public Guid? AcceptedByUserId { get; set; }
}
