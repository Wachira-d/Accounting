using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ─────────────────────────────────────────────────────────────────────────
//  AI integration — provider-agnostic abstraction so DeepSeek today can
//  be swapped for OpenAI / Anthropic / Gemini / a local Llama tomorrow
//  without touching call sites. Three concerns split into three tables:
//
//    1. AiProviderConfig    — registry of configured providers + which
//                              one is currently active.
//    2. AiSuggestionFeedback — every LLM call recorded with full prompt,
//                              response, local-model baseline, and
//                              eventual user choice → nightly training set.
//    3. AiResponseCache      — content-addressed prompt cache → cuts
//                              cost on repeat prompts (vendor canon hits
//                              the same vendor over and over).
//    4. LocalModelHealth     — per-feature accuracy tracker so under-
//                              performing local models can be flagged for
//                              redesign or replacement.
//    5. AiUsageDaily         — daily cost / call rollup powering the
//                              admin usage widget.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// One row per configured AI provider. Multiple rows can exist; exactly
/// one is marked IsActive at any time. Admin can pre-stage credentials
/// for OpenAI as a standby while DeepSeek is the active provider — flip
/// IsActive to migrate instantly.
/// </summary>
public class AiProviderConfig : BaseEntity
{
    public AiProviderType ProviderType { get; set; }

    /// <summary>Display name shown in admin UI. e.g. "DeepSeek Primary",
    /// "OpenAI Fallback". Free-form, not used for routing.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Only one row per system has IsActive = true; the
    /// orchestrator routes every call there. Switching providers is a
    /// one-row UPDATE — no service restart needed.</summary>
    public bool IsActive { get; set; } = false;

    /// <summary>Base URL of the chat-completion endpoint. DeepSeek default:
    /// https://api.deepseek.com. OpenAI: https://api.openai.com/v1.
    /// Stored per-provider so corporate proxies / Azure-routed deployments
    /// can override without code changes.</summary>
    public string? Endpoint { get; set; }

    /// <summary>API key — stored as-is for now (matches AzureDiApiKey pattern
    /// in SiteSettings). Production should encrypt at rest via
    /// IDataProtectionProvider; tracked as TODO.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model identifier per provider. DeepSeek: "deepseek-chat" or
    /// "deepseek-reasoner". OpenAI: "gpt-4o-mini" etc. Anthropic:
    /// "claude-sonnet-4-6". The orchestrator passes this through verbatim.</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>Temperature for chat completion. 0.0–2.0. Default 0.1 since
    /// every prompt asks for JSON and we want determinism. Per-feature
    /// builders may override at call time.</summary>
    public decimal Temperature { get; set; } = 0.1m;

    /// <summary>Hard cap on output tokens. Stops a runaway model from
    /// generating a 30k-token explanation when we asked for a 200-token
    /// JSON object. Per-feature builders may shrink further.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Per-call timeout. 8 seconds is the orchestrator's hard
    /// budget for user-facing flows (anything longer becomes UX-blocking).
    /// Background jobs (forecast narrative) can raise via PromptBuilder.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;   // 8s was too low for DeepSeek reasoning; per-call overrides still apply

    /// <summary>Daily call cap across all tenants — second line of defence
    /// against runaway cost. Null = no cap.</summary>
    public int? DailyCallCap { get; set; }

    /// <summary>Monthly USD budget across all tenants. The budget guard
    /// blocks calls once 95% is consumed and surfaces a warning at 80%.
    /// Null = no cap (production should always set this).</summary>
    public decimal? MonthlyBudgetUsd { get; set; }

    /// <summary>Estimated input-token price USD / 1M tokens. DeepSeek
    /// 2025-01 publishes $0.14/1M input + $0.28/1M output for deepseek-chat.
    /// Stored per-provider so cost rollups don't need to know provider
    /// pricing in code.</summary>
    public decimal? PricePerInputTokenUsd1M { get; set; } = 0.14m;

    /// <summary>Output token price USD / 1M tokens. Default mirrors
    /// DeepSeek's published rate; admin sets the matching number when
    /// switching provider.</summary>
    public decimal? PricePerOutputTokenUsd1M { get; set; } = 0.28m;

    /// <summary>Last successful "test connection" ping from the admin
    /// page. Surfaces "configured but never tested" vs "tested OK 5min
    /// ago" status badges.</summary>
    public DateTime? LastTestedAt { get; set; }
    public string? LastTestStatus { get; set; }

