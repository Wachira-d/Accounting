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
