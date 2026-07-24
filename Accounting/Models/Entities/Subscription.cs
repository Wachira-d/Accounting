using System.ComponentModel.DataAnnotations.Schema;
using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ระบบ Subscription & Trial Management
/// ควบคุมการทดลองใช้งานอย่างละเอียด
/// </summary>
public class Subscription : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>When set, this Company gets its quota / features from the
    /// owner's AccountSubscription instead of this row. The row still exists
    /// so per-month usage counters (DocumentsThisMonth etc.) have somewhere to
    /// live — but limits + features come from the account plan.
    /// Null = legacy per-company plan, this row's limits are authoritative.</summary>
    public Guid? AccountSubscriptionId { get; set; }
    public AccountSubscription? AccountSubscription { get; set; }

    // Plan
    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.FreeTrial;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    // Pricing
    public decimal PricePerCycle { get; set; }
    public string Currency { get; set; } = "THB";

    // Active Period
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? CancelledAt { get; set; }

    // Trial Management (ควบคุมการทดลองใช้อย่างละเอียด)
    public TrialConfig? TrialConfig { get; set; }

    // Feature Access
    public FeatureFlags EnabledFeatures { get; set; } = FeatureFlags.TrialFeatures;

    // ฟรีตลอดไป (ไม่หมดอายุ ไม่นับวัน trial)
    public bool IsPermanentFree { get; set; } = false;

    // Usage Limits
    public int MaxUsers { get; set; } = 1;
    public int MaxCompanies { get; set; } = 1;
    public int MaxDocumentsPerMonth { get; set; } = 50;
    public int MaxJournalEntriesPerMonth { get; set; } = 100;
    public long MaxStorageBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public int MaxOcrPagesPerMonth { get; set; } = 10;

    // ─── OCR engine-specific quotas ───
    // Lets each subscription split its OCR budget between premium-tier
    // Azure DI (which costs us per page) and free-tier local OCR (no
    // marginal cost). MaxOcrPagesPerMonth above stays as the legacy
    // total; the engine-specific fields below take effect when set.
    //
    // Common configurations:
    //   • Free plan:    AzurePages=0, LocalPages=unlimited (null)
    //                   → all scans use local OCR, no cloud cost.
    //   • Basic:        AzurePages=50, LocalPages=unlimited
    //                   → first 50 = Azure DI, then fall back to local.
    //   • Enterprise:   AzurePages=5000, LocalPages=unlimited
    //                   → primarily Azure DI, local for overflow.
    // When AzurePages quota is hit AND FallbackToLocalWhenAzureExhausted
    // is true, the cascade automatically routes to local engines instead
    // of refusing the scan.
    public int? AzureOcrPagesPerMonth { get; set; }        // null = use MaxOcrPagesPerMonth as Azure budget
    public int? LocalOcrPagesPerMonth { get; set; }        // null = unlimited
    public bool FallbackToLocalWhenAzureExhausted { get; set; } = true;
    public int CurrentMonthAzureOcrPages { get; set; }
    public int CurrentMonthLocalOcrPages { get; set; }

    // Usage Tracking (legacy total — kept in sync with engine-specific counters)
    public int CurrentMonthDocuments { get; set; }
    public int CurrentMonthJournalEntries { get; set; }
    public long CurrentStorageUsed { get; set; }
    public int CurrentMonthOcrPages { get; set; }
    public int OcrBonusPages { get; set; }
    public DateTime? OcrBonusExpiresAt { get; set; }
    public DateTime UsageResetDate { get; set; }

    // Payment
    public string? PaymentReference { get; set; }
    public DateTime? LastPaymentDate { get; set; }
    public DateTime? NextBillingDate { get; set; }

    // Notification Settings (กำหนดการแจ้งเตือน Subscription)
    public bool NotifyBeforeExpiry { get; set; } = true;
    public string NotifyDaysBeforeExpiry { get; set; } = "30,15,7,3,1"; // comma-separated days
    public bool NotifyOnExpiry { get; set; } = true;
    public bool NotifyAfterExpiry { get; set; } = true;
    public string NotifyDaysAfterExpiry { get; set; } = "1,3,7";  // comma-separated days
    public int DeactivationDaysAfterExpiry { get; set; } = 14;     // ตัดบัญชีหลังหมดอายุกี่วัน
    public bool NotifyBeforeDeactivation { get; set; } = true;
    public string NotifyDaysBeforeDeactivation { get; set; } = "7,3,1"; // comma-separated days before deactivation

    // Navigation
    public ICollection<SubscriptionHistory> History { get; set; } = new List<SubscriptionHistory>();
    public ICollection<SubscriptionPayment> SubscriptionPayments { get; set; } = new List<SubscriptionPayment>();
}

