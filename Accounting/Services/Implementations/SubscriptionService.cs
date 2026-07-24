using Accounting.Data;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// ระบบจัดการ Subscription และ Trial อย่างละเอียด
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly AccountingDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly INotificationEngine? _notify;
    private readonly ISaasBillingDocumentService? _billing;

    public SubscriptionService(AccountingDbContext db, INotificationService notificationService,
        INotificationEngine? notify = null, ISaasBillingDocumentService? billing = null)
    {
        _db = db;
        _notificationService = notificationService;
        _notify = notify;
        _billing = billing;
    }

    /// <summary>duplicate audit #7 phase 2: route subscription notification ผ่าน
    /// NotificationEngine (production) → กลับไป NotificationService legacy
    /// ถ้าไม่ inject (test fixture). พอ phase 2 ครบจะลบ INotificationService dep ได้</summary>
    private async Task NotifyUserAsync(Guid recipientUserId, Guid? companyId,
        string eventKey, Models.Enums.NotificationType type, string title, string message,
        string? actionUrl = null, string? entityType = null, Guid? entityId = null)
    {
        if (_notify != null && companyId.HasValue)
        {
            await _notify.DispatchAsync(companyId.Value, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = actionUrl,
                EntityType = entityType, EntityId = entityId,
                BellType = type, RecipientUserId = recipientUserId,
            });
        }
        else
        {
            await _notificationService.SendAsync(recipientUserId, companyId, type,
                title, message, actionUrl, entityType, entityId);
        }
    }

    // ==================== Trial Management ====================

    public async Task<TrialStatusResponse> StartTrialAsync(StartTrialRequest request, string performedBy)
    {
        var company = await _db.Companies.FindAsync(request.CompanyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        var existing = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == request.CompanyId);
        if (existing != null)
            throw new InvalidOperationException("บริษัทนี้มี subscription อยู่แล้ว");

        // Get plan template for trial settings
        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.TargetPlan && p.IsActive);

        var isPermanentFree = template?.IsPermanentFree ?? false;
        var trialDays = isPermanentFree
            ? 36500
            : (request.TrialDays ?? template?.TrialDurationDays ?? 30);
        var now = DateTime.UtcNow;

        var subscription = new Subscription
        {
            CompanyId = request.CompanyId,
            Plan = request.TargetPlan,
            Status = isPermanentFree ? SubscriptionStatus.Active : SubscriptionStatus.Trial,
            StartDate = now,
            EndDate = now.AddDays(trialDays),
            IsPermanentFree = isPermanentFree,
            EnabledFeatures = isPermanentFree
                ? (template?.EnabledFeatures ?? FeatureFlags.TrialFeatures)
                : (template?.TrialFeatures ?? FeatureFlags.TrialFeatures),
            MaxUsers = template?.TrialMaxUsers ?? 2,
            MaxCompanies = 1,
            MaxDocumentsPerMonth = template?.TrialMaxDocumentsPerMonth ?? 20,
            MaxJournalEntriesPerMonth = template?.TrialMaxJournalEntriesPerMonth ?? 50,
            MaxStorageBytes = 50 * 1024 * 1024,
            // Pull OCR quotas from the template so a new subscription
            // honors the per-engine budget the admin configured for this
            // plan tier (free=0 Azure pages, paid tiers get more).
            MaxOcrPagesPerMonth = template?.TrialMaxOcrPagesPerMonth ?? 10,
            AzureOcrPagesPerMonth = template?.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth = template?.LocalOcrPagesPerMonth,
            FallbackToLocalWhenAzureExhausted = template?.FallbackToLocalWhenAzureExhausted ?? true,
            UsageResetDate = new DateTime(now.Year, now.Month, 1).AddMonths(1),
            CreatedBy = performedBy
        };

        _db.Subscriptions.Add(subscription);

        // Create trial config
        var trialConfig = new TrialConfig
        {
            SubscriptionId = subscription.Id,
            TrialStatus = TrialStatus.Active,
            TrialStartDate = now,
            TrialEndDate = now.AddDays(trialDays),
            TrialDurationDays = trialDays,
            AllowExtension = template != null ? template.TrialMaxExtensions > 0 : true,
            MaxExtensions = template?.TrialMaxExtensions ?? 2,
            ExtensionDays = template?.TrialExtensionDays ?? 15,
            TrialFeatures = template?.TrialFeatures ?? FeatureFlags.TrialFeatures,
            TrialMaxUsers = template?.TrialMaxUsers ?? 2,
            TrialMaxDocumentsPerMonth = template?.TrialMaxDocumentsPerMonth ?? 20,
            TrialMaxJournalEntriesPerMonth = template?.TrialMaxJournalEntriesPerMonth ?? 50,
            TrialMaxCompanies = 1,
            BlockAccessOnExpiry = template?.TrialBlockOnExpiry ?? false,
            GracePeriodDays = template?.TrialGracePeriodDays ?? 7,
            CreatedBy = performedBy
        };

        _db.TrialConfigs.Add(trialConfig);

        // Record history
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = subscription.Id,
            Action = "TrialStarted",
            ToPlan = request.TargetPlan,
            ToStatus = SubscriptionStatus.Trial,
            Notes = $"Trial started for {trialDays} days",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        return await GetTrialStatusAsync(request.CompanyId);
    }

    public async Task<TrialStatusResponse> GetTrialStatusAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มีข้อมูล trial config");

        var now = DateTime.UtcNow;
        var daysRemaining = Math.Max(0, (int)(tc.TrialEndDate - now).TotalDays);
        var daysUsed = (int)(now - tc.TrialStartDate).TotalDays;

        // Count current users
        var userCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);

        return new TrialStatusResponse(
            sub.Id,
            companyId,
            tc.TrialStatus,
            tc.TrialStartDate,
            tc.TrialEndDate,
            daysRemaining,
            daysUsed,
            tc.TrialDurationDays + (tc.ExtensionsUsed * tc.ExtensionDays),
            tc.AllowExtension && tc.ExtensionsUsed < tc.MaxExtensions,
            tc.ExtensionsUsed,
            tc.MaxExtensions,
            sub.EnabledFeatures,
            new TrialUsageInfo(
                sub.CurrentMonthDocuments,
                tc.TrialMaxDocumentsPerMonth,
                sub.CurrentMonthJournalEntries,
                tc.TrialMaxJournalEntriesPerMonth,
                userCount,
                tc.TrialMaxUsers,
                sub.CurrentStorageUsed,
                sub.MaxStorageBytes),
            new TrialExpiryBehavior(
                tc.BlockAccessOnExpiry,
                tc.AllowDataExportAfterExpiry,
                tc.GracePeriodDays,
                tc.ShowTrialWatermark,
                tc.TrialMessage),
            tc.DiscountPercentOnConversion.HasValue
                ? new ConversionIncentive(tc.DiscountPercentOnConversion, tc.DiscountValidUntil, tc.ConversionPromoCode)
                : null);
    }

    public async Task<TrialStatusResponse> ExtendTrialAsync(Guid companyId, ExtendTrialRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มีข้อมูล trial config");

        if (!tc.AllowExtension)
            throw new InvalidOperationException("Trial นี้ไม่อนุญาตให้ขยายเวลา");

        if (tc.ExtensionsUsed >= tc.MaxExtensions)
            throw new InvalidOperationException($"ใช้สิทธิ์ขยายเวลาครบแล้ว ({tc.MaxExtensions} ครั้ง)");

        var additionalDays = request.AdditionalDays > 0 ? request.AdditionalDays : tc.ExtensionDays;
        tc.TrialEndDate = tc.TrialEndDate.AddDays(additionalDays);
        tc.ExtensionsUsed++;
        tc.TrialStatus = TrialStatus.Extended;

        sub.EndDate = tc.TrialEndDate;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialExtended",
            Notes = $"Extended by {additionalDays} days (extension {tc.ExtensionsUsed}/{tc.MaxExtensions})",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetTrialStatusAsync(companyId);
    }

    public async Task ExpireTrialAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.TrialConfig != null)
            sub.TrialConfig.TrialStatus = TrialStatus.Expired;

        sub.Status = SubscriptionStatus.Expired;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialExpired",
            FromStatus = SubscriptionStatus.Trial,
            ToStatus = SubscriptionStatus.Expired,
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
    }

    // ==================== Subscription Management ====================

    public async Task<SubscriptionResponse> ConvertTrialAsync(Guid companyId, ConvertTrialRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.Status != SubscriptionStatus.Trial && sub.Status != SubscriptionStatus.Expired)
            throw new InvalidOperationException("ไม่สามารถแปลง subscription ที่ไม่ใช่ trial ได้");

        // Get plan template
        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.Plan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {request.Plan}");

        // Calculate price
        var price = request.BillingCycle switch
        {
            BillingCycle.Monthly => template.MonthlyPrice,
            BillingCycle.Quarterly => template.QuarterlyPrice,
            BillingCycle.SemiAnnual => template.SemiAnnualPrice,
            BillingCycle.Annual => template.AnnualPrice,
            _ => template.MonthlyPrice
        };

        // Apply promo code discount
        if (!string.IsNullOrEmpty(request.PromoCode) && sub.TrialConfig != null)
        {
            if (sub.TrialConfig.ConversionPromoCode == request.PromoCode
                && sub.TrialConfig.DiscountPercentOnConversion.HasValue
                && (sub.TrialConfig.DiscountValidUntil == null || sub.TrialConfig.DiscountValidUntil > DateTime.UtcNow))
            {
                price *= (1 - sub.TrialConfig.DiscountPercentOnConversion.Value / 100);
            }
        }

        var oldPlan = sub.Plan;
        var now = DateTime.UtcNow;
        var months = (int)request.BillingCycle;

        sub.Plan = request.Plan;
        sub.Status = SubscriptionStatus.Active;
        sub.BillingCycle = request.BillingCycle;
        sub.PricePerCycle = price;
        sub.StartDate = now;
        sub.EndDate = now.AddMonths(months);
        sub.NextBillingDate = now.AddMonths(months);
        sub.EnabledFeatures = template.EnabledFeatures;
        sub.MaxUsers = template.MaxUsers;
        sub.MaxCompanies = template.MaxCompanies;
        sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
        sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
        sub.MaxStorageBytes = template.MaxStorageBytes;
        // Bring per-engine OCR quotas across when subscriber upgrades to paid.
        sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
        sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
        sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
        sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;

        if (sub.TrialConfig != null)
            sub.TrialConfig.TrialStatus = TrialStatus.Converted;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialConverted",
            FromPlan = oldPlan,
            ToPlan = request.Plan,
            FromStatus = SubscriptionStatus.Trial,
            ToStatus = SubscriptionStatus.Active,
            Notes = $"Converted to {request.Plan} ({request.BillingCycle}) at {price:N2} THB",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetSubscriptionAsync(companyId);
    }

    public async Task<SubscriptionResponse> GetSubscriptionAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);

        // Auto-create a default Free Edition (permanent free) if missing (e.g. legacy
        // company created before signup auto-subscription was added). Avoids 404 cascading
        // errors on settings/dashboard.
        if (sub == null)
        {
            try
            {
                await StartTrialAsync(
                    new Models.DTOs.Subscription.StartTrialRequest(companyId, SubscriptionPlan.FreeTrial),
                    "auto");
                sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
            }
            catch
            {
                // If auto-start fails (e.g. plan templates missing), throw the original 404
            }
            if (sub == null) throw new KeyNotFoundException("ไม่พบ subscription");
        }

        // Resolver overlay: when this company rides under a User License, the
        // License's features/limits/status win. Without this overlay the
        // SubscriptionMiddleware sees the stale per-company FreeTrial features
        // and blocks paid routes even after admin attached the company to an
        // Enterprise License. Mirrors the source-of-truth picked by
        // GetEffectivePlanAsync but stays inline so we don't double-query.
        var plan = sub.Plan;
        var status = sub.Status;
        var features = sub.EnabledFeatures;
        var endDate = sub.EndDate;
        var maxUsers = sub.MaxUsers;
        var maxCompanies = sub.MaxCompanies;
        var maxDocs = sub.MaxDocumentsPerMonth;
        var maxJournals = sub.MaxJournalEntriesPerMonth;
        var maxStorage = sub.MaxStorageBytes;

        if (sub.AccountSubscriptionId.HasValue)
        {
            var acct = await _db.AccountSubscriptions
                .Include(a => a.PlanTemplate)
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct != null)
            {
                plan = acct.PlanTemplate.Plan;
                status = acct.Status;
                features = acct.EnabledFeatures;
                endDate = acct.EndDate;
                maxUsers = acct.MaxUsersPerCompany;
                maxCompanies = acct.MaxCompanies;
                maxDocs = acct.MaxDocumentsPerMonth;
                maxJournals = acct.MaxJournalEntriesPerMonth;
                maxStorage = acct.MaxStorageBytes;
            }
        }

        // Owner-level subtractive override — when the Owner has flipped
        // off features in CompanySettings.OwnerDisabledFeatures, mask
        // them out so the frontend sees only what the Owner has chosen
        // to expose. SystemAdmin's Subscription assignment is still
        // the upper bound; this is opt-out only.
        var ownerDisabled = await _db.Set<CompanySettings>()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (FeatureFlags?)s.OwnerDisabledFeatures)
            .FirstOrDefaultAsync() ?? FeatureFlags.None;
        if (ownerDisabled != FeatureFlags.None)
            features = features & ~ownerDisabled;

        return new SubscriptionResponse(
            sub.Id, sub.CompanyId, plan, status, sub.BillingCycle,
            sub.PricePerCycle, sub.StartDate, endDate, sub.NextBillingDate,
            features,
            FeatureFlagsHelper.ToNameList(features),
            new UsageLimits(maxUsers, maxCompanies, maxDocs, maxJournals, maxStorage),
            new UsageCurrent(sub.CurrentMonthDocuments, sub.CurrentMonthJournalEntries, sub.CurrentStorageUsed),
            sub.IsPermanentFree);
    }

    public async Task<SubscriptionResponse> ChangeSubscriptionAsync(Guid companyId, ChangeSubscriptionRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.Status != SubscriptionStatus.Active)
            throw new InvalidOperationException("สามารถเปลี่ยน plan ได้เฉพาะ subscription ที่ active เท่านั้น");

        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.NewPlan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {request.NewPlan}");

        var oldPlan = sub.Plan;
        var cycle = request.NewBillingCycle ?? sub.BillingCycle;

        var price = cycle switch
        {
            BillingCycle.Monthly => template.MonthlyPrice,
            BillingCycle.Quarterly => template.QuarterlyPrice,
            BillingCycle.SemiAnnual => template.SemiAnnualPrice,
            BillingCycle.Annual => template.AnnualPrice,
            _ => template.MonthlyPrice
        };

        sub.Plan = request.NewPlan;
        sub.BillingCycle = cycle;
        sub.PricePerCycle = price;
        sub.EnabledFeatures = template.EnabledFeatures;
        sub.MaxUsers = template.MaxUsers;
        sub.MaxCompanies = template.MaxCompanies;
        sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
        sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
        sub.MaxStorageBytes = template.MaxStorageBytes;
        // Bring per-engine OCR quotas across when subscriber upgrades to paid.
        sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
        sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
        sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
        sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
        sub.IsPermanentFree = template.IsPermanentFree;
        if (template.IsPermanentFree)
            sub.EndDate = DateTime.UtcNow.AddYears(100);

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "PlanChanged",
            FromPlan = oldPlan,
            ToPlan = request.NewPlan,
            Notes = $"Changed from {oldPlan} to {request.NewPlan}",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetSubscriptionAsync(companyId);
    }

    public async Task CancelSubscriptionAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        sub.Status = SubscriptionStatus.Cancelled;
        sub.CancelledAt = DateTime.UtcNow;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "Cancelled",
            FromStatus = sub.Status,
            ToStatus = SubscriptionStatus.Cancelled,
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
    }

    // ==================== Usage Tracking ====================

    public async Task<bool> CheckFeatureAccessAsync(Guid companyId, FeatureFlags feature)
    {
        var eff = await GetEffectivePlanAsync(companyId);
        if (eff == null || !eff.IsActive) return false;
        return eff.EnabledFeatures.HasFlag(feature);
    }

    /// <summary>The resolved plan for a Company at quota-check time. Encapsulates
    /// the hybrid model: a Company's own Subscription (per-company plan) wins
    /// when AccountSubscriptionId is null; otherwise the owner's AccountSubscription
    /// (account-level plan) drives limits and features. Grace-period logic is
    /// applied here so callers don't reimplement it.</summary>
    public record EffectivePlan(
        string Source,                    // "Company" or "Account"
        bool IsActive,                    // false = past-expiry past-grace → readonly
        bool InGrace,                     // true = expired but within grace days
        SubscriptionStatus Status,
        DateTime EndDate,
        int MaxUsers,
        int MaxCompanies,
        int MaxDocumentsPerMonth,
        int MaxJournalEntriesPerMonth,
        long MaxStorageBytes,
        int MaxOcrPagesPerMonth,
        int? AzureOcrPagesPerMonth,
        int? LocalOcrPagesPerMonth,
        FeatureFlags EnabledFeatures);

    public async Task<EffectivePlan?> GetEffectivePlanAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return null;

        // Per-company plan wins when AccountSubscriptionId is null (legacy /
        // explicit "this company pays its own bill" mode).
        if (!sub.AccountSubscriptionId.HasValue)
        {
            return BuildFromCompanySub(sub);
        }

        // Account-plan path. If the account row is missing / deleted, fall back
        // to the per-company row so we never lock the user out by accident.
        var acct = await _db.AccountSubscriptions
            .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
        if (acct == null)
        {
            // Defensive: detach the dangling pointer so the next check is clean.
            sub.AccountSubscriptionId = null;
            await _db.SaveChangesAsync();
            return BuildFromCompanySub(sub);
        }

        var now = DateTime.UtcNow;
        var inGrace = acct.Status == SubscriptionStatus.Expired
                      && now <= acct.EndDate.AddDays(acct.GracePeriodDays);
        var active = (acct.Status == SubscriptionStatus.Trial
                      || acct.Status == SubscriptionStatus.Active
                      || acct.Status == SubscriptionStatus.PastDue)
                     && now <= acct.EndDate.AddDays(acct.GracePeriodDays);
        return new EffectivePlan(
            Source: "Account",
            IsActive: active,
            InGrace: inGrace,
            Status: acct.Status,
            EndDate: acct.EndDate,
            MaxUsers: acct.MaxUsersPerCompany,
            MaxCompanies: acct.MaxCompanies,
            MaxDocumentsPerMonth: acct.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth: acct.MaxJournalEntriesPerMonth,
            MaxStorageBytes: acct.MaxStorageBytes,
            MaxOcrPagesPerMonth: acct.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth: acct.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth: acct.LocalOcrPagesPerMonth,
            EnabledFeatures: acct.EnabledFeatures);
    }

    private static EffectivePlan BuildFromCompanySub(Subscription sub)
    {
        var now = DateTime.UtcNow;
        var graceDays = sub.TrialConfig?.GracePeriodDays ?? 7;
        var inGrace = sub.Status == SubscriptionStatus.Expired && now <= sub.EndDate.AddDays(graceDays);
        var active = sub.Status != SubscriptionStatus.Cancelled
                     && sub.Status != SubscriptionStatus.Suspended
                     && (sub.Status != SubscriptionStatus.Expired || inGrace);
        return new EffectivePlan(
            Source: "Company",
            IsActive: active,
            InGrace: inGrace,
            Status: sub.Status,
            EndDate: sub.EndDate,
            MaxUsers: sub.MaxUsers,
            MaxCompanies: sub.MaxCompanies,
            MaxDocumentsPerMonth: sub.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth: sub.MaxJournalEntriesPerMonth,
            MaxStorageBytes: sub.MaxStorageBytes,
            MaxOcrPagesPerMonth: sub.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth: sub.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth: sub.LocalOcrPagesPerMonth,
            EnabledFeatures: sub.EnabledFeatures);
    }

    public async Task<bool> CheckUsageLimitAsync(Guid companyId, string limitType)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return false;

        // Reset per-company monthly usage when the month rolls. Per-company
        // counters stay so the UI can show "Company X used 230 / 1,000" —
        // the AGGREGATE is what we enforce against when on an Account Plan.
        if (DateTime.UtcNow >= sub.UsageResetDate)
        {
            sub.CurrentMonthDocuments = 0;
            sub.CurrentMonthJournalEntries = 0;
            sub.UsageResetDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
            await _db.SaveChangesAsync();
        }

        // Account Plan path — aggregate across every Company under the same
        // AccountSubscription. The accountant case ("1 Pro plan covers 10
        // client books") needs total docs across all 10 to stay under the
        // plan limit, not 10× the limit.
        if (sub.AccountSubscriptionId.HasValue)
        {
            var agg = await GetAggregateUsageAsync(sub.AccountSubscriptionId.Value);
            if (agg == null) return false;
            return limitType switch
            {
                "document" => agg.Documents < agg.MaxDocuments,
                "journal" => agg.JournalEntries < agg.MaxJournalEntries,
                "storage" => agg.StorageBytes < agg.MaxStorageBytes,
                _ => true
            };
        }

        // Per-company plan path — original behavior.
        return limitType switch
        {
            "document" => sub.CurrentMonthDocuments < sub.MaxDocumentsPerMonth,
            "journal" => sub.CurrentMonthJournalEntries < sub.MaxJournalEntriesPerMonth,
            "storage" => sub.CurrentStorageUsed < sub.MaxStorageBytes,
            _ => true
        };
    }

    public async Task<bool> CanFitStorageAsync(Guid companyId, long additionalBytes)
    {
        if (additionalBytes < 0) return true;
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return false;

        long maxBytes;
        List<Guid> poolCompanyIds;
        if (sub.AccountSubscriptionId.HasValue)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct == null)
            {
                // Dangling pointer — fall back to per-company so the user isn't
                // accidentally locked out by a deleted License.
                maxBytes = sub.MaxStorageBytes;
                poolCompanyIds = new List<Guid> { companyId };
            }
            else
            {
                maxBytes = acct.MaxStorageBytes;
                poolCompanyIds = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
                    .Select(s => s.CompanyId)
                    .ToListAsync();
            }
        }
        else
        {
            maxBytes = sub.MaxStorageBytes;
            poolCompanyIds = new List<Guid> { companyId };
        }

        // Accounting-document storage tracked per-company.
        var docBytes = await _db.Subscriptions.AsNoTracking()
            .Where(s => poolCompanyIds.Contains(s.CompanyId) && !s.IsDeleted)
            .SumAsync(s => (long?)s.CurrentStorageUsed) ?? 0L;
        // CMS media storage tracked per-Site. Both pools share the same License
        // budget so a tenant doesn't get to double-spend by routing big files
        // through the CMS instead of the accounting upload path.
        var cmsBytes = await _db.Sites.AsNoTracking()
            .Where(s => poolCompanyIds.Contains(s.CompanyId) && !s.IsDeleted)
            .SumAsync(s => (long?)s.CurrentStorageUsed) ?? 0L;

        return docBytes + cmsBytes + additionalBytes <= maxBytes;
    }

    public record AggregateUsage(
        int Documents, int MaxDocuments,
        int JournalEntries, int MaxJournalEntries,
        long StorageBytes, long MaxStorageBytes,
        int OcrPages, int MaxOcrPages,
        int CompaniesUsed, int MaxCompanies);

    /// <summary>Sum per-month counters across every Company under an
    /// AccountSubscription. Drives the aggregate quota check + the usage
    /// gauge on /account-subscription. Null when the account row is gone.</summary>
    public async Task<AggregateUsage?> GetAggregateUsageAsync(Guid accountSubscriptionId)
    {
        var acct = await _db.AccountSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountSubscriptionId && !a.IsDeleted);
        if (acct == null) return null;

        var subs = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.AccountSubscriptionId == accountSubscriptionId && !s.IsDeleted)
            .Select(s => new {
                s.CurrentMonthDocuments,
                s.CurrentMonthJournalEntries,
                s.CurrentStorageUsed,
                s.CurrentMonthOcrPages,
            })
            .ToListAsync();

        return new AggregateUsage(
            Documents: subs.Sum(s => s.CurrentMonthDocuments),
            MaxDocuments: acct.MaxDocumentsPerMonth,
            JournalEntries: subs.Sum(s => s.CurrentMonthJournalEntries),
            MaxJournalEntries: acct.MaxJournalEntriesPerMonth,
            StorageBytes: subs.Sum(s => s.CurrentStorageUsed),
            MaxStorageBytes: acct.MaxStorageBytes,
            OcrPages: subs.Sum(s => s.CurrentMonthOcrPages),
            MaxOcrPages: acct.MaxOcrPagesPerMonth,
            CompaniesUsed: subs.Count,
            MaxCompanies: acct.MaxCompanies);
    }

    public async Task IncrementUsageAsync(Guid companyId, string usageType)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return;

        switch (usageType)
        {
            case "document":
                sub.CurrentMonthDocuments++;
                break;
            case "journal":
                sub.CurrentMonthJournalEntries++;
                break;
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Admin: Plan Templates ====================

    public async Task<PlanTemplateResponse> CreatePlanTemplateAsync(CreatePlanTemplateRequest request)
    {
        var template = new PlanTemplate
        {
            Name = request.Name,
            Description = request.Description,
            Plan = request.Plan,
            MonthlyPrice = request.MonthlyPrice,
            QuarterlyPrice = request.QuarterlyPrice ?? request.MonthlyPrice * 3 * 0.95m,
            SemiAnnualPrice = request.SemiAnnualPrice ?? request.MonthlyPrice * 6 * 0.9m,
            AnnualPrice = request.AnnualPrice ?? request.MonthlyPrice * 12 * 0.85m,
            MaxUsers = request.MaxUsers,
            MaxCompanies = request.MaxCompanies,
            MaxDocumentsPerMonth = request.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth = request.MaxJournalEntriesPerMonth,
            MaxStorageBytes = request.MaxStorageBytes,
            EnabledFeatures = request.EnabledFeatures,
            TrialDurationDays = request.TrialDurationDays,
            TrialMaxExtensions = request.TrialMaxExtensions,
            TrialExtensionDays = request.TrialExtensionDays,
            TrialFeatures = request.TrialFeatures,
            TrialMaxUsers = request.TrialMaxUsers,
            TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth,
            TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth,
            TrialBlockOnExpiry = request.TrialBlockOnExpiry,
            TrialGracePeriodDays = request.TrialGracePeriodDays,
            IsPermanentFree = request.IsPermanentFree
        };

        _db.PlanTemplates.Add(template);
        await _db.SaveChangesAsync();

        return MapTemplateToResponse(template);
    }

    public async Task<List<PlanTemplateResponse>> GetPlanTemplatesAsync(bool includeInactive = false)
    {
        var query = _db.PlanTemplates.AsQueryable();
        if (!includeInactive)
            query = query.Where(p => p.IsActive);

        var templates = await query.OrderBy(p => p.Plan).ToListAsync();
        return templates.Select(MapTemplateToResponse).ToList();
    }

    public async Task<PlanTemplateResponse> UpdatePlanTemplateAsync(Guid templateId, UpdatePlanTemplateRequest request)
    {
        var template = await _db.PlanTemplates.FindAsync(templateId)
            ?? throw new KeyNotFoundException("ไม่พบ plan template");

        // Track which fields changed so we can propagate to existing subscriptions
        var oldEnabled = template.EnabledFeatures;
        var oldTrial = template.TrialFeatures;
        var oldMaxUsers = template.MaxUsers;
        var oldMaxCompanies = template.MaxCompanies;
        var oldMaxDocs = template.MaxDocumentsPerMonth;
        var oldMaxJournals = template.MaxJournalEntriesPerMonth;
        var oldMaxStorage = template.MaxStorageBytes;
        var oldTrialMaxUsers = template.TrialMaxUsers;
        var oldTrialMaxDocs = template.TrialMaxDocumentsPerMonth;
        var oldTrialMaxJournals = template.TrialMaxJournalEntriesPerMonth;
        var oldGrace = template.TrialGracePeriodDays;
        var oldBlock = template.TrialBlockOnExpiry;
        // OCR quota snapshot — needed so a template-side change propagates
        // to every existing subscription on this plan (so the user-facing
        // quota banner immediately reflects "5000 หน้า/เดือน" instead of
        // still showing the old 10 they got when they were on Trial).
        var oldMaxOcr = template.MaxOcrPagesPerMonth;
        var oldAzureOcr = template.AzureOcrPagesPerMonth;
        var oldLocalOcr = template.LocalOcrPagesPerMonth;
        var oldFallback = template.FallbackToLocalWhenAzureExhausted;
        var oldTrialMaxOcr = template.TrialMaxOcrPagesPerMonth;

        if (request.Name != null) template.Name = request.Name;
        if (request.Description != null) template.Description = request.Description;
        if (request.MonthlyPrice.HasValue) template.MonthlyPrice = request.MonthlyPrice.Value;
        if (request.QuarterlyPrice.HasValue) template.QuarterlyPrice = request.QuarterlyPrice.Value;
        if (request.SemiAnnualPrice.HasValue) template.SemiAnnualPrice = request.SemiAnnualPrice.Value;
        if (request.AnnualPrice.HasValue) template.AnnualPrice = request.AnnualPrice.Value;
        if (request.MaxUsers.HasValue) template.MaxUsers = request.MaxUsers.Value;
        if (request.MaxCompanies.HasValue) template.MaxCompanies = request.MaxCompanies.Value;
        if (request.MaxDocumentsPerMonth.HasValue) template.MaxDocumentsPerMonth = request.MaxDocumentsPerMonth.Value;
        if (request.MaxJournalEntriesPerMonth.HasValue) template.MaxJournalEntriesPerMonth = request.MaxJournalEntriesPerMonth.Value;
        if (request.MaxStorageBytes.HasValue) template.MaxStorageBytes = request.MaxStorageBytes.Value;

        // Resolve features: prefer NameList over enum value if provided
        if (request.EnabledFeatureNames != null)
            template.EnabledFeatures = FeatureFlagsHelper.FromNameList(request.EnabledFeatureNames);
        else if (request.EnabledFeatures.HasValue)
            template.EnabledFeatures = request.EnabledFeatures.Value;

        if (request.TrialFeatureNames != null)
            template.TrialFeatures = FeatureFlagsHelper.FromNameList(request.TrialFeatureNames);
        else if (request.TrialFeatures.HasValue)
            template.TrialFeatures = request.TrialFeatures.Value;

        if (request.IsActive.HasValue) template.IsActive = request.IsActive.Value;
        if (request.TrialDurationDays.HasValue) template.TrialDurationDays = request.TrialDurationDays.Value;
        if (request.TrialMaxExtensions.HasValue) template.TrialMaxExtensions = request.TrialMaxExtensions.Value;
        if (request.TrialExtensionDays.HasValue) template.TrialExtensionDays = request.TrialExtensionDays.Value;
        if (request.TrialMaxUsers.HasValue) template.TrialMaxUsers = request.TrialMaxUsers.Value;
        if (request.TrialMaxDocumentsPerMonth.HasValue) template.TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
        if (request.TrialMaxJournalEntriesPerMonth.HasValue) template.TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
        if (request.TrialBlockOnExpiry.HasValue) template.TrialBlockOnExpiry = request.TrialBlockOnExpiry.Value;
        if (request.TrialGracePeriodDays.HasValue) template.TrialGracePeriodDays = request.TrialGracePeriodDays.Value;
        if (request.IsPermanentFree.HasValue) template.IsPermanentFree = request.IsPermanentFree.Value;

        // OCR per-engine quotas. Null preserves the existing value so a UI
        // that only sends the field for plans that customize it doesn't
        // accidentally zero out other plans.
        if (request.MaxOcrPagesPerMonth.HasValue) template.MaxOcrPagesPerMonth = request.MaxOcrPagesPerMonth.Value;
        if (request.AzureOcrPagesPerMonth.HasValue) template.AzureOcrPagesPerMonth = request.AzureOcrPagesPerMonth.Value;
        if (request.LocalOcrPagesPerMonth.HasValue) template.LocalOcrPagesPerMonth = request.LocalOcrPagesPerMonth.Value;
        if (request.FallbackToLocalWhenAzureExhausted.HasValue) template.FallbackToLocalWhenAzureExhausted = request.FallbackToLocalWhenAzureExhausted.Value;
        if (request.TrialMaxOcrPagesPerMonth.HasValue) template.TrialMaxOcrPagesPerMonth = request.TrialMaxOcrPagesPerMonth.Value;

        await _db.SaveChangesAsync();

        // Propagate changes (features + limits + trial config) to all subscriptions
        // of this plan so users see updates without re-subscribing.
        var featuresChanged = template.EnabledFeatures != oldEnabled || template.TrialFeatures != oldTrial;
        var paidLimitsChanged = template.MaxUsers != oldMaxUsers
            || template.MaxCompanies != oldMaxCompanies
            || template.MaxDocumentsPerMonth != oldMaxDocs
            || template.MaxJournalEntriesPerMonth != oldMaxJournals
            || template.MaxStorageBytes != oldMaxStorage;
        var trialLimitsChanged = template.TrialMaxUsers != oldTrialMaxUsers
            || template.TrialMaxDocumentsPerMonth != oldTrialMaxDocs
            || template.TrialMaxJournalEntriesPerMonth != oldTrialMaxJournals
            || template.TrialGracePeriodDays != oldGrace
            || template.TrialBlockOnExpiry != oldBlock;
        // OCR-side change flag — split into "paid" and "trial" buckets so
        // we update only the relevant subscriptions per their status.
        var paidOcrChanged = template.MaxOcrPagesPerMonth != oldMaxOcr
            || template.AzureOcrPagesPerMonth != oldAzureOcr
            || template.LocalOcrPagesPerMonth != oldLocalOcr
            || template.FallbackToLocalWhenAzureExhausted != oldFallback;
        var trialOcrChanged = template.TrialMaxOcrPagesPerMonth != oldTrialMaxOcr;

        if (featuresChanged || paidLimitsChanged || trialLimitsChanged
            || paidOcrChanged || trialOcrChanged)
        {
            var subs = await _db.Subscriptions
                .Include(s => s.TrialConfig)
                .Where(s => s.Plan == template.Plan)
                .ToListAsync();
            foreach (var sub in subs)
            {
                if (sub.Status == SubscriptionStatus.Trial)
                {
                    if (featuresChanged) sub.EnabledFeatures = template.TrialFeatures;
                    if (trialLimitsChanged)
                    {
                        sub.MaxUsers = template.TrialMaxUsers;
                        sub.MaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                        sub.MaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                    }
                    // Trial subscriptions use the Trial OCR budget. Without
                    // this propagation an admin who edits the template sees
                    // their user still stuck on the old default of 10.
                    if (trialOcrChanged)
                    {
                        sub.MaxOcrPagesPerMonth = template.TrialMaxOcrPagesPerMonth;
                    }
                    if (sub.TrialConfig != null)
                    {
                        if (featuresChanged) sub.TrialConfig.TrialFeatures = template.TrialFeatures;
                        if (trialLimitsChanged)
                        {
                            sub.TrialConfig.TrialMaxUsers = template.TrialMaxUsers;
                            sub.TrialConfig.TrialMaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                            sub.TrialConfig.TrialMaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                            sub.TrialConfig.GracePeriodDays = template.TrialGracePeriodDays;
                            sub.TrialConfig.BlockAccessOnExpiry = template.TrialBlockOnExpiry;
                        }
                    }
                }
                else
                {
                    if (featuresChanged) sub.EnabledFeatures = template.EnabledFeatures;
                    if (paidLimitsChanged)
                    {
                        sub.MaxUsers = template.MaxUsers;
                        sub.MaxCompanies = template.MaxCompanies;
                        sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
                        sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
                        sub.MaxStorageBytes = template.MaxStorageBytes;
                    }
                    // Paid (Active/Past-due/etc.) subscriptions: bring the
                    // full OCR breakdown across. Total + per-engine + the
                    // fallback toggle — same set CreateSubscriptionAsync
                    // copies in for new subs.
                    if (paidOcrChanged)
                    {
                        sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
                        sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
                        sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
                        sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
                    }
                }

                // Propagate permanent-free flag — if template now is permanent free,
                // sync the subscription so existing customers benefit immediately
                if (sub.IsPermanentFree != template.IsPermanentFree)
                {
                    sub.IsPermanentFree = template.IsPermanentFree;
                    if (template.IsPermanentFree)
                    {
                        sub.Status = SubscriptionStatus.Active;
                        sub.EndDate = DateTime.UtcNow.AddYears(100);
                        sub.EnabledFeatures = template.EnabledFeatures;
                    }
                }
                sub.UpdatedAt = DateTime.UtcNow;
            }
            if (subs.Any()) await _db.SaveChangesAsync();
        }

        return MapTemplateToResponse(template);
    }

    /// <summary>
    /// Force-resync every subscription on the given plan against the
    /// template's current values — used when admin edited the template
    /// but a particular tenant's subscription was missed (e.g. status was
    /// hand-flipped in the DB and never went through UpgradeAsync). Same
    /// branch logic as UpdatePlanTemplateAsync: Trial subs take the Trial
    /// OCR budget; everyone else takes the per-engine paid breakdown.
    /// Returns the number of subscriptions touched.
    /// </summary>
    public async Task<int> ResyncSubscriptionsFromTemplateAsync(Guid templateId)
    {
        var template = await _db.PlanTemplates.FindAsync(templateId)
            ?? throw new KeyNotFoundException("ไม่พบ plan template");

        var subs = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .Where(s => s.Plan == template.Plan && !s.IsDeleted)
            .ToListAsync();

        foreach (var sub in subs)
        {
            if (sub.Status == SubscriptionStatus.Trial)
            {
                sub.EnabledFeatures = template.TrialFeatures;
                sub.MaxUsers = template.TrialMaxUsers;
                sub.MaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                sub.MaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                sub.MaxOcrPagesPerMonth = template.TrialMaxOcrPagesPerMonth;
                if (sub.TrialConfig != null)
                {
                    sub.TrialConfig.TrialFeatures = template.TrialFeatures;
                    sub.TrialConfig.TrialMaxUsers = template.TrialMaxUsers;
                    sub.TrialConfig.TrialMaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                    sub.TrialConfig.TrialMaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                    sub.TrialConfig.GracePeriodDays = template.TrialGracePeriodDays;
                    sub.TrialConfig.BlockAccessOnExpiry = template.TrialBlockOnExpiry;
                }
            }
            else
            {
                sub.EnabledFeatures = template.EnabledFeatures;
                sub.MaxUsers = template.MaxUsers;
                sub.MaxCompanies = template.MaxCompanies;
                sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
                sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
                sub.MaxStorageBytes = template.MaxStorageBytes;
                sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
                sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
                sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
                sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
            }
            sub.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return subs.Count;
    }

    // ==================== Admin: Direct Trial Config Update ====================

    public async Task<TrialStatusResponse> UpdateTrialConfigAsync(Guid companyId, UpdateTrialConfigRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มี trial config");

        if (request.TrialDurationDays.HasValue)
        {
            tc.TrialDurationDays = request.TrialDurationDays.Value;
            tc.TrialEndDate = tc.TrialStartDate.AddDays(request.TrialDurationDays.Value + (tc.ExtensionsUsed * tc.ExtensionDays));
            sub.EndDate = tc.TrialEndDate;
        }
        if (request.AllowExtension.HasValue) tc.AllowExtension = request.AllowExtension.Value;
        if (request.MaxExtensions.HasValue) tc.MaxExtensions = request.MaxExtensions.Value;
        if (request.ExtensionDays.HasValue) tc.ExtensionDays = request.ExtensionDays.Value;
        if (request.TrialFeatures.HasValue)
        {
            tc.TrialFeatures = request.TrialFeatures.Value;
            sub.EnabledFeatures = request.TrialFeatures.Value;
        }
        if (request.TrialMaxUsers.HasValue)
        {
            tc.TrialMaxUsers = request.TrialMaxUsers.Value;
            sub.MaxUsers = request.TrialMaxUsers.Value;
        }
        if (request.TrialMaxDocumentsPerMonth.HasValue)
        {
            tc.TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
            sub.MaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
        }
        if (request.TrialMaxJournalEntriesPerMonth.HasValue)
        {
            tc.TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
            sub.MaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
        }
        if (request.TrialMaxCompanies.HasValue) tc.TrialMaxCompanies = request.TrialMaxCompanies.Value;
        if (request.ShowTrialWatermark.HasValue) tc.ShowTrialWatermark = request.ShowTrialWatermark.Value;
        if (request.TrialMessage != null) tc.TrialMessage = request.TrialMessage;
        if (request.NotifyBeforeExpiry.HasValue) tc.NotifyBeforeExpiry = request.NotifyBeforeExpiry.Value;
        if (request.NotifyDaysBeforeExpiry.HasValue) tc.NotifyDaysBeforeExpiry = request.NotifyDaysBeforeExpiry.Value;
        if (request.BlockAccessOnExpiry.HasValue) tc.BlockAccessOnExpiry = request.BlockAccessOnExpiry.Value;
        if (request.AllowDataExportAfterExpiry.HasValue) tc.AllowDataExportAfterExpiry = request.AllowDataExportAfterExpiry.Value;
        if (request.GracePeriodDays.HasValue) tc.GracePeriodDays = request.GracePeriodDays.Value;
        if (request.DeleteDataAfterGracePeriod.HasValue) tc.DeleteDataAfterGracePeriod = request.DeleteDataAfterGracePeriod.Value;
        if (request.DataRetentionDays.HasValue) tc.DataRetentionDays = request.DataRetentionDays.Value;
        if (request.DiscountPercentOnConversion.HasValue) tc.DiscountPercentOnConversion = request.DiscountPercentOnConversion.Value;
        if (request.DiscountValidUntil.HasValue) tc.DiscountValidUntil = request.DiscountValidUntil.Value;
        if (request.ConversionPromoCode != null) tc.ConversionPromoCode = request.ConversionPromoCode;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialConfigUpdated",
            Notes = "Trial configuration updated by admin",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetTrialStatusAsync(companyId);
    }

    // ==================== Usage Monitor ====================

    public async Task<UsageDetailResponse> GetUsageDetailAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription สำหรับบริษัทนี้");

        // Get company users
        var companyUsers = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId)
            .Join(_db.Users, cu => cu.UserId, u => u.Id, (cu, u) => new UsageUserInfo(
                u.Id, u.FullName, u.Email, cu.Role.ToString(), cu.JoinedAt, u.LastLoginAt))
            .ToListAsync();

        // Get file attachments for storage breakdown
        var files = await _db.FileAttachments
            .Where(f => f.CompanyId == companyId)
            .Select(f => new { f.Id, f.OriginalFileName, f.EntityType, f.FileSize, f.CreatedAt, f.UploadedByUserId })
            .ToListAsync();

        // Storage breakdown by entity type
        var breakdown = files
            .GroupBy(f => f.EntityType ?? "Other")
            .Select(g => new StorageCategoryInfo(
                g.Key,
                GetCategoryLabel(g.Key),
                g.Sum(f => f.FileSize),
                g.Count()))
            .OrderByDescending(c => c.Bytes)
            .ToList();

        // Top 10 largest files
        var uploaderIds = files.Select(f => f.UploadedByUserId).Where(id => id != Guid.Empty).Distinct().ToList();
        var userMap = await _db.Users
            .Where(u => uploaderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var largest = files
            .OrderByDescending(f => f.FileSize)
            .Take(10)
            .Select(f => new StorageFileInfo(
                f.Id, f.OriginalFileName ?? "unknown", f.EntityType ?? "Other",
                f.FileSize, f.CreatedAt,
                f.UploadedByUserId != Guid.Empty && userMap.ContainsKey(f.UploadedByUserId) ? userMap[f.UploadedByUserId] : "-"))
            .ToList();

        // License overlay: when this company rides under a User License the
        // dashboard should show the License's limits (and aggregate Used)
        // not the stale per-company FreeTrial values. Otherwise the user
        // sees scary "1/1 users — upgrade now" warnings on a 100-seat
        // Enterprise account.
        var plan = sub.Plan;
        var status = sub.Status;
        var endDate = sub.EndDate;
        var maxUsers = sub.MaxUsers;
        var maxStorage = sub.MaxStorageBytes;
        var maxDocs = sub.MaxDocumentsPerMonth;
        var maxJournals = sub.MaxJournalEntriesPerMonth;
        var maxOcr = sub.MaxOcrPagesPerMonth;
        var storageUsed = sub.CurrentStorageUsed;
        var docsUsed = sub.CurrentMonthDocuments;
        var journalsUsed = sub.CurrentMonthJournalEntries;
        var ocrUsed = sub.CurrentMonthOcrPages;

        if (sub.AccountSubscriptionId.HasValue)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking()
                .Include(a => a.PlanTemplate)
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct != null)
            {
                plan = acct.PlanTemplate.Plan;
                status = acct.Status;
                endDate = acct.EndDate;
                maxUsers = acct.MaxUsersPerCompany;
                maxStorage = acct.MaxStorageBytes;
                maxDocs = acct.MaxDocumentsPerMonth;
                maxJournals = acct.MaxJournalEntriesPerMonth;
                maxOcr = acct.MaxOcrPagesPerMonth;
                // Used counters are aggregate across every attached company
                // so the dashboard's "X / Y" matches what enforcement sees.
                var agg = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
                    .Select(s => new { s.CurrentStorageUsed, s.CurrentMonthDocuments, s.CurrentMonthJournalEntries, s.CurrentMonthOcrPages })
                    .ToListAsync();
                storageUsed = agg.Sum(x => x.CurrentStorageUsed);
                docsUsed = agg.Sum(x => x.CurrentMonthDocuments);
                journalsUsed = agg.Sum(x => x.CurrentMonthJournalEntries);
                ocrUsed = agg.Sum(x => x.CurrentMonthOcrPages);
            }
        }

        // Build alerts
        var alerts = new List<UsageAlert>();
        var storagePct = maxStorage > 0 ? (double)storageUsed / maxStorage * 100 : 0;
        var docPct = maxDocs > 0 ? (double)docsUsed / maxDocs * 100 : 0;
        var userPct = maxUsers > 0 ? (double)companyUsers.Count / maxUsers * 100 : 0;

        if (storagePct >= 90)
            alerts.Add(new UsageAlert("danger", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว กรุณาเคลียร์ไฟล์หรืออัปเกรดแพ็กเกจ"));
        else if (storagePct >= 70)
            alerts.Add(new UsageAlert("warning", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว"));

        if (docPct >= 90)
            alerts.Add(new UsageAlert("danger", "documents", $"เอกสารเดือนนี้ใช้ไป {docsUsed}/{maxDocs} รายการ"));
        else if (docPct >= 70)
            alerts.Add(new UsageAlert("warning", "documents", $"เอกสารเดือนนี้ใช้ไป {docPct:F0}%"));

        if (userPct >= 100)
            alerts.Add(new UsageAlert("danger", "users", "จำนวนผู้ใช้เต็มแล้ว อัปเกรดแพ็กเกจเพื่อเพิ่มผู้ใช้"));
        else if (companyUsers.Count >= maxUsers - 1 && maxUsers < 999)
            alerts.Add(new UsageAlert("warning", "users", $"เหลือโควต้าผู้ใช้อีก {maxUsers - companyUsers.Count} คน"));

        var daysLeft = (endDate - DateTime.UtcNow).Days;
        if (daysLeft <= 0)
            alerts.Add(new UsageAlert("danger", "subscription", "Subscription หมดอายุแล้ว กรุณาต่ออายุ"));
        else if (daysLeft <= 7)
            alerts.Add(new UsageAlert("warning", "subscription", $"Subscription จะหมดอายุใน {daysLeft} วัน"));

        var ocrPct = maxOcr > 0 ? (double)ocrUsed / maxOcr * 100 : 0;
        if (ocrPct >= 90)
            alerts.Add(new UsageAlert("danger", "ocr", $"โควต้า OCR ใช้ไป {ocrUsed}/{maxOcr} หน้า"));
        else if (ocrPct >= 70)
            alerts.Add(new UsageAlert("warning", "ocr", $"โควต้า OCR ใช้ไป {ocrPct:F0}%"));

        var ocrCredits = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                && p.PagesRemaining > 0
                && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
            .SumAsync(p => p.PagesRemaining);

        return new UsageDetailResponse(
            plan, status, endDate,
            companyUsers.Count, maxUsers, companyUsers,
            storageUsed, maxStorage, breakdown, largest,
            docsUsed, maxDocs,
            journalsUsed, maxJournals,
            sub.UsageResetDate,
            OcrPagesThisMonth: ocrUsed,
            MaxOcrPagesPerMonth: maxOcr,
            OcrBonusPages: sub.OcrBonusPages,
            OcrCreditPagesRemaining: ocrCredits,
            Alerts: alerts);
    }

    private static string GetCategoryLabel(string entityType)
    {
        return entityType switch
        {
            "Document" => "เอกสาร",
            "JournalEntry" => "รายการบัญชี",
            "Contact" => "ผู้ติดต่อ",
            "Product" => "สินค้า",
            "ExpenseClaim" => "เบิกค่าใช้จ่าย",
            "FixedAsset" => "สินทรัพย์ถาวร",
            "PayrollSlip" => "สลิปเงินเดือน",
            "Logo" => "โลโก้",
            _ => entityType
        };
    }

    // ==================== Background: Process Expired Trials ====================

    public async Task ProcessExpiredTrialsAsync()
    {
        var now = DateTime.UtcNow;
        var expiredTrials = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .Where(s => s.Status == SubscriptionStatus.Trial && s.EndDate < now && !s.IsPermanentFree)
            .ToListAsync();

        foreach (var sub in expiredTrials)
        {
            sub.Status = SubscriptionStatus.Expired;
            if (sub.TrialConfig != null)
                sub.TrialConfig.TrialStatus = TrialStatus.Expired;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "TrialExpired",
                FromStatus = SubscriptionStatus.Trial,
                ToStatus = SubscriptionStatus.Expired,
                Notes = "Auto-expired by system",
                PerformedBy = "System"
            });
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Subscription Notification Settings ====================

    public async Task<SubscriptionNotificationSettingsResponse> GetNotificationSettingsAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        return MapNotificationSettings(sub);
    }

    public async Task<SubscriptionNotificationSettingsResponse> UpdateNotificationSettingsAsync(
        Guid companyId, UpdateSubscriptionNotificationRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (request.NotifyBeforeExpiry.HasValue) sub.NotifyBeforeExpiry = request.NotifyBeforeExpiry.Value;
        if (request.NotifyDaysBeforeExpiry != null) sub.NotifyDaysBeforeExpiry = request.NotifyDaysBeforeExpiry;
        if (request.NotifyOnExpiry.HasValue) sub.NotifyOnExpiry = request.NotifyOnExpiry.Value;
        if (request.NotifyAfterExpiry.HasValue) sub.NotifyAfterExpiry = request.NotifyAfterExpiry.Value;
        if (request.NotifyDaysAfterExpiry != null) sub.NotifyDaysAfterExpiry = request.NotifyDaysAfterExpiry;
        if (request.DeactivationDaysAfterExpiry.HasValue) sub.DeactivationDaysAfterExpiry = request.DeactivationDaysAfterExpiry.Value;
        if (request.NotifyBeforeDeactivation.HasValue) sub.NotifyBeforeDeactivation = request.NotifyBeforeDeactivation.Value;
        if (request.NotifyDaysBeforeDeactivation != null) sub.NotifyDaysBeforeDeactivation = request.NotifyDaysBeforeDeactivation;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "NotificationSettingsUpdated",
            Notes = "Subscription notification settings updated",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return MapNotificationSettings(sub);
    }

    // ==================== Subscription Payment ====================

    public async Task<SubscriptionPaymentResponse> SubmitPaymentAsync(
        Guid companyId, SubmitSubscriptionPaymentRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        // Generate payment number
        var count = await _db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id);
        var paymentNumber = $"SP-{sub.CompanyId.ToString()[..8].ToUpper()}-{count + 1:D4}";

        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = paymentNumber,
            Amount = request.Amount,
            PaymentDate = request.PaymentDate,
            PaymentMethod = request.PaymentMethod,
            FromBankName = request.FromBankName,
            FromAccountNumber = request.FromAccountNumber,
            ToBankName = request.ToBankName,
            ToAccountNumber = request.ToAccountNumber,
            TransferReference = request.TransferReference,
            RequestedPlan = request.RequestedPlan,
            RequestedBillingCycle = request.RequestedBillingCycle,
            RequestedPeriodMonths = request.RequestedPeriodMonths,
            CustomerNotes = request.CustomerNotes,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy
        };

        _db.SubscriptionPayments.Add(payment);

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "PaymentSubmitted",
            Notes = $"Payment {paymentNumber} submitted: {request.Amount:N2} THB",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        // Notify admins about pending payment
        var adminUsers = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId && (cu.Role == UserRole.Owner || cu.Role == UserRole.SystemAdmin))
            .Select(cu => cu.UserId)
            .ToListAsync();

        foreach (var adminId in adminUsers)
        {
            await NotifyUserAsync(adminId, companyId,
                Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentPending,
                "มีการชำระเงิน Subscription รอตรวจสอบ",
                $"การชำระเงิน {paymentNumber} จำนวน {request.Amount:N2} THB รอการตรวจสอบ",
                $"/admin/subscription-payments/{payment.Id}",
                "SubscriptionPayment", payment.Id);
        }

        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentResponse> UploadPaymentSlipAsync(
        Guid paymentId, string fileName, string originalFileName,
        string contentType, long fileSize, string storagePath, string performedBy)
    {
        var payment = await _db.SubscriptionPayments.FindAsync(paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        if (payment.Status != SubscriptionPaymentStatus.Pending)
            throw new InvalidOperationException("ไม่สามารถอัพโหลดสลิปได้ เนื่องจากสถานะไม่ใช่รอตรวจสอบ");

        payment.SlipFileName = fileName;
        payment.SlipOriginalFileName = originalFileName;
        payment.SlipContentType = contentType;
        payment.SlipFileSize = fileSize;
        payment.SlipStoragePath = storagePath;

        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentListResponse> GetPaymentsAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var payments = await _db.SubscriptionPayments
            .Include(p => p.ReviewedByUser)
            .Where(p => p.SubscriptionId == sub.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return new SubscriptionPaymentListResponse(
            payments.Select(MapPaymentToResponse).ToList(),
            payments.Count,
            payments.Count(p => p.Status == SubscriptionPaymentStatus.Pending));
    }

    public async Task<SubscriptionPaymentResponse> GetPaymentAsync(Guid paymentId)
    {
        var payment = await _db.SubscriptionPayments
            .Include(p => p.ReviewedByUser)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentResponse> ReviewPaymentAsync(
        Guid paymentId, ReviewSubscriptionPaymentRequest request, string performedBy)
    {
        var payment = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        if (payment.Status != SubscriptionPaymentStatus.Pending && payment.Status != SubscriptionPaymentStatus.UnderReview)
            throw new InvalidOperationException("ไม่สามารถตรวจสอบรายการนี้ได้ เนื่องจากสถานะไม่ใช่รอตรวจสอบ");

        if (!Guid.TryParse(performedBy, out var reviewerUserId))
            throw new InvalidOperationException("ไม่สามารถระบุผู้ตรวจสอบได้");

        payment.ReviewedByUserId = reviewerUserId;
        payment.ReviewedAt = DateTime.UtcNow;
        payment.ReviewNotes = request.ReviewNotes;

        if (request.Approve)
        {
            payment.Status = SubscriptionPaymentStatus.Approved;

            // Extend/renew subscription
            var sub = payment.Subscription;
            var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == payment.RequestedPlan && p.IsActive);

            var now = DateTime.UtcNow;
            var baseDate = sub.EndDate > now ? sub.EndDate : now; // ต่อจากวันหมดอายุเดิม หรือจากวันนี้
            var newEndDate = baseDate.AddMonths(payment.RequestedPeriodMonths);

            sub.Plan = payment.RequestedPlan;
            sub.Status = SubscriptionStatus.Active;
            sub.BillingCycle = payment.RequestedBillingCycle;
            sub.EndDate = newEndDate;
            sub.NextBillingDate = newEndDate;
            sub.LastPaymentDate = payment.PaymentDate;
            sub.PaymentReference = payment.PaymentNumber;

            if (template != null)
            {
                sub.EnabledFeatures = template.EnabledFeatures;
                sub.MaxUsers = template.MaxUsers;
                sub.MaxCompanies = template.MaxCompanies;
                sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
                sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
                sub.MaxStorageBytes = template.MaxStorageBytes;
                // OCR quotas — same upgrade-to-paid propagation as UpgradeAsync
                sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
                sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
                sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
                sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;

                var price = payment.RequestedBillingCycle switch
                {
                    BillingCycle.Monthly => template.MonthlyPrice,
                    BillingCycle.Quarterly => template.QuarterlyPrice,
                    BillingCycle.SemiAnnual => template.SemiAnnualPrice,
                    BillingCycle.Annual => template.AnnualPrice,
                    _ => template.MonthlyPrice
                };
                sub.PricePerCycle = price;
            }

            payment.SubscriptionExtendedTo = newEndDate;

            // Cascade renewal to the AccountSubscription if this Company is
            // covered by one. Without this the user's License (the layer above
            // company-scoped Subscription) would expire even though they
            // already paid. Sync EndDate + LastPaidAt + Status; quotas stay
            // whatever the admin / sales set on the AccountSubscription so a
            // negotiated enterprise limit isn't overwritten by a slip approval.
            if (sub.AccountSubscriptionId.HasValue)
            {
                var acct = await _db.AccountSubscriptions
                    .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
                if (acct != null)
                {
                    var acctBase = acct.EndDate > now ? acct.EndDate : now;
                    var acctNewEnd = acctBase.AddMonths(payment.RequestedPeriodMonths);
                    if (acctNewEnd > acct.EndDate)
                    {
                        acct.EndDate = acctNewEnd;
                        acct.LastPaidAt = payment.PaymentDate;
                        // Bring the parent plan out of expiry / past-due so all
                        // companies under it light up immediately. Trial → Active
                        // is intentional: paying mid-trial converts to paid.
                        if (acct.Status == SubscriptionStatus.Expired
                            || acct.Status == SubscriptionStatus.PastDue
                            || acct.Status == SubscriptionStatus.Trial)
                            acct.Status = SubscriptionStatus.Active;
                        acct.UpdatedBy = performedBy;
                        acct.UpdatedAt = DateTime.UtcNow;
                        // Reset reminder mask now that EndDate moved forward —
                        // next expiry cycle starts fresh, otherwise we'd skip
                        // the 7-day warning the second time around.
                        acct.ExpiryRemindersSentMask = 0;
                        acct.LastExpiryReminderAt = null;

                        // History row scoped to the account-level extension —
                        // SubscriptionId still points at the company subscription
                        // that triggered the cascade so admin can trace cause.
                        _db.SubscriptionHistories.Add(new SubscriptionHistory
                        {
                            SubscriptionId = sub.Id,
                            AccountSubscriptionId = acct.Id,
                            Action = "AccountPlanExtendedViaSlip",
                            ToStatus = acct.Status,
                            Notes = $"Account Plan EndDate extended to {acctNewEnd:yyyy-MM-dd} via slip {payment.PaymentNumber} (company {sub.CompanyId})",
                            PerformedBy = performedBy
                        });
                    }
                }
            }

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "PaymentApproved",
                ToPlan = payment.RequestedPlan,
                ToStatus = SubscriptionStatus.Active,
                Notes = $"Payment {payment.PaymentNumber} approved. Subscription extended to {newEndDate:yyyy-MM-dd}",
                PerformedBy = performedBy
            });

            // Notify customer
            var ownerUserId = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId)
                .FirstOrDefaultAsync();

            if (ownerUserId != Guid.Empty)
            {
                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                    NotificationType.SubscriptionPaymentApproved,
                    "การชำระเงินได้รับการอนุมัติ",
                    $"การชำระเงิน {payment.PaymentNumber} ได้รับการอนุมัติ Subscription ต่ออายุถึง {newEndDate:dd/MM/yyyy}",
                    $"/subscription",
                    "SubscriptionPayment", payment.Id);

                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                    NotificationType.SubscriptionRenewed,
                    "ต่ออายุ Subscription สำเร็จ",
                    $"Subscription plan {payment.RequestedPlan} ต่ออายุถึง {newEndDate:dd/MM/yyyy}",
                    $"/subscription",
                    "Subscription", sub.Id);
            }
        }
        else
        {
            payment.Status = SubscriptionPaymentStatus.Rejected;
            payment.RejectionReason = request.RejectionReason;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = payment.SubscriptionId,
                Action = "PaymentRejected",
                Notes = $"Payment {payment.PaymentNumber} rejected: {request.RejectionReason}",
                PerformedBy = performedBy
            });

            // Notify customer
            var sub = payment.Subscription;
            var ownerUserId = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId)
                .FirstOrDefaultAsync();

            if (ownerUserId != Guid.Empty)
            {
                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentFailed,
                    NotificationType.SubscriptionPaymentRejected,
                    "การชำระเงินถูกปฏิเสธ",
                    $"การชำระเงิน {payment.PaymentNumber} ถูกปฏิเสธ: {request.RejectionReason}",
                    $"/subscription/payments/{payment.Id}",
                    "SubscriptionPayment", payment.Id);
            }
        }

        await _db.SaveChangesAsync();

        // WP-B2: อนุมัติแล้ว → ออกใบเสร็จ/ใบกำกับค่าบริการ (best-effort, ไม่ block approval)
        if (payment.Status == SubscriptionPaymentStatus.Approved && _billing != null)
            await _billing.GenerateReceiptForApprovedPaymentAsync(payment.Id);

        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentResponse> RecordManualPaymentAsync(
        Guid companyId, RecordManualPaymentRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (request.RequestedPeriodMonths <= 0)
            throw new InvalidOperationException("จำนวนเดือนที่ต่ออายุต้องมากกว่า 0");

        // Waived = ต่ออายุให้ฟรี → บังคับเหตุผล + ยอดต้องเป็น 0 (กัน admin แอบตั้งยอด)
        if (request.IsWaived)
        {
            if (request.WaiveReason == null)
                throw new InvalidOperationException("การยกเว้นค่าบริการต้องระบุเหตุผล (Goodwill/Compensation/Correction)");
        }
        else if (request.Amount <= 0)
        {
            throw new InvalidOperationException("ยอดรับเงินต้องมากกว่า 0 (หรือเลือกยกเว้นค่าบริการ)");
        }

        var count = await _db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id);
        var paymentNumber = $"SP-{sub.CompanyId.ToString()[..8].ToUpper()}-{count + 1:D4}";

        var kindLabel = request.IsWaived ? "ยกเว้นค่าบริการ" : "บันทึกรับเงินโดย admin";
        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = paymentNumber,
            Amount = request.IsWaived ? 0m : request.Amount,
            PaymentDate = request.PaymentDate,
            PaymentMethod = request.PaymentMethod,
            TransferReference = request.TransferReference,
            RequestedPlan = request.RequestedPlan,
            RequestedBillingCycle = request.RequestedBillingCycle,
            RequestedPeriodMonths = request.RequestedPeriodMonths,
            Kind = request.IsWaived ? SubscriptionPaymentKind.Waived : SubscriptionPaymentKind.ManualByAdmin,
            WaiveReason = request.IsWaived ? request.WaiveReason : null,
            CustomerNotes = request.Notes,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy
        };
        _db.SubscriptionPayments.Add(payment);

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = request.IsWaived ? "PaymentWaivedRecorded" : "PaymentManualRecorded",
            Notes = $"{kindLabel} {paymentNumber}: {payment.Amount:N2} THB"
                  + (request.IsWaived ? $" (เหตุผล: {request.WaiveReason})" : ""),
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        // วิ่งเข้าเส้น approve เดิม → ต่ออายุ + cascade License + ประวัติ + (WP-B2) ใบเสร็จ
        return await ReviewPaymentAsync(payment.Id,
            new ReviewSubscriptionPaymentRequest(true,
                $"{kindLabel}" + (string.IsNullOrWhiteSpace(request.Notes) ? "" : $" — {request.Notes}"),
                null),
            performedBy);
    }

    public async Task<SubscriptionPaymentListResponse> GetAllPendingPaymentsAsync()
    {
        var payments = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .Include(p => p.ReviewedByUser)
            .Where(p => p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        return new SubscriptionPaymentListResponse(
            payments.Select(MapPaymentToResponse).ToList(),
            payments.Count,
            payments.Count(p => p.Status == SubscriptionPaymentStatus.Pending));
    }

    // ==================== Background: Process Subscription Notifications ====================

    public async Task ProcessSubscriptionNotificationsAsync()
    {
        var now = DateTime.UtcNow;
        var activeSubscriptions = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in activeSubscriptions)
        {
            var daysUntilExpiry = (int)(sub.EndDate - now).TotalDays;
            var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
            var daysUntilDeactivation = (int)(deactivationDate - now).TotalDays;

            // แจ้งเตือนก่อนหมดอายุ
            if (sub.NotifyBeforeExpiry && daysUntilExpiry > 0)
            {
                var notifyDays = ParseDaysList(sub.NotifyDaysBeforeExpiry);
                if (notifyDays.Contains(daysUntilExpiry))
                {
                    await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpiring,
                        $"Subscription จะหมดอายุในอีก {daysUntilExpiry} วัน",
                        $"Subscription plan {sub.Plan} จะหมดอายุในวันที่ {sub.EndDate:dd/MM/yyyy} (อีก {daysUntilExpiry} วัน) กรุณาต่ออายุก่อนหมดอายุ");
                }
            }

            // แจ้งเตือนก่อนตัดบัญชี
            if (sub.NotifyBeforeDeactivation && daysUntilExpiry <= 0 && daysUntilDeactivation > 0)
            {
                var notifyDays = ParseDaysList(sub.NotifyDaysBeforeDeactivation);
                if (notifyDays.Contains(daysUntilDeactivation))
                {
                    await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionDeactivation,
                        $"บัญชีจะถูกระงับในอีก {daysUntilDeactivation} วัน",
                        $"Subscription หมดอายุแล้ว บัญชีจะถูกระงับในวันที่ {deactivationDate:dd/MM/yyyy} (อีก {daysUntilDeactivation} วัน) กรุณาต่ออายุโดยด่วน");
                }
            }
        }

        // แจ้งเตือนวันหมดอายุ
        var todayStart = now.Date;
        var todayEnd = todayStart.AddDays(1);
        var expiringToday = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate >= todayStart && s.EndDate < todayEnd)
            .ToListAsync();

        foreach (var sub in expiringToday)
        {
            if (sub.NotifyOnExpiry)
            {
                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpired,
                    "Subscription หมดอายุวันนี้",
                    $"Subscription plan {sub.Plan} หมดอายุวันนี้ ({sub.EndDate:dd/MM/yyyy}) กรุณาต่ออายุเพื่อใช้งานต่อ");
            }
        }

        // แจ้งเตือนหลังหมดอายุ
        var expiredSubscriptions = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Expired || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in expiredSubscriptions)
        {
            if (!sub.NotifyAfterExpiry) continue;

            var daysSinceExpiry = (int)(now - sub.EndDate).TotalDays;
            var notifyDays = ParseDaysList(sub.NotifyDaysAfterExpiry);
            if (notifyDays.Contains(daysSinceExpiry))
            {
                var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
                var daysUntilDeactivation = (int)(deactivationDate - now).TotalDays;

                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpired,
                    $"Subscription หมดอายุแล้ว {daysSinceExpiry} วัน",
                    $"Subscription plan {sub.Plan} หมดอายุไปแล้ว {daysSinceExpiry} วัน บัญชีจะถูกระงับในอีก {Math.Max(0, daysUntilDeactivation)} วัน");
            }
        }
    }

    public async Task ProcessExpiredSubscriptionsAsync()
    {
        var now = DateTime.UtcNow;

        // Mark active subscriptions as expired
        var newlyExpired = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate < now)
            .ToListAsync();

        foreach (var sub in newlyExpired)
        {
            sub.Status = SubscriptionStatus.PastDue;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "SubscriptionPastDue",
                FromStatus = SubscriptionStatus.Active,
                ToStatus = SubscriptionStatus.PastDue,
                Notes = $"Subscription expired on {sub.EndDate:yyyy-MM-dd}",
                PerformedBy = "System"
            });
        }

        // Deactivate subscriptions past deactivation period
        var pastDue = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in pastDue)
        {
            var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
            if (now >= deactivationDate)
            {
                sub.Status = SubscriptionStatus.Suspended;

                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id,
                    Action = "SubscriptionSuspended",
                    FromStatus = SubscriptionStatus.PastDue,
                    ToStatus = SubscriptionStatus.Suspended,
                    Notes = $"Suspended after {sub.DeactivationDaysAfterExpiry} days past expiry",
                    PerformedBy = "System"
                });

                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionDeactivation,
                    "บัญชีถูกระงับ",
                    $"Subscription plan {sub.Plan} ถูกระงับเนื่องจากไม่ได้ต่ออายุภายในกำหนด กรุณาชำระเงินเพื่อเปิดใช้งานอีกครั้ง");
            }
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Private Helpers ====================

    private async Task SendSubscriptionNotificationToOwner(Subscription sub, NotificationType type, string title, string message)
    {
        var ownerUserId = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();

        if (ownerUserId != Guid.Empty)
        {
            var evt = type switch
            {
                NotificationType.SubscriptionExpiring => Models.Constants.NotificationEvents.SubscriptionExpired,
                NotificationType.SubscriptionRenewed => Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentApproved => Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentRejected => Models.Constants.NotificationEvents.SubscriptionPaymentFailed,
                _ => Models.Constants.NotificationEvents.SubscriptionExpired,
            };
            await NotifyUserAsync(ownerUserId, sub.CompanyId, evt,
                type, title, message, "/subscription", "Subscription", sub.Id);
        }
    }

    private static List<int> ParseDaysList(string daysString)
    {
        if (string.IsNullOrWhiteSpace(daysString)) return new List<int>();
        return daysString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? d : -1)
            .Where(d => d > 0)
            .ToList();
    }

    private static SubscriptionNotificationSettingsResponse MapNotificationSettings(Subscription sub)
    {
        return new SubscriptionNotificationSettingsResponse(
            sub.NotifyBeforeExpiry,
            ParseDaysList(sub.NotifyDaysBeforeExpiry),
            sub.NotifyOnExpiry,
            sub.NotifyAfterExpiry,
            ParseDaysList(sub.NotifyDaysAfterExpiry),
            sub.DeactivationDaysAfterExpiry,
            sub.NotifyBeforeDeactivation,
            ParseDaysList(sub.NotifyDaysBeforeDeactivation));
    }

    private static SubscriptionPaymentResponse MapPaymentToResponse(SubscriptionPayment p)
    {
        return new SubscriptionPaymentResponse(
            p.Id,
            p.SubscriptionId,
            p.PaymentNumber,
            p.Amount,
            p.Currency,
            p.PaymentDate,
            p.PaymentMethod,
            p.FromBankName,
            p.FromAccountNumber,
            p.ToBankName,
            p.ToAccountNumber,
            p.TransferReference,
            p.SlipOriginalFileName,
            p.SlipStoragePath,
            p.RequestedPlan,
            p.RequestedBillingCycle,
            p.RequestedPeriodMonths,
            p.Status,
            p.ReviewedByUser?.FullName,
            p.ReviewedAt,
            p.ReviewNotes,
            p.RejectionReason,
            p.SubscriptionExtendedTo,
            p.CustomerNotes,
            p.CreatedAt,
            p.ReceiptNumber,
            p.ReceiptIsTaxInvoice);
    }

    private static PlanTemplateResponse MapTemplateToResponse(PlanTemplate t)
    {
        return new PlanTemplateResponse(
            t.Id, t.Name, t.Description, t.Plan, t.IsActive,
            t.MonthlyPrice, t.QuarterlyPrice, t.SemiAnnualPrice, t.AnnualPrice,
            t.MaxUsers, t.MaxCompanies, t.MaxDocumentsPerMonth, t.MaxJournalEntriesPerMonth,
            t.MaxStorageBytes, t.EnabledFeatures,
            FeatureFlagsHelper.ToNameList(t.EnabledFeatures),
            t.TrialDurationDays, t.TrialMaxExtensions, t.TrialExtensionDays, t.TrialFeatures,
            FeatureFlagsHelper.ToNameList(t.TrialFeatures),
            t.IsPermanentFree,
            t.MaxOcrPagesPerMonth,
            t.AzureOcrPagesPerMonth,
            t.LocalOcrPagesPerMonth,
            t.FallbackToLocalWhenAzureExhausted,
            t.TrialMaxOcrPagesPerMonth);
    }
}

