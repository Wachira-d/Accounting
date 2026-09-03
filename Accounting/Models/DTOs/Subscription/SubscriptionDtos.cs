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
    List<string> EnabledFeatureNames,
    UsageLimits Limits,
    UsageCurrent Current,
    bool IsPermanentFree = false,
    /// <summary>รหัส add-on ที่บริษัทนี้เปิดใช้อยู่ (`AddOnCodes.*`) — ส่งมาพร้อม
    /// แพ็กเกจเพื่อให้ `Layout.hasFeature` ตัวเดียวตอบได้ทั้ง "ความสามารถของ
    /// แพ็กเกจ" (bitmask) และ "ส่วนเสริมที่ซื้อเพิ่ม" (string) — ห้ามให้หน้าเว็บ
    /// ไปเรียก endpoint ที่สองแล้วตัดสินเอง (จะกลายเป็น resolver ตัวที่สอง)</summary>
    List<string>? EnabledAddOnCodes = null);

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
    int TrialGracePeriodDays = 7,
    bool IsPermanentFree = false);

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
    List<string>? EnabledFeatureNames,
    bool? IsActive,
    // Trial settings
    int? TrialDurationDays,
    int? TrialMaxExtensions,
    int? TrialExtensionDays,
    FeatureFlags? TrialFeatures,
    List<string>? TrialFeatureNames,
    int? TrialMaxUsers,
    int? TrialMaxDocumentsPerMonth,
    int? TrialMaxJournalEntriesPerMonth,
    bool? TrialBlockOnExpiry,
    int? TrialGracePeriodDays,
    bool? IsPermanentFree = null,
    // ─── OCR per-engine quotas (null on AzureOcrPagesPerMonth /
    //     LocalOcrPagesPerMonth means "use the legacy single-bucket
    //     MaxOcrPagesPerMonth"). 0 = no Azure for this plan tier.
    int? MaxOcrPagesPerMonth = null,
    int? AzureOcrPagesPerMonth = null,
    int? LocalOcrPagesPerMonth = null,
    bool? FallbackToLocalWhenAzureExhausted = null,
    int? TrialMaxOcrPagesPerMonth = null);

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
    List<string> EnabledFeatureNames,
    int TrialDurationDays,
    int TrialMaxExtensions,
    int TrialExtensionDays,
    FeatureFlags TrialFeatures,
    List<string> TrialFeatureNames,
    bool IsPermanentFree = false,
    // ─── OCR per-engine quotas surfaced for the admin plans UI ───
    int MaxOcrPagesPerMonth = 0,
    int? AzureOcrPagesPerMonth = null,
    int? LocalOcrPagesPerMonth = null,
    bool FallbackToLocalWhenAzureExhausted = true,
    int TrialMaxOcrPagesPerMonth = 10);

// ===== Subscription Notification Settings =====
public record UpdateSubscriptionNotificationRequest(
    bool? NotifyBeforeExpiry,
    string? NotifyDaysBeforeExpiry,          // e.g. "30,15,7,3,1"
    bool? NotifyOnExpiry,
    bool? NotifyAfterExpiry,
    string? NotifyDaysAfterExpiry,           // e.g. "1,3,7"
    int? DeactivationDaysAfterExpiry,        // ตัดบัญชีหลังหมดอายุกี่วัน
    bool? NotifyBeforeDeactivation,
    string? NotifyDaysBeforeDeactivation);   // e.g. "7,3,1"

public record SubscriptionNotificationSettingsResponse(
    bool NotifyBeforeExpiry,
    List<int> NotifyDaysBeforeExpiry,
    bool NotifyOnExpiry,
    bool NotifyAfterExpiry,
    List<int> NotifyDaysAfterExpiry,
    int DeactivationDaysAfterExpiry,
    bool NotifyBeforeDeactivation,
    List<int> NotifyDaysBeforeDeactivation);

// ===== Subscription Payment (Slip Upload & Approval) =====
public record SubmitSubscriptionPaymentRequest(
    Guid SubscriptionId,
    decimal Amount,
    DateTime PaymentDate,
    PaymentMethod PaymentMethod,
    string? FromBankName,
    string? FromAccountNumber,
    string? ToBankName,
    string? ToAccountNumber,
    string? TransferReference,
    SubscriptionPlan RequestedPlan,
    BillingCycle RequestedBillingCycle,
    int RequestedPeriodMonths,
    string? CustomerNotes);

public record ReviewSubscriptionPaymentRequest(
    bool Approve,
    string? ReviewNotes,
    string? RejectionReason);