    /// <summary>Disable a provider without deleting the row so its
    /// credentials are preserved for instant re-enable.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Free-form JSON for provider-specific knobs that don't
    /// deserve their own column (e.g. OpenAI's organization id,
    /// Anthropic's anthropic-version header, custom proxy auth).</summary>
    public string? ExtraSettingsJson { get; set; }
}

/// <summary>
/// One row per AI call. The full prompt + response + the local model's
/// baseline prediction + (eventually) the user's choice. Drives:
///   • Nightly local-model retraining (LLMFeedbackTrainingJob)
///   • Admin "AI accuracy" dashboard
///   • Fine-tuning dataset export (when the provider supports it)
///   • PDPA audit trail of outbound data
/// </summary>
public class AiSuggestionFeedback : TenantEntity
{
    /// <summary>Stable identifier for the call site — "VendorCanon",
    /// "GlAccountSuggestion", "ApprovalWarningFix", etc. Defined in
    /// AiFeatureKey enum (kept as string for forward-compat when
    /// new features ship without an enum migration).</summary>
    public string FeatureKey { get; set; } = "";

    /// <summary>SHA-256 of the canonicalised prompt payload (provider-
    /// agnostic — strips per-call IDs so the same logical question
    /// produces the same hash). Doubles as the AiResponseCache key.</summary>
    public string PromptHash { get; set; } = "";

    /// <summary>Full prompt as actually sent to the provider (JSON). Used
    /// for replay during local retrain and for audit.</summary>
    public string PromptJson { get; set; } = "";

    /// <summary>Provider's raw response, parsed JSON. The orchestrator
    /// stores both the structured fields and the full payload so future
    /// schema additions don't require a migration.</summary>
    public string? ResponseJson { get; set; }

    /// <summary>The single value the AI proposed as its primary answer.
    /// For VendorCanon this is the matched ContactId; for GlAccount it
    /// is the suggested account code. Indexed for accuracy queries.</summary>
    public string? AiPrimaryAnswer { get; set; }

    /// <summary>AI's stated confidence (0.0–1.0). Lets us weight the
    /// feedback when retraining — a 0.95 prediction that the user
    /// confirms is a stronger signal than a 0.55 one.</summary>
    public decimal? AiConfidence { get; set; }

    /// <summary>What the local model would have predicted before the AI
    /// was consulted. Powers the head-to-head accuracy comparison that
    /// decides whether DeepSeek calls can be reduced for this feature.</summary>
    public string? LocalModelAnswer { get; set; }
    public decimal? LocalModelConfidence { get; set; }
    public string? LocalModelVersion { get; set; }

    /// <summary>What the user finally chose. NULL when the call was
    /// auto-accepted without UI presentation (low-stakes features) or
    /// the user hasn't reviewed yet. The retraining job ignores rows
    /// where this is still NULL after 7 days.</summary>
    public string? UserChosenAnswer { get; set; }
    public DateTime? UserChosenAt { get; set; }

    /// <summary>Did the user accept the AI's primary answer verbatim
    /// (= true), pick an alternative (= false but UserChosenAnswer set),
    /// or override entirely (= false, UserChosenAnswer = free-text)?</summary>
    public bool? UserAcceptedAi { get; set; }

    /// <summary>คำยืนยันนี้ "ตั้งใจแค่ไหน" — <c>null</c> = แถวเก่าก่อนรอบ 178
    /// (ชื่อพร็อพเพอร์ตี้ตั้งใจไม่ให้ตรงกับชื่อชนิด เพื่อเลี่ยงกรณี "Color Color"
    /// ที่การอ้าง <c>UserChoiceSource.Implicit</c> ในคลาสนี้จะไปชนกับพร็อพเพอร์ตี้)
    /// (ตีความเป็น <see cref="UserChoiceSource.Implicit"/> เวลาคำนวณ)
    ///
    /// <para>ใช้แยก "ผู้ใช้เลือกเอง" ออกจาก "ค่าที่ระบบเติมแล้วถูกกดผ่าน" —
    /// ดูเหตุผลเต็มที่ doc ของ <see cref="UserChoiceSource"/></para></summary>
    public UserChoiceSource? UserChoiceOrigin { get; set; }

