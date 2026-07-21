using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Local distillation model for AnomalyExplanation — answers "why was
/// this flagged + what should the user do?" without DeepSeek when the
/// numeric signal is unambiguous.
///
/// The detection layer (AmountAnomalyDetector) already produces a
/// scored MAD z-score + a Thai-language reason string. This model
/// just promotes that reason + a routed action ("เช็คใบกำกับ", "โทร
/// คอนเฟิร์มกับผู้ขาย", "อาจเป็น typo OCR") into the LocalPrediction
/// the orchestrator expects, so AnomalyExplanation can be served
/// locally for the common case.
///
/// Prompt schema (matches AdvancedPrompts.BuildAnomalyExplanation...):
///   { "amount": 12345.67, "vendor": {...},
///     "history": [1000, 1200, 950, 1100, ...],
///     "documentType": "Invoice" }
///
/// DeepSeek still wins on novel patterns or when the user wants prose
/// commentary; this model handles the "obvious" 80% at zero token cost.
/// </summary>
public class AnomalyExplanationDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.AnomalyExplanation;
    public string Version { get; private set; } = "v1";
    public bool IsReady => true;        // Stateless — uses runtime stats only.

    private readonly ILogger<AnomalyExplanationDistillationModel> _logger;

    public AnomalyExplanationDistillationModel(ILogger<AnomalyExplanationDistillationModel> logger)
    { _logger = logger; }

    public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        // No corpus to mine — explanation logic is deterministic from
        // the input statistics. Bump version on every reload so the
        // health stats attribute correctly when admin retunes thresholds.
        Version = "v" + DateTime.UtcNow.ToString("yyyyMMddHH");
        return Task.CompletedTask;
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var input = ExtractInput(inputJson);
        if (input == null) return Task.FromResult<LocalPrediction?>(null);

        // Defer to the same MAD-z detector the rest of the pipeline
        // already uses — single source of truth for "is this an
        // anomaly". Threshold 3.5 = standard outlier cutoff.
        var result = AmountAnomalyDetector.CheckModifiedZScore(input.Amount, input.History);
        if (result == null) return Task.FromResult<LocalPrediction?>(null);

        // Build the AI-shaped answer: { isAnomaly, severity, reason,
        // suggestedActions[] }. This is what AnomalyExplanation prompts
        // expect DeepSeek to return — we match the schema so call
        // sites don't need a special-case for local vs remote.
        var severity = result.IsAnomaly
            ? (Math.Abs(result.Score) > 5m ? "High" : "Medium")
            : "Low";
        var actions = BuildActions(result, input);
        var answer = JsonSerializer.Serialize(new
        {
            isAnomaly = result.IsAnomaly,
            severity,
            reason = result.Reason,
            score = Math.Round(result.Score, 2),
            suggestedActions = actions,
        });

        // Confidence: high when the MAD z-score is far from the
        // threshold (clearly anomalous OR clearly normal); medium near
        // the boundary so admin can route the borderline 5% to DeepSeek
        // for narrative help.
        var distance = Math.Abs(Math.Abs(result.Score) - 3.5m);
        var confidence = (decimal)Math.Min(0.99, 0.70 + 0.05 * (double)distance);

        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            PrimaryAnswer: answer,
            Confidence: confidence,
            Alternatives: Array.Empty<string>(),
            SupportingSamples: input.History.Count,
            ModelVersion: Version));
    }

    private static IReadOnlyList<string> BuildActions(
        AmountAnomalyDetector.AnomalyResult r, InputDoc input)
    {
        if (!r.IsAnomaly) return Array.Empty<string>();
        var actions = new List<string>();
        if (r.Score > 0)
        {
            // Higher-than-usual — typical reasons in Thai accounting:
            actions.Add("ตรวจสอบใบกำกับภาษีว่า amount ตรงกับเอกสารกระดาษ");
            if (input.Amount > 100_000)
                actions.Add("ยอดเกิน 100,000 — ตรวจสอบ approval ระดับสูงขึ้น");
            actions.Add("เช็คว่าเป็นค่าใช้จ่ายแบบ recurring หรือ one-off");
        }
        else
        {
            // Lower-than-usual — typically: partial payment, refund,
            // or OCR misread of a decimal point.
            actions.Add("ตรวจสอบว่ามียอด partial payment หรือไม่");
            actions.Add("เช็ค OCR ว่าจุดทศนิยมถูกต้อง (เช่น 1,234.50 vs 12.3450)");
        }
        return actions;
    }

    private static InputDoc? ExtractInput(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("amount", out var a)) return null;
            var amount = a.GetDecimal();
            if (amount <= 0) return null;

            var history = new List<decimal>();
            if (root.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in h.EnumerateArray())
                    if (el.TryGetDecimal(out var v) && v > 0) history.Add(v);
            }
            if (history.Count < 3) return null;
            return new InputDoc(amount, history);
        }
        catch { return null; }
    }

    private sealed record InputDoc(decimal Amount, IReadOnlyList<decimal> History);
}
