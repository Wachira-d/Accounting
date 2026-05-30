using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for BankStatementMatch — wraps the
/// existing BankReconciliationPatterns table (which BankService.Learning
/// already maintains on every confirmed reconciliation) and exposes it
/// as an ILocalDistillationModel so the orchestrator can short-circuit
/// the DeepSeek call when the pattern table already knows the answer.
///
/// The pattern table is already a battle-tested fuzzy matcher (token
/// signature + amount bucket + counterparty hint); this model just
/// adds Wilson-score confidence so the orchestrator can route on it
/// the same way it routes vendor canon or GL account predictions.
///
/// Prompt schema (matched against BankAndAnalyticsPrompts.BuildBankStatementMatchUserPrompt):
///   { "bankTxn": { "description": "...", "reference": "...",
///                  "payee": "...", "amount": 1234.56, "direction": "In"|"Out" },
///     "candidates": [...] }
/// We extract the description signature + amount bucket, look up the
/// pattern table, and return the most-confirmed candidate. The
/// orchestrator wraps with the same head-to-head logging that vendor
/// canon uses.
/// </summary>
public class BankMatchDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.BankStatementMatch;
    public string Version { get; private set; } = "v0";
    public bool IsReady => _entries.Count > 0;

    private readonly IServiceProvider _services;
    private readonly ILogger<BankMatchDistillationModel> _logger;

    // Keyed by (CompanyId, signature, amountBucket). Value is the
    // ranked list of (target type + contact id) candidates the user
    // confirmed for this signature.
    private readonly Dictionary<(Guid CompanyId, string Signature, string AmountBucket), List<PatternHit>> _entries = new();
    private readonly object _lock = new();

    public BankMatchDistillationModel(IServiceProvider services,
        ILogger<BankMatchDistillationModel> logger)
    { _services = services; _logger = logger; }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // Read directly from BankReconciliationPatterns — BankService
        // already maintains TimesConfirmed on every user confirm. No
        // need to re-mine AiSuggestionFeedback; the bank pattern table
        // IS the supervised corpus for this feature.
        var rows = await db.BankReconciliationPatterns.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.TimesConfirmed > 0)
            .Select(p => new
            {
                p.DescriptionSignature, p.AmountBucket, p.TargetType,
                p.ContactId, p.TargetAccountCode, p.TimesConfirmed,
                p.AvgAmount, p.LastUsedAt,
            })
            .ToListAsync(ct);

        // Group by (sig, bucket) — same key the orchestrator queries.
        var grouped = rows.GroupBy(r => (r.DescriptionSignature, r.AmountBucket));
        var fresh = new Dictionary<(Guid, string, string), List<PatternHit>>();
        foreach (var g in grouped)
        {
            var totalConfirms = g.Sum(x => x.TimesConfirmed);
            var hits = g.OrderByDescending(x => x.TimesConfirmed)
                .ThenByDescending(x => x.LastUsedAt)
                .Take(5)
                .Select(x => new PatternHit(
                    TargetType: x.TargetType.ToString(),
                    ContactId: x.ContactId?.ToString(),
                    AccountCode: x.TargetAccountCode,
                    AvgAmount: x.AvgAmount,
                    Confirmed: x.TimesConfirmed,
                    // Wilson over (confirms / total-for-this-key) — a
                    // sig+bucket with 50 confirms all routing to the
                    // same contact gets near-1.0; 1 confirm gets the
                    // appropriate small-sample discount.
                    WilsonScore: Wilson(x.TimesConfirmed, Math.Max(x.TimesConfirmed, totalConfirms))))
                .ToList();
            fresh[(companyId, g.Key.DescriptionSignature, g.Key.AmountBucket)] = hits;
        }

        lock (_lock)
        {
            var stale = _entries.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
            foreach (var (k, v) in fresh) _entries[k] = v;
        }
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "BankMatchDistillationModel reloaded for company {Cid}: {Keys} keys from {Rows} pattern rows",
            companyId, fresh.Count, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var (sig, bucket) = ExtractKey(inputJson);
        if (string.IsNullOrEmpty(sig)) return Task.FromResult<LocalPrediction?>(null);
        List<PatternHit>? hits;
        lock (_lock) hits = _entries.GetValueOrDefault((companyId, sig, bucket));
        if (hits == null || hits.Count == 0)
            return Task.FromResult<LocalPrediction?>(null);

        var top = hits[0];
        // Encode the answer the orchestrator expects: a JSON blob with
        // matched type + contact id so the caller can act on it without
        // re-parsing the pattern row.
        var primary = JsonSerializer.Serialize(new
        {
            type = top.TargetType,
            contactId = top.ContactId,
            accountCode = top.AccountCode,
        });
        var alts = hits.Skip(1).Take(3).Select(h => JsonSerializer.Serialize(new
        {
            type = h.TargetType, contactId = h.ContactId, accountCode = h.AccountCode,
        })).ToList();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            PrimaryAnswer: primary,
            Confidence: top.WilsonScore,
            Alternatives: alts,
            SupportingSamples: top.Confirmed,
            ModelVersion: Version));
    }

    private static decimal Wilson(int successes, int n)
    {
        if (n == 0) return 0m;
        const double z = 1.96;
        var p = (double)successes / n;
        var denom = 1 + z * z / n;
        var center = p + z * z / (2 * n);
        var spread = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
        return (decimal)Math.Max(0, (center - spread) / denom);
    }

    /// <summary>Pull description + amount from the orchestrator's
    /// prompt JSON and recompute the same signature + bucket the
    /// pattern table is keyed by. Reuses the public helpers BankService
    /// already exposes for symmetry — if signatures ever diverge,
    /// pattern-based suggestions would silently miss.</summary>
    private static (string Signature, string AmountBucket) ExtractKey(string promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            if (!doc.RootElement.TryGetProperty("bankTxn", out var t)) return ("", "");
            var desc = t.TryGetProperty("description", out var d) ? d.GetString() : null;
            var refr = t.TryGetProperty("reference", out var r) ? r.GetString() : null;
            var payee = t.TryGetProperty("payee", out var p) ? p.GetString() : null;
            var amount = t.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0m;
            var sig = BankService.ComputeDescriptionSignature(desc, refr, payee);
            var bucket = BankService.ComputeAmountBucket(amount);
            return (sig, bucket);
        }
        catch { return ("", ""); }
    }

    private sealed record PatternHit(
        string TargetType, string? ContactId, string? AccountCode,
        decimal AvgAmount, int Confirmed, decimal WilsonScore);
}