    /// <summary>Source entity that triggered the call — Document, Payment,
    /// OcrScanResult, etc. Lets us cross-reference back to the document
    /// being reasoned about for audit and for follow-up feature work.</summary>
    public string? SourceEntityType { get; set; }
    public Guid? SourceEntityId { get; set; }

    /// <summary>The orchestrator status. Drives the daily rollup.</summary>
    public AiCallStatus Status { get; set; } = AiCallStatus.Success;

    /// <summary>Provider used for THIS call — preserved even if the
    /// admin later switches providers, so historic accuracy can be
    /// attributed correctly.</summary>
    public AiProviderType ProviderUsed { get; set; }
    public string? ModelVersion { get; set; }

    /// <summary>End-to-end latency including queue + provider + parse.
    /// Powers the "your AI is slow today" indicator.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>Token usage and estimated USD cost — populated from
    /// the provider's usage block in the response. Drives the budget
    /// guard's rolling burn calculation.</summary>
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }

    /// <summary>Set when Status = Cached — points back to the original
    /// row that produced the response. Lets the accuracy job give
    /// credit to the original answer even when later reused.</summary>
    public Guid? CacheHitOfFeedbackId { get; set; }

    /// <summary>Set when Status = Failed — captures whatever the provider
    /// returned so the orchestrator can decide on retry policy.</summary>
    public string? ErrorMessage { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  ป้ายกำกับ "ใครเป็นคนเรียก" — ทำให้รายงานแยกรายลูกค้า/ช่องทางได้
    //  ยึด semantics ชุดเดียวกับ UsageEvent (มิเตอร์คิดเงิน) เพื่อให้
    //  "รายงานการใช้ AI" กับ "บิล" พูดภาษาเดียวกัน กระทบยอดกันได้
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>ช่องทางที่ request เข้ามา — Web (หน้า NextAcc) / ApiKey
    /// (ลูกค้า Connected ยิง API) / Background (งานของระบบเอง).
    /// นี่คือฟิลด์ที่ตอบว่า "ลูกค้ารายนี้ใช้ผ่านเว็บหรือใช้ API อย่างเดียว"</summary>
    public AiUsageChannel Channel { get; set; } = AiUsageChannel.Unknown;

    /// <summary>กลุ่มผู้จ่ายเงิน **ณ ขณะที่เรียก** (snapshot ไม่ join สด) —
    /// เหตุผลเดียวกับ `UsageEvent.BillingAccountId`: บริษัทถูกขายออกจากเครือ
    /// แล้ว `Company.BillingAccountId` เปลี่ยน รายงานย้อนหลังต้องยังอยู่กับ
    /// กลุ่มเดิม ไม่ใช่ย้ายตามไปทั้งประวัติ</summary>
    public Guid? BillingAccountId { get; set; }

    /// <summary>สาขาที่ใช้งาน (ถ้าคีย์/ผู้ใช้ผูกสาขา) — สำหรับ charge-back ภายใน</summary>
    public Guid? BranchId { get; set; }

    /// <summary>API key ที่ยิงเข้ามา — **null = ใช้ผ่านหน้าเว็บปกติ**
    /// (นิยามเดียวกับ `UsageEvent.ApiClientId` เป๊ะ ๆ). ลูกค้า API รายเดียว
    /// อาจมีหลายคีย์ (POS สาขา A, ERP สำนักงานใหญ่) จึงเจาะดูรายคีย์ได้</summary>
    public Guid? ApiClientId { get; set; }

    /// <summary>ผู้ใช้ที่กด (เฉพาะช่องทาง Web) — ให้ตอบได้ว่าใครในบริษัท
    /// ใช้ AI มากที่สุด และเวลามีข้อโต้แย้งเรื่องบิลก็ไล่กลับได้</summary>
    public Guid? UserId { get; set; }

    /// <summary>คีย์ sandbox → นับเพื่อดูพฤติกรรมช่วงทดสอบ แต่ **ไม่เข้าบิล**
    /// (ตรงกับ `UsageEvent.IsSandbox`)</summary>
    public bool IsSandbox { get; set; }
}

/// <summary>
/// Content-addressed prompt cache. Key = SHA-256 over the canonicalised
/// prompt payload (same hash AiSuggestionFeedback uses). Vendor canon
/// hits the same vendor name across hundreds of scans → cache hit ratio
/// matters for cost.
/// </summary>
public class AiResponseCache : BaseEntity
{
    /// <summary>SHA-256 hex string. Indexed unique.</summary>
    public string PromptHash { get; set; } = "";

