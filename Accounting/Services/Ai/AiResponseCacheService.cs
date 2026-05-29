using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Content-addressed prompt cache. Key = SHA-256 hash + CompanyId (tenant
/// isolation MUST hold — a vendor canon match for company A must never
/// serve company B). Hit increments the counter; expired rows are
/// ignored at read time and cleaned by AiCacheCleanupJob.
/// </summary>
public interface IAiResponseCacheService
{
    Task<(bool hit, string? content, Guid? originalFeedbackId, string? modelVersion, decimal? confidence)>
        TryGetAsync(string promptHash, Guid companyId, CancellationToken ct);

    Task PutAsync(string promptHash, string featureKey, string responseJson,
        AiProviderType provider, string? modelVersion, decimal? confidence,
        Guid companyId, int ttlDays, CancellationToken ct);
}

public class AiResponseCacheService : IAiResponseCacheService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AiResponseCacheService> _logger;

    public AiResponseCacheService(AccountingDbContext db, ILogger<AiResponseCacheService> logger)
    { _db = db; _logger = logger; }

    public async Task<(bool hit, string? content, Guid? originalFeedbackId, string? modelVersion, decimal? confidence)>
        TryGetAsync(string promptHash, Guid companyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await _db.AiResponseCaches
            .AsNoTracking()
            .Where(c => c.PromptHash == promptHash
                && (c.CompanyId == companyId || c.CompanyId == null)
                && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (row == null)
            return (false, null, null, null, null);

        // Bump hit count fire-and-forget — don't await so cache lookup
        // stays sub-millisecond and a write failure doesn't break the
        // happy path.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _db.Database.BeginTransaction();
                var tracked = await _db.AiResponseCaches.FirstOrDefaultAsync(c => c.Id == row.Id);
                if (tracked != null)
                {
                    tracked.HitCount++;
                    tracked.LastHitAt = now;
                    await _db.SaveChangesAsync();
                    scope.Commit();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cache hit-count update failed (non-fatal)");
            }
        });

        return (true, row.ResponseJson, null, row.ModelVersion, row.Confidence);
    }

    public async Task PutAsync(string promptHash, string featureKey, string responseJson,
        AiProviderType provider, string? modelVersion, decimal? confidence,
        Guid companyId, int ttlDays, CancellationToken ct)
    {
        try
        {
            var existing = await _db.AiResponseCaches
                .FirstOrDefaultAsync(c => c.PromptHash == promptHash && c.CompanyId == companyId, ct);
            var expires = DateTime.UtcNow.AddDays(Math.Max(1, ttlDays));
            if (existing != null)
            {
                // Same prompt re-asked → refresh expiry + payload (model
                // may have changed; cheaper to overwrite than introduce
                // a "stale flag" column).
                existing.ResponseJson = responseJson;
                existing.ProviderUsed = provider;
                existing.ModelVersion = modelVersion;
                existing.Confidence = confidence;
                existing.ExpiresAt = expires;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.AiResponseCaches.Add(new AiResponseCache
                {
                    PromptHash = promptHash,
                    FeatureKey = featureKey,
                    ResponseJson = responseJson,
                    ProviderUsed = provider,
                    ModelVersion = modelVersion,
                    Confidence = confidence,
                    CompanyId = companyId,
                    ExpiresAt = expires,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Cache write failure must NEVER bubble up — orchestrator
            // already returned the response to the caller.
            _logger.LogWarning(ex, "AiResponseCache put failed for {Feature}", featureKey);
        }
    }
}
