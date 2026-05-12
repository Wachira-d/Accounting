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
            CreditMinPurchase: siteSettings?.OcrCreditMinPurchase ?? 100,
            // Per-engine breakdown so the user-facing UI can show
            // "Azure used X / Y, Local used X / Y" instead of one
            // opaque total. Null on either side means "this plan
            // doesn't split — uses MaxPagesPerMonth above".
            AzureMaxPagesPerMonth: sub.AzureOcrPagesPerMonth,
            AzureUsedThisMonth: sub.AzureOcrPagesPerMonth.HasValue ? sub.CurrentMonthAzureOcrPages : null,
            LocalMaxPagesPerMonth: sub.LocalOcrPagesPerMonth,
            LocalUsedThisMonth: sub.LocalOcrPagesPerMonth.HasValue ? sub.CurrentMonthLocalOcrPages : null,
            FallbackToLocalWhenAzureExhausted: sub.FallbackToLocalWhenAzureExhausted,
            PlanName: sub.Plan.ToString());
    }

    public async Task<bool> CanScanAsync(Guid companyId)
    {
        var status = await GetQuotaStatusAsync(companyId);
        return status.TotalAvailable > 0;
    }

    public async Task<bool> TryConsumeAsync(Guid companyId)
    {
        // ReadCommitted + explicit FOR UPDATE row-level lock — narrower scope than
        // Serializable (which locks the whole table predicate). PostgreSQL row lock
        // serializes only concurrent updates to the SAME subscription row, allowing
        // other tenants' scans to proceed in parallel.
        using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);
        try
        {
            var sub = await _db.Subscriptions
                .FromSqlInterpolated($@"
                    SELECT * FROM ""Subscriptions""
                    WHERE ""CompanyId"" = {companyId}
                      AND ""Status"" != {(int)SubscriptionStatus.Cancelled}
                      AND ""Status"" != {(int)SubscriptionStatus.Suspended}
                      AND ""IsDeleted"" = false
                    FOR UPDATE")
                .FirstOrDefaultAsync();
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
    /// <summary>Engine-aware quota check. Returns true when Azure DI is
    /// still allowed for this tenant this month.</summary>
    public async Task<bool> CanUseAzureAsync(Guid companyId)
        => (await CheckAzureQuotaAsync(companyId)).Allowed;

    public async Task<(bool Allowed, string? Reason)> CheckAzureQuotaAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.AzureOcrPagesPerMonth, s.CurrentMonthAzureOcrPages,
                s.MaxOcrPagesPerMonth, s.CurrentMonthOcrPages })
            .FirstOrDefaultAsync();
        if (sub == null) return (true, null);  // unknown plan — let the cascade decide
        // Legacy single-budget mode: no Azure-specific cap → fall back to total budget.
        if (!sub.AzureOcrPagesPerMonth.HasValue)
        {
            if (sub.CurrentMonthOcrPages < sub.MaxOcrPagesPerMonth) return (true, null);
            return (false, $"โควต้า OCR ทั้งหมดเดือนนี้เต็มแล้ว ({sub.CurrentMonthOcrPages}/{sub.MaxOcrPagesPerMonth} หน้า) — กรุณาซื้อเครดิตเพิ่ม หรือรอเดือนหน้า");
        }
        // Engine-specific mode.
        if (sub.AzureOcrPagesPerMonth.Value == 0)
            return (false, "แผนปัจจุบันให้ Azure DI 0 หน้า/เดือน — กรุณา upgrade plan หรือเพิ่ม AzureOcrPagesPerMonth บน Subscription");
        if (sub.CurrentMonthAzureOcrPages < sub.AzureOcrPagesPerMonth.Value) return (true, null);
        return (false, $"โควต้า Azure DI เดือนนี้เต็มแล้ว ({sub.CurrentMonthAzureOcrPages}/{sub.AzureOcrPagesPerMonth.Value} หน้า) — Local OCR ยังใช้งานได้");
    }

    public async Task<bool> TryConsumeForEngineAsync(Guid companyId, string engineKind)
    {
        var isAzure = string.Equals(engineKind, "Azure", StringComparison.OrdinalIgnoreCase)
            || engineKind.StartsWith("Azure", StringComparison.OrdinalIgnoreCase);
        using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);
        try
        {
            var sub = await _db.Subscriptions
                .FromSqlInterpolated($@"
                    SELECT * FROM ""Subscriptions""
                    WHERE ""CompanyId"" = {companyId}
                      AND ""IsDeleted"" = false
                    FOR UPDATE")
                .FirstOrDefaultAsync();
            if (sub == null) { await tx.RollbackAsync(); return false; }

            // Lazy monthly reset
            if (sub.UsageResetDate <= DateTime.UtcNow)
            {
                sub.CurrentMonthOcrPages = 0;
                sub.CurrentMonthAzureOcrPages = 0;
                sub.CurrentMonthLocalOcrPages = 0;
                var now = DateTime.UtcNow;
                sub.UsageResetDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            }

            if (isAzure)
            {
                var azureBudget = sub.AzureOcrPagesPerMonth ?? sub.MaxOcrPagesPerMonth;
                if (sub.CurrentMonthAzureOcrPages >= azureBudget)
                {
                    await tx.RollbackAsync();
                    return false;
                }
                sub.CurrentMonthAzureOcrPages++;
            }
            else
            {
                var localBudget = sub.LocalOcrPagesPerMonth;
                if (localBudget.HasValue && sub.CurrentMonthLocalOcrPages >= localBudget.Value)
                {
                    await tx.RollbackAsync();
                    return false;
                }
                sub.CurrentMonthLocalOcrPages++;
            }
            sub.CurrentMonthOcrPages++;   // keep the legacy total in lockstep

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            _logger.LogInformation(
                "OCR engine quota consumed CompanyId={C} Engine={E} AzureUsed={A} LocalUsed={L}",
                companyId, engineKind, sub.CurrentMonthAzureOcrPages, sub.CurrentMonthLocalOcrPages);
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "OCR engine quota consume failed");
            throw;
        }
    }

    public async Task RecordEngineUsageAsync(Guid companyId, string engineKind)
    {
        var isAzure = string.Equals(engineKind, "Azure", StringComparison.OrdinalIgnoreCase)
            || engineKind.StartsWith("Azure", StringComparison.OrdinalIgnoreCase);
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return;
        if (isAzure) sub.CurrentMonthAzureOcrPages++;
        else sub.CurrentMonthLocalOcrPages++;
        await _db.SaveChangesAsync();
    }

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
        // Direct SQL UPDATE — no entity materialization for thousands of subs.
        var now = DateTime.UtcNow;
        var startOfNextMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var resetCount = await _db.Subscriptions
            .Where(s => s.UsageResetDate <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CurrentMonthOcrPages, 0)
                .SetProperty(x => x.CurrentMonthDocuments, 0)
                .SetProperty(x => x.CurrentMonthJournalEntries, 0)
                .SetProperty(x => x.UsageResetDate, startOfNextMonth));

        _logger.LogInformation("Monthly usage reset for {Count} subscriptions", resetCount);
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
