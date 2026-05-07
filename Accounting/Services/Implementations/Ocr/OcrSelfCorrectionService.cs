using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Periodic OCR learning maintenance:
///  • Prunes negative patterns whose FailureCount has stabilized (model has learned)
///  • Caps TimesConfirmed to prevent score inflation
///  • Computes per-company accuracy metrics for monitoring
///  • Garbage-collects very old learned patterns nobody confirms anymore
/// </summary>
public class OcrSelfCorrectionService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<OcrSelfCorrectionService> _logger;

    public OcrSelfCorrectionService(AccountingDbContext db, ILogger<OcrSelfCorrectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record AccuracyReport(
        Guid CompanyId,
        int TotalScans,
        int CorrectedScans,
        decimal CorrectionRate,
        int TotalLearnedPatterns,
        int NegativeExamples,
        int HighConfidencePatterns,
        DateTime GeneratedAt);

    public async Task<List<AccuracyReport>> ComputeAccuracyAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var reports = new List<AccuracyReport>();

        var companyIds = await _db.Set<Models.Entities.OcrScanResult>()
            .Where(r => r.CreatedAt >= since)
            .Select(r => r.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var companyId in companyIds)
        {
            var totalScans = await _db.Set<Models.Entities.OcrScanResult>()
                .CountAsync(r => r.CompanyId == companyId && r.CreatedAt >= since, ct);

            var corrected = await _db.Set<Models.Entities.OcrScanResult>()
                .CountAsync(r => r.CompanyId == companyId && r.CreatedAt >= since
                    && r.UpdatedAt != null, ct);

            var patterns = await _db.OcrLearnedPatterns
                .Where(p => p.CompanyId == companyId)
                .ToListAsync(ct);

            reports.Add(new AccuracyReport(
                companyId,
                TotalScans: totalScans,
                CorrectedScans: corrected,
                CorrectionRate: totalScans == 0 ? 0m : (decimal)corrected / totalScans,
                TotalLearnedPatterns: patterns.Count(p => !p.IsNegativeExample),
                NegativeExamples: patterns.Count(p => p.IsNegativeExample),
                HighConfidencePatterns: patterns.Count(p => !p.IsNegativeExample && p.TimesConfirmed >= 5),
                GeneratedAt: DateTime.UtcNow));
        }

        return reports;
    }

    public async Task RunMaintenanceAsync(CancellationToken ct = default)
    {
        var maintenanceStart = DateTime.UtcNow;

        // 1. Cap TimesConfirmed at 100 to prevent any single pattern dominating scoring
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.TimesConfirmed > 100)
            .ToListAsync(ct);
        foreach (var p in capped) p.TimesConfirmed = 100;

        // 2. Prune negative examples that have been stable (no new failures) for 60+ days
        // — model has already learned to avoid them; clutter dragging down query perf.
        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ToListAsync(ct);
        _db.OcrLearnedPatterns.RemoveRange(stale);

        // 3. Garbage-collect positive patterns that haven't been re-confirmed in 180 days
        // and only have 1 confirmation (one-off / probably unreliable).
        // NOTE: We use 180 days specifically to protect low-volume companies that
        // may scan only a handful of vendor invoices per quarter — anything more
        // aggressive would erase legitimate patterns from infrequent users.
        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ToListAsync(ct);
        _db.OcrLearnedPatterns.RemoveRange(abandoned);

        await _db.SaveChangesAsync(ct);

        // Per-company breakdown for observability
        var perCompanyStats = capped.Concat(stale).Concat(abandoned)
            .GroupBy(p => p.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToList();

        _logger.LogInformation(
            "OCR self-correction maintenance: capped {Capped}, pruned {Stale} stale negatives, removed {Abandoned} abandoned positives across {Companies} companies in {Elapsed}ms",
            capped.Count, stale.Count, abandoned.Count, perCompanyStats.Count,
            (int)(DateTime.UtcNow - maintenanceStart).TotalMilliseconds);
    }

    /// <summary>Run maintenance scoped to a single company (admin debug tool).</summary>
    public async Task RunMaintenanceForCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId && p.TimesConfirmed > 100)
            .ToListAsync(ct);
        foreach (var p in capped) p.TimesConfirmed = 100;

        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ToListAsync(ct);
        _db.OcrLearnedPatterns.RemoveRange(stale);

        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ToListAsync(ct);
        _db.OcrLearnedPatterns.RemoveRange(abandoned);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Per-company OCR maintenance for {Company}: {Capped}/{Stale}/{Abandoned}",
            companyId, capped.Count, stale.Count, abandoned.Count);
    }
}
