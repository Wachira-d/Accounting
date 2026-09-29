using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;

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
/// Prompt schema (ตัวจริง = <c>AnomalyExplainPrompt.Build</c> · รอบ 200 ทีม RF แก้ doc ให้ตรง — เดิมบรรยาย schema ที่ไม่มีใครส่ง):
///   { "task": "anomaly_explain",
///     "anomaly": { "amount": 12345.67, "mad_z_score": …, "typical_range": {min,max} },
///     "vendor_history_12mo": { "history": [...] } | { "count", "avg", "min", "max", "median" },
///     "recent_12mo": [...], "local_model": { "pick": "LikelyError|NeedReview|null" } }
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
        // รอบ 200 ทีม RF (R200-X2): อ่าน payload ของ AnomalyExplainPrompt.Build **ตัวจริง** (anomaly.amount · vendor_history_12mo ·
        // recent_12mo · local_model.pick) ผ่าน Helpers/AnomalyExplainStudent — เดิมอ่าน root.amount/root.history ที่ prompt ไม่เคยส่ง
        // ⇒ คืน null ทุกครั้ง · และ PrimaryAnswer ต้องเป็นค่าในชุด LikelyError/LikelyLegit/NeedReview (เดิมเป็น JSON ⇒ controller
        // บันทึกไม่ได้) · คำอธิบาย/ข้อแนะนำไปทาง StructuredJson (รูปเดียวกับคำตอบครู) ⇒ ตอนปิด provider ยังได้คำอธิบายครบ
        var facts = Accounting.Helpers.AnomalyExplainStudent.ReadPrompt(inputJson);
        if (facts == null) return Task.FromResult<LocalPrediction?>(null);

        // ตัวตรวจจับ MAD z ตัวเดียวกับชั้นตรวจจับ (single source of truth ของ "ผิดปกติไหม")
        decimal? seriesZ = null;
        if (facts.Amount > 0 && facts.Series.Count >= 3)
            seriesZ = AmountAnomalyDetector.CheckModifiedZScore(facts.Amount, facts.Series)?.Score;

        var answer = Accounting.Helpers.AnomalyExplainStudent.Decide(facts, seriesZ);
        var alternatives = Accounting.Helpers.AnomalyExplainVerdict.Candidates.Where(c => c != answer.Primary).ToArray();
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            PrimaryAnswer: answer.Primary,
            Confidence: answer.Confidence,
            Alternatives: alternatives,
            SupportingSamples: facts.Series.Count > 0 ? facts.Series.Count : facts.HistoryCount ?? 0,
            ModelVersion: Version)
        {
            StructuredJson = answer.ToJson(),
        });
    }
}
