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

        // Direct UPDATE/DELETE — no entity materialization, no change tracking.
        // For 10k+ patterns this is ~30ms instead of 200-500ms with .ToListAsync + RemoveRange.

        // 1. Cap TimesConfirmed at 100
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.TimesConfirmed > 100)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TimesConfirmed, 100), ct);

        // 2. Prune stable negative examples (60+ days, FailureCount <= 2)
        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ExecuteDeleteAsync(ct);

        // 3. GC abandoned positive patterns (180+ days, TimesConfirmed <= 1)
        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ExecuteDeleteAsync(ct);

        // 4. Cleanup orphaned scan files
        //    Two cohorts targeted:
        //      a) Failed scans older than 7 days — likely won't be retried
        //      b) Completed scans without CreatedDocumentId older than 30 days
        //         — user reviewed but never converted to document; cold storage is wasteful
        var orphanFilesPurged = 0;
        var orphanRowsDeleted = 0;
        try
        {
            var failedCutoff = DateTime.UtcNow.AddDays(-7);
            var abandonedCutoff2 = DateTime.UtcNow.AddDays(-30);

            // Identify scan ids and their file ids in one query
            var orphans = await _db.Set<Models.Entities.OcrScanResult>()
                .Where(s =>
                    (s.ScanStatus == "Failed" && s.CreatedAt < failedCutoff) ||
                    (s.ScanStatus == "Completed" && s.CreatedDocumentId == null && s.CreatedAt < abandonedCutoff2))
                .Where(s => s.FileAttachmentId != null)
                .Select(s => new { s.Id, FileId = s.FileAttachmentId!.Value })
                .Take(500)  // throttled per cycle
                .ToListAsync(ct);

            foreach (var orphan in orphans)
            {
                var attachment = await _db.FileAttachments
                    .FirstOrDefaultAsync(f => f.Id == orphan.FileId
                        && f.EntityType == "OcrScan", ct);   // only purge ones still tagged as scan
                if (attachment == null) continue;
                try
                {
                    if (System.IO.File.Exists(attachment.StoragePath))
                    {
                        System.IO.File.Delete(attachment.StoragePath);
                        orphanFilesPurged++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete orphan file {Path}", attachment.StoragePath);
                }
                _db.FileAttachments.Remove(attachment);
                orphanRowsDeleted++;
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphan-file cleanup pass failed (non-fatal)");
        }

        _logger.LogInformation(
            "OCR self-correction maintenance: capped {Capped}, pruned {Stale} stale negatives, removed {Abandoned} abandoned positives, purged {Files} orphan files ({Rows} rows) in {Elapsed}ms",
            capped, stale, abandoned, orphanFilesPurged, orphanRowsDeleted,
            (int)(DateTime.UtcNow - maintenanceStart).TotalMilliseconds);
    }

    /// <summary>Run maintenance scoped to a single company (admin debug tool).</summary>
    public async Task RunMaintenanceForCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        var capped = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId && p.TimesConfirmed > 100)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TimesConfirmed, 100), ct);

        var staleCutoff = DateTime.UtcNow.AddDays(-60);
        var stale = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && p.IsNegativeExample && p.LastConfirmedAt < staleCutoff && p.FailureCount <= 2)
            .ExecuteDeleteAsync(ct);

        var abandonedCutoff = DateTime.UtcNow.AddDays(-180);
        var abandoned = await _db.OcrLearnedPatterns
            .Where(p => p.CompanyId == companyId
                && !p.IsNegativeExample && p.LastConfirmedAt < abandonedCutoff && p.TimesConfirmed <= 1)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation("Per-company OCR maintenance for {Company}: {Capped}/{Stale}/{Abandoned}",
            companyId, capped, stale, abandoned);
    }
}
