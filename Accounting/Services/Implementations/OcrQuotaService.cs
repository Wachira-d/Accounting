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

    /// <summary>OCR quota source-of-truth after the License overlay. When the
    /// company rides under a User License, Max comes from the License and
    /// Used is the aggregate across every Company attached to it. Otherwise
    /// these are the per-company subscription values.</summary>
    private record EffectiveOcr(
        int MaxPagesPerMonth, int UsedThisMonth,
        int? AzureMax, int AzureUsed,
        int? LocalMax, int LocalUsed,
        bool ViaLicense, Guid? LicenseId, bool FallbackToLocalWhenAzureExhausted);

    private async Task<EffectiveOcr> ResolveEffectiveOcrAsync(Subscription sub)
    {
        // ตัวนับที่ "ค้างจากเดือนก่อน" (ยังไม่มีใครเขียนเดือนนี้) ต้องนับเป็น 0 — เส้นอ่านหลายเส้น
        // (ด่านก่อนสแกน · หน้าแสดงผล · ผลรวมบริษัทพี่น้องใต้ License) ไม่ได้ล็อกแถวเพื่อขึ้นเดือนใหม่
        var now = DateTime.UtcNow;
        int Eff(int counter, DateTime resetAt)
            => Accounting.Helpers.SubscriptionUsageRollover.Effective(counter, resetAt, now);

        if (!sub.AccountSubscriptionId.HasValue)
        {
            return new EffectiveOcr(
                sub.MaxOcrPagesPerMonth, Eff(sub.CurrentMonthOcrPages, sub.UsageResetDate),
                sub.AzureOcrPagesPerMonth, Eff(sub.CurrentMonthAzureOcrPages, sub.UsageResetDate),
                sub.LocalOcrPagesPerMonth, Eff(sub.CurrentMonthLocalOcrPages, sub.UsageResetDate),
                ViaLicense: false, LicenseId: null,
                FallbackToLocalWhenAzureExhausted: sub.FallbackToLocalWhenAzureExhausted);
        }
        var acct = await _db.AccountSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
        if (acct == null)
        {
            // Dangling pointer — treat as per-company so the user isn't locked.
            return new EffectiveOcr(
                sub.MaxOcrPagesPerMonth, Eff(sub.CurrentMonthOcrPages, sub.UsageResetDate),
                sub.AzureOcrPagesPerMonth, Eff(sub.CurrentMonthAzureOcrPages, sub.UsageResetDate),
                sub.LocalOcrPagesPerMonth, Eff(sub.CurrentMonthLocalOcrPages, sub.UsageResetDate),
                ViaLicense: false, LicenseId: null,
                FallbackToLocalWhenAzureExhausted: sub.FallbackToLocalWhenAzureExhausted);
        }
        var agg = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
            .Select(s => new { s.CurrentMonthOcrPages, s.CurrentMonthAzureOcrPages, s.CurrentMonthLocalOcrPages, s.UsageResetDate })
            .ToListAsync();
        // บริษัทพี่น้องที่ยังไม่มีกิจกรรมเดือนนี้ ยังถือตัวนับของเดือนก่อน — ต้องนับเป็น 0
        // ไม่งั้นผลรวมทั้ง License เต็มค้างจากเดือนก่อน (บั๊กเดียวกันในทรงหลายบริษัท)
        return new EffectiveOcr(
            acct.MaxOcrPagesPerMonth, agg.Sum(x => Eff(x.CurrentMonthOcrPages, x.UsageResetDate)),
            acct.AzureOcrPagesPerMonth, agg.Sum(x => Eff(x.CurrentMonthAzureOcrPages, x.UsageResetDate)),
            acct.LocalOcrPagesPerMonth, agg.Sum(x => Eff(x.CurrentMonthLocalOcrPages, x.UsageResetDate)),
            ViaLicense: true, LicenseId: acct.Id,
            // Per-company fallback flag — License doesn't have one of its own;
            // the per-company value is still used (consistent with engine-pick
            // logic that reads from per-company sub).
            FallbackToLocalWhenAzureExhausted: sub.FallbackToLocalWhenAzureExhausted);
    }

    /// <summary>Take a row-level lock on the AccountSubscription so concurrent
    /// TryConsume calls on different companies under the same License
    /// serialize on it — without the lock two companies could both pass the
    /// aggregate check and over-consume by one each.</summary>
    private async Task LockLicenseAsync(Guid licenseId)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT 1 FROM ""AccountSubscriptions"" WHERE ""Id"" = {licenseId} FOR UPDATE");
    }

    public async Task<OcrQuotaStatus> GetQuotaStatusAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId
                && s.Status != SubscriptionStatus.Cancelled
                && s.Status != SubscriptionStatus.Suspended);

        if (sub == null)
            return new OcrQuotaStatus(0, 0, 0, 0, 0, DateTime.UtcNow, 0, 0);

        // Self-heal: when the subscription's OCR quotas don't match the
        // PlanTemplate for its current status, sync them on the fly. This
        // catches the case where admin edited the template before the
        // propagation code existed (or the subscription's status was
        // hand-flipped in the DB outside the upgrade flow) — without it
        // the tenant sees "10 / 10 pages" forever on an Enterprise plan
        // and has to wait for the admin to click Resync manually.
        //
        // Skip when this company rides under a User License — the License's
        // OCR quota is authoritative and the per-company sub fields are no
        // longer consulted for enforcement. Running self-heal here would
        // overwrite the per-company values with the (stale) Trial template
        // they were left at when attach happened, which is noise.
        if (!sub.AccountSubscriptionId.HasValue)
        {
            try
            {
                var template = await _db.PlanTemplates.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive);
                if (template != null)
                {
                    bool changed = false;
                    if (sub.Status == SubscriptionStatus.Trial)
                    {
                        if (sub.MaxOcrPagesPerMonth != template.TrialMaxOcrPagesPerMonth)
                        { sub.MaxOcrPagesPerMonth = template.TrialMaxOcrPagesPerMonth; changed = true; }
                    }
                    else
                    {
                        // Paid statuses (Active / PastDue / Expired-in-grace etc.)
                        // get the full per-engine breakdown.
                        if (sub.MaxOcrPagesPerMonth != template.MaxOcrPagesPerMonth)
                        { sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth; changed = true; }
                        if (sub.AzureOcrPagesPerMonth != template.AzureOcrPagesPerMonth)
                        { sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth; changed = true; }
                        if (sub.LocalOcrPagesPerMonth != template.LocalOcrPagesPerMonth)
                        { sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth; changed = true; }
                        if (sub.FallbackToLocalWhenAzureExhausted != template.FallbackToLocalWhenAzureExhausted)
                        { sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted; changed = true; }
                    }
                    if (changed)
                    {
                        sub.UpdatedBy = "OcrQuota-SelfHeal";
                        await _db.SaveChangesAsync();
                        _logger.LogInformation(
                            "OCR quota self-heal: Subscription {CompanyId} synced from template (status={Status}, total={Total})",
                            companyId, sub.Status, sub.MaxOcrPagesPerMonth);
                    }
                }
            }
            catch (Exception ex)
            {
                // Self-heal is best-effort — never let a sync failure block the
                // quota-read path from returning. The Resync admin button stays
                // available as a manual escape hatch.
                _logger.LogWarning(ex, "OCR quota self-heal failed for company {CompanyId}", companyId);
            }
        }

        var siteSettings = await _db.SiteSettings.FirstOrDefaultAsync();

        var creditPages = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                && p.PagesRemaining > 0
                && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
            .SumAsync(p => p.PagesRemaining);

        // Bonus pages expire if OcrBonusExpiresAt has passed
        var effectiveBonus = (sub.OcrBonusExpiresAt == null || sub.OcrBonusExpiresAt > DateTime.UtcNow)
            ? sub.OcrBonusPages : 0;

        // License-aware view: Max + Used resolve to the AccountSubscription
        // (and SUM of every attached company's counter) when this company
        // rides under a User License. Bonus + credit pages stay per-company
        // since they're purchased by the company directly.
        var eff = await ResolveEffectiveOcrAsync(sub);
        var totalAvailable = eff.MaxPagesPerMonth - eff.UsedThisMonth + effectiveBonus + creditPages;

        return new OcrQuotaStatus(
            MaxPagesPerMonth: eff.MaxPagesPerMonth,
            UsedThisMonth: eff.UsedThisMonth,
            BonusPages: effectiveBonus,
            CreditPagesRemaining: creditPages,
            TotalAvailable: Math.Max(0, totalAvailable),
            // ตัวนับค้างจากเดือนก่อน = วันรีเซ็ตเป็นอดีต — แสดงวันรีเซ็ตรอบถัดไปแทน
            // (ไม่งั้นหน้าจอบอก "รีเซ็ตวันที่ 1 ก.ค." ทั้งที่ตอนนี้เป็น ก.ย.)
            UsageResetDate: Accounting.Helpers.SubscriptionUsageRollover.IsStale(sub.UsageResetDate, DateTime.UtcNow)
                ? Accounting.Helpers.SubscriptionUsageRollover.NextResetDate(DateTime.UtcNow)
                : sub.UsageResetDate,
            CreditPricePerPage: siteSettings?.OcrCreditPricePerPage ?? 2.0m,
            CreditMinPurchase: siteSettings?.OcrCreditMinPurchase ?? 100,
            // Per-engine breakdown so the user-facing UI can show
            // "Azure used X / Y, Local used X / Y" instead of one
            // opaque total. Null on either side means "this plan
            // doesn't split — uses MaxPagesPerMonth above".
            AzureMaxPagesPerMonth: eff.AzureMax,
            AzureUsedThisMonth: eff.AzureMax.HasValue ? eff.AzureUsed : (int?)null,
            LocalMaxPagesPerMonth: eff.LocalMax,
            LocalUsedThisMonth: eff.LocalMax.HasValue ? eff.LocalUsed : (int?)null,
            FallbackToLocalWhenAzureExhausted: eff.FallbackToLocalWhenAzureExhausted,
            PlanName: eff.ViaLicense ? $"License/{sub.Plan}" : sub.Plan.ToString());
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

            // Lazy monthly reset — ล้างตัวนับทุกตัวผ่านตัวกลางตัวเดียว (เดิมล้างแค่ OCR รวม
            // ⇒ ตัวนับ Azure/local สะสมข้ามเดือนไม่รู้จบ) — Helpers/SubscriptionUsageRollover
            Accounting.Helpers.SubscriptionUsageRollover.RollIfDue(sub, DateTime.UtcNow);

            // License path: lock the License row, sum every attached company's
            // counter, and only consume if the aggregate is still under the
            // License's quota. Per-company counter still increments so the
            // aggregate read by the next consumer reflects this one.
            if (sub.AccountSubscriptionId.HasValue)
            {
                await LockLicenseAsync(sub.AccountSubscriptionId.Value);
            }
            var eff = await ResolveEffectiveOcrAsync(sub);

            if (eff.UsedThisMonth < eff.MaxPagesPerMonth)
            {
                sub.CurrentMonthOcrPages++;
            }
            else if (sub.OcrBonusPages > 0
                && (sub.OcrBonusExpiresAt == null || sub.OcrBonusExpiresAt > DateTime.UtcNow))
            {
                // Bonus is per-company — purchased by this company, only it
                // can spend. License attach doesn't pool bonus.
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
                "OCR quota consumed CompanyId={CompanyId} Source={Source} Used={Used}/{Max} BonusRemaining={BonusRemaining}",
                companyId, eff.ViaLicense ? $"License:{eff.LicenseId}" : "Company",
                eff.ViaLicense ? eff.UsedThisMonth + 1 : sub.CurrentMonthOcrPages,
                eff.MaxPagesPerMonth, sub.OcrBonusPages);
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
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return (true, null);  // unknown plan — let the cascade decide
        var eff = await ResolveEffectiveOcrAsync(sub);
        // Legacy single-budget mode: no Azure-specific cap → fall back to total budget.
        if (!eff.AzureMax.HasValue)
        {
            if (eff.UsedThisMonth < eff.MaxPagesPerMonth) return (true, null);
            return (false, $"โควต้า OCR ทั้งหมดเดือนนี้เต็มแล้ว ({eff.UsedThisMonth}/{eff.MaxPagesPerMonth} หน้า) — กรุณาซื้อเครดิตเพิ่ม หรือรอเดือนหน้า");
        }
        // Engine-specific mode.
        if (eff.AzureMax.Value == 0)
            return (false, "แผนปัจจุบันให้ Azure DI 0 หน้า/เดือน — กรุณา upgrade plan หรือเพิ่ม AzureOcrPagesPerMonth");
        if (eff.AzureUsed < eff.AzureMax.Value) return (true, null);
        return (false, $"โควต้า Azure DI เดือนนี้เต็มแล้ว ({eff.AzureUsed}/{eff.AzureMax.Value} หน้า) — Local OCR ยังใช้งานได้");
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

            // Lazy monthly reset — ตัวกลางตัวเดียว (Helpers/SubscriptionUsageRollover)
            Accounting.Helpers.SubscriptionUsageRollover.RollIfDue(sub, DateTime.UtcNow);

            // Lock the License row when attached so the aggregate counters
            // we're about to read can't be stale-read by a concurrent consume
            // on another company under the same License.
            if (sub.AccountSubscriptionId.HasValue)
            {
                await LockLicenseAsync(sub.AccountSubscriptionId.Value);
            }
            var eff = await ResolveEffectiveOcrAsync(sub);

            if (isAzure)
            {
                var azureBudget = eff.AzureMax ?? eff.MaxPagesPerMonth;
                if (eff.AzureUsed >= azureBudget)
                {
                    await tx.RollbackAsync();
                    return false;
                }
                sub.CurrentMonthAzureOcrPages++;
            }
            else
            {
                if (eff.LocalMax.HasValue && eff.LocalUsed >= eff.LocalMax.Value)
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
                "OCR engine quota consumed CompanyId={C} Engine={E} Source={S} AzureUsed={A} LocalUsed={L}",
                companyId, engineKind,
                eff.ViaLicense ? $"License:{eff.LicenseId}" : "Company",
                sub.CurrentMonthAzureOcrPages, sub.CurrentMonthLocalOcrPages);
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
        // โหลดเฉพาะแถวที่ถึงวันรีเซ็ต (ต้นเดือนครั้งเดียว) แล้วล้างผ่านตัวกลางตัวเดียว —
        // เดิมเป็น ExecuteUpdate ที่พิมพ์รายชื่อตัวนับเอง แล้ว**ลืม Azure/local** (สำเนาที่สองของ
        // กติกาเดียวกัน — F2 ข้อ 4) · ความถูกต้องสำคัญกว่าประหยัดการโหลดแถวเดือนละครั้ง
        var now = DateTime.UtcNow;
        var due = await _db.Subscriptions.Where(s => s.UsageResetDate <= now).ToListAsync();
        var resetCount = 0;
        foreach (var sub in due)
            if (Accounting.Helpers.SubscriptionUsageRollover.RollIfDue(sub, now)) resetCount++;
        if (resetCount > 0) await _db.SaveChangesAsync();

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
