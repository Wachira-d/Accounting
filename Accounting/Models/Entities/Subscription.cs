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

    // Usage Limits
    public int MaxUsers { get; set; } = 1;
    public int MaxCompanies { get; set; } = 1;
    public int MaxDocumentsPerMonth { get; set; } = 50;
    public int MaxJournalEntriesPerMonth { get; set; } = 100;
    public long MaxStorageBytes { get; set; } = 100 * 1024 * 1024; // 100MB

    // Usage Tracking
    public int CurrentMonthDocuments { get; set; }
    public int CurrentMonthJournalEntries { get; set; }
    public long CurrentStorageUsed { get; set; }
    public DateTime UsageResetDate { get; set; }

    // Payment
    public string? PaymentReference { get; set; }
    public DateTime? LastPaymentDate { get; set; }
    public DateTime? NextBillingDate { get; set; }

    // Navigation
    public ICollection<SubscriptionHistory> History { get; set; } = new List<SubscriptionHistory>();
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

    public string Action { get; set; } = null!;  // e.g. "Created", "Upgraded", "TrialExtended", "Expired"
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

    // Features
    public FeatureFlags EnabledFeatures { get; set; }

    // Trial Settings (สำหรับ trial ของ plan นี้)
    public int TrialDurationDays { get; set; } = 30;
    public int TrialMaxExtensions { get; set; } = 2;
    public int TrialExtensionDays { get; set; } = 15;
    public FeatureFlags TrialFeatures { get; set; } = FeatureFlags.TrialFeatures;
    public int TrialMaxUsers { get; set; } = 2;
    public int TrialMaxDocumentsPerMonth { get; set; } = 20;
    public int TrialMaxJournalEntriesPerMonth { get; set; } = 50;
    public bool TrialBlockOnExpiry { get; set; } = false;
    public int TrialGracePeriodDays { get; set; } = 7;
}
