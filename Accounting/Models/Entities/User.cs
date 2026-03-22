using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? Phone { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public bool IsSystemAdmin { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiry { get; set; }

    // Security
    public int? FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }
    public bool EmailVerified { get; set; } = false;

    // Navigation
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
}

/// <summary>
/// Many-to-many: User ↔ Company with Role
/// </summary>
public class CompanyUser
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public UserRole Role { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
