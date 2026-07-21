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

        // Bump hit-count synchronously — short row update, on a tracked
        // entity, completes well inside the orchestrator's overall
        // budget. (Earlier fire-and-forget version risked using the
        // scoped DbContext after request disposal — moved to sync
        // here for safety.)
        try
        {
            var tracked = await _db.AiResponseCaches.FirstOrDefaultAsync(c => c.Id == row.Id, ct);
            if (tracked != null)
            {
                tracked.HitCount++;
                tracked.LastHitAt = now;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Hit-count update is non-critical — log + return the
            // cached content regardless. The next hit corrects the
            // count.
            _logger.LogDebug(ex, "Cache hit-count update failed (non-fatal)");
        }

        return (true, row.ResponseJson, null, row.ModelVersion, row.Confidence);
    }

    public async Task PutAsync(string promptHash, string featureKey, string responseJson,
        AiProviderType provider, string? modelVersion, decimal? confidence,
        Guid companyId, int ttlDays, CancellationToken ct)
    {
        // ResponseJson is jsonb — coerce non-JSON content (AI can reply with
        // prose) into a wrapper so the column accepts it. Without this the
        // SaveChangesAsync hit Postgres 22P02 and the failed entity poisoned
        // every subsequent SaveChanges in the same request scope.
        responseJson = CoerceJson(responseJson);
        AiResponseCache? row = null;
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
                row = new AiResponseCache
                {
                    PromptHash = promptHash,
                    FeatureKey = featureKey,
                    ResponseJson = responseJson,
                    ProviderUsed = provider,
                    ModelVersion = modelVersion,
                    Confidence = confidence,
                    CompanyId = companyId,
                    ExpiresAt = expires,
                };
                _db.AiResponseCaches.Add(row);
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Cache write failure must NEVER bubble up — orchestrator
            // already returned the response to the caller. Detach the bad
            // entity so it doesn't taint the next SaveChanges in the request.
            if (row != null)
            {
                try { _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { }
            }
            _logger.LogWarning(ex, "AiResponseCache put failed for {Feature}", featureKey);
        }
    }

    private static string CoerceJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return s; }
        catch { return System.Text.Json.JsonSerializer.Serialize(new { raw = s }); }
    }
}
