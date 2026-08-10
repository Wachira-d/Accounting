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

    // ===== ผลิตภัณฑ์ Connected / `/api/v1` (ACCOUNT_STRUCTURE.md §3.2, §7) =====
    // ต่อยอดบน ApiKey เดิมแทนการสร้าง entity ใหม่แข่งกัน — key ที่มีอยู่ทำงาน
    // เหมือนเดิมทุกประการ (field ใหม่ทั้งหมด optional/มี default ที่เป็นกลาง)

    /// <summary>ผูก key เข้ากลุ่มผู้จ่ายเงินเพื่อรวมบิล/รวม dashboard.
    /// null = อ่านจาก `Company.BillingAccountId` แทน (ปกติไม่ต้องตั้ง —
    /// มีไว้รองรับเคสที่ key ของบริษัทหนึ่งถูกเรียกเก็บกับอีกกลุ่มตามสัญญา)</summary>
    public Guid? BillingAccountId { get; set; }

    /// <summary>จำกัด key ให้ทำงานได้เฉพาะสาขานี้ (POS/PMS ประจำสาขา) —
    /// เอกสารที่สร้างผ่าน key นี้จะได้ series/รหัสสาขาถูกต้องโดยไม่ต้องส่งมา
    /// ในทุก request และ key หลุดก็จำกัดความเสียหายอยู่สาขาเดียว.
    /// null = ทำงานได้ทั้งบริษัท (พฤติกรรมเดิม)</summary>
    public Guid? BranchId { get; set; }

    /// <summary>สิทธิ์ระดับฟีเจอร์สำหรับ `/api/v1` — space-separated เช่น
    /// "ocr:write bank:write". ว่าง = ไม่มีสิทธิ์แตะ `/api/v1` เลย
    /// (คีย์เก่าทุกใบจึงถูกกันออกจากพื้นที่ใหม่โดยอัตโนมัติ ต้องตั้งใจให้สิทธิ์)</summary>
    public string? Scopes { get; set; }

    /// <summary>ปลายทางรับผลงาน async (OCR/bank matching ใช้เวลาหลายวินาที
    /// จึงคืน jobId ทันทีแล้วแจ้งผลทางนี้ ไม่ให้ลูกค้า polling)</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>ความลับสำหรับเซ็น HMAC-SHA256 บน payload ที่ยิงออก — ผู้รับ
    /// ตรวจได้ว่ามาจากเราจริงและไม่ถูกแก้ระหว่างทาง</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>ระบบบัญชีปลายทางที่ลูกค้าเลือกตอน onboard — กำหนดว่าใช้ปลั๊ก
    /// แปลง payload ตัวไหน (GenericRest = ยิง contract กลางของเราตรง ๆ)</summary>
    public ErpConnectorType ConnectorType { get; set; } = ErpConnectorType.GenericRest;

    /// <summary>ค่าเชื่อมต่อเฉพาะ ERP (endpoint/tenant id) — **ห้ามเก็บ secret
    /// ดิบ** ให้เก็บผ่าน SecretProtector เหมือน credential อื่นของระบบ</summary>
    public string? ConnectorConfigJson { get; set; }

    /// <summary>คีย์ทดสอบ — งานที่ยิงผ่าน key นี้ไม่เข้าบิล (UsageEvent
    /// ถูก mark IsSandbox) ให้ลูกค้าลอง integrate ก่อนเซ็นสัญญาได้</summary>
    public bool IsSandbox { get; set; }
}
