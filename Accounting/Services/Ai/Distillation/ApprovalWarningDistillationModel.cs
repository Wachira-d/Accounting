using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for ApprovalWarningFixSuggestion. Mines
/// every confirmed warning_fix feedback row and learns:
///
///   (normalised warning template) → most-confirmed fix action
///
/// Approval warnings are highly repetitive — "ใบกำกับภาษีไม่มี Tax ID"
/// gets flagged hundreds of times across vendors but the fix is always
/// the same. After 10-20 confirms the local model can short-circuit
/// the DeepSeek call with high confidence.
///
/// Normalisation strategy:
///   • Lowercase + collapse whitespace.
///   • Strip vendor names, document numbers, tax IDs, amounts — the
///     warning template ("ใบกำกับภาษีไม่มี Tax ID ของผู้ขาย") is what
///     determines the fix, not the specific values that filled it.
///   • Cap at 80 chars (templates are short).
///
/// Wilson-scored confidence + same head-to-head feedback recording as
/// VendorCanon/GlAccount distillation models.
/// </summary>
public class ApprovalWarningDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.ApprovalWarningFixSuggestion;
    public string Version { get; private set; } = "v0";
    public bool IsReady => _entries.Count > 0;

    private readonly IServiceProvider _services;
    private readonly ILogger<ApprovalWarningDistillationModel> _logger;

    private readonly Dictionary<(Guid CompanyId, string Template), List<FixCandidate>> _entries = new();
    private readonly object _lock = new();

    public ApprovalWarningDistillationModel(IServiceProvider services,
        ILogger<ApprovalWarningDistillationModel> logger)
    { _services = services; _logger = logger; }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                        && f.FeatureKey == nameof(AiFeatureKey.ApprovalWarningFixSuggestion)
                        && f.UserChosenAt != null)
            .Select(f => new
            {
                f.PromptJson, f.UserChosenAnswer, f.UserAcceptedAi, f.AiPrimaryAnswer,
            })
            .ToListAsync(ct);

        var local = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.UserChosenAnswer)) continue;
            var template = ExtractWarningTemplate(r.PromptJson);
            if (string.IsNullOrEmpty(template)) continue;
            if (!local.TryGetValue(template, out var perFix))
            {
                perFix = new Dictionary<string, (int, int)>();
                local[template] = perFix;
            }
            var slot = perFix.GetValueOrDefault(r.UserChosenAnswer);
            var isConfirm = r.UserAcceptedAi == true
                || (r.AiPrimaryAnswer != null && r.AiPrimaryAnswer == r.UserChosenAnswer);
            perFix[r.UserChosenAnswer] = isConfirm
                ? (slot.Item1 + 1, slot.Item2)
                : (slot.Item1, slot.Item2 + 1);
        }

        lock (_lock)
        {
            var stale = _entries.Keys.Where(k => k.CompanyId == companyId).ToList();
            foreach (var k in stale) _entries.Remove(k);
            foreach (var (template, candidates) in local)
            {
                var scored = candidates.Select(kv => new FixCandidate(
                    Fix: kv.Key,
                    Confirmed: kv.Value.Confirmed,
                    Overridden: kv.Value.Overridden,
                    WilsonScore: Wilson(kv.Value.Confirmed,
                        kv.Value.Confirmed + kv.Value.Overridden)))
                    .OrderByDescending(c => c.WilsonScore).ToList();
                _entries[(companyId, template)] = scored;
            }
        }
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmm");
        _logger.LogInformation(
            "ApprovalWarningDistillationModel reloaded for company {Cid}: {Keys} templates from {Rows} rows",
            companyId, local.Count, rows.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var template = ExtractWarningTemplate(inputJson);
        if (string.IsNullOrEmpty(template)) return Task.FromResult<LocalPrediction?>(null);
        List<FixCandidate>? hits;
        lock (_lock) hits = _entries.GetValueOrDefault((companyId, template));
        if (hits == null || hits.Count == 0) return Task.FromResult<LocalPrediction?>(null);

        var top = hits[0];
        var alts = hits.Skip(1).Take(2).Select(h => h.Fix).ToList();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            PrimaryAnswer: top.Fix,
            Confidence: top.WilsonScore,
            Alternatives: alts,
            SupportingSamples: top.Confirmed + top.Overridden,
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

    /// <summary>Normalise the warning into a template: strip vendor
    /// names + tax IDs + amounts + doc numbers + dates so warnings that
    /// say the same thing about different vendors collapse into one
    /// template key. Bulk-call format (warnings array) and single-call
    /// format (warning string) are both supported.</summary>
    private static string ExtractWarningTemplate(string promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            var root = doc.RootElement;
            // Single-warning prompt: { warning: "..." }
            if (root.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String)
                return Normalise(w.GetString() ?? "");
            // Bulk prompt: { warnings: [{ index, text }] } — concatenate
            // template fingerprint across all to capture the bulk pattern.
            if (root.TryGetProperty("warnings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var el in arr.EnumerateArray())
                {
                    var t = el.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                    if (!string.IsNullOrEmpty(t)) parts.Add(Normalise(t));
                }
                return string.Join("|", parts.OrderBy(x => x, StringComparer.Ordinal));
            }
            return "";
        }
        catch { return ""; }
    }

    private static readonly System.Text.RegularExpressions.Regex _taxIdRe =
        new(@"\b\d{13}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _docNumRe =
        new(@"\b(?:INV|TXN|REF|PV|RV|BIL|TAX|IV)[-_/]?\d{4,}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex _amountRe =
        new(@"\b[\d,]+\.?\d*\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _dateRe =
        new(@"\b\d{4}-\d{2}-\d{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _wsRe =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Normalise(string s)
    {
        var t = s.ToLowerInvariant();
        t = _taxIdRe.Replace(t, "<TAXID>");
        t = _docNumRe.Replace(t, "<DOCNUM>");
        t = _dateRe.Replace(t, "<DATE>");
        t = _amountRe.Replace(t, "<AMT>");
        t = _wsRe.Replace(t, " ").Trim();
        if (t.Length > 80) t = t[..80];
        return t;
    }

    private sealed record FixCandidate(
        string Fix, int Confirmed, int Overridden, decimal WilsonScore);
}