/// <summary>WP-C1: admin บันทึกรับเงินเอง (เงินสด/โอนนอกระบบ) หรือยกเว้นค่าบริการ.
/// สร้าง SubscriptionPayment แล้ววิ่งเข้าเส้น approve เดิม (ต่ออายุ+ประวัติ+ใบเสร็จ)
/// เพื่อให้ทุกบาทมี record — ไม่ต่ออายุแบบไร้ร่องรอย.
/// Waived: Amount=0 + WaiveReason required.</summary>
public record RecordManualPaymentRequest(
    decimal Amount,
    DateTime PaymentDate,
    PaymentMethod PaymentMethod,
    SubscriptionPlan RequestedPlan,
    BillingCycle RequestedBillingCycle,
    int RequestedPeriodMonths,
    bool IsWaived,
    SubscriptionWaiveReason? WaiveReason,
    string? TransferReference,
    string? Notes,
    /// <summary>ภาษีหัก ณ ที่จ่ายที่ลูกค้าหักจากค่าบริการงวดนี้ (นิติบุคคลหักบริการ 3%
    /// ตาม ท.ป.4/2528). <c>Amount</c> = เงินที่ได้รับจริง ⇒ ยอดตามใบกำกับ = Amount + ตัวนี้.
    /// ไม่ระบุ = ไม่ถูกหัก</summary>
    decimal WithholdingTaxAmount = 0m);

public record SubscriptionPaymentResponse(
    Guid Id,
    Guid SubscriptionId,
    string PaymentNumber,
    decimal Amount,
    string Currency,
    DateTime PaymentDate,
    PaymentMethod PaymentMethod,
    string? FromBankName,
    string? FromAccountNumber,
    string? ToBankName,
    string? ToAccountNumber,
    string? TransferReference,
    string? SlipFileName,
    string? SlipUrl,
    SubscriptionPlan RequestedPlan,
    BillingCycle RequestedBillingCycle,
    int RequestedPeriodMonths,
    SubscriptionPaymentStatus Status,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNotes,
    string? RejectionReason,
    DateTime? SubscriptionExtendedTo,
    string? CustomerNotes,
    DateTime CreatedAt,
    // WP-B2: เอกสารใบเสร็จ/ใบกำกับที่ออกแล้ว (null = ยังไม่ออก)
    string? ReceiptNumber = null,
    bool ReceiptIsTaxInvoice = false,
    // WP-C3: ผล OCR สลิป (advisory)
    decimal? SlipOcrAmount = null,
    bool? SlipOcrAmountMatches = null,
    string? SlipOcrReference = null,
    DateTime? SlipOcrDate = null);

public record SubscriptionPaymentListResponse(
    List<SubscriptionPaymentResponse> Payments,
    int TotalCount,
    int PendingCount);

// ===== Usage Monitor Detail =====
public record UsageDetailResponse(
    // Subscription info
    SubscriptionPlan Plan,
    SubscriptionStatus Status,
    DateTime EndDate,
    // Users
    int CurrentUsers,
    int MaxUsers,
    List<UsageUserInfo> Users,
    // Storage
    long StorageUsed,
    long MaxStorageBytes,
    List<StorageCategoryInfo> StorageBreakdown,
    List<StorageFileInfo> LargestFiles,
    // Monthly usage
    int DocumentsThisMonth,
    int MaxDocumentsPerMonth,
    int JournalEntriesThisMonth,
    int MaxJournalEntriesPerMonth,
    DateTime UsageResetDate,
    // OCR usage
    int OcrPagesThisMonth = 0,
    int MaxOcrPagesPerMonth = 0,
    int OcrBonusPages = 0,
    int OcrCreditPagesRemaining = 0,
    // Alerts
    List<UsageAlert>? Alerts = null);

public record UsageUserInfo(
    Guid UserId,
    string FullName,
    string Email,
    string Role,
    DateTime JoinedAt,
    DateTime? LastLoginAt);

public record StorageCategoryInfo(
    string Category,
    string Label,
    long Bytes,
    int FileCount);

public record StorageFileInfo(
    Guid Id,
    string FileName,
    string EntityType,
    long FileSize,
    DateTime UploadedAt,
    string UploadedBy);

public record UsageAlert(
    string Type,     // warning, danger, info
    string Category, // storage, documents, users, subscription
    string Message);

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

// ===== Admin: Direct Subscription Management =====
public record AdminChangePlanRequest(
    SubscriptionPlan Plan,
    BillingCycle? BillingCycle = null,
    decimal? PricePerCycle = null,
    bool KeepCurrentLimits = false,
    string? Notes = null);

public record AdminChangeSubStatusRequest(
    SubscriptionStatus Status,
    string? Notes = null);

public record AdminChangeDatesRequest(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    DateTime? NextBillingDate = null,
    string? Notes = null);

public record AdminChangeLimitsRequest(
    int? MaxUsers = null,
    int? MaxDocumentsPerMonth = null,
    int? MaxJournalEntriesPerMonth = null,
    long? MaxStorageBytes = null,
    int? MaxCompanies = null,
    string? Notes = null);
