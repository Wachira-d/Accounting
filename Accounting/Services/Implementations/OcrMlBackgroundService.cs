using Accounting.Services.Implementations.Ocr;
using Microsoft.Extensions.Configuration;

namespace Accounting.Services.Implementations;

/// <summary>
/// Nightly batch job that keeps the system-wide OCR knowledge tables
/// fresh without operator intervention. Three workloads run on a single
/// timer so they don't fight for the DB connection pool:
///
///   1. CrossTenantKnowledgeAggregator
///      Promotes per-tenant learned mappings to system tables when ≥N
///      distinct tenants agree on them (k-anonymity).
///
///   2. AssociationRuleMiner.MineSystemWideAsync
///      Refreshes the Apriori basket-analysis rules from the latest
///      approved-document history. Replaces stale rules with the top
///      2000 by lift.
///
///   3. (future) Recurring-expense / clustering caches — currently
///      computed on-demand; can be moved here if profiler shows them
///      to be hot spots.
///
/// Schedule:
///   • Default 24 hours, configurable via appsettings
///       "Ocr:Ml:NightlyIntervalHours" (default 24)
///       "Ocr:Ml:Enabled"               (default true)
///   • First run starts ~5 minutes after app boot — lets DB migrations
///     and seed steps complete first.
///
/// Resilience:
///   • Each workload is wrapped in its own try/catch — one failing job
///     doesn't block the next.
///   • IServiceScope is created per run so DbContext + DI graph are
///     fresh; no cross-tick state leakage.
/// </summary>
public class OcrMlBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<OcrMlBackgroundService> _logger;

    public OcrMlBackgroundService(IServiceProvider services, IConfiguration config,
        ILogger<OcrMlBackgroundService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue("Ocr:Ml:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("OCR ML background service disabled via config");
            return;
        }

        var intervalHours = _config.GetValue("Ocr:Ml:NightlyIntervalHours", 24);
        var interval = TimeSpan.FromHours(Math.Max(1, intervalHours));

        // Delay first run so app startup (migrations / seed) finishes first
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR ML nightly batch failed (will retry next tick)");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass — exposed so the admin endpoint can trigger
    /// the same code path as the scheduler does.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var minTenants = _config.GetValue("Ocr:Ml:AggregateMinTenants", 2);
        var minSupport = _config.GetValue("Ocr:Ml:MineMinSupport", 0.005m);
        var minConfidence = _config.GetValue("Ocr:Ml:MineMinConfidence", 0.5m);
        var sinceMonths = _config.GetValue("Ocr:Ml:MineSinceMonths", 24);

        // 1. Cross-tenant aggregation
        try
        {
            var aggregator = sp.GetRequiredService<CrossTenantKnowledgeAggregator>();
            var r = await aggregator.AggregateAsync(minTenants, ct);
            _logger.LogInformation("[ML-Nightly] Aggregator: tenants={T} cat+{C} vi+{V} ({D}ms)",
                r.TenantsConsidered, r.CategoryMappingsPromoted, r.VendorIntelligencePromoted,
                r.Duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ML-Nightly] Aggregator step failed");
        }

        // 2. Association rule mining
        try
        {
            var miner = sp.GetRequiredService<AssociationRuleMiner>();
            var r = await miner.MineSystemWideAsync(minSupport, minConfidence, sinceMonths, ct);
            _logger.LogInformation("[ML-Nightly] Miner: scanned={S} discovered={D} persisted={P} ({Ms}ms)",
                r.TransactionsScanned, r.RulesDiscovered, r.RulesPersisted, r.Duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ML-Nightly] Miner step failed");
        }
    }
}
