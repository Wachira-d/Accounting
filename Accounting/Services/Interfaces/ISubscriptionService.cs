using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ISubscriptionService
{
    // Trial Management
    Task<TrialStatusResponse> StartTrialAsync(StartTrialRequest request, string performedBy);
    Task<TrialStatusResponse> GetTrialStatusAsync(Guid companyId);
    Task<TrialStatusResponse> ExtendTrialAsync(Guid companyId, ExtendTrialRequest request, string performedBy);
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

    /// <summary>Check whether <paramref name="additionalBytes"/> of new storage
    /// would fit in the License-overlaid quota for this company. Aggregates
    /// per-company Subscription storage + every CMS Site's storage under the
    /// same License so all uploads (accounting docs + CMS media) share one
    /// pool. Returns false when the upload would exceed the cap.</summary>
    Task<bool> CanFitStorageAsync(Guid companyId, long additionalBytes);

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
    Task<SubscriptionPaymentResponse> ReviewPaymentAsync(Guid paymentId, ReviewSubscriptionPaymentRequest request, string performedBy);
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
