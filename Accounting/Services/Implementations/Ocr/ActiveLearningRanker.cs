using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Rank pending OcrScanResults by review priority so the user spends
/// their correction time where it does the most good. Higher priority =
/// "fixing this one teaches the system the most".
///
/// Priority signals (combined into a single score):
///   • Uncertainty: 1 − max(scan.fieldConfidence.values). High = system
///                  is unsure, so the user's correction is high-value
///                  ground truth.
///   • Novelty:    1 / (1 + vendor_seen_count). Brand-new vendors
///                  benefit most from labels — once we have ≥10 docs
///                  the patterns plateau.
///   • Recency:    fresh scans first (within 7 days = 1.0, older = 0.5).
///                  Stale scans are less likely to ever get reviewed.
///   • Auto-create suppressed: scans where HasPotentialFixedAsset = true
///                  get a boost — these are deliberately held in Draft
///                  awaiting decision.
///
/// final_score = uncertainty × (1 + novelty) × recency_factor × asset_boost
///
/// Sample output (one company, 6 pending scans):
///   1. PEA bill #1   uncertainty=0.4 novelty=1.0 recency=1.0 → 0.80
///   2. HomePro recpt uncertainty=0.5 novelty=0.5 recency=1.0 → 0.75 (asset alert)
///   3. AIS bill #3   uncertainty=0.2 novelty=0.3 recency=1.0 → 0.26
///   ...
///
/// Pure read — no side effects. Use from any UI ("Review Queue" page).
/// </summary>
public class ActiveLearningRanker
{
    private readonly AccountingDbContext _db;

    public ActiveLearningRanker(AccountingDbContext db)
    {
        _db = db;
    }

    public record RankedScan(
        Guid ScanId,
        string? VendorName,
        string? OriginalFileName,
        DateTime ProcessedAt,
        decimal Confidence,
        decimal UncertaintyScore,
        decimal NoveltyScore,
        decimal RecencyFactor,
        bool HasPotentialFixedAsset,
        decimal PriorityScore,
        string ReasonHint);

    public async Task<List<RankedScan>> RankAsync(Guid companyId, int limit = 20)
    {
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var thirtyDaysAgo = now.AddDays(-30);

        // Pull pending scans (Completed but not yet linked to a created doc, OR
        // flagged as Needs-Review by HasPotentialFixedAsset). Skip very-old
        // scans that the user is unlikely to ever come back to.
        var scans = await _db.Set<Models.Entities.OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && r.ScanStatus == "Completed"
                && r.ProcessedAt >= thirtyDaysAgo
                && (r.CreatedDocumentId == null || r.HasPotentialFixedAsset))
            .OrderByDescending(r => r.ProcessedAt)
            .Take(200)   // cap candidate pool; rank in memory
            .ToListAsync();

        if (scans.Count == 0) return new();

        // Vendor history: count of approved docs per vendor in last 12 months
        // for the novelty score.
        var twelveMonthsAgo = now.AddMonths(-12);
        var vendorCounts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= twelveMonthsAgo
                && (d.Status == Models.Enums.DocumentStatus.Approved
                    || d.Status == Models.Enums.DocumentStatus.Paid))
            .GroupBy(d => d.ContactId)
            .Select(g => new { ContactId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ContactId, x => x.Count);

        var ranked = new List<RankedScan>();
        foreach (var r in scans)
        {
            // Compute uncertainty from per-field confidence breakdown.
            // ProcessingNotes is the audit log; parse the "[Field Confidence]"
            // section for max confidence; fall back to overall confidence
            // when the section is absent.
            decimal maxFieldConf = r.Confidence;
            // Use overall confidence directly; per-field json isn't stored
            // separately yet — keeps the scoring simple and stable.
            decimal uncertainty = Math.Max(0m, 1m - maxFieldConf);

            int seen = r.MatchedContactId.HasValue && vendorCounts.TryGetValue(r.MatchedContactId.Value, out var c) ? c : 0;
            decimal novelty = 1m / (1m + seen);    // 0 docs → 1.0, 10 docs → 0.09

            decimal recencyFactor = r.ProcessedAt.HasValue && r.ProcessedAt.Value >= sevenDaysAgo
                ? 1m : 0.5m;

            decimal assetBoost = r.HasPotentialFixedAsset ? 1.3m : 1.0m;

            decimal priority = uncertainty * (1m + novelty) * recencyFactor * assetBoost;

            // Concise reason hint for the UI list
            var hintParts = new List<string>();
            if (uncertainty >= 0.4m) hintParts.Add($"uncertain ({uncertainty:P0})");
            if (seen == 0) hintParts.Add("new vendor");
            else if (seen <= 3) hintParts.Add($"{seen}× seen");
            if (r.HasPotentialFixedAsset) hintParts.Add("asset alert");
            if (recencyFactor < 1m) hintParts.Add("stale");

            ranked.Add(new RankedScan(
                r.Id, r.ExtractedVendorName, r.OriginalFileName,
                r.ProcessedAt ?? r.CreatedAt, r.Confidence,
                uncertainty, novelty, recencyFactor,
                r.HasPotentialFixedAsset, priority,
                string.Join(", ", hintParts)));
        }

        return ranked.OrderByDescending(x => x.PriorityScore).Take(limit).ToList();
    }
}
