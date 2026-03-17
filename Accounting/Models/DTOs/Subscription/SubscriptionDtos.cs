using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Subscription;

// ===== Trial Management =====
public record StartTrialRequest(
    Guid CompanyId,
    SubscriptionPlan TargetPlan = SubscriptionPlan.Pro,
    int? TrialDays = null);

public record ExtendTrialRequest(
    int AdditionalDays = 0);  // 0 = ใช้ค่าเริ่มต้นจาก config

public record TrialStatusResponse(
    Guid SubscriptionId,
    Guid CompanyId,
    TrialStatus TrialStatus,
    DateTime TrialStartDate,
    DateTime TrialEndDate,
    int DaysRemaining,
    int DaysUsed,
    int TotalTrialDays,
    bool CanExtend,
    int ExtensionsUsed,
    int MaxExtensions,
    FeatureFlags EnabledFeatures,
    TrialUsageInfo Usage,
    TrialExpiryBehavior ExpiryBehavior,
    ConversionIncentive? Incentive);

public record TrialUsageInfo(
    int DocumentsUsed,
    int DocumentsLimit,
    int JournalEntriesUsed,
    int JournalEntriesLimit,
    int UsersUsed,
    int UsersLimit,
    long StorageUsedBytes,
    long StorageLimitBytes);

public record TrialExpiryBehavior(
    bool BlockAccessOnExpiry,
    bool AllowDataExportAfterExpiry,
    int GracePeriodDays,
    bool ShowWatermark,
    string? TrialMessage);

public record ConversionIncentive(
    decimal? DiscountPercent,
    DateTime? ValidUntil,
    string? PromoCode);

// ===== Subscription Management =====
public record ConvertTrialRequest(
    SubscriptionPlan Plan,
    BillingCycle BillingCycle,
    string? PromoCode = null);

public record ChangeSubscriptionRequest(
    SubscriptionPlan NewPlan,
    BillingCycle? NewBillingCycle = null);

public record SubscriptionResponse(
    Guid Id,
    Guid CompanyId,
    SubscriptionPlan Plan,
    SubscriptionStatus Status,
    BillingCycle BillingCycle,
    decimal PricePerCycle,
    DateTime StartDate,
    DateTime EndDate,
    DateTime? NextBillingDate,
    FeatureFlags EnabledFeatures,
    UsageLimits Limits,
    UsageCurrent Current);

public record UsageLimits(
    int MaxUsers,
    int MaxCompanies,
    int MaxDocumentsPerMonth,
    int MaxJournalEntriesPerMonth,
    long MaxStorageBytes);

public record UsageCurrent(
    int DocumentsThisMonth,
    int JournalEntriesThisMonth,
    long StorageUsed);

// ===== Admin: Plan Template Management =====
public record CreatePlanTemplateRequest(
    string Name,
    string? Description,
    SubscriptionPlan Plan,
    decimal MonthlyPrice,
    decimal? QuarterlyPrice,
    decimal? SemiAnnualPrice,
    decimal? AnnualPrice,
    int MaxUsers,
    int MaxCompanies,
    int MaxDocumentsPerMonth,
    int MaxJournalEntriesPerMonth,
    long MaxStorageBytes,
    FeatureFlags EnabledFeatures,
    // Trial settings
    int TrialDurationDays = 30,
    int TrialMaxExtensions = 2,
    int TrialExtensionDays = 15,
    FeatureFlags TrialFeatures = FeatureFlags.TrialFeatures,
    int TrialMaxUsers = 2,
    int TrialMaxDocumentsPerMonth = 20,
    int TrialMaxJournalEntriesPerMonth = 50,
    bool TrialBlockOnExpiry = false,
    int TrialGracePeriodDays = 7);

public record UpdatePlanTemplateRequest(
    string? Name,
    string? Description,
    decimal? MonthlyPrice,
    decimal? QuarterlyPrice,
    decimal? SemiAnnualPrice,
    decimal? AnnualPrice,
    int? MaxUsers,
    int? MaxCompanies,
    int? MaxDocumentsPerMonth,
    int? MaxJournalEntriesPerMonth,
    long? MaxStorageBytes,
    FeatureFlags? EnabledFeatures,
    bool? IsActive,
    // Trial settings
    int? TrialDurationDays,
    int? TrialMaxExtensions,
    int? TrialExtensionDays,
    FeatureFlags? TrialFeatures,
    int? TrialMaxUsers,
    int? TrialMaxDocumentsPerMonth,
    int? TrialMaxJournalEntriesPerMonth,
    bool? TrialBlockOnExpiry,
    int? TrialGracePeriodDays);

public record PlanTemplateResponse(
    Guid Id,
    string Name,
    string? Description,
    SubscriptionPlan Plan,
    bool IsActive,
    decimal MonthlyPrice,
    decimal QuarterlyPrice,
    decimal SemiAnnualPrice,
    decimal AnnualPrice,
    int MaxUsers,
    int MaxCompanies,
    int MaxDocumentsPerMonth,
    int MaxJournalEntriesPerMonth,
    long MaxStorageBytes,
    FeatureFlags EnabledFeatures,
    int TrialDurationDays,
    int TrialMaxExtensions,
    int TrialExtensionDays,
    FeatureFlags TrialFeatures);

// ===== Admin: Update Trial Config Directly =====
public record UpdateTrialConfigRequest(
    int? TrialDurationDays,
    bool? AllowExtension,
    int? MaxExtensions,
    int? ExtensionDays,
    FeatureFlags? TrialFeatures,
    int? TrialMaxUsers,
    int? TrialMaxDocumentsPerMonth,
    int? TrialMaxJournalEntriesPerMonth,
    int? TrialMaxCompanies,
    bool? ShowTrialWatermark,
    string? TrialMessage,
    bool? NotifyBeforeExpiry,
    int? NotifyDaysBeforeExpiry,
    bool? BlockAccessOnExpiry,
    bool? AllowDataExportAfterExpiry,
    int? GracePeriodDays,
    bool? DeleteDataAfterGracePeriod,
    int? DataRetentionDays,
    decimal? DiscountPercentOnConversion,
    DateTime? DiscountValidUntil,
    string? ConversionPromoCode);
