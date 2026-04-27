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

    public SubscriptionService(AccountingDbContext db, INotificationService notificationService)
    {
        _db = db;
        _notificationService = notificationService;
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

        var trialDays = request.TrialDays ?? template?.TrialDurationDays ?? 30;
        var now = DateTime.UtcNow;

        var subscription = new Subscription
        {
            CompanyId = request.CompanyId,
            Plan = request.TargetPlan,
            Status = SubscriptionStatus.Trial,
            StartDate = now,
            EndDate = now.AddDays(trialDays),
            EnabledFeatures = template?.TrialFeatures ?? FeatureFlags.TrialFeatures,
            MaxUsers = template?.TrialMaxUsers ?? 2,
            MaxCompanies = 1,
            MaxDocumentsPerMonth = template?.TrialMaxDocumentsPerMonth ?? 20,
            MaxJournalEntriesPerMonth = template?.TrialMaxJournalEntriesPerMonth ?? 50,
            MaxStorageBytes = 50 * 1024 * 1024,
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
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        return new SubscriptionResponse(
            sub.Id, sub.CompanyId, sub.Plan, sub.Status, sub.BillingCycle,
            sub.PricePerCycle, sub.StartDate, sub.EndDate, sub.NextBillingDate,
            sub.EnabledFeatures,
            FeatureFlagsHelper.ToNameList(sub.EnabledFeatures),
            new UsageLimits(sub.MaxUsers, sub.MaxCompanies, sub.MaxDocumentsPerMonth, sub.MaxJournalEntriesPerMonth, sub.MaxStorageBytes),
            new UsageCurrent(sub.CurrentMonthDocuments, sub.CurrentMonthJournalEntries, sub.CurrentStorageUsed));
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
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);

        if (sub == null) return false;

        // Check subscription status
        if (sub.Status == SubscriptionStatus.Cancelled || sub.Status == SubscriptionStatus.Suspended)
            return false;

        if (sub.Status == SubscriptionStatus.Expired)
        {
            // In grace period?
            if (sub.TrialConfig != null)
            {
                var graceEnd = sub.EndDate.AddDays(sub.TrialConfig.GracePeriodDays);
                if (DateTime.UtcNow > graceEnd)
                    return sub.TrialConfig.BlockAccessOnExpiry ? false : feature == FeatureFlags.BasicAccounting;
            }
            return false;
        }

        return sub.EnabledFeatures.HasFlag(feature);
    }

    public async Task<bool> CheckUsageLimitAsync(Guid companyId, string limitType)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return false;

        // Reset monthly usage if needed
        if (DateTime.UtcNow >= sub.UsageResetDate)
        {
            sub.CurrentMonthDocuments = 0;
            sub.CurrentMonthJournalEntries = 0;
            sub.UsageResetDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);
            await _db.SaveChangesAsync();
        }

        return limitType switch
        {
            "document" => sub.CurrentMonthDocuments < sub.MaxDocumentsPerMonth,
            "journal" => sub.CurrentMonthJournalEntries < sub.MaxJournalEntriesPerMonth,
            "storage" => sub.CurrentStorageUsed < sub.MaxStorageBytes,
            _ => true
        };
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
            TrialGracePeriodDays = request.TrialGracePeriodDays
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
        var oldEnabled = template.EnabledFeatures;
        var oldTrial = template.TrialFeatures;

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

        await _db.SaveChangesAsync();

        // Propagate feature changes to all subscriptions of this plan so users
        // see updated feature access without re-subscribing.
        if (template.EnabledFeatures != oldEnabled || template.TrialFeatures != oldTrial)
        {
            var subs = await _db.Subscriptions
                .Include(s => s.TrialConfig)
                .Where(s => s.Plan == template.Plan)
                .ToListAsync();
            foreach (var sub in subs)
            {
                if (sub.Status == SubscriptionStatus.Trial)
                {
                    sub.EnabledFeatures = template.TrialFeatures;
                    if (sub.TrialConfig != null)
                        sub.TrialConfig.TrialFeatures = template.TrialFeatures;
                }
                else
                {
                    sub.EnabledFeatures = template.EnabledFeatures;
                }
                sub.UpdatedAt = DateTime.UtcNow;
            }
            if (subs.Any()) await _db.SaveChangesAsync();
        }

        return MapTemplateToResponse(template);
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

        // Build alerts
        var alerts = new List<UsageAlert>();
        var storagePct = sub.MaxStorageBytes > 0 ? (double)sub.CurrentStorageUsed / sub.MaxStorageBytes * 100 : 0;
        var docPct = sub.MaxDocumentsPerMonth > 0 ? (double)sub.CurrentMonthDocuments / sub.MaxDocumentsPerMonth * 100 : 0;
        var userPct = sub.MaxUsers > 0 ? (double)companyUsers.Count / sub.MaxUsers * 100 : 0;

        if (storagePct >= 90)
            alerts.Add(new UsageAlert("danger", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว กรุณาเคลียร์ไฟล์หรืออัปเกรดแพ็กเกจ"));
        else if (storagePct >= 70)
            alerts.Add(new UsageAlert("warning", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว"));

        if (docPct >= 90)
            alerts.Add(new UsageAlert("danger", "documents", $"เอกสารเดือนนี้ใช้ไป {sub.CurrentMonthDocuments}/{sub.MaxDocumentsPerMonth} รายการ"));
        else if (docPct >= 70)
            alerts.Add(new UsageAlert("warning", "documents", $"เอกสารเดือนนี้ใช้ไป {docPct:F0}%"));

        if (userPct >= 100)
            alerts.Add(new UsageAlert("danger", "users", "จำนวนผู้ใช้เต็มแล้ว อัปเกรดแพ็กเกจเพื่อเพิ่มผู้ใช้"));
        else if (companyUsers.Count >= sub.MaxUsers - 1 && sub.MaxUsers < 999)
            alerts.Add(new UsageAlert("warning", "users", $"เหลือโควต้าผู้ใช้อีก {sub.MaxUsers - companyUsers.Count} คน"));

        var daysLeft = (sub.EndDate - DateTime.UtcNow).Days;
        if (daysLeft <= 0)
            alerts.Add(new UsageAlert("danger", "subscription", "Subscription หมดอายุแล้ว กรุณาต่ออายุ"));
        else if (daysLeft <= 7)
            alerts.Add(new UsageAlert("warning", "subscription", $"Subscription จะหมดอายุใน {daysLeft} วัน"));

        return new UsageDetailResponse(
            sub.Plan, sub.Status, sub.EndDate,
            companyUsers.Count, sub.MaxUsers, companyUsers,
            sub.CurrentStorageUsed, sub.MaxStorageBytes, breakdown, largest,
            sub.CurrentMonthDocuments, sub.MaxDocumentsPerMonth,
            sub.CurrentMonthJournalEntries, sub.MaxJournalEntriesPerMonth,
            sub.UsageResetDate,
            alerts);
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
            .Where(s => s.Status == SubscriptionStatus.Trial && s.EndDate < now)
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
            await _notificationService.SendAsync(adminId, companyId,
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
                await _notificationService.SendAsync(ownerUserId, sub.CompanyId,
                    NotificationType.SubscriptionPaymentApproved,
                    "การชำระเงินได้รับการอนุมัติ",
                    $"การชำระเงิน {payment.PaymentNumber} ได้รับการอนุมัติ Subscription ต่ออายุถึง {newEndDate:dd/MM/yyyy}",
                    $"/subscription",
                    "SubscriptionPayment", payment.Id);

                await _notificationService.SendAsync(ownerUserId, sub.CompanyId,
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
                await _notificationService.SendAsync(ownerUserId, sub.CompanyId,
                    NotificationType.SubscriptionPaymentRejected,
                    "การชำระเงินถูกปฏิเสธ",
                    $"การชำระเงิน {payment.PaymentNumber} ถูกปฏิเสธ: {request.RejectionReason}",
                    $"/subscription/payments/{payment.Id}",
                    "SubscriptionPayment", payment.Id);
            }
        }

        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
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
            await _notificationService.SendAsync(ownerUserId, sub.CompanyId,
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
            p.CreatedAt);
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
            FeatureFlagsHelper.ToNameList(t.TrialFeatures));
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
        "Inventory" or "WarehouseManagement" or "FixedAssets" or "BankReconciliation" or "ExpenseManagement" or "PurchaseOrders" or "RecurringTransactions" or "CostCenter" or "ProjectAccounting" or "Payroll" or "Commission" or "FreelanceManagement" or "TimeBilling" or "LoanManagement" or "RevenueRecognition" => "operations",
        "WorkflowEngine" or "ApprovalWorkflow" or "AuditLog" or "EtaxInvoice" or "OpenBanking" or "Webhook" => "advanced",
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
        "WorkflowEngine" => "Workflow Engine",
        "AuditLog" => "บันทึกประวัติการใช้งาน",
        "EmailNotification" => "แจ้งเตือนทางอีเมล",
        "MultiUser" => "ผู้ใช้หลายคน",
        "BankReconciliation" => "กระทบยอดธนาคาร",
        "Inventory" => "สินค้าคงคลัง",
        "FixedAssets" => "สินทรัพย์ถาวร",
        "RecurringTransactions" => "รายการอัตโนมัติ",
        "MultiCurrency" => "หลายสกุลเงิน",
        "FreelanceManagement" => "จัดการฟรีแลนซ์",
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
