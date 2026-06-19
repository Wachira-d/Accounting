using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Reusable local distillation student for any single-answer AI feature that
/// doesn't justify a bespoke model. One instance owns ONE AiFeatureKey (set
/// via the constructor) and is registered once per gap-feature in Program.cs.
///
/// Why it exists — "Local-First Sovereignty" (ดู CLAUDE.md): every feature that
/// calls DeepSeek MUST have a registered ILocalDistillationModel, otherwise the
/// orchestrator's FallbackToLocal returns a null answer when the provider is
/// disabled / over budget / unreachable — i.e. the feature silently dies when
/// AI is switched off. This model guarantees a non-null local answer for those
/// features the moment ANY user-confirmed feedback exists.
///
/// Two-tier prediction:
///   1. EXACT — normalised fingerprint of the prompt → most-confirmed answer.
///      Wilson-scored; after a handful of identical confirms the confidence
///      climbs past the Hybrid short-circuit threshold so the orchestrator
///      stops paying DeepSeek for the repeated input (true distillation).
///   2. MAJORITY fallback — the company's single most-confirmed answer for the
///      feature, capped at low confidence so it NEVER short-circuits a live
///      provider, but IS what FallbackToLocal serves when AI is unavailable.
///      This is the cold-start safety net that keeps the feature working at
///      "best-known default" quality with the provider fully off.
///
/// Normalisation strips volatile tokens (tax IDs, amounts, dates, doc numbers)
/// so (a) similar inputs collapse onto one key, and (b) the train-time
/// PII-sanitised prompt and the predict-time raw prompt converge to the same
/// fingerprint. For structured/bulk/free-form-essay features (OcrFullReview,
/// ImportColumnMatch, AgingExplanation, …) a single-answer model is the wrong
/// shape — those keep their own heuristic fallbacks and are NOT registered here.
/// </summary>
public sealed class GenericFeedbackDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey { get; }
    public string Version { get; private set; } = "v0";
    public bool IsReady
    {
        get { lock (_lock) return _entries.Count > 0 || _majority.Count > 0; }
    }

    private readonly IServiceProvider _services;
    private readonly ILogger<GenericFeedbackDistillationModel> _logger;

    // Exact-input memory: (company, fingerprint) → answer candidates ranked by Wilson.
    private readonly Dictionary<(Guid CompanyId, string Key), List<Cand>> _entries = new();
    // Cold-start fallback: company → its single most-confirmed answer for this feature.
    private readonly Dictionary<Guid, (string Answer, decimal Confidence, int N)> _majority = new();
    private readonly object _lock = new();

    /// <summary>Majority-fallback confidence is capped here so it stays BELOW
    /// the default Hybrid short-circuit threshold (0.85) — it must never make
    /// the orchestrator skip a healthy provider, only serve as the answer when
    /// the provider is unavailable.</summary>
    private const decimal MajorityConfidenceCap = 0.45m;

    public GenericFeedbackDistillationModel(
        AiFeatureKey featureKey,
        IServiceProvider services,
        ILogger<GenericFeedbackDistillationModel> logger)
    {
        FeatureKey = featureKey;
        _services = services;
        _logger = logger;
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var featureName = FeatureKey.ToString();
        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                        && f.FeatureKey == featureName
                        && f.UserChosenAt != null)
            .Select(f => new { f.PromptJson, f.UserChosenAnswer, f.UserAcceptedAi, f.AiPrimaryAnswer })
            .ToListAsync(ct);

        // (fingerprint → answer → confirmed/overridden) + company-wide answer tally.
        var perInput = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        var companyTally = new Dictionary<string, (int Confirmed, int Overridden)>();

        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var key = Fingerprint(r.PromptJson);
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);

            if (!string.IsNullOrEmpty(key))
            {
                if (!perInput.TryGetValue(key, out var perAns))
                    perInput[key] = perAns = new Dictionary<string, (int, int)>();
                var slot = perAns.GetValueOrDefault(r.UserChosenAnswer);
                perAns[r.UserChosenAnswer] = isConfirm
                    ? (slot.Item1 + 1, slot.Item2)
                    : (slot.Item1, slot.Item2 + 1);
            }

            var ctally = companyTally.GetValueOrDefault(r.UserChosenAnswer);
            companyTally[r.UserChosenAnswer] = isConfirm
                ? (ctally.Item1 + 1, ctally.Item2)
                : (ctally.Item1, ctally.Item2 + 1);
        }

        lock (_lock)
        {
            // Drop this company's stale state, then rebuild.
            var stale = _entries.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
            _majority.Remove(companyId);

            foreach (var (key, candidates) in perInput)
            {
                var scored = candidates.Select(kv => new Cand(
                        Answer: kv.Key,
                        Confirmed: kv.Value.Confirmed,
                        Overridden: kv.Value.Overridden,
                        WilsonScore: Wilson(kv.Value.Confirmed, kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(c => c.WilsonScore)
                    .ToList();
                _entries[(companyId, key)] = scored;
            }

            if (companyTally.Count > 0)
            {
                var best = companyTally
                    .Select(kv => (Answer: kv.Key, N: kv.Value.Confirmed + kv.Value.Overridden,
                                   Score: Wilson(kv.Value.Confirmed, kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.N)
                    .First();
                _majority[companyId] = (best.Answer, Math.Min(MajorityConfidenceCap, best.Score), best.N);
            }
        }

        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "GenericFeedbackDistillationModel[{Feature}] reloaded for company {Cid}: {Keys} fingerprints, majority={HasMaj} from {Rows} rows",
            featureName, companyId, perInput.Count, companyTally.Count > 0, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var key = Fingerprint(inputJson);
        lock (_lock)
        {
            // Tier 1 — exact fingerprint hit (can reach short-circuit confidence).
            if (!string.IsNullOrEmpty(key)
                && _entries.TryGetValue((companyId, key), out var hits) && hits.Count > 0)
            {
                var top = hits[0];
                return Task.FromResult<LocalPrediction?>(new LocalPrediction(
                    PrimaryAnswer: top.Answer,
                    Confidence: top.WilsonScore,
                    Alternatives: hits.Skip(1).Take(2).Select(h => h.Answer).ToList(),
                    SupportingSamples: top.Confirmed + top.Overridden,
                    ModelVersion: Version));
            }

            // Tier 2 — company majority fallback (capped confidence; the
            // safety net that keeps the feature alive when AI is switched off).
            if (_majority.TryGetValue(companyId, out var maj))
            {
                return Task.FromResult<LocalPrediction?>(new LocalPrediction(
                    PrimaryAnswer: maj.Answer,
                    Confidence: maj.Confidence,
                    Alternatives: Array.Empty<string>(),
                    SupportingSamples: maj.N,
                    ModelVersion: Version + "-majority"));
            }
        }
        return Task.FromResult<LocalPrediction?>(null);
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

    /// <summary>SHA-256 of the volatile-token-stripped prompt JSON. Stripping
    /// tax IDs / amounts / dates / doc numbers means (a) inputs that differ only
    /// in those values share a key, and (b) the PII-sanitised prompt stored at
    /// train time and the raw prompt seen at predict time converge.</summary>
    private static string Fingerprint(string? promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return "";
        var normalised = Normalise(promptJson);
        if (normalised.Length == 0) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(bytes);
    }

    private static readonly System.Text.RegularExpressions.Regex _taxIdRe =
        new(@"\b\d{13}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _docNumRe =
        new(@"\b(?:INV|TXN|REF|PV|RV|BIL|TAX|IV)[-_/]?\d{4,}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex _dateRe =
        new(@"\b\d{4}-\d{2}-\d{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _amountRe =
        new(@"\b[\d,]+\.?\d*\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _wsRe =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Normalise(string s)
    {
        // Best-effort: re-serialise to canonical JSON so key ordering /
        // whitespace don't affect the fingerprint; fall back to raw on parse error.
        var t = s;
        try
        {
            using var doc = JsonDocument.Parse(s);
            t = JsonSerializer.Serialize(doc.RootElement);
        }
        catch { /* not JSON — fingerprint the raw text */ }

        t = t.ToLowerInvariant();
        t = _taxIdRe.Replace(t, "<taxid>");
        t = _docNumRe.Replace(t, "<docnum>");
        t = _dateRe.Replace(t, "<date>");
        t = _amountRe.Replace(t, "<amt>");
        t = _wsRe.Replace(t, " ").Trim();
        return t;
    }

    private sealed record Cand(string Answer, int Confirmed, int Overridden, decimal WilsonScore);
}