    /// <summary>Which feature wrote this entry — lets us purge
    /// per-feature when a prompt schema changes.</summary>
    public string FeatureKey { get; set; } = "";

    /// <summary>The cached response payload (JSON), reused verbatim on
    /// cache hit.</summary>
    public string ResponseJson { get; set; } = "";

    /// <summary>Which provider produced this. Cache hits don't migrate
    /// when admin switches providers — we expire old entries instead so
    /// the new provider gets a fresh chance.</summary>
    public AiProviderType ProviderUsed { get; set; }
    public string? ModelVersion { get; set; }

    public decimal? Confidence { get; set; }

    /// <summary>Tenant scope — cache MUST be tenant-isolated. A vendor
    /// match for company A must never serve as the answer for company
    /// B. NULL for cross-tenant features (none today, but reserved).</summary>
    public Guid? CompanyId { get; set; }

    public int HitCount { get; set; } = 0;
    public DateTime? LastHitAt { get; set; }

    /// <summary>Expiry. Vendor canon: 30 days. Doc-type: 7 days. Approval
    /// fix: 24 hours. Per-feature TTL decided by the PromptBuilder.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Per-feature accuracy tracker. Refreshed by LocalModelHealthJob nightly.
/// When LocalAccuracy30d < AiAccuracy30d - 0.15 for 30 consecutive days,
/// the local model is flagged NeedsRedesign so an admin can decide whether
/// to add features, switch algorithm, or retire the local model in favour
/// of always-on AI.
/// </summary>
public class LocalModelHealth : BaseEntity
{
    public string FeatureKey { get; set; } = "";

    /// <summary>Local model implementation tag — bump when the algorithm
    /// changes so historic accuracy isn't unfairly attributed.</summary>
    public string LocalModelVersion { get; set; } = "v1";

    /// <summary>บริษัทที่ตัวเลขชุดนี้เป็นของ — <c>null</c> = แถวรวมทุก tenant
    /// (รูปแบบเดิมก่อนรอบ 178)
    ///
    /// <para>⚠️ เดิมตารางนี้ไม่มีคอลัมน์นี้เลย ⇒ วัดรวมทุกบริษัท: ลูกค้าใหม่หนึ่งราย
    /// ที่ยังไม่มีข้อมูลจะดึงตัวเลขของทุกคนลง และบริษัทที่นักเรียนแย่จริงจะถูกกลบ
    /// ด้วยค่าเฉลี่ย ⇒ สถานะที่ได้ไม่ตรงกับความจริงของใครเลย (ทีม T3 รอบ 177)</para></summary>
    public Guid? CompanyId { get; set; }

    public int SamplesLast30d { get; set; }

    /// <summary>จำนวนแถวที่<b>นักเรียนตอบได้</b> (มี <c>LocalModelAnswer</c>) —
    /// ตัวหารที่ถูกของ <see cref="LocalAccuracy30d"/>
    ///
    /// <para>เดิมความแม่นของนักเรียนถูกหารด้วย <see cref="SamplesLast30d"/> ทั้งก้อน
    /// ซึ่งรวมแถวที่นักเรียน<b>ไม่ได้ตอบเลย</b> ⇒ ตัวเลขที่ได้เป็น "ความแม่น × ความครอบคลุม"
    /// ปนกัน แล้วถูกเอาไปลบกับความแม่นของ AI ที่หารด้วยตัวหารคนละตัว
    /// ⇒ feature ที่นักเรียนยังตอบไม่ครบจะติดสถานะ Degraded โดยโครงสร้าง</para></summary>
    public int LocalSamplesLast30d { get; set; }

    /// <summary>สัดส่วนแถวที่นักเรียนตอบได้ ต่อแถวทั้งหมด — "โตแค่ไหน" คนละเรื่องกับ
    /// "แม่นแค่ไหน" · ตัวนี้ตกพร้อม <c>UsedAi</c> = ระบบ<b>เงียบลง</b> ไม่ใช่โตขึ้น</summary>
    public decimal LocalCoverage30d { get; set; }

