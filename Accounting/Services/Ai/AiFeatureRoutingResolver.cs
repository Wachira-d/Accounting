using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Per-feature routing policy lookup. Reads AiFeatureRoutingConfig
/// rows (one per AiFeatureKey) and caches them in-memory for ~1 minute
/// so the orchestrator doesn't issue a SELECT on every AI call.
///
/// Admin writes through SetAsync; the cache is invalidated by version
/// bump so the next read picks up the change without an app restart.
/// </summary>
public interface IAiFeatureRoutingResolver
{
    Task<AiFeatureRoutingDecision> ResolveAsync(AiFeatureKey feature, CancellationToken ct);
    Task SetAsync(AiFeatureKey feature, AiFeatureRoutingMode mode,
        decimal? threshold, decimal? samplingRate, string? note, string? user,
        CancellationToken ct);
    Task<IReadOnlyList<AiFeatureRoutingConfig>> ListAllAsync(CancellationToken ct);
    /// <summary>Force the next ResolveAsync to re-read from DB. Called
    /// after admin SetAsync; also exposed for tests.</summary>
    void InvalidateCache();
}

/// <summary>
/// Effective routing decision the orchestrator acts on. The defaults
/// (Hybrid, 0.85 threshold, 0.10 sampling) match the global constants
/// the orchestrator used to hard-code — admin-set rows OVERRIDE these.
/// </summary>
public sealed record AiFeatureRoutingDecision(
    AiFeatureRoutingMode Mode,
    decimal LocalConfidenceThreshold,
    decimal ProviderSamplingRate);

public class AiFeatureRoutingResolver : IAiFeatureRoutingResolver
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AiFeatureRoutingResolver> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private const decimal DefaultThreshold = 0.85m;
    private const decimal DefaultSampling = 0.10m;
    private const AiFeatureRoutingMode DefaultMode = AiFeatureRoutingMode.Hybrid;

    private Dictionary<string, AiFeatureRoutingDecision> _cache = new();
    private DateTime _cacheLoadedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public AiFeatureRoutingResolver(IServiceProvider services,
        ILogger<AiFeatureRoutingResolver> logger)
    { _services = services; _logger = logger; }

    public async Task<AiFeatureRoutingDecision> ResolveAsync(AiFeatureKey feature, CancellationToken ct)
    {
        await EnsureCacheFreshAsync(ct);
        lock (_lock)
        {
            return _cache.TryGetValue(feature.ToString(), out var dec)
                ? dec
                : new AiFeatureRoutingDecision(DefaultMode, DefaultThreshold, DefaultSampling);
        }
    }

    public async Task SetAsync(AiFeatureKey feature, AiFeatureRoutingMode mode,
        decimal? threshold, decimal? samplingRate, string? note, string? user,
        CancellationToken ct)
    {
        if (threshold.HasValue && (threshold.Value < 0 || threshold.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(threshold), "Must be in [0,1].");
        if (samplingRate.HasValue && (samplingRate.Value < 0 || samplingRate.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(samplingRate), "Must be in [0,1].");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var key = feature.ToString();
        var row = await db.AiFeatureRoutingConfigs.FirstOrDefaultAsync(c => c.FeatureKey == key, ct);
        if (row == null)
        {
            row = new AiFeatureRoutingConfig { FeatureKey = key };
            db.AiFeatureRoutingConfigs.Add(row);
        }
        row.Mode = mode;
        row.LocalConfidenceThreshold = threshold;
        row.ProviderSamplingRate = samplingRate;
        row.AdminNote = note;
        row.LastModifiedBy = user;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        InvalidateCache();
        _logger.LogInformation("AI routing updated by {User}: {Feature} → {Mode}", user, feature, mode);
    }

    public async Task<IReadOnlyList<AiFeatureRoutingConfig>> ListAllAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        return await db.AiFeatureRoutingConfigs.AsNoTracking().OrderBy(c => c.FeatureKey).ToListAsync(ct);
    }

    public void InvalidateCache()
    {
        lock (_lock) _cacheLoadedAt = DateTime.MinValue;
    }

    private async Task EnsureCacheFreshAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cacheLoadedAt < CacheTtl) return;
        }
        Dictionary<string, AiFeatureRoutingDecision> fresh;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var rows = await db.AiFeatureRoutingConfigs.AsNoTracking().ToListAsync(ct);
            fresh = rows.ToDictionary(
                r => r.FeatureKey,
                r => new AiFeatureRoutingDecision(
                    r.Mode,
                    r.LocalConfidenceThreshold ?? DefaultThreshold,
                    r.ProviderSamplingRate ?? DefaultSampling));
        }
        catch (Exception ex)
        {
            // First-run safety: table may not exist yet on a brand-new DB
            // before EnsureCreated+ApplyMissingColumns has run. Fall back
            // to defaults until next reload.
            _logger.LogWarning(ex, "Routing config load failed; using defaults");
            fresh = new();
        }
        lock (_lock) { _cache = fresh; _cacheLoadedAt = DateTime.UtcNow; }
    }
}
