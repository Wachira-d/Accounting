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

    /// <summary>Teacher answers with AiConfidence below this are NOT used as
    /// pseudo-labels — a hesitant DeepSeek answer is too weak to distil.</summary>
    private const decimal TeacherDistillFloor = 0.70m;
    /// <summary>User-confirmed rows count for more than a raw teacher answer:
    /// a human said "yes this is right". Weights feed the (confirmed, total)
    /// tally that the Wilson score is computed over.</summary>
    private const int StrongWeight = 2;
    private const int WeakWeight = 1;

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var featureName = FeatureKey.ToString();
        // Two label sources — this is real teacher→student distillation, not just
        // "learn from corrections": (1) user-confirmed rows (strong truth), and
        // (2) confident DeepSeek answers the user never touched (weak pseudo-
        // labels). Without (2) the student would starve, because users rarely
        // click "confirm" on an answer that was already correct — yet those are
        // exactly the cases we want the local model to reproduce for free.
        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                        && f.FeatureKey == featureName
                        && (f.UserChosenAt != null
                            || (f.Status == AiCallStatus.Success
                                && f.AiPrimaryAnswer != null
                                && f.AiConfidence >= TeacherDistillFloor)))
            .Select(f => new
            {
                f.PromptJson, f.UserChosenAnswer, f.UserChosenAt,
                f.UserAcceptedAi, f.AiPrimaryAnswer,
            })
            .ToListAsync(ct);

        // (fingerprint → answer → confirmed/overridden) + company-wide answer tally.
        var perInput = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        var companyTally = new Dictionary<string, (int Confirmed, int Overridden)>();

        void Add(string? key, string answer, int confirmedDelta, int overriddenDelta)
        {
            if (!string.IsNullOrEmpty(key))
            {
                if (!perInput.TryGetValue(key, out var perAns))
                    perInput[key] = perAns = new Dictionary<string, (int, int)>();
                var s = perAns.GetValueOrDefault(answer);
                perAns[answer] = (s.Item1 + confirmedDelta, s.Item2 + overriddenDelta);
            }
            var c = companyTally.GetValueOrDefault(answer);
            companyTally[answer] = (c.Item1 + confirmedDelta, c.Item2 + overriddenDelta);
        }

        foreach (var r in rows)
        {
            var key = Fingerprint(r.PromptJson);

            if (r.UserChosenAt != null && !string.IsNullOrEmpty(r.UserChosenAnswer))
            {
                // Strong signal — the human's pick is ground truth (high weight).
                Add(key, r.UserChosenAnswer, StrongWeight, 0);
                // If they overrode a DIFFERENT AI answer, record that as a
                // negative example so the wrong answer's score is pulled down.
                if (!string.IsNullOrEmpty(r.AiPrimaryAnswer)
                    && r.AiPrimaryAnswer != r.UserChosenAnswer)
                    Add(key, r.AiPrimaryAnswer, 0, StrongWeight);
            }
            else if (!string.IsNullOrEmpty(r.AiPrimaryAnswer))
            {
                // Weak signal — distil the confident teacher answer (low weight).
                Add(key, r.AiPrimaryAnswer, WeakWeight, 0);
            }
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
    // ⚠️ รูปแบบ "หลัง mask" ของ AiPromptSanitizer ต้อง normalise ให้เป็น token
    // เดียวกับค่าดิบ — แถว feedback ถูกบันทึกด้วย prompt ที่ sanitize แล้ว
    // (AiStripPiiInPrompts=true) ขณะที่ตอนทำนายใช้ prompt ดิบ ถ้าไม่ทำให้ตรงกัน
    // fingerprint จะไม่มีวันชนกัน → Tier-1 exact-memory ของ student ตายสนิท
    private static readonly System.Text.RegularExpressions.Regex _taxIdMaskedRe =
        new(@"\b(?:\dx{10}\d|x{13})\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _phoneRe =
        new(@"\b0\d{1,2}[-\s]?\d{3}[-\s]?\d{4}\b|\b0\d{8,9}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _phoneMaskedRe =
        new(@"\b(?:\d{2}x{4}\d{2}|0x{6})\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _piiHashRe =
        new(@"""h:[0-9a-f]{6,}""", System.Text.RegularExpressions.RegexOptions.Compiled);
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
        // พร้อมกันนี้ทำให้ field ที่ sanitizer แทนด้วย hash ("*_pii"/"*_personal")
        // กลายเป็น token คงที่ทั้งฝั่งบันทึกและฝั่งทำนาย
        var t = s;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(s);
            if (node != null)
            {
                MaskPiiKeys(node);
                t = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            }
        }
        catch { /* not JSON — fingerprint the raw text */ }

        t = t.ToLowerInvariant();
        t = _piiHashRe.Replace(t, "\"<pii>\"");
        // masked ก่อน raw: ค่าที่ถูก mask แล้วมีตัวอักษร x ปน ถ้าปล่อยให้ _amountRe
        // จับก่อนจะได้ "<amt>xxxx<amt>" ซึ่งไม่ตรงกับค่าดิบที่ได้ "<phone>"
        t = _taxIdMaskedRe.Replace(t, "<taxid>");
        t = _taxIdRe.Replace(t, "<taxid>");
        t = _phoneMaskedRe.Replace(t, "<phone>");
        t = _phoneRe.Replace(t, "<phone>");
        t = _docNumRe.Replace(t, "<docnum>");
        t = _dateRe.Replace(t, "<date>");
        t = _amountRe.Replace(t, "<amt>");
        t = _wsRe.Replace(t, " ").Trim();
        return t;
    }

    /// <summary>แทนค่าของ field ที่เป็น PII ตาม convention ของ AiPromptSanitizer
    /// ("*_pii" / "*_personal") ด้วย token คงที่ — ฝั่งบันทึกเก็บเป็น "h:{hash}"
    /// ฝั่งทำนายเป็นค่าดิบ ถ้าไม่ทำให้เหมือนกัน fingerprint จะไม่ตรงกันตลอดไป</summary>
    private static void MaskPiiKeys(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var val = obj[key];
                if (key.EndsWith("_pii", StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("_personal", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = "<pii>";
                }
                else if (val is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
                {
                    MaskPiiKeys(val);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr.Where(i => i != null))
                MaskPiiKeys(item!);
        }
    }

    private sealed record Cand(string Answer, int Confirmed, int Overridden, decimal WilsonScore);
}