    /// <summary>จำนวนแถวที่ผู้ใช้ยืนยันแบบ<b>ตั้งใจ</b> (Explicit) ใน 30 วัน —
    /// ถ้าตัวนี้ตกลงเรื่อย ๆ แปลว่าคลังกำลังเรียนจากการกดผ่าน ไม่ใช่จากการตัดสินใจ</summary>
    public int ExplicitLabels30d { get; set; }

    /// <summary>Fraction of cases where local model's primary answer ==
    /// user's chosen answer. 0.0–1.0.</summary>
    public decimal LocalAccuracy30d { get; set; }
    public decimal AiAccuracy30d { get; set; }

    /// <summary>Fraction of cases where local and AI agreed AND the user
    /// confirmed. Drives the cost-saving "skip-AI-when-local-confident"
    /// optimisation — high agreement → trust local for that confidence
    /// band.</summary>
    public decimal AgreementRate30d { get; set; }

    public DateTime LastEvaluatedAt { get; set; } = DateTime.UtcNow;

    public LocalModelHealthStatus Status { get; set; } = LocalModelHealthStatus.Healthy;

    /// <summary>Human-readable summary written by the health job — surfaced
    /// on the admin AI page. e.g. "WHT category: local 62%, AI 89%, 1,243
    /// samples — recommend adding industry-context features."</summary>
    public string? Recommendation { get; set; }
}

/// <summary>
/// Online-learning memory — the "train ไปเลย" store. Unlike the nightly
/// distillation job, this table is upserted INLINE the moment a user
/// confirms or overrides a suggestion (via RecordUserChoiceAsync). One
/// row per (Company, Feature, InputKey) holds the answer the company's
/// own users have settled on for that exact input, so the very next
/// suggestion for the same input returns the learned value with no
/// provider call and no batch wait.
///
/// InputKey is a stable, feature-defined key (contactId, normalised
/// product name, description-keyword + side, …) carried in the feedback
/// row's PromptHash. AcceptCount / OverrideCount track agreement so
/// Confidence = Accept / (Accept + Override) and a single accidental
/// override doesn't immediately flip a well-established mapping.
/// </summary>
public class AiSuggestionMemory : TenantEntity
{
    public string FeatureKey { get; set; } = "";

    /// <summary>Stable per-feature input key (lowercased). Together with
    /// CompanyId + FeatureKey this uniquely identifies "the same question
    /// asked again". Indexed for O(1) lookup at suggestion time.</summary>
    public string InputKey { get; set; } = "";

    /// <summary>The answer the company's users have converged on for this
    /// input — an account code / contactId / rate / category string. Read
    /// back verbatim by the suggestion endpoint.</summary>
    public string LearnedAnswer { get; set; } = "";

    /// <summary>Times a user confirmed (kept) this learned answer.</summary>
    public int AcceptCount { get; set; }

    /// <summary>Times a user overrode it with something else. When an
    /// override wins repeatedly, LearnedAnswer flips to the new value.</summary>
    public int OverrideCount { get; set; }

    /// <summary>ในจำนวน <see cref="AcceptCount"/> นั้น มีกี่ครั้งที่ผู้ใช้
    /// <b>ลงมือยืนยันเอง</b> (<see cref="UserChoiceSource.Explicit"/>)
    ///
    /// <para>ฝั่งอ่านทุกที่ต้องใช้ตัวนี้เป็นด่าน ไม่ใช่ <c>AcceptCount</c> —
    /// ไม่งั้นการกด "ยอมรับและอนุมัติต่อ" รัว ๆ จะกลายเป็นความจริงของบริษัท
    /// (ทีม T3 รอบ 177 §3.1 "คลังเอียง")</para></summary>
    public int ExplicitAcceptCount { get; set; }

    /// <summary>Accept / (Accept + Override). Suggestion endpoints only
    /// trust the memory above a threshold (e.g. ≥0.6 with ≥2 samples).</summary>
    public decimal Confidence { get; set; }

