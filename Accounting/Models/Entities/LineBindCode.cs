namespace Accounting.Models.Entities;

/// <summary>
/// One-shot 6-digit code a user generates in the app and sends to the
/// LINE bot to bind their LINE userId to their Next Acc account. Code
/// expires after 10 minutes and is consumed on first successful match.
/// </summary>
public class LineBindCode : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string Code { get; set; } = "";          // 6 digits, zero-padded
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? UsedByLineUserId { get; set; }
}

/// <summary>
/// Per-LINE-user state — most importantly, the active CompanyId selected
/// for users who belong to more than one company. Updated by the bot on
/// "เลือกบริษัท {N}" / on bind / on first message.
/// </summary>
public class LineUserState : BaseEntity
{
    public string LineUserId { get; set; } = "";
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? ActiveCompanyId { get; set; }
    public Company? ActiveCompany { get; set; }
    public DateTime? LastInteractionAt { get; set; }
}
