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
    /// <summary>รอบ 219 ส่วน B (คำตัดสินข้อ 179): ผู้ใช้กดยกเลิกระหว่างรอบที่ชำระแล้ว ⇒ ใช้ได้ถึง <see cref="EndDate"/> แล้วเปลี่ยนเป็น Cancelled
    /// (ไม่เข้า PastDue · ไม่ออกใบต่ออายุ) · ตัวตัดสินเดียว <c>Helpers/SubscriptionCancelPolicy</c> · กลับมาใช้ต่อก่อนสิ้นรอบ = ล้างค่า</summary>
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CancelRequestedAt { get; set; }

    /// <summary>รอบ 234 (คำตัดสิน 234-3): ค่าตั้งของบริษัท "หักภาษี ณ ที่จ่ายทุกครั้งที่ชำระค่าบริการ NextAcc" — null = ยังไม่เคยเลือก (หน้าเว็บติ๊กให้เมื่อเข้าเกณฑ์) ·
    /// true/false = ตัวเลือกล่าสุดของผู้เช่า (ประทับเมื่อเลือกที่หน้าชำระ/การ์ดข้อมูลบริษัท) · ผู้อ่าน: ค่าตั้งต้นของช่องติ๊ก (ศูนย์บิล · checkout) +
    /// ตัดบัตรอัตโนมัติ (<c>PlatformAutoChargeService</c> — true ⇒ ตัดยอดสุทธิ + ประทับภาษีหัก) · ยอดหักคิดที่ <c>Helpers/PlatformWhtSuggestion</c> เสมอ</summary>
    public bool? WithholdTaxPreference { get; set; }


    // WP-B1: ใบแจ้งหนี้ต่ออายุที่ออกล่วงหน้าก่อนหมดอายุ. ผูกกับ EndDate ที่ออกให้
    // → รอบใหม่ (EndDate เปลี่ยนหลังจ่าย) จะออกใบใหม่ ไม่ออกซ้ำรอบเดิม (idempotent).
    public string? RenewalInvoiceNumber { get; set; }
    public DateTime? RenewalInvoiceIssuedAt { get; set; }
    public DateTime? RenewalInvoiceForEndDate { get; set; }
    /// <summary>เอกสารใบแจ้งหนี้จริงใน tenant ผู้ให้บริการ (ถ้าออกผ่าน pipeline เอกสาร)</summary>
    public Guid? RenewalInvoiceDocumentId { get; set; }

    /// <summary>รอบ 219 เฟส 0 (G-0 · คำตัดสินข้อ 178): ดาวน์เกรด/เปลี่ยนรอบบิลที่<b>รอมีผลวันครบรอบ</b> — ไม่ตัดสิทธิ์แพ็กเกจที่จ่ายไปแล้ว ·
    /// <c>ProcessExpiredSubscriptionsAsync</c> ใช้เมื่อถึง <see cref="ScheduledPlanEffectiveAt"/> · ใบแจ้งหนี้ต่ออายุคิดราคาจากแพ็กเกจนี้</summary>
    public SubscriptionPlan? ScheduledPlan { get; set; }
    public BillingCycle? ScheduledBillingCycle { get; set; }
    public DateTime? ScheduledPlanEffectiveAt { get; set; }

    // Trial Management (ควบคุมการทดลองใช้อย่างละเอียด)
    public TrialConfig? TrialConfig { get; set; }

    // Feature Access
    public FeatureFlags EnabledFeatures { get; set; } = FeatureFlags.TrialFeatures;

    // ฟรีตลอดไป (ไม่หมดอายุ ไม่นับวัน trial)
    public bool IsPermanentFree { get; set; } = false;

    /// <summary>รอบ 232 (ฝ่ายค้าน B1 ข): แผนจ่ายเงินที่ EndDate เป็นค่าแทน "ไม่หมดอายุ" (ค.ศ. 2126) โดยธงฟรีถาวร false — ประทับโดย migration (ไม่แก้ EndDate เงียบ) ·
    /// งานต่ออายุ/เตือน/ปิดใช้ข้ามแถวนี้ · แอดมินกด "กำหนดรอบ" (<c>SubscriptionExpiry.CloseOpenEnded</c>) เพื่อปลด · ผู้เขียนเดียว <c>SubscriptionService.CloseOpenEndedAsync</c></summary>
    public bool ExpiryNeedsReview { get; set; } = false;

    /// <summary>รอบ 231 (คำตัดสินข้อ 231-10): ชนิด — ทดลองใช้ / Free Edition / จ่ายเงิน (เดิมต้องอ่าน <see cref="IsPermanentFree"/> + <see cref="Status"/> + <c>LastPaymentDate</c> 3 ช่องเสมอ) ·
    /// ผู้เขียนเดียว <c>Helpers/SubscriptionKindRule.Derive</c> (เริ่มทดลอง/ฟรี · อนุมัติชำระ · ดาวน์เกรดถึงวันมีผล) · แถวเก่า backfill ด้วย SQL สูตรเดียวกัน
    /// (<c>Helpers/PlatformPriceOverrideMigration</c>) · <c>Unknown</c> = ยังไม่ backfill</summary>
    public SubscriptionKind Kind { get; set; } = SubscriptionKind.Unknown;

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

    /// <summary>โควตาเอกสารพิเศษ (ซื้อ top-up / ทำภารกิจแลกโควตา) — บวกเพิ่มจาก
    /// <see cref="MaxDocumentsPerMonth"/> จนถึง <see cref="DocumentBonusExpiresAt"/>
    /// (แบบเดียวกับ OcrBonusPages ที่มีอยู่แล้ว — ใช้ pattern เดิม ไม่สร้างกลไกใหม่)</summary>
    public int DocumentBonusQuota { get; set; }
    public DateTime? DocumentBonusExpiresAt { get; set; }

    /// <summary>รอบ 212 (คำตัดสินข้อ 171): เพดานข้อความผ่านช่องกลาง (LINE OA/อีเมลของ NextAcc) ต่อเดือนเฉพาะบริษัทนี้ —
    /// null = ตามแพ็กเกจ (<see cref="PlanTemplate.MaxSharedMessagesPerMonth"/>) · -1 = ไม่จำกัด · ตัวตัดสิน <c>Helpers/NotifyQuotaPolicy.PlanLimit</c></summary>
    public int? MaxSharedMessagesPerMonth { get; set; }
    /// <summary>รอบ 212: ข้อความช่องกลางที่ซื้อเพิ่ม (<c>notify.topup</c>) — ใช้เมื่อเกินเพดานแพ็กเกจ จนถึง <see cref="NotifyBonusExpiresAt"/> (แบบ <see cref="DocumentBonusQuota"/>)</summary>
    public int NotifyBonusQuota { get; set; }
    public DateTime? NotifyBonusExpiresAt { get; set; }

    /// <summary>ปิด "ทำภารกิจแลกโควตา" เฉพาะบริษัทนี้ (ชั้นที่ 3 ของสวิตช์ —
    /// ใช้กับบริษัทที่ใช้ในทางที่ผิด). ชั้น 1 = ปิดทุก QuotaRewardOption ·
    /// ชั้น 2 = PlanTemplate.AllowQuotaReward</summary>
    public bool QuotaRewardBlocked { get; set; }
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
    /// <summary>รอบ 231 (ฝ่ายค้าน B2): แจ้งเจ้าของล่วงหน้ากี่วันก่อนปิดข้อมูลทดลองใช้ (0 = ไม่แจ้ง) — ผู้อ่าน <c>TrialDataRetentionJob</c>/<c>TrialDataRetentionPolicy</c></summary>
    public int PurgeNoticeDays { get; set; } = 14;
    /// <summary>เวลาที่แจ้งเจ้าของแล้ว (งานตั้งเอง · null = ยังไม่แจ้ง) — ปิดข้อมูลได้เมื่อแจ้งแล้วครบ <see cref="PurgeNoticeDays"/></summary>
    public DateTime? PurgeNoticeSentAt { get; set; }

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
    /// <summary>รอบ 212 (คำตัดสินข้อ 171): เพดานข้อความผ่านช่องกลาง (LINE OA/อีเมลของ NextAcc) ต่อบริษัทต่อเดือน · <b>null/-1 = ไม่จำกัด (ค่าเริ่มต้น = พฤติกรรมเดิม)</b> ·
    /// ผู้ประสานงานตัดสินหลังฝ่ายค้าน: migration ไม่ตั้งตัวเลขให้ — เจ้าของตั้งเองที่ /admin/plans.html (ค่าแนะนำ <c>NotifyQuotaPolicy.SuggestedLimits</c>) ·
    /// ตัวตัดสิน <c>Helpers/NotifyQuotaPolicy.PlanLimit</c></summary>
    public int? MaxSharedMessagesPerMonth { get; set; }

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
    /// <summary>รอบ 212: เพดานข้อความช่องกลาง/เดือนระหว่างทดลองใช้ (null = ใช้ค่าของแพ็กเกจ · -1 = ไม่จำกัด) — เจ้าของตั้งที่ /admin/plans.html</summary>
    public int? TrialMaxSharedMessagesPerMonth { get; set; }
    public bool TrialBlockOnExpiry { get; set; } = false;
    public int TrialGracePeriodDays { get; set; } = 7;
    /// <summary>แพ็กเกจนี้ให้ "ทำภารกิจแลกโควตาเอกสาร" ได้ไหม (LODGING_LICENSING_PLAN §12)
    /// — ปกติเปิดเฉพาะแพ็กเกจเล็ก/ทดลอง เพราะลูกค้าใหญ่ควรอัปเกรดแทนการดูโฆษณา</summary>
    public bool AllowQuotaReward { get; set; } = false;

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

    // WP-B2: เอกสารใบเสร็จ/ใบกำกับค่าบริการ SaaS ที่ออกตอนอนุมัติ (platform → ลูกค้า)
    public string? ReceiptNumber { get; set; }            // เลขรันเอกสาร platform (แยกจาก tenant)
    public Guid? ReceiptAttachmentId { get; set; }        // FileAttachment ของ PDF ที่ gen
    public DateTime? ReceiptIssuedAt { get; set; }
    public bool ReceiptIsTaxInvoice { get; set; }         // true = ใบกำกับภาษี §86/4 (platform จด VAT)

    /// <summary>เอกสารจริงใน tenant ของผู้ให้บริการ (ACCOUNT_STRUCTURE §6.1) —
    /// มีค่าเมื่อระบบออกใบผ่าน pipeline เอกสารจริง (ได้ JE + เข้า ภ.พ.30 + e-Tax)
    /// null = ใบเสร็จโหมดเดิม (PDF เดี่ยว ไม่ลงบัญชี)</summary>
    public Guid? PlatformDocumentId { get; set; }

    /// <summary>ภาษีหัก ณ ที่จ่ายที่ลูกค้าหักจากค่าบริการงวดนี้ (ท.ป.4/2528 — บริการ 3%).
    /// <para><c>Amount</c> = เงินที่ได้รับจริง (สุทธิ) ⇒ ยอดตามใบกำกับ = Amount + ตัวนี้.
    /// ไม่เก็บไว้ = เอกสารที่ออกจะมีแต่ยอดสุทธิ ⇒ รายได้ต่ำไป และเครดิตภาษี 3%
    /// ที่ควรได้คืนหายไป (เคสเดียวกับที่เจอในฝั่งเอกสารลูกค้า)</para></summary>
    public decimal WithholdingTaxAmount { get; set; }

    // WP-C3: ผล OCR สลิป (local Tesseract + rule-based) — advisory ช่วย admin review
    // ไม่ auto-approve (admin ยืนยันเอง). null = ยังไม่ได้อ่าน/อ่านไม่ได้.
    public decimal? SlipOcrAmount { get; set; }           // ยอดที่อ่านได้จากสลิป
    public DateTime? SlipOcrDate { get; set; }            // วันที่ที่อ่านได้
    public string? SlipOcrReference { get; set; }         // เลขอ้างอิง/รายการที่อ่านได้
    public bool? SlipOcrAmountMatches { get; set; }       // ยอดในสลิปตรงกับที่ลูกค้าแจ้งไหม
    public DateTime? SlipOcrParsedAt { get; set; }

    /// <summary>รอบ 219 เฟส 2 (คำตัดสินข้อ 180): รายการนี้คือการชำระ <b>ใบแจ้งหนี้</b> ใน tenant ผู้ให้บริการ (Billing Center / ลิงก์ชำระ) —
    /// อนุมัติแล้ว <b>ตัดชำระใบเดิม</b> (ไม่ออกขายสดใบใหม่ = ไม่นับรายได้ซ้ำ G-10) · <c>RequestedPeriodMonths = 0</c> = ใบค่าใช้งาน (ไม่ต่ออายุ) ·
    /// &gt; 0 = ใบต่ออายุของรอบนี้ (ต่ออายุตามเส้นเดิม) · null = รายการแบบเดิม (ขอแพ็กเกจ/ต่ออายุโดยไม่มีใบ)</summary>
    public Guid? PlatformInvoiceDocumentId { get; set; }

    /// <summary>รอบ 219 เฟส 2 (ฝ่ายค้าน S2/B2/S3): เหตุที่ผู้ดูแลระบบต้องจัดการรายการนี้ด้วยมือ (ตัดชำระใบอัตโนมัติไม่สำเร็จ ·
    /// พักการอนุมัติอัตโนมัติเพราะใบที่ผูกไม่มียอดค้าง/เป็นใบต่ออายุที่ถูกแทนที่ · รับเกินยอดค้าง) — null = ไม่มีเรื่องค้าง ·
    /// ตัวเขียนเดียว <c>Helpers/PlatformPaymentAttention</c> (audit chain ทุกครั้ง) · หน้าแอดมิน <c>payment-exceptions</c> แสดง</summary>
    public string? AttentionReason { get; set; }
    /// <summary>รหัสกฎของ <see cref="AttentionReason"/> (<c>PLATFORM-SETTLE-FAILED</c> / <c>PLATFORM-PAYMENT-HELD</c> / <c>PLATFORM-OVERPAYMENT</c>)</summary>
    public string? AttentionRule { get; set; }
    public DateTime? AttentionAt { get; set; }
    /// <summary>ฝ่ายค้านรอบสุดท้าย 4a: ไฟล์หนังสือรับรอง 50 ทวิ ที่ผู้เช่าส่ง (เก็บที่เดียวกับสลิป — ไม่เสิร์ฟตรง) · null = ยังไม่ส่ง</summary>
    public string? WhtCertificateFileName { get; set; }
    /// <summary>รอบ 234 (คำตัดสิน 234-W1): แถวทะเบียน "ภาษีถูกหัก ณ ที่จ่าย" (<c>WhtCreditReceived</c>) ในบริษัทผู้ให้บริการที่ผูกกับรายการนี้ —
    /// แถวเกิดจาก GL (Dr 11910 ของใบเสร็จ/ใบแจ้งหนี้ · <c>DocumentService.SyncWhtCreditReceivedAsync</c> ตัวเดียว) · ตัวผูก <c>PlatformWhtCertificateService.LinkCreditAsync</c></summary>
    public Guid? WhtCreditId { get; set; }
    /// <summary>รอบ 234 (234-W2): ผู้เช่าส่ง 50 ทวิ ล่าสุดเมื่อ (สถานะ "รอตรวจ" จนแอดมินยืนยัน)</summary>
    public DateTime? WhtCertificateSubmittedAt { get; set; }
    /// <summary>รอบ 234 (234-W3): แอดมินตีกลับ 50 ทวิ — เหตุผล (ผู้เช่าเห็นในศูนย์บิล · ส่งใหม่ = ล้างค่า) · null = ไม่ถูกตีกลับ</summary>
    public string? WhtCertificateRejectedReason { get; set; }
    public DateTime? WhtCertificateRejectedAt { get; set; }
    /// <summary>รอบ 234 (234-W3): ทวง 50 ทวิ ครั้งล่าสุด (กันทวงถี่)</summary>
    public DateTime? WhtCertificateRemindedAt { get; set; }

    // ===== รอบ 230 (คำตัดสินข้อ 230-Q1/230-Q2) — ส่วนต่างอัปเกรดกลางรอบ · ยอดที่เซิร์ฟเวอร์เสนอ · คูปอง · แอดมินแก้ยอด (คอลัมน์: Helpers/PlatformCouponMigration) =====
    /// <summary>รายการนี้คือ<b>ส่วนต่างอัปเกรดกลางรอบ</b> (<c>Helpers/PlatformProration</c>) — อนุมัติ ⇒ เปลี่ยนแพ็กเกจทันที <b>ไม่ขยับวันหมดอายุ</b> · <c>RequestedPeriodMonths = 0</c></summary>
    public bool IsProration { get; set; }
    /// <summary>แพ็กเกจเดิมตอนคิดส่วนต่าง (ชื่อ enum) — เทียบตอนเงินเข้า (เปลี่ยนแล้ว = ล้าสมัย ⇒ พักให้แอดมิน)</summary>
    public string? ProrationFromPlan { get; set; }
    public int? ProrationDays { get; set; }
    public int? ProrationCycleDays { get; set; }
    /// <summary>วันหมดอายุของรอบที่คิดส่วนต่าง (เทียบตอนเงินเข้า · คลาดเคลื่อน ≤ 1 วินาที)</summary>
    public DateTime? ProrationCycleEndUtc { get; set; }
    /// <summary>ยอดที่<b>เซิร์ฟเวอร์</b>เสนอ (หลังส่วนต่าง/คูปอง/แอดมินแก้) — ช่องทางออนไลน์อนุมัติอัตโนมัติเมื่อเงินเข้า = ค่านี้ (<c>PlatformPlanPricing.AutoApproveExpected</c>) ·
    /// null = รายการแบบเดิม (ต้องเท่าราคาเต็มของแพ็กเกจ)</summary>
    public decimal? QuotedAmount { get; set; }
    /// <summary>ยอดก่อนคูปอง (รวม VAT ตามค่าตั้ง) · null = ไม่มีคูปอง</summary>
    public decimal? ListAmount { get; set; }
    public Guid? CouponId { get; set; }
    public string? CouponCode { get; set; }
    /// <summary>ส่วนลดจากคูปอง (รวม VAT ตามค่าตั้ง) — ใบกำกับแสดงเป็นส่วนลดของบรรทัดก่อน VAT (§86/4 · <c>PlatformCouponRule.SplitDiscounted</c>)</summary>
    public decimal CouponDiscountAmount { get; set; }
    /// <summary>เหตุผลที่แอดมินแก้ยอด (audit chain <c>PLATFORM-ADMIN-PRICE-OVERRIDE</c>) · null = ไม่เคยแก้</summary>
    public string? AmountOverrideReason { get; set; }

    // ===== รอบ 231 (คำตัดสินข้อ 231-8/231-9) — ราคาพิเศษรายบริษัท · เปลี่ยนรอบบิลกลางรอบ (คอลัมน์: Helpers/PlatformPriceOverrideMigration) =====
    /// <summary>แถว <c>CompanyPlanPriceOverride</c> ที่ใช้คิดยอดรายการนี้ (ตามรอยกลับได้) · null = ราคาแม่แบบ</summary>
    public Guid? PriceOverrideId { get; set; }
    /// <summary>ราคาแม่แบบ (ราคาตั้ง) ก่อนราคาพิเศษ — ใบกำกับ<b>ไม่</b>แสดงเป็นราคาตั้ง−ส่วนลด (ราคาที่ตกลง = ราคาขายจริง §86/4) · เก็บไว้ให้แอดมิน/ประวัติเห็นส่วนต่าง · null = ไม่มีราคาพิเศษ</summary>
    public decimal? StandardAmount { get; set; }
    /// <summary>รายการนี้คือ<b>เปลี่ยนรอบบิลกลางรอบ</b> (รายเดือน→รายปี · <c>Helpers/PlatformProration.CycleChange</c>) — อนุมัติ ⇒ <b>เริ่มรอบใหม่วันที่ชำระ</b> ตามรอบใหม่
    /// (ต่างจากส่วนต่างอัปเกรดที่คงวันหมดอายุ) · <c>RequestedPeriodMonths</c> = เดือนของรอบใหม่</summary>
    public bool IsCycleChange { get; set; }
    /// <summary>มูลค่าคงเหลือของรอบเดิมที่หักให้ (บาท · = ราคาเดิมต่อรอบ × D/C) — บรรทัดที่สองบนใบเสร็จเป็น <c>DiscountAmount</c> ของบรรทัดแรก</summary>
    public decimal? CycleChangeCredit { get; set; }

    /// <summary>รอบ 237 (K-1 · DECISIONS 237-5): แบบอัปเกรดที่ลูกค้าเลือกตอนซื้อ (<c>Helpers/PlatformProration.UpgradeByMode</c>) — อนุมัติใช้ค่านี้ ไม่ใช่ค่าตั้งปัจจุบัน
    /// (<c>PlatformProration.ApprovalRouteOf</c>) · RestartCycle ⇒ <see cref="IsCycleChange"/> = true รอบเท่าเดิม · null = แถวก่อนรอบ 237 / ไม่ใช่อัปเกรด (ตามธงเดิม) ·
    /// คอลัมน์: <c>Helpers/PlatformUpgradeModeMigration</c></summary>
    public PlatformUpgradeMode? UpgradeMode { get; set; }

    /// <summary>รอบ 237 (ฝ่ายค้าน N2): วันเริ่มรอบที่จ่ายจริงตอนอนุมัติ (<c>PlanPurchasePreview.PaidPeriod.StartUtc</c> — ตัวเดียวกับที่คิด EndDate) · ผู้อ่าน: ข้อความใบเสร็จ/ศูนย์บิล/หน้าตรวจสลิป
    /// ของอัปเกรดแบบเริ่มรอบใหม่ (<c>PlatformProration.RestartUpgradeDescription</c>) · null = ยังไม่อนุมัติ/แถวเก่า</summary>
    public DateTime? PaidPeriodStartUtc { get; set; }
}
