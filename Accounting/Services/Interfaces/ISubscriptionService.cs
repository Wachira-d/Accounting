using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ISubscriptionService
{
    // Trial Management
    Task<TrialStatusResponse> StartTrialAsync(StartTrialRequest request, string performedBy);
    Task<TrialStatusResponse> GetTrialStatusAsync(Guid companyId);
    /// <param name="allowCustomDays">true = ผู้ดูแลแพลตฟอร์ม กำหนดจำนวนวันเองได้ · false (ลูกค้า) = ไม่เกิน <c>TrialConfig.ExtensionDays</c></param>
    Task<TrialStatusResponse> ExtendTrialAsync(Guid companyId, ExtendTrialRequest request, string performedBy,
        bool allowCustomDays = false);
    Task ExpireTrialAsync(Guid companyId, string performedBy);

    // Subscription Management
    Task<SubscriptionResponse> ConvertTrialAsync(Guid companyId, ConvertTrialRequest request, string performedBy);
    Task<SubscriptionResponse> GetSubscriptionAsync(Guid companyId);
    Task<SubscriptionResponse> ChangeSubscriptionAsync(Guid companyId, ChangeSubscriptionRequest request, string performedBy);
    Task CancelSubscriptionAsync(Guid companyId, string performedBy);

    // Usage Tracking
    Task<bool> CheckFeatureAccessAsync(Guid companyId, FeatureFlags feature);
    Task<Accounting.Services.Implementations.SubscriptionService.EffectivePlan?> GetEffectivePlanAsync(Guid companyId);
    Task<Accounting.Services.Implementations.SubscriptionService.AggregateUsage?> GetAggregateUsageAsync(Guid accountSubscriptionId);
    Task<bool> CheckUsageLimitAsync(Guid companyId, string limitType);
    Task IncrementUsageAsync(Guid companyId, string usageType);
    /// <summary>ตัวเลข “ใช้ไปเท่าไร” ตัวเดียวของทั้งระบบ (เอกสาร/สมุดรายวัน/พื้นที่) —
    /// หน้าลูกค้า · แดชบอร์ด · พอร์ทัลแอดมิน ต้องเรียกตัวนี้ ห้ามอ่าน counter บน
    /// Subscription ตรง ๆ (สองตัวในนั้นไม่มีใครเขียน)</summary>
    Task<UsageCurrent> GetUsageCurrentAsync(Guid companyId);

    /// <summary>Check whether <paramref name="additionalBytes"/> of new storage
    /// would fit in the License-overlaid quota for this company. Aggregates
    /// per-company Subscription storage + every CMS Site's storage under the
    /// same License so all uploads (accounting docs + CMS media) share one
    /// pool. Returns false when the upload would exceed the cap.</summary>
    Task<bool> CanFitStorageAsync(Guid companyId, long additionalBytes);

    /// <summary>พื้นที่ที่ใช้จริง (Σ ไฟล์แนบ + สื่อ CMS ของ pool) + เพดาน — <c>null</c> = ไม่มี subscription.
    /// เส้นไฟล์แนบใช้ตัวนี้เพื่อ<b>เตือน</b> (ไม่บล็อกหลักฐานบัญชี — รอบ 193 ข้อ 30)</summary>
    Task<StorageStatus?> GetStorageStatusAsync(Guid companyId);

    /// <summary>พื้นที่ที่ใช้จริงรายบริษัท (แทนคอลัมน์ <c>Subscription.CurrentStorageUsed</c> ที่ไม่มีใครเขียน)</summary>
    Task<Dictionary<Guid, long>> GetStorageBytesByCompanyAsync(IReadOnlyCollection<Guid> companyIds);

    // Admin: Plan Templates
    Task<PlanTemplateResponse> CreatePlanTemplateAsync(CreatePlanTemplateRequest request);
    Task<List<PlanTemplateResponse>> GetPlanTemplatesAsync(bool includeInactive = false);
    Task<PlanTemplateResponse> UpdatePlanTemplateAsync(Guid templateId, UpdatePlanTemplateRequest request);
    Task<int> ResyncSubscriptionsFromTemplateAsync(Guid templateId);

    // Admin: Direct Trial Config Update
    Task<TrialStatusResponse> UpdateTrialConfigAsync(Guid companyId, UpdateTrialConfigRequest request, string performedBy);

    // Subscription Notification Settings
    Task<SubscriptionNotificationSettingsResponse> GetNotificationSettingsAsync(Guid companyId);
    Task<SubscriptionNotificationSettingsResponse> UpdateNotificationSettingsAsync(Guid companyId, UpdateSubscriptionNotificationRequest request, string performedBy);

    // Subscription Payment (Slip Upload & Approval)
    Task<SubscriptionPaymentResponse> SubmitPaymentAsync(Guid companyId, SubmitSubscriptionPaymentRequest request, string performedBy);
    Task<SubscriptionPaymentResponse> UploadPaymentSlipAsync(Guid paymentId, string fileName, string originalFileName, string contentType, long fileSize, string storagePath, string performedBy);
    Task<SubscriptionPaymentListResponse> GetPaymentsAsync(Guid companyId);
    Task<SubscriptionPaymentResponse> GetPaymentAsync(Guid paymentId);
    /// <param name="systemConfirmed">ยืนยันโดย<b>ระบบ</b> (เงินเข้าจริงผ่านช่องทางชำระออนไลน์)
    /// ไม่ใช่คนกดตรวจสลิป ⇒ ไม่มี "ผู้ตรวจสอบ" ให้บันทึก · <c>ReviewedByUserId</c> เป็น null
    /// และเหตุผลถูกเขียนลง <c>ReviewNotes</c> แทน
    /// <para>เส้นที่คนกดยังต้องส่ง <paramref name="performedBy"/> เป็น Guid เหมือนเดิม —
    /// พารามิเตอร์นี้<b>ไม่ผ่อนด่านของเส้นนั้น</b> (ผู้สอบบัญชีต้องแยกออกว่าใครอนุมัติ)</para></param>
    Task<SubscriptionPaymentResponse> ReviewPaymentAsync(Guid paymentId,
        ReviewSubscriptionPaymentRequest request, string performedBy, bool systemConfirmed = false);
    /// <summary>WP-C1: admin บันทึกรับเงินเอง/ยกเว้น — สร้าง payment แล้วอนุมัติ
    /// ทันทีผ่านเส้น renewal เดิม (ต่ออายุ+ประวัติ+ใบเสร็จ). ทุกบาทมี record.</summary>
    Task<SubscriptionPaymentResponse> RecordManualPaymentAsync(Guid companyId, RecordManualPaymentRequest request, string performedBy);
    Task<SubscriptionPaymentListResponse> GetAllPendingPaymentsAsync(); // Admin: ดูรายการชำระเงินรอตรวจสอบทั้งหมด

    // Usage Monitor
    Task<UsageDetailResponse> GetUsageDetailAsync(Guid companyId);

    // Background: Check expired trials & subscriptions
    Task ProcessExpiredTrialsAsync();
    Task ProcessSubscriptionNotificationsAsync(); // ตรวจสอบและส่งแจ้งเตือน
    Task ProcessExpiredSubscriptionsAsync();       // ตรวจสอบ subscription หมดอายุ
}

/// <summary>พื้นที่ที่ใช้จริงของ pool (ไบต์) + เพดานของแพ็กเกจ</summary>
public record StorageStatus(long UsedBytes, long MaxBytes);
