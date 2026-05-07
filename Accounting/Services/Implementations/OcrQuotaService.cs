using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OcrQuotaService : IOcrQuotaService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<OcrQuotaService> _logger;

    public OcrQuotaService(AccountingDbContext db, ILogger<OcrQuotaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OcrQuotaStatus> GetQuotaStatusAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId
                && s.Status != SubscriptionStatus.Cancelled
                && s.Status != SubscriptionStatus.Suspended);

        if (sub == null)
            return new OcrQuotaStatus(0, 0, 0, 0, 0, DateTime.UtcNow, 0, 0);

        var siteSettings = await _db.SiteSettings.FirstOrDefaultAsync();

        var creditPages = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                && p.PagesRemaining > 0
                && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
            .SumAsync(p => p.PagesRemaining);

        // Bonus pages expire if OcrBonusExpiresAt has passed
        var effectiveBonus = (sub.OcrBonusExpiresAt == null || sub.OcrBonusExpiresAt > DateTime.UtcNow)
            ? sub.OcrBonusPages : 0;

        var totalAvailable = sub.MaxOcrPagesPerMonth - sub.CurrentMonthOcrPages + effectiveBonus + creditPages;

        return new OcrQuotaStatus(
            MaxPagesPerMonth: sub.MaxOcrPagesPerMonth,
            UsedThisMonth: sub.CurrentMonthOcrPages,
            BonusPages: effectiveBonus,
            CreditPagesRemaining: creditPages,
            TotalAvailable: Math.Max(0, totalAvailable),
            UsageResetDate: sub.UsageResetDate,
            CreditPricePerPage: siteSettings?.OcrCreditPricePerPage ?? 2.0m,
            CreditMinPurchase: siteSettings?.OcrCreditMinPurchase ?? 100);
    }

    public async Task<bool> CanScanAsync(Guid companyId)
    {
        var status = await GetQuotaStatusAsync(companyId);
        return status.TotalAvailable > 0;
    }

    public async Task<bool> TryConsumeAsync(Guid companyId)
    {
        // Use serializable isolation to prevent two parallel scans from both
        // passing the availability check and over-consuming quota.
        using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);
        try
        {
            var sub = await _db.Subscriptions
                .FirstOrDefaultAsync(s => s.CompanyId == companyId
                    && s.Status != SubscriptionStatus.Cancelled
                    && s.Status != SubscriptionStatus.Suspended);
            if (sub == null) return false;

            // Lazy monthly reset — if we've crossed the reset boundary, zero usage now
            if (sub.UsageResetDate <= DateTime.UtcNow)
            {
                sub.CurrentMonthOcrPages = 0;
                var now = DateTime.UtcNow;
                sub.UsageResetDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            }

            if (sub.CurrentMonthOcrPages < sub.MaxOcrPagesPerMonth)
            {
                sub.CurrentMonthOcrPages++;
            }
            else if (sub.OcrBonusPages > 0
                && (sub.OcrBonusExpiresAt == null || sub.OcrBonusExpiresAt > DateTime.UtcNow))
            {
                sub.OcrBonusPages--;
            }
            else
            {
                var credit = await _db.OcrCreditPurchases
                    .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                        && p.PagesRemaining > 0
                        && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
                    .OrderBy(p => p.ExpiresAt ?? DateTime.MaxValue)
                    .FirstOrDefaultAsync();
                if (credit == null)
                {
                    await tx.RollbackAsync();
                    return false;
                }
                credit.PagesRemaining--;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "OCR quota consumed CompanyId={CompanyId} MonthlyUsed={MonthlyUsed}/{MonthlyMax} BonusRemaining={BonusRemaining}",
                companyId, sub.CurrentMonthOcrPages, sub.MaxOcrPagesPerMonth, sub.OcrBonusPages);
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "OCR quota consume failed CompanyId={CompanyId}", companyId);
            throw;
        }
    }

    public async Task RefundAsync(Guid companyId)
    {
        // Refund in reverse order: monthly counter first (most recent debit)
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return;

        if (sub.CurrentMonthOcrPages > 0)
        {
            sub.CurrentMonthOcrPages--;
        }
        else if (sub.OcrBonusPages < int.MaxValue)
        {
            sub.OcrBonusPages++;
        }
        // Note: not refunding to credit purchases — too complex to track which credit was debited
        await _db.SaveChangesAsync();

        _logger.LogInformation("OCR quota refunded CompanyId={CompanyId} MonthlyUsed={MonthlyUsed}",
            companyId, sub.CurrentMonthOcrPages);
    }

    // Kept for backward compat — delegates to atomic TryConsumeAsync
    public async Task IncrementUsageAsync(Guid companyId)
    {
        await TryConsumeAsync(companyId);
    }

    public async Task<OcrCreditPurchaseResponse> PurchaseCreditsAsync(Guid companyId, int pages, string performedBy)
    {
        var siteSettings = await _db.SiteSettings.FirstOrDefaultAsync();
        var minPurchase = siteSettings?.OcrCreditMinPurchase ?? 100;
        if (pages < minPurchase)
            throw new InvalidOperationException($"ต้องซื้อขั้นต่ำ {minPurchase} หน้า");

        var pricePerPage = siteSettings?.OcrCreditPricePerPage ?? 2.0m;

        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId
                && s.Status != SubscriptionStatus.Cancelled);
        if (sub == null)
            throw new InvalidOperationException("ไม่พบ Subscription");

        var purchase = new OcrCreditPurchase
        {
            CompanyId = companyId,
            SubscriptionId = sub.Id,
            PagesPurchased = pages,
            PagesRemaining = pages,
            AmountPaid = pages * pricePerPage,
            Status = "Pending",
            CreatedBy = performedBy
        };

        _db.OcrCreditPurchases.Add(purchase);
        await _db.SaveChangesAsync();

        return MapToResponse(purchase);
    }

    public async Task<OcrCreditPurchaseResponse> ReviewCreditPurchaseAsync(
        Guid purchaseId, bool approve, string? notes, string performedBy)
    {
        var purchase = await _db.OcrCreditPurchases
            .FirstOrDefaultAsync(p => p.Id == purchaseId)
            ?? throw new InvalidOperationException("ไม่พบรายการซื้อ");

        purchase.Status = approve ? "Approved" : "Rejected";
        purchase.ReviewedAt = DateTime.UtcNow;
        purchase.ReviewNotes = notes;
        purchase.ReviewedByUserId = Guid.TryParse(performedBy, out var uid) ? uid : null;

        if (approve)
            purchase.ExpiresAt = DateTime.UtcNow.AddMonths(12);
        else
            purchase.PagesRemaining = 0;

        await _db.SaveChangesAsync();
        return MapToResponse(purchase);
    }

    public async Task<List<OcrCreditPurchaseResponse>> GetPurchaseHistoryAsync(Guid companyId)
    {
        var purchases = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
        return purchases.Select(MapToResponse).ToList();
    }

    public async Task<List<OcrCreditPurchaseResponse>> GetPendingPurchasesAsync()
    {
        var purchases = await _db.OcrCreditPurchases
            .Where(p => p.Status == "Pending")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        return purchases.Select(MapToResponse).ToList();
    }

    public async Task ResetMonthlyUsageAsync()
    {
        // Idempotent — only resets subs whose UsageResetDate has passed.
        // Safe to call multiple times within the same day.
        var now = DateTime.UtcNow;
        var startOfNextMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var subsToReset = await _db.Subscriptions
            .Where(s => s.UsageResetDate <= now)
            .ToListAsync();

        foreach (var sub in subsToReset)
        {
            sub.CurrentMonthOcrPages = 0;
            sub.CurrentMonthDocuments = 0;
            sub.CurrentMonthJournalEntries = 0;
            sub.UsageResetDate = startOfNextMonth;
        }

        await _db.SaveChangesAsync();
    }

    private static OcrCreditPurchaseResponse MapToResponse(OcrCreditPurchase p) => new(
        Id: p.Id,
        CompanyId: p.CompanyId,
        PagesPurchased: p.PagesPurchased,
        PagesRemaining: p.PagesRemaining,
        AmountPaid: p.AmountPaid,
        Status: p.Status,
        PaymentReference: p.PaymentReference,
        CreatedAt: p.CreatedAt,
        ReviewedAt: p.ReviewedAt,
        ReviewNotes: p.ReviewNotes);
}