    public DateTime LastLearnedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-feature routing policy — admin sets this; AiOrchestrator reads
/// it on every call. Separate from LocalModelHealth (which is nightly-
/// recomputed STATS) because this is admin INTENT and lives across
/// retrains. One row per AiFeatureKey; sparse (rows missing fall back
/// to the global defaults baked into the orchestrator).
///
/// Modes the admin can pick per feature:
///   • Disabled       → orchestrator returns local fallback, no provider call.
///   • LocalOnly      → only the distilled student answers; never bill DeepSeek.
///   • ProviderOnly   → always hit DeepSeek; ignore local student.
///   • Hybrid         → local short-circuit when ≥ threshold + sample N%.
///   • AlwaysTeach    → always hit DeepSeek AND record the local prediction
///                      head-to-head so the feedback corpus grows. Use
///                      while the model is still maturing — costs full
///                      provider fees, gains maximum training signal.
/// </summary>
public class AiFeatureRoutingConfig : BaseEntity
{
    /// <summary>Stringified AiFeatureKey — same value as
    /// LocalModelHealth.FeatureKey + AiSuggestionFeedback.FeatureKey
    /// so they join naturally.</summary>
    public string FeatureKey { get; set; } = "";

    public AiFeatureRoutingMode Mode { get; set; } = AiFeatureRoutingMode.Hybrid;

    /// <summary>Override the orchestrator's global 0.85 threshold. Only
    /// honoured in Hybrid mode. Null → use global.</summary>
    public decimal? LocalConfidenceThreshold { get; set; }

    /// <summary>Override the orchestrator's global 0.10 sampling rate.
    /// In Hybrid mode this is the fraction of confident-local cases
    /// that still go to DeepSeek for drift calibration. In AlwaysTeach
    /// mode this is the fraction of cases that hit DeepSeek (others
    /// short-circuit so you can dial cost). Null → use global.</summary>
    public decimal? ProviderSamplingRate { get; set; }

    /// <summary>Human-readable note from the admin — why this feature
    /// is in this mode (e.g. "Teaching until 5k samples", "Local
    /// performs at 92% — locked to LocalOnly").</summary>
    public string? AdminNote { get; set; }

    /// <summary>Who last touched this row — surfaced in the audit log.</summary>
    public string? LastModifiedBy { get; set; }
}

/// <summary>
/// Daily rollup feeding the admin "AI burn" widget. Mirrors the
/// pattern of the Azure DI usage endpoint added in /admin/ocr-config.
/// </summary>
public class AiUsageDaily : BaseEntity
{
    /// <summary>Calendar day in UTC (date only). Composite-unique with
    /// (ProviderType, FeatureKey).</summary>
    public DateTime UsageDate { get; set; }

    public AiProviderType ProviderType { get; set; }

    public string FeatureKey { get; set; } = "";

    public int CallsAttempted { get; set; }
    public int CallsSuccessful { get; set; }
    public int CallsCached { get; set; }
    public int CallsFailed { get; set; }
    public int CallsBudgetBlocked { get; set; }

    public long InputTokensTotal { get; set; }
    public long OutputTokensTotal { get; set; }
    public decimal CostUsdTotal { get; set; }

    /// <summary>Average end-to-end latency including queue. Slow days
    /// surface as a warning on the admin widget.</summary>
    public int AvgLatencyMs { get; set; }
}

/// <summary>
/// **สรุปการใช้ AI รายวัน แยกตามลูกค้าและช่องทาง** — ตารางที่ทำให้รายงาน
/// "ลูกค้ารายไหนใช้ AI เท่าไร" ตอบได้ในคิวรีเดียวโดยไม่ต้องสแกน
/// <see cref="AiSuggestionFeedback"/> ทั้งปี (แถวระดับ call โตเป็นล้านแถว)
///
/// <para><b>ทำไมเป็นตารางใหม่ ไม่เติม CompanyId ลง AiUsageDaily</b>: ตารางเดิม
/// unique ที่ (วันที่, provider, feature) และวิดเจ็ตแอดมินอ่านอยู่ ถ้าเติมคอลัมน์
/// แล้วแถวเก่ามี CompanyId = null ปนกับแถวใหม่ที่มีค่า ผลรวมจะ **นับซ้ำ**
/// ทันที (ทั้งแถวรวมและแถวแยกอยู่ในตารางเดียวกัน). แยกตารางจึงไม่แตะของเดิมเลย</para>
///
/// <para><b>ทำไมเก็บ LatencySumMs ไม่ใช่ค่าเฉลี่ย</b>: ค่าเฉลี่ยเอามาเฉลี่ยซ้ำ
/// ข้ามแถวไม่ได้ (เฉลี่ยของเฉลี่ย ≠ เฉลี่ยจริง เมื่อจำนวน call ต่างกัน) —
/// รายงานนี้ต้องรวมข้าม feature/ช่องทาง/วัน ตลอดเวลา จึงเก็บผลรวมกับตัวหาร
/// แล้วค่อยหารตอนแสดง</para>
///
/// <para><b>ทำไมนับ CallsLocalServed แยก</b>: ตัวชี้วัดตามกฎเหล็ก #1 คือ
/// "local โตพอจะยืนเองได้แค่ไหน" — ยิ่ง local ตอบได้เองมาก ยิ่งจ่าย token น้อย
/// รายงานจึงต้องโชว์ทั้ง "เรียก AI จริงกี่ครั้ง" และ "ประหยัดไปกี่ครั้ง"
/// ไม่ใช่โชว์แค่ยอดที่จ่ายเงิน</para>
/// </summary>
public class AiUsageDailyTenant : BaseEntity
{
    /// <summary>วันตามปฏิทิน UTC (date only) — unique ร่วมกับ
    /// (CompanyId, ProviderType, FeatureKey, Channel)</summary>
    public DateTime UsageDate { get; set; }

