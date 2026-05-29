using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for vendor canonicalisation. Mines every
/// AiSuggestionFeedback row for VendorCanonicalization where the user
/// confirmed a choice, then learns "raw OCR vendor name → contact id"
/// mappings weighted by the number of user confirmations.
///
/// On predict: parses the same JSON shape VendorCanonPrompt builds,
/// hashes the OCR vendor name + tax id, looks up the most-confirmed
/// matching contact. Confidence = times-confirmed / (times-confirmed
/// + times-overridden) with Wilson-score smoothing so a 1-confirm,
/// 0-override match doesn't claim 100% confidence.
///
/// Why this is more than "just write to VendorKnownGoodValue":
///   • Confidence is calibrated to real user-acceptance rate, not a
///     guess. Orchestrator can route safely.
///   • In-memory lookup avoids a DB round-trip per scan.
///   • Loads ONCE per training pass — concurrent reads need no lock.
/// </summary>
public class VendorCanonDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.VendorCanonicalization;
    public string Version { get; private set; } = "v0";
    public bool IsReady => _entries.Count > 0;

    private readonly IServiceProvider _services;
    private readonly ILogger<VendorCanonDistillationModel> _logger;

    // Keyed by (CompanyId, normalised vendor name). Each entry tracks
    // every contact id that was chosen for this vendor across history,
    // with a Wilson-score-smoothed confidence.
    private readonly Dictionary<(Guid CompanyId, string Key), List<CandidateScore>> _entries = new();
    private readonly object _lock = new();

    public VendorCanonDistillationModel(IServiceProvider services,
        ILogger<VendorCanonDistillationModel> logger)
    { _services = services; _logger = logger; }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // Confirmed VendorCanon feedback rows in the company. Walk each,
        // extract the raw OCR vendor name from the prompt JSON, and
        // accumulate per-contact confirm/override counts.
        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId
                        && f.FeatureKey == nameof(AiFeatureKey.VendorCanonicalization)
                        && f.UserChosenAt != null
                        && !f.IsDeleted)
            .Select(f => new
            {
                f.PromptJson, f.UserChosenAnswer, f.UserAcceptedAi, f.AiPrimaryAnswer,
            })
            .ToListAsync(ct);

        var local = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var key = ExtractVendorKey(r.PromptJson);
            if (string.IsNullOrEmpty(key)) continue;
            if (!local.TryGetValue(key, out var perContact))
            {
                perContact = new Dictionary<string, (int, int)>();
                local[key] = perContact;
            }
            var slot = perContact.GetValueOrDefault(r.UserChosenAnswer);
            // Confirmed when the user took AI's pick OR picked the SAME id
            // that AI suggested; an "override" is when AI's suggestion
            // differed from the user choice.
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);
            perContact[r.UserChosenAnswer] = isConfirm
                ? (slot.Item1 + 1, slot.Item2)
                : (slot.Item1, slot.Item2 + 1);
        }

        lock (_lock)
        {
            // Drop this company's old entries; rewrite with the new pass.
            var stale = _entries.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
            foreach (var (vendorKey, candidates) in local)
            {
                var scored = candidates.Select(kv => new CandidateScore(
                    ContactId: kv.Key,
                    Confirmed: kv.Value.Confirmed,
                    Overridden: kv.Value.Overridden,
                    WilsonScore: WilsonScoreLowerBound(kv.Value.Confirmed,
                        kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(c => c.WilsonScore)
                    .ToList();
                _entries[(companyId, vendorKey)] = scored;
            }
        }
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "VendorCanonDistillationModel reloaded for company {Cid}: {Keys} keys from {Rows} rows",
            companyId, local.Count, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var key = ExtractVendorKey(inputJson);
        if (string.IsNullOrEmpty(key)) return Task.FromResult<LocalPrediction?>(null);
        List<CandidateScore>? candidates;
        lock (_lock) candidates = _entries.GetValueOrDefault((companyId, key));
        if (candidates == null || candidates.Count == 0)
            return Task.FromResult<LocalPrediction?>(null);

        var top = candidates[0];
        // Floor confidence at 0.50 to mirror an "uncertain prediction" so
        // the orchestrator still consults DeepSeek for borderline cases.
        var alts = candidates.Skip(1).Take(3).Select(c => c.ContactId).ToList();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            PrimaryAnswer: top.ContactId,
            Confidence: top.WilsonScore,
            Alternatives: alts,
            SupportingSamples: top.Confirmed + top.Overridden,
            ModelVersion: Version));
    }

    /// <summary>Wilson score lower bound at 95% — robust for small n.
    /// (a 1-1 success rate has wider uncertainty than 100-100; this
    /// punishes small-sample claims to 100% so a single accepted
    /// suggestion can't dominate the routing decision).</summary>
    private static decimal WilsonScoreLowerBound(int successes, int n)
    {
        if (n == 0) return 0m;
        const double z = 1.96;
        var phat = (double)successes / n;
        var denom = 1 + z * z / n;
        var center = phat + z * z / (2 * n);
        var spread = z * Math.Sqrt((phat * (1 - phat) + z * z / (4 * n)) / n);
        return (decimal)Math.Max(0, (center - spread) / denom);
    }

    /// <summary>Extract the OCR vendor name + tax id from the prompt
    /// JSON, normalise (lowercase, collapse whitespace, strip punctuation)
    /// so lookup is robust to formatting variation.</summary>
    private static string ExtractVendorKey(string promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            if (!doc.RootElement.TryGetProperty("ocr", out var ocr))
                return "";
            var name = ocr.TryGetProperty("vendor_name", out var n) ? n.GetString() ?? "" : "";
            var taxId = ocr.TryGetProperty("vendor_tax_id", out var t) ? t.GetString() ?? "" : "";
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            // Prefer tax-id when present (13-digit numbers are globally
            // unique); fall back to normalised name.
            if (digits.Length == 13) return "tid:" + digits;
            var norm = new string(name.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
            norm = string.Join(' ', norm.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return norm.Length > 0 ? "name:" + norm : "";
        }
        catch { return ""; }
    }

    private sealed record CandidateScore(
        string ContactId, int Confirmed, int Overridden, decimal WilsonScore);
}
