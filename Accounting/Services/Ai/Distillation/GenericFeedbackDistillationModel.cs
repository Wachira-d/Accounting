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
                f.UserAcceptedAi, f.AiPrimaryAnswer, f.UserChoiceOrigin,
            })
            .ToListAsync(ct);

        // (fingerprint → answer → confirmed/overridden) + company-wide answer tally.
        var perInput = new Dictionary<string, Dictionary<string, (int Confirmed, int Overridden)>>();
        var companyTally = new Dictionary<string, (int Confirmed, int Overridden)>();

        // ⚠️ `countTowardMajority` แยกสองถังออกจากกัน (ทีม T3 รอบ 177 §3.1):
        //  • ถังต่ออินพุต (tier 1) รับได้ทุกสัญญาณ — มันผูกกับ "คำถามเดิมเป๊ะ"
        //    อยู่แล้ว การเรียนผิดจึงจำกัดวงอยู่ที่อินพุตนั้น
        //  • ถังรวมทั้งบริษัท (tier 2) ตอบ **ทุกอินพุตที่ไม่เคยเห็น** ⇒ สัญญาณอ่อน
        //    (กดอนุมัติผ่าน · ยืนยันทั้งชุด · คำตอบครูที่ไม่มีใครแตะ) ห้ามเข้าถังนี้
        //    ไม่งั้น "คำตอบยอดฮิตที่ไม่มีใครเคยมอง" จะกลายเป็นค่าตั้งต้นของทั้งบริษัท
        //    โดยเฉพาะตอนปิด provider ซึ่งเป็นโหมดเป้าหมายของโปรเจกต์
        void Add(string? key, string answer, int confirmedDelta, int overriddenDelta,
            bool countTowardMajority)
        {
            if (!string.IsNullOrEmpty(key))
            {
                if (!perInput.TryGetValue(key, out var perAns))
                    perInput[key] = perAns = new Dictionary<string, (int, int)>();
                var s = perAns.GetValueOrDefault(answer);
                perAns[answer] = (s.Item1 + confirmedDelta, s.Item2 + overriddenDelta);
            }
            if (!countTowardMajority) return;
            var c = companyTally.GetValueOrDefault(answer);
            companyTally[answer] = (c.Item1 + confirmedDelta, c.Item2 + overriddenDelta);
        }

        foreach (var r in rows)
        {
            var key = Accounting.Helpers.AiMemoryKey.Of(r.PromptJson);

            if (r.UserChosenAt != null && !string.IsNullOrEmpty(r.UserChosenAnswer))
            {
                // แถวก่อนรอบ 178 ไม่มีค่านี้ — ตีความเป็น Explicit เพื่อไม่ให้ของที่
                // เรียนมาแล้วหายไปทั้งก้อน (กติกาใหม่มีผลกับสิ่งที่เรียนต่อจากนี้)
                var deliberate = r.UserChoiceOrigin is null
                    or Models.Enums.UserChoiceSource.Explicit;
                // Strong signal — the human's pick is ground truth (high weight).
                Add(key, r.UserChosenAnswer, StrongWeight, 0, countTowardMajority: deliberate);
                // If they overrode a DIFFERENT AI answer, record that as a
                // negative example so the wrong answer's score is pulled down.
                if (!string.IsNullOrEmpty(r.AiPrimaryAnswer)
                    && r.AiPrimaryAnswer != r.UserChosenAnswer)
                    Add(key, r.AiPrimaryAnswer, 0, StrongWeight, countTowardMajority: deliberate);
            }
            else if (r.UserChosenAt != null && !string.IsNullOrEmpty(r.AiPrimaryAnswer))
            {
                // ★ รอบ 182 — แถว "ผู้ใช้ตรวจแล้วแต่ไม่บอกคำตอบ"
                //
                // เกิดจาก `AiFeedbackRecorder` ที่ปฏิเสธ sentinel (`__USER_KEPT_EXISTING__`
                // จากปุ่ม "ใช้ของเดิม") ⇒ `UserChosenAt` ถูกประทับแต่ `UserChosenAnswer`
                // เป็น null. ถ้าปล่อยให้ตกไปสาขาถัดไป มันจะกลายเป็น "ครูตอบแล้วไม่มีใคร
                // แตะ" ⇒ ได้คะแนน**บวก** ให้คำตอบที่มนุษย์เพิ่ง**ปฏิเสธ** = สอนกลับทาง
                //
                // เรารู้แน่ชัดแค่ "คำตอบของ AI ไม่ถูกใช้" (ไม่รู้ว่าอะไรถูก) ⇒ ลงคะแนน
                // **ลบ** ให้คำตอบนั้น น้ำหนักเต็ม แต่ **ไม่นับเข้าถังรวมบริษัท** เพราะ
                // ไม่มีคำตอบที่ถูกให้นับ (majority ต้องมีตัวเลือกที่ชนะ ไม่ใช่แค่ตัวแพ้)
                Add(key, r.AiPrimaryAnswer, 0, StrongWeight, countTowardMajority: false);
            }
            else if (!string.IsNullOrEmpty(r.AiPrimaryAnswer))
            {
                // Weak signal — distil the confident teacher answer (low weight).
                // **ห้ามเข้าถังรวมบริษัท**: คำตอบครูที่ไม่มีมนุษย์แตะเลยยังไม่ใช่
                // ความจริงของบริษัท — มันคือสิ่งที่เรากำลังจะตรวจสอบ ไม่ใช่ข้อสรุป
                Add(key, r.AiPrimaryAnswer, WeakWeight, 0, countTowardMajority: false);
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

    /// <summary>ความมั่นใจของคำตอบที่มาจาก "คลังที่เรียนทันที" (tier 0) —
    /// **ต่ำกว่าเกณฑ์ short-circuit 0.85 โดยตั้งใจ**: มันคือคำยืนยันของคนคนเดียว
    /// ที่ยังไม่ผ่านงานกลางคืน ⇒ ใช้ตอบได้ (โดยเฉพาะตอนปิด provider) แต่ต้อง
    /// **ไม่ทำให้เลิกถามครูทันทีตั้งแต่ครั้งแรก**</summary>
    private const decimal InstantMemoryConfidence = 0.80m;

    /// <summary>จำนวนคำยืนยันแบบ<b>ตั้งใจ</b>ขั้นต่ำก่อนจะเสิร์ฟคำตอบจากคลังทันที
    ///
    /// <para>ผู้ใช้ที่กด "ยอมรับและอนุมัติต่อ" เป็นนิสัยสร้างคำยืนยันแบบ
    /// <see cref="Models.Enums.UserChoiceSource.Implicit"/> ได้วันละหลายสิบแถว
    /// โดยไม่เคยมองค่าที่ระบบเติมให้ ⇒ ถ้านับรวมเป็นความจริง คลังจะเอียงตามนิสัย
    /// การกด ไม่ใช่ตามความถูกต้อง (ทีม T3 รอบ 177 §3.1)</para></summary>
    private const int MinExplicitConfirms = 1;

    public async Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var key = Accounting.Helpers.AiMemoryKey.Of(inputJson);

        // ── Tier 0 — คลังที่ "เรียนทันทีเมื่อผู้ใช้ยืนยัน" ─────────────────────
        // งานกลางคืนเป็นตัวที่ทำให้นักเรียนเก่งขึ้นจริง แต่มันรันวันละครั้ง ⇒ คำตอบ
        // ที่ผู้ใช้เพิ่งแก้เมื่อเช้า ระบบจะยังตอบผิดแบบเดิมไปทั้งวัน. แถวใน
        // `AiSuggestionMemory` ถูกเขียนทันทีที่ผู้ใช้ยืนยัน/แก้ — เดิมเส้นนี้
        // **ไม่มีใครอ่านกลับเลย** เพราะกุญแจฝั่งเขียนกับฝั่งอ่านคนละแบบ (รอบ 178)
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                var featureName = FeatureKey.ToString();
                var mem = await db.AiSuggestionMemories.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.CompanyId == companyId
                                              && m.FeatureKey == featureName
                                              && m.InputKey == key, ct);
                if (mem != null
                    && !string.IsNullOrWhiteSpace(mem.LearnedAnswer)
                    && mem.ExplicitAcceptCount >= MinExplicitConfirms
                    && mem.Confidence >= 0.50m)
                {
                    return new LocalPrediction(
                        PrimaryAnswer: mem.LearnedAnswer,
                        Confidence: Math.Min(InstantMemoryConfidence, mem.Confidence),
                        Alternatives: Array.Empty<string>(),
                        SupportingSamples: mem.AcceptCount + mem.OverrideCount,
                        ModelVersion: Version + "-instant");
                }
            }
            catch (Exception ex)
            {
                // คลังล่ม ≠ ตอบไม่ได้ — ตกไปใช้ชั้นในหน่วยความจำต่อ
                _logger.LogWarning(ex, "อ่านคลังคำตอบทันทีไม่สำเร็จ (feature {Feature})", FeatureKey);
            }
        }

        lock (_lock)
        {
            // Tier 1 — exact fingerprint hit (can reach short-circuit confidence).
            if (!string.IsNullOrEmpty(key)
                && _entries.TryGetValue((companyId, key), out var hits) && hits.Count > 0)
            {
                var top = hits[0];
                return new LocalPrediction(
                    PrimaryAnswer: top.Answer,
                    Confidence: top.WilsonScore,
                    Alternatives: hits.Skip(1).Take(2).Select(h => h.Answer).ToList(),
                    SupportingSamples: top.Confirmed + top.Overridden,
                    ModelVersion: Version);
            }

            // Tier 2 — company majority fallback (capped confidence; the
            // safety net that keeps the feature alive when AI is switched off).
            if (_majority.TryGetValue(companyId, out var maj))
            {
                return new LocalPrediction(
                    PrimaryAnswer: maj.Answer,
                    Confidence: maj.Confidence,
                    Alternatives: Array.Empty<string>(),
                    SupportingSamples: maj.N,
                    ModelVersion: Version + "-majority");
            }
        }
        return null;
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

    // กุญแจของ "อินพุตเดียวกัน" ย้ายไป Helpers/AiMemoryKey แล้ว (รอบ 178) —
    // เดิมสำเนานี้เป็นตัวเดียวที่รู้วิธีทำกุญแจ ทำให้ฝั่ง orchestrator เขียนคลัง
    // ด้วยกุญแจคนละแบบจนไม่มีใครอ่านกลับได้ (ดู doc ของ AiMemoryKey)

    private sealed record Cand(string Answer, int Confirmed, int Overridden, decimal WilsonScore);
}
