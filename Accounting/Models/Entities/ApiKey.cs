using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// API Key Management สำหรับ Integration
/// </summary>
public class ApiKey : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public string Name { get; set; } = null!;            // ชื่อ API Key
    public string KeyHash { get; set; } = null!;          // Hash ของ key (ไม่เก็บ raw)
    public string KeyPrefix { get; set; } = null!;        // 8 ตัวแรกสำหรับ identify
    public ApiKeyStatus Status { get; set; } = ApiKeyStatus.Active;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    // Permissions
    public FeatureFlags AllowedFeatures { get; set; }     // จำกัด feature ที่เข้าถึงได้
    public string? AllowedIpAddresses { get; set; }       // CSV of allowed IPs
    public int RateLimitPerMinute { get; set; } = 60;

    // Scopes (fine-grained)
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; } = false;
    public bool CanDelete { get; set; } = false;
}
