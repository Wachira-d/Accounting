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
    Task<bool> CheckUsageLimitAsync(Guid companyId, string limitType);
    Task IncrementUsageAsync(Guid companyId, string usageType);

    // Admin: Plan Templates
    Task<PlanTemplateResponse> CreatePlanTemplateAsync(CreatePlanTemplateRequest request);
    Task<List<PlanTemplateResponse>> GetPlanTemplatesAsync(bool includeInactive = false);
    Task<PlanTemplateResponse> UpdatePlanTemplateAsync(Guid templateId, UpdatePlanTemplateRequest request);

    // Admin: Direct Trial Config Update
    Task<TrialStatusResponse> UpdateTrialConfigAsync(Guid companyId, UpdateTrialConfigRequest request, string performedBy);

    // Background: Check expired trials
    Task ProcessExpiredTrialsAsync();
}
