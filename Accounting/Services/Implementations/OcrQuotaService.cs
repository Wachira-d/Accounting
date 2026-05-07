using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OcrQuotaService : IOcrQuotaService
{
    private readonly AccountingDbContext _db;

    public OcrQuotaService(AccountingDbContext db) => _db = db;

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

        var totalAvailable = sub.MaxOcrPagesPerMonth - sub.CurrentMonthOcrPages + sub.OcrBonusPages + creditPages;

        return new OcrQuotaStatus(
            MaxPagesPerMonth: sub.MaxOcrPagesPerMonth,
            UsedThisMonth: sub.CurrentMonthOcrPages,
            BonusPages: sub.OcrBonusPages,
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

    public async Task IncrementUsageAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId
                && s.Status != SubscriptionStatus.Cancelled
                && s.Status != SubscriptionStatus.Suspended);
        if (sub == null) return;

        if (sub.CurrentMonthOcrPages < sub.MaxOcrPagesPerMonth)
        {
            sub.CurrentMonthOcrPages++;
        }
        else if (sub.OcrBonusPages > 0)
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
            if (credit != null)
                credit.PagesRemaining--;
        }

        await _db.SaveChangesAsync();
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
        var now = DateTime.UtcNow;
        var subsToReset = await _db.Subscriptions
            .Where(s => s.UsageResetDate <= now)
            .ToListAsync();

        foreach (var sub in subsToReset)
        {
            sub.CurrentMonthOcrPages = 0;
            sub.UsageResetDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
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
