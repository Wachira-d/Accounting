namespace Accounting.Models.Entities;

// ===== External System Integration =====

/// <summary>
/// การเชื่อมต่อระบบภายนอก (TakeTime, PMS, ERP, etc.)
/// </summary>
public class ExternalIntegration : TenantEntity
{
    public string SystemName { get; set; } = "";          // "TakeTime", "Opera", "Cloudbeds"
    public string SystemType { get; set; } = "PMS";       // PMS, ERP, CRM, POS
    public string? SystemVersion { get; set; }
    public string? BaseUrl { get; set; }                   // URL ของระบบภายนอก

    // Authentication
    public string ApiKey { get; set; } = "";               // API Key สำหรับ authenticate
    public string ApiKeyHash { get; set; } = "";           // BCrypt hash
    public string ApiKeyPrefix { get; set; } = "";         // First 8 chars for display
    public string? SecretKey { get; set; }                  // HMAC signing secret

    // Status
    public bool IsActive { get; set; } = true;
    public DateTime? LastSyncAt { get; set; }
    public int TotalSyncCount { get; set; }
    public int ErrorCount { get; set; }
    public int ConsecutiveErrors { get; set; }

    // Configuration (JSON)
    public string? MappingConfigJson { get; set; }          // Account mapping config
    public string? SettingsJson { get; set; }                // Additional settings

    // Webhook
    public string? WebhookUrl { get; set; }
    public bool WebhookEnabled { get; set; }

    // Rate limiting
    public int RateLimitPerMinute { get; set; } = 60;

    // ── สิทธิ์ของคีย์ (G2-01 · คำตัดสินเจ้าของข้อ 37 รอบ 193) ──
    // ตัวตัดสินสิทธิ์ที่บังคับใช้จริงคือ Helpers/IntegrationKeyPolicy.EffectiveScopes
    // — อย่าอ่านสามช่องนี้ตรง ๆ เพื่อตัดสิน (คีย์รุ่นเก่าในช่วงผ่อนผันได้สิทธิ์เต็ม)
    /// <summary>อ่านข้อมูลได้ (GET) — คีย์ใหม่ default = true</summary>
    public bool CanRead { get; set; } = true;
    /// <summary>เขียนข้อมูลได้ (POST/PUT/PATCH) — คีย์ใหม่ default = false (เจ้าของเลือกเอง)</summary>
    public bool CanWrite { get; set; }
    /// <summary>ลบข้อมูลได้ (DELETE) — คีย์ใหม่ default = false · เขียนได้ ≠ ลบได้</summary>
    public bool CanDelete { get; set; }
    /// <summary>คีย์ที่ออกก่อนนโยบายสิทธิ์แยก — migration ติดป้ายให้แถวที่มีอยู่ ณ วันเพิ่มคอลัมน์
    /// · คีย์ที่ออกใหม่เป็น false เสมอ · เจ้าของตั้งสิทธิ์เอง = ย้ายเข้านโยบายใหม่ (false)</summary>
    public bool IsLegacyKey { get; set; }
    /// <summary>วันสิ้นสุดช่วงผ่อนผันของคีย์รุ่นเก่า (UTC) — หลังจากนี้ใช้สิทธิ์ที่เก็บไว้ +
    /// สวมผู้ใช้ได้เฉพาะที่ผูกไว้ · null บนคีย์รุ่นเก่า = ไม่ได้สิทธิ์ผ่อนผัน (fail closed)</summary>
    public DateTime? LegacyDeprecatesAt { get; set; }

    public ICollection<IntegrationSyncLog> SyncLogs { get; set; } = new List<IntegrationSyncLog>();
    public ICollection<IntegrationAccountMapping> AccountMappings { get; set; } = new List<IntegrationAccountMapping>();
    public ICollection<IntegrationUserMapping> UserMappings { get; set; } = new List<IntegrationUserMapping>();
}

/// <summary>
/// Maps an EXTERNAL system's operator identity → a real NextAcc user, so an
/// action arriving over an integration (int_) key can be attributed to the
/// person who actually performed it on the partner side — and the document's
/// creator signature reflects that real operator instead of falling back to
/// the company Owner. The partner sends the operator in the X-Acting-User
/// header (an email, or any agreed external user id). NextAcc resolves it
/// via Helpers/IntegrationKeyPolicy.ResolveActingUser (รอบ 193 · G2-01):
///   1. an explicit row here (ExternalUserKey → UserId) — the ONLY path for
///      keys issued under the new policy,
///   2. a direct email match against a company member — legacy keys only,
///      until ExternalIntegration.LegacyDeprecatesAt (logged + audited each use),
///   3. else no acting user (identity = the integration; downstream falls back
///      to the Owner for the creator signature only, not for permissions).
/// </summary>
public class IntegrationUserMapping : TenantEntity
{
    public Guid IntegrationId { get; set; }
    public ExternalIntegration Integration { get; set; } = null!;
    /// <summary>The id/email the partner sends in X-Acting-User.</summary>
    public string ExternalUserKey { get; set; } = null!;
    /// <summary>The NextAcc user this maps to (must be a member of the company).</summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string? ExternalUserName { get; set; }
}

/// <summary>
/// Log ทุก sync transaction ที่รับเข้ามา
/// </summary>
public class IntegrationSyncLog : TenantEntity
{
    public Guid IntegrationId { get; set; }
    public ExternalIntegration Integration { get; set; } = null!;

    public string EventType { get; set; } = "";             // invoice.created, payment.received, customer.synced
    public string? ExternalId { get; set; }                  // ID จากระบบภายนอก
    public string? ExternalRef { get; set; }                 // Reference number จากภายนอก

    public string Status { get; set; } = "Pending";         // Pending, Success, Failed, Skipped
    public string? RequestPayloadJson { get; set; }          // Raw request body
    public string? ResponseJson { get; set; }                // Response sent back
    public string? ErrorMessage { get; set; }

    // Created entities
    public Guid? CreatedDocumentId { get; set; }
    public Guid? CreatedContactId { get; set; }
    public Guid? CreatedJournalEntryId { get; set; }
    public Guid? CreatedPaymentId { get; set; }

    public int ProcessingTimeMs { get; set; }
}

/// <summary>
/// Mapping ระหว่าง category/code ของระบบภายนอก กับ Account Code ใน Next Acc
/// </summary>
public class IntegrationAccountMapping : TenantEntity
{
    public Guid IntegrationId { get; set; }
    public ExternalIntegration Integration { get; set; } = null!;

    public string ExternalCategory { get; set; } = "";       // e.g., "ROOM_CHARGE", "F&B", "MINIBAR"
    public string? ExternalCode { get; set; }                 // e.g., "RC001"
    public string? ExternalDescription { get; set; }

    // Target accounts in Next Acc
    public Guid? DebitAccountId { get; set; }                 // Dr. account
    public ChartOfAccount? DebitAccount { get; set; }
    public Guid? CreditAccountId { get; set; }                // Cr. account
    public ChartOfAccount? CreditAccount { get; set; }

    public string? JournalDescription { get; set; }           // Template for journal entry description
    public bool IsActive { get; set; } = true;
    public bool AutoCreateJournal { get; set; } = true;       // Auto-create journal entry on sync
}
