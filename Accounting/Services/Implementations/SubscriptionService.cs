using Accounting.Data;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ระบบจัดการ Subscription และ Trial อย่างละเอียด
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly AccountingDbContext _db;

    public SubscriptionService(AccountingDbContext db)
    {
        _db = db;
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
        if (request.EnabledFeatures.HasValue) template.EnabledFeatures = request.EnabledFeatures.Value;
        if (request.IsActive.HasValue) template.IsActive = request.IsActive.Value;
        if (request.TrialDurationDays.HasValue) template.TrialDurationDays = request.TrialDurationDays.Value;
        if (request.TrialMaxExtensions.HasValue) template.TrialMaxExtensions = request.TrialMaxExtensions.Value;
        if (request.TrialExtensionDays.HasValue) template.TrialExtensionDays = request.TrialExtensionDays.Value;
        if (request.TrialFeatures.HasValue) template.TrialFeatures = request.TrialFeatures.Value;
        if (request.TrialMaxUsers.HasValue) template.TrialMaxUsers = request.TrialMaxUsers.Value;
        if (request.TrialMaxDocumentsPerMonth.HasValue) template.TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
        if (request.TrialMaxJournalEntriesPerMonth.HasValue) template.TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
        if (request.TrialBlockOnExpiry.HasValue) template.TrialBlockOnExpiry = request.TrialBlockOnExpiry.Value;
        if (request.TrialGracePeriodDays.HasValue) template.TrialGracePeriodDays = request.TrialGracePeriodDays.Value;

        await _db.SaveChangesAsync();
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

    // ==================== Private Helpers ====================

    private static PlanTemplateResponse MapTemplateToResponse(PlanTemplate t)
    {
        return new PlanTemplateResponse(
            t.Id, t.Name, t.Description, t.Plan, t.IsActive,
            t.MonthlyPrice, t.QuarterlyPrice, t.SemiAnnualPrice, t.AnnualPrice,
            t.MaxUsers, t.MaxCompanies, t.MaxDocumentsPerMonth, t.MaxJournalEntriesPerMonth,
            t.MaxStorageBytes, t.EnabledFeatures,
            t.TrialDurationDays, t.TrialMaxExtensions, t.TrialExtensionDays, t.TrialFeatures);
    }
}