/// <summary>
/// การตั้งค่า Trial อย่างละเอียด
/// </summary>
public class TrialConfig : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    // Trial Period
    public TrialStatus TrialStatus { get; set; } = TrialStatus.Active;
    public DateTime TrialStartDate { get; set; }
    public DateTime TrialEndDate { get; set; }
    public int TrialDurationDays { get; set; } = 30;

    // Extension
    public bool AllowExtension { get; set; } = true;
    public int MaxExtensions { get; set; } = 2;
    public int ExtensionsUsed { get; set; } = 0;
    public int ExtensionDays { get; set; } = 15;  // จำนวนวันต่อครั้งที่ขยาย

    // Feature Restrictions during Trial
    public FeatureFlags TrialFeatures { get; set; } = FeatureFlags.TrialFeatures;
    public int TrialMaxUsers { get; set; } = 2;
    public int TrialMaxDocumentsPerMonth { get; set; } = 20;
    public int TrialMaxJournalEntriesPerMonth { get; set; } = 50;
    public int TrialMaxOcrPagesPerMonth { get; set; } = 10;
    public int TrialMaxCompanies { get; set; } = 1;

    // Watermark / Branding
    public bool ShowTrialWatermark { get; set; } = true;
    public string? TrialMessage { get; set; } = "ระบบทดลองใช้งาน";

    // Notification Settings
    public bool NotifyBeforeExpiry { get; set; } = true;
    public int NotifyDaysBeforeExpiry { get; set; } = 7;
    public bool NotifyOnExpiry { get; set; } = true;
    public bool NotifyAfterExpiry { get; set; } = true;
    public int NotifyDaysAfterExpiry { get; set; } = 3;

    // Behavior on Expiry
    public bool BlockAccessOnExpiry { get; set; } = false;  // true = บล็อกเลย, false = read-only
    public bool AllowDataExportAfterExpiry { get; set; } = true;
    public int GracePeriodDays { get; set; } = 7;  // จำนวนวัน grace period หลังหมดอายุ
    public bool DeleteDataAfterGracePeriod { get; set; } = false;
    public int DataRetentionDays { get; set; } = 90; // เก็บข้อมูลกี่วันหลังหมดอายุ

    // Computed Properties
    [NotMapped]
    public bool IsTrialExpired => DateTime.UtcNow > TrialEndDate;
    [NotMapped]
    public DateTime GracePeriodEndDate => TrialEndDate.AddDays(GracePeriodDays);

    // Conversion Incentive
    public decimal? DiscountPercentOnConversion { get; set; }  // ส่วนลดเมื่อแปลงเป็น paid
    public DateTime? DiscountValidUntil { get; set; }
    public string? ConversionPromoCode { get; set; }
}

/// <summary>
/// ประวัติการเปลี่ยนแปลง Subscription
/// </summary>
public class SubscriptionHistory : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    /// <summary>When the history row was created by an action against an
    /// AccountSubscription (User-level License), this points at it. Both FKs
    /// can be populated when the slip-approval cascade extended both layers.</summary>
    public Guid? AccountSubscriptionId { get; set; }
    public AccountSubscription? AccountSubscription { get; set; }

    public string Action { get; set; } = null!;  // e.g. "Created", "Upgraded", "TrialExtended", "Expired", "AccountPlanExtended"
    public SubscriptionPlan? FromPlan { get; set; }
    public SubscriptionPlan? ToPlan { get; set; }
    public SubscriptionStatus? FromStatus { get; set; }
    public SubscriptionStatus? ToStatus { get; set; }
    public string? Notes { get; set; }
    public string? PerformedBy { get; set; }
}