/// <summary>
/// Helper for converting FeatureFlags bitmask to/from name lists
/// </summary>
public static class FeatureFlagsHelper
{
    private static readonly string[] PresetNames = { "None", "TrialFeatures", "BasicFeatures", "ProFeatures", "EnterpriseFeatures" };

    public static List<string> ToNameList(FeatureFlags flags)
    {
        var result = new List<string>();
        foreach (FeatureFlags v in Enum.GetValues(typeof(FeatureFlags)))
        {
            var name = v.ToString();
            if (PresetNames.Contains(name)) continue;
            var val = (long)v;
            // Single-bit flag only (power of 2)
            if (val > 0 && (val & (val - 1)) == 0 && flags.HasFlag(v))
                result.Add(name);
        }
        return result;
    }

    public static FeatureFlags FromNameList(IEnumerable<string>? names)
    {
        var result = FeatureFlags.None;
        if (names == null) return result;
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (Enum.TryParse<FeatureFlags>(n, true, out var f) && !PresetNames.Contains(f.ToString()))
                result |= f;
        }
        return result;
    }

    public static List<FeatureFlagInfo> AllFeatures()
    {
        var result = new List<FeatureFlagInfo>();
        foreach (FeatureFlags v in Enum.GetValues(typeof(FeatureFlags)))
        {
            var name = v.ToString();
            if (PresetNames.Contains(name)) continue;
            var val = (long)v;
            if (val > 0 && (val & (val - 1)) == 0)
                result.Add(new FeatureFlagInfo(name, FeatureCategory(name), FeatureLabelTh(name)));
        }
        return result;
    }

    private static string FeatureCategory(string name) => name switch
    {
        "BasicAccounting" or "DocumentEngine" or "TaxManagement" or "Dashboard" or "CustomChartOfAccounts" or "AutoPosting" => "core",
        "AdvancedReporting" or "AgingReport" or "ReportBuilder" or "FPA" or "BudgetManagement" => "reporting",
        "MultiCompany" or "MultiUser" or "MultiCurrency" or "Consolidation" or "Intercompany" => "multi",
        "Inventory" or "WarehouseManagement" or "FixedAssets" or "BankReconciliation" or "ExpenseManagement" or "PurchaseOrders" or "RecurringTransactions" or "CostCenter" or "ProjectAccounting" or "Payroll" or "Commission" or "TimeBilling" or "LoanManagement" or "RevenueRecognition" => "operations",
        "WorkflowEngine" or "ApprovalWorkflow" or "AuditLog" or "EtaxInvoice" or "EtaxByEmail" or "EtaxDirect" or "OpenBanking" or "Webhook" => "advanced",
        "APIAccess" or "BulkImport" or "EmailNotification" or "FileAttachments" or "CustomerPortal" => "integration",
        "AI_Features" or "DocumentOCR" => "ai",
        _ => "other"
    };

    private static string FeatureLabelTh(string name) => name switch
    {
        "BasicAccounting" => "บัญชีพื้นฐาน",
        "AdvancedReporting" => "รายงานขั้นสูง",
        "TaxManagement" => "จัดการภาษี",
        "DocumentEngine" => "ระบบเอกสาร",
        "MultiCompany" => "หลายบริษัท",
        "APIAccess" => "เปิดใช้ API",
        "BulkImport" => "นำเข้าจำนวนมาก",
        "CustomChartOfAccounts" => "ผังบัญชีแบบกำหนดเอง",
        "AutoPosting" => "ลงบัญชีอัตโนมัติ",
        "EtaxInvoice" => "e-Tax Invoice",
        "EtaxByEmail" => "e-Tax by Email (RD เก็บเวลาประทับ)",
        "EtaxDirect" => "e-Tax Direct (ยิง XML เข้า RD API)",
        "WorkflowEngine" => "Workflow Engine",
        "AuditLog" => "บันทึกประวัติการใช้งาน",
        "EmailNotification" => "แจ้งเตือนทางอีเมล",
        "MultiUser" => "ผู้ใช้หลายคน",
        "BankReconciliation" => "กระทบยอดธนาคาร",
        "Inventory" => "สินค้าคงคลัง",
        "FixedAssets" => "สินทรัพย์ถาวร",
        "RecurringTransactions" => "รายการอัตโนมัติ",
        "MultiCurrency" => "หลายสกุลเงิน",
        "ApprovalWorkflow" => "ระบบอนุมัติ",
        "FileAttachments" => "แนบไฟล์",
        "PurchaseOrders" => "ใบสั่งซื้อ",
        "ExpenseManagement" => "จัดการค่าใช้จ่าย",
        "Dashboard" => "แดชบอร์ด",
        "BudgetManagement" => "จัดการงบประมาณ",
        "AgingReport" => "รายงาน Aging",
        "Payroll" => "เงินเดือน",
        "ProjectAccounting" => "บัญชีโครงการ",
        "CostCenter" => "ศูนย์ต้นทุน",
        "Consolidation" => "รวมงบบริษัทในกลุ่ม",
        "WarehouseManagement" => "จัดการคลังสินค้า",
        "LoanManagement" => "จัดการสินเชื่อ",
        "Commission" => "ระบบค่าคอมมิชชั่น",
        "AI_Features" => "ฟีเจอร์ AI",
        "DocumentOCR" => "OCR เอกสาร",
        "ReportBuilder" => "สร้างรายงานเอง",
        "CustomerPortal" => "พอร์ทัลลูกค้า",
        "TimeBilling" => "บันทึกชั่วโมงทำงาน",
        "OpenBanking" => "Open Banking",
        "Webhook" => "Webhook",
        "RevenueRecognition" => "รับรู้รายได้",
        "FPA" => "วางแผน/วิเคราะห์การเงิน",
        _ => name
    };
}

public record FeatureFlagInfo(string Name, string Category, string LabelTh);
