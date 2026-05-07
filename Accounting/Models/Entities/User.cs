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

    // SSO / External Auth
    public string? AuthProvider { get; set; }      // "Google", "Facebook", or null for local
    public string? AuthProviderId { get; set; }     // Provider's unique user ID

    // Security
    public int? FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiry { get; set; }
    public bool EmailVerified { get; set; } = false;

    /// <summary>
    /// Timestamp when login first detected the user's password no longer meets
    /// current complexity rules. Cleared on successful password change.
    /// Drives a 30-day grace period before forcing change.
    /// </summary>
    public DateTime? PasswordWeakDetectedAt { get; set; }

    /// <summary>
    /// User's signature image (base64 data URL or raw base64). Stamped onto
    /// auto-generated PDF documents (e-Tax, receipts) when this user creates
    /// or approves them.
    /// </summary>
    public string? SignatureImageBase64 { get; set; }

    /// <summary>Display name shown next to the signature on documents.</summary>
    public string? SignatureName { get; set; }

    /// <summary>Job title shown under the signature name (e.g. "ผู้จัดการ").</summary>
    public string? SignatureTitle { get; set; }

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
    public Guid? CompanyRoleId { get; set; }
    public CompanyRole? CompanyRole { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