/// <summary>
/// Template สำหรับตั้งค่า Plan ที่ Admin กำหนดได้
/// </summary>
public class PlanTemplate : BaseEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public SubscriptionPlan Plan { get; set; }
    public bool IsActive { get; set; } = true;

    // Pricing
    public decimal MonthlyPrice { get; set; }
    public decimal QuarterlyPrice { get; set; }
    public decimal SemiAnnualPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public string Currency { get; set; } = "THB";

    // Limits
    public int MaxUsers { get; set; }
    public int MaxCompanies { get; set; }
    public int MaxDocumentsPerMonth { get; set; }
    public int MaxJournalEntriesPerMonth { get; set; }
    public long MaxStorageBytes { get; set; }
    public int MaxOcrPagesPerMonth { get; set; }

    // ─── Per-engine OCR quotas (split Azure DI vs local) ───
    // When set, override the single MaxOcrPagesPerMonth and let admins
    // sell premium-OCR tiers without paying Azure costs for free users:
    //   Free plan: AzurePages=0, LocalPages=null (unlimited)
    //   Basic:     AzurePages=50,  LocalPages=null (50 Azure, then local)
    //   Pro:       AzurePages=500, LocalPages=null
    //   Enterprise:AzurePages=5000,LocalPages=null
    public int? AzureOcrPagesPerMonth { get; set; }
    public int? LocalOcrPagesPerMonth { get; set; }
    public bool FallbackToLocalWhenAzureExhausted { get; set; } = true;

    // Features
    public FeatureFlags EnabledFeatures { get; set; }

    // Permanent free plan — ไม่หมดอายุ, ไม่นับวัน trial
    public bool IsPermanentFree { get; set; } = false;

    // Trial Settings (สำหรับ trial ของ plan นี้)
    public int TrialDurationDays { get; set; } = 30;
    public int TrialMaxExtensions { get; set; } = 2;
    public int TrialExtensionDays { get; set; } = 15;
    public FeatureFlags TrialFeatures { get; set; } = FeatureFlags.TrialFeatures;
    public int TrialMaxUsers { get; set; } = 2;
    public int TrialMaxDocumentsPerMonth { get; set; } = 20;
    public int TrialMaxJournalEntriesPerMonth { get; set; } = 50;
    public int TrialMaxOcrPagesPerMonth { get; set; } = 10;
    public bool TrialBlockOnExpiry { get; set; } = false;
    public int TrialGracePeriodDays { get; set; } = 7;
}

/// <summary>
/// การชำระเงิน Subscription (โอนเงิน + อัพโหลดสลิป)
/// ลูกค้าโอนเงินแล้วอัพโหลดสลิป → Admin ตรวจสอบ → Approve → ต่ออายุ Subscription
/// </summary>
public class SubscriptionPayment : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;

    // Payment Info
    public string PaymentNumber { get; set; } = null!;  // รหัสการชำระ (auto-generated)
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime PaymentDate { get; set; }            // วันที่ลูกค้าโอน
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;

    // Bank Transfer Details
    public string? FromBankName { get; set; }            // ธนาคารผู้โอน
    public string? FromAccountNumber { get; set; }       // เลขบัญชีผู้โอน (last 4 digits)
    public string? ToBankName { get; set; }              // ธนาคารปลายทาง
    public string? ToAccountNumber { get; set; }         // เลขบัญชีปลายทาง
    public string? TransferReference { get; set; }       // เลขอ้างอิงการโอน

    // Slip Upload
    public string? SlipFileName { get; set; }
    public string? SlipOriginalFileName { get; set; }
    public string? SlipContentType { get; set; }
    public long? SlipFileSize { get; set; }
    public string? SlipStoragePath { get; set; }

    // Subscription Plan Requested
    public SubscriptionPlan RequestedPlan { get; set; }
    public BillingCycle RequestedBillingCycle { get; set; }
    public int RequestedPeriodMonths { get; set; }       // จำนวนเดือนที่ต้องการต่อ

    // Approval
    public SubscriptionPaymentStatus Status { get; set; } = SubscriptionPaymentStatus.Pending;
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }             // หมายเหตุจาก Admin
    public string? RejectionReason { get; set; }         // เหตุผลที่ปฏิเสธ

    // Result (after approval)
    public DateTime? SubscriptionExtendedTo { get; set; } // วันที่ต่ออายุถึง
    public string? CustomerNotes { get; set; }            // หมายเหตุจากลูกค้า

    // WP-C1: ที่มา/ประเภทการบันทึก — แยกรับเงินปกติ vs admin บันทึกเอง vs ยกเว้น
    public SubscriptionPaymentKind Kind { get; set; } = SubscriptionPaymentKind.Normal;
    public SubscriptionWaiveReason? WaiveReason { get; set; }  // required เมื่อ Kind=Waived
}
