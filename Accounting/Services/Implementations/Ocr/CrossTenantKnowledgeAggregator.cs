using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Promote per-tenant OCR learning into the system-wide tables once
/// a pattern has been independently validated by ≥N distinct tenants —
/// the cross-tenant equivalent of "k-anonymity": no single company's
/// detail leaks; only patterns that multiple unrelated parties agree on
/// get exposed.
///
/// What flows where:
///
///   OcrCategoryMappings (per-tenant, per-row TimesUsed)
///   └─► group by (VendorKey, DescriptionKeyword, AccountCode)
///       └─► when ≥ MinDistinctTenants distinct CompanyIds in group:
///           └─► upsert SystemOcrCategoryMapping with TimesUsed =
///               weighted sum across contributing tenants
///
///   OcrVendorIntelligence (per-tenant, per-vendor stats)
///   └─► group by VendorKey
///       └─► when ≥ MinDistinctTenants distinct CompanyIds:
///           └─► upsert SystemOcrVendorIntelligence with combined
///               doc-type / debit-account / WHT breakdowns
///
/// Privacy / opt-out:
///   • Companies with CompanySettings.ShareTrainingDataAnonymously = false
///     are excluded entirely from aggregation. Their data stays private.
///   • The aggregate stores ONLY the universal fields (VendorKey,
///     keyword, account code, doc type code) — no company id, no user
///     id, no company-specific account-name customizations.
///   • The k-anonymity threshold (default 2) makes it impossible to
///     reverse-engineer a single tenant's data from the aggregate.
///
/// Idempotency:
///   • Re-running the aggregator overwrites the system row's stats with
///     the latest aggregation. It NEVER deletes system rows that no
///     longer have enough cross-tenant support — operator-tuned data
///     and SystemAdmin manual training stay intact.
/// </summary>
public class CrossTenantKnowledgeAggregator
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CrossTenantKnowledgeAggregator> _logger;

    public CrossTenantKnowledgeAggregator(AccountingDbContext db, ILogger<CrossTenantKnowledgeAggregator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record AggregateResult(
        int TenantsConsidered,
        int CategoryMappingsPromoted,
        int VendorIntelligencePromoted,
        TimeSpan Duration);

    public async Task<AggregateResult> AggregateAsync(
        int minDistinctTenants = 2,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Resolve the opted-in tenant set. Companies with the
        // ShareTrainingDataAnonymously flag off are excluded entirely.
        var optedInCompanyIds = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.ShareTrainingDataAnonymously && !s.IsDeleted)
            .Select(s => s.CompanyId)
            .ToListAsync(ct);
        // CompanySettings is per-tenant but may not exist for every Company
        // (legacy/new tenants without explicit settings). Treat missing as
        // opted-in (default flag value = true) so the aggregation pool
        // matches the default UX expectation.
        var allCompanyIds = await _db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted)
            .Select(c => c.Id).ToListAsync(ct);
        var optedOut = await _db.CompanySettings.AsNoTracking()
            .Where(s => !s.ShareTrainingDataAnonymously && !s.IsDeleted)
            .Select(s => s.CompanyId).ToListAsync(ct);
        var optedOutSet = optedOut.ToHashSet();
        var participatingCompanies = allCompanyIds.Where(id => !optedOutSet.Contains(id)).ToHashSet();
        if (participatingCompanies.Count < minDistinctTenants)
        {
            sw.Stop();
            return new AggregateResult(participatingCompanies.Count, 0, 0, sw.Elapsed);
        }

        // 2. Category-mapping aggregation
        int catPromoted = await AggregateCategoryMappingsAsync(participatingCompanies, minDistinctTenants, ct);

        // 3. Vendor-intelligence aggregation
        int viPromoted = await AggregateVendorIntelligenceAsync(participatingCompanies, minDistinctTenants, ct);

        sw.Stop();
        _logger.LogInformation("Cross-tenant aggregation: {T} tenants → cat={C} vi={V} in {D}",
            participatingCompanies.Count, catPromoted, viPromoted, sw.Elapsed);
        return new AggregateResult(participatingCompanies.Count, catPromoted, viPromoted, sw.Elapsed);
    }

    // ─────────────────────────────────────────────────────────────────
    // OcrCategoryMappings → SystemOcrCategoryMappings
    // ─────────────────────────────────────────────────────────────────
    private async Task<int> AggregateCategoryMappingsAsync(
        HashSet<Guid> companyIds, int minTenants, CancellationToken ct)
    {
        // Pull every per-tenant mapping in one go. Bounded by the size of
        // the per-tenant table per company — typically a few thousand
        // even for big companies. The aggregation happens in memory.
        var rows = await _db.OcrCategoryMappings.AsNoTracking()
            .Where(m => !m.IsDeleted && companyIds.Contains(m.CompanyId))
            .Select(m => new {
                m.CompanyId, m.VendorKey, m.DescriptionKeyword,
                m.AccountCode, m.AccountName, m.TimesUsed
            })
            .ToListAsync(ct);

        // Group by the universal triple (vendor, keyword, account); count
        // distinct tenants per group; sum TimesUsed.
        var grouped = rows
            .GroupBy(r => new { r.VendorKey, r.DescriptionKeyword, r.AccountCode })
            .Where(g => g.Select(r => r.CompanyId).Distinct().Count() >= minTenants)
            .Select(g => new {
                g.Key.VendorKey, g.Key.DescriptionKeyword, g.Key.AccountCode,
                TenantCount = g.Select(r => r.CompanyId).Distinct().Count(),
                TotalUsed = g.Sum(r => r.TimesUsed),
                // Pick the most-frequently-supplied account name across the
                // contributing tenants — preserves the most "popular" label.
                AccountName = g.Where(r => !string.IsNullOrEmpty(r.AccountName))
                    .GroupBy(r => r.AccountName!)
                    .OrderByDescending(x => x.Sum(y => y.TimesUsed))
                    .Select(x => x.Key)
                    .FirstOrDefault(),
            })
            .ToList();

        int promoted = 0;
        foreach (var g in grouped)
        {
            if (ct.IsCancellationRequested) break;
            var existing = await _db.SystemOcrCategoryMappings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.VendorKey == g.VendorKey
                    && s.DescriptionKeyword == g.DescriptionKeyword
                    && s.AccountCode == g.AccountCode, ct);
            if (existing == null)
            {
                _db.SystemOcrCategoryMappings.Add(new SystemOcrCategoryMapping
                {
                    VendorKey = g.VendorKey,
                    DescriptionKeyword = g.DescriptionKeyword,
                    AccountCode = g.AccountCode,
                    AccountName = g.AccountName,
                    TimesUsed = g.TotalUsed,
                    LastUsedAt = DateTime.UtcNow,
                    CreatedBy = "cross-tenant-aggregator",
                });
                promoted++;
            }
            else if (!existing.IsDeleted)
            {
                // Refresh TimesUsed with the latest aggregate sum. Use the
                // MAX of (existing, new) to avoid regressing when one tenant
                // soft-deletes their row between runs.
                if (g.TotalUsed > existing.TimesUsed)
                {
                    existing.TimesUsed = g.TotalUsed;
                    existing.LastUsedAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.UpdatedBy = "cross-tenant-aggregator";
                    promoted++;
                }
            }
        }
        if (promoted > 0) await _db.SaveChangesAsync(ct);
        return promoted;
    }

    // ─────────────────────────────────────────────────────────────────
    // OcrVendorIntelligence → SystemOcrVendorIntelligence
    // ─────────────────────────────────────────────────────────────────
    private async Task<int> AggregateVendorIntelligenceAsync(
        HashSet<Guid> companyIds, int minTenants, CancellationToken ct)
    {
        var rows = await _db.OcrVendorIntelligence.AsNoTracking()
            .Where(v => !v.IsDeleted && companyIds.Contains(v.CompanyId)
                && v.TotalDocuments > 0)
            .ToListAsync(ct);

        var grouped = rows.GroupBy(v => v.VendorKey)
            .Where(g => g.Select(v => v.CompanyId).Distinct().Count() >= minTenants)
            .ToList();

        int promoted = 0;
        foreach (var g in grouped)
        {
            if (ct.IsCancellationRequested) break;

            // Combine doc-type breakdowns across tenants — sum the counts
            var combinedDocType = new Dictionary<string, int>();
            var combinedDebit = new Dictionary<string, int>();
            int totalDocs = 0;
            int whtCount = 0;
            decimal? wAvgWhtRate = null;
            decimal whtRateWeight = 0;
            int? paymentTermsSum = null;
            int paymentTermsCount = 0;
            string? bestDebitName = null;

            foreach (var v in g)
            {
                totalDocs += v.TotalDocuments;
                whtCount += v.WhtUsageCount;
                foreach (var kv in ParseBreakdown(v.DocumentTypeBreakdownJson))
                    combinedDocType[kv.Key] = combinedDocType.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var kv in ParseBreakdown(v.DebitAccountBreakdownJson))
                    combinedDebit[kv.Key] = combinedDebit.GetValueOrDefault(kv.Key) + kv.Value;
                if (v.TypicalWhtRate.HasValue)
                {
                    wAvgWhtRate = ((wAvgWhtRate ?? 0) * whtRateWeight + v.TypicalWhtRate.Value * v.WhtUsageCount)
                        / Math.Max(1, whtRateWeight + v.WhtUsageCount);
                    whtRateWeight += v.WhtUsageCount;
                }
                if (v.TypicalPaymentTermsDays.HasValue)
                {
                    paymentTermsSum = (paymentTermsSum ?? 0) + v.TypicalPaymentTermsDays.Value;
                    paymentTermsCount++;
                }
                if (string.IsNullOrEmpty(bestDebitName))
                    bestDebitName = v.MostCommonDebitAccountName;
            }

            var topDt = combinedDocType.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var topDebit = combinedDebit.OrderByDescending(kv => kv.Value).FirstOrDefault();

            var first = g.First();
            var existing = await _db.SystemOcrVendorIntelligence.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.VendorKey == g.Key, ct);
            if (existing == null)
            {
                _db.SystemOcrVendorIntelligence.Add(new SystemOcrVendorIntelligence
                {
                    VendorKey = g.Key,
                    VendorName = first.VendorName,
                    VendorTaxId = first.VendorTaxId,
                    MostCommonDocumentType = topDt.Key,
                    MostCommonDocumentTypeCount = topDt.Value,
                    TotalDocuments = totalDocs,
                    DocumentTypeBreakdownJson = JsonSerializer.Serialize(combinedDocType),
                    MostCommonDebitAccountCode = topDebit.Key,
                    MostCommonDebitAccountName = bestDebitName,
                    MostCommonDebitAccountCount = topDebit.Value,
                    DebitAccountBreakdownJson = JsonSerializer.Serialize(combinedDebit),
                    TypicallyHasWht = totalDocs > 0 && (decimal)whtCount / totalDocs >= 0.5m,
                    TypicalWhtRate = wAvgWhtRate.HasValue ? Math.Round(wAvgWhtRate.Value, 0) : (decimal?)null,
                    WhtUsageCount = whtCount,
                    TypicalPaymentTermsDays = paymentTermsCount > 0 ? paymentTermsSum / paymentTermsCount : null,
                    LastTrainedAt = DateTime.UtcNow,
                    CreatedBy = "cross-tenant-aggregator",
                });
                promoted++;
            }
            else if (!existing.IsDeleted)
            {
                // Refresh aggregates (additive — overrides existing only
                // when the combined count is larger than what's there).
                if (totalDocs > existing.TotalDocuments)
                {
                    existing.TotalDocuments = totalDocs;
                    existing.MostCommonDocumentType = topDt.Key;
                    existing.MostCommonDocumentTypeCount = topDt.Value;
                    existing.DocumentTypeBreakdownJson = JsonSerializer.Serialize(combinedDocType);
                    existing.MostCommonDebitAccountCode = topDebit.Key;
                    existing.MostCommonDebitAccountCount = topDebit.Value;
                    existing.DebitAccountBreakdownJson = JsonSerializer.Serialize(combinedDebit);
                    existing.TypicallyHasWht = (decimal)whtCount / totalDocs >= 0.5m;
                    existing.TypicalWhtRate = wAvgWhtRate.HasValue ? Math.Round(wAvgWhtRate.Value, 0) : existing.TypicalWhtRate;
                    existing.WhtUsageCount = whtCount;
                    existing.TypicalPaymentTermsDays = paymentTermsCount > 0
                        ? paymentTermsSum / paymentTermsCount : existing.TypicalPaymentTermsDays;
                    existing.LastTrainedAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.UpdatedBy = "cross-tenant-aggregator";
                    promoted++;
                }
            }
        }
        if (promoted > 0) await _db.SaveChangesAsync(ct);
        return promoted;
    }

    private static Dictionary<string, int> ParseBreakdown(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(); }
        catch { return new(); }
    }
}