    /// <summary>บริษัทผู้ใช้ (tenant). ไม่ใช้ TenantEntity เพราะแถวนี้เป็น
    /// ข้อมูลระดับแพลตฟอร์มที่แอดมินอ่านข้ามบริษัท — การอ่านฝั่งลูกค้า
    /// ต้องใส่ `CompanyId ==` เองตามกฎ M</summary>
    public Guid CompanyId { get; set; }

    /// <summary>กลุ่มผู้จ่ายเงิน ณ วันนั้น (snapshot) — ให้รวมยอดทั้งเครือได้
    /// โดยไม่ต้อง join Company สด (ซึ่งจะทำให้ประวัติย้ายตามการขายกิจการ)</summary>
    public Guid? BillingAccountId { get; set; }

    public AiProviderType ProviderType { get; set; }
    public string FeatureKey { get; set; } = "";
    public AiUsageChannel Channel { get; set; } = AiUsageChannel.Unknown;

    // ── ปริมาณ ───────────────────────────────────────────────────────
    /// <summary>ทุกครั้งที่ orchestrator ถูกเรียก (รวมที่ไม่ได้ยิงออกไปจริง)</summary>
    public int CallsTotal { get; set; }

    /// <summary>ยิงถึง provider แล้วสำเร็จ = ครั้งที่ **เสียเงินจริง**</summary>
    public int CallsAi { get; set; }

    /// <summary>ตอบจากแคช — ไม่เสีย token (ประหยัดจากการถามซ้ำ)</summary>
    public int CallsCached { get; set; }

    /// <summary>local model ตอบเองจนไม่ต้องเรียก AI — ตัวชี้วัด sovereignty</summary>
    public int CallsLocalServed { get; set; }

    public int CallsFailed { get; set; }
    public int CallsBudgetBlocked { get; set; }
    public int CallsNoProvider { get; set; }

    // ── ต้นทุน ───────────────────────────────────────────────────────
    public long InputTokensTotal { get; set; }
    public long OutputTokensTotal { get; set; }
    public decimal CostUsdTotal { get; set; }

    // ── คุณภาพ ───────────────────────────────────────────────────────
    /// <summary>ผลรวม latency (ms) ของ call ที่วัดได้ — หารด้วย
    /// <see cref="LatencySamples"/> ตอนแสดงผล</summary>
    public long LatencySumMs { get; set; }
    public int LatencySamples { get; set; }

    /// <summary>จำนวน call ที่ผู้ใช้ "ตัดสินใจแล้ว" (ยืนยันหรือแก้) —
    /// อัปเดตตอน RecordUserChoiceAsync ไม่ใช่ตอนเรียก</summary>
    public int UserReviewed { get; set; }

    /// <summary>ในจำนวนที่ตัดสินใจแล้ว ผู้ใช้รับคำตอบ AI ไปตรง ๆ กี่ครั้ง —
    /// UserAcceptedAi / UserReviewed = "AI แม่นแค่ไหนในสายตาผู้ใช้จริง"</summary>
    public int UserAcceptedAi { get; set; }

    /// <summary>แถวของคีย์ sandbox — นับแยกเพื่อไม่ให้ปนยอดที่เอาไปคิดเงิน</summary>
    public bool IsSandbox { get; set; }
}
