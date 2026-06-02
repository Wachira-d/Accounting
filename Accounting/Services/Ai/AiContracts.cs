using Accounting.Models.Enums;

namespace Accounting.Services.Ai;

// Canonical request / response shapes used between call sites, the
// orchestrator, and the provider implementations. Provider-agnostic by
// design — DeepSeek today, OpenAI / Anthropic / Gemini tomorrow share
// the same DTOs because the provider implementation translates to/from
// its native wire format.

/// <summary>
/// What a call site hands to the orchestrator. The local model's
/// prediction is included so the orchestrator can short-circuit when
/// confidence is high enough AND record the head-to-head accuracy for
/// LocalModelHealth. Never throws — failure modes return AiResponse
/// with Status != Success and PrimaryAnswer = LocalPrimaryAnswer.
/// </summary>
public sealed record AiRequest
{
    public required AiFeatureKey FeatureKey { get; init; }
    public required Guid CompanyId { get; init; }

    /// <summary>Domain-specific system message — e.g. "You are a Thai
    /// accounting expert assistant; respond only as JSON matching this
    /// schema...". Built by the per-feature PromptBuilder.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Already-serialised JSON payload for the user message.
    /// Per-feature builder decides what goes in. Examples:
    ///   • VendorCanon: { ocrName, ocrTaxId, candidates[] }
    ///   • GlAccount:  { vendor, line, amount, candidateAccounts[],
    ///                   vendorHistoricalAccounts[], thaiTaxContext }</summary>
    public required string UserPromptJson { get; init; }

    /// <summary>The local model's best guess BEFORE the AI call. Required —
    /// the whole architecture depends on having a baseline to compare,
    /// and the fallback path returns this verbatim when AI is down.</summary>
    public string? LocalPrimaryAnswer { get; init; }
    public decimal? LocalConfidence { get; init; }
    public string? LocalModelVersion { get; init; }

    /// <summary>Ranked alternatives the local model would have offered.
    /// When AI is down, the orchestrator surfaces these alongside the
    /// primary so the UI can still show "or one of these" options.</summary>
    public IReadOnlyList<string> LocalAlternatives { get; init; } = Array.Empty<string>();

    /// <summary>Which entity triggered this — recorded on the feedback row
    /// for audit and for downstream "what document caused this AI call"
    /// drilldown on the admin page.</summary>
    public string? SourceEntityType { get; init; }
    public Guid? SourceEntityId { get; init; }

    /// <summary>Per-call cache TTL override. Vendor canon: 30 days.
    /// Approval-warning fix: 1 day (data changes too often). Null = use
    /// SiteSettings.AiDefaultCacheTtlDays.</summary>
    public int? CacheTtlOverrideDays { get; init; }

    /// <summary>Per-call temperature override. Defaults to active provider's
    /// configured temperature. Forecast-narrative may raise to 0.4 for
    /// readable prose; vendor canon stays at 0.0 for determinism.</summary>
    public decimal? TemperatureOverride { get; init; }

    /// <summary>Per-call max-tokens override. Lets approval-warning fix
    /// (200 tokens) and forecast-narrative (2000 tokens) coexist.</summary>
    public int? MaxTokensOverride { get; init; }

    /// <summary>Per-call HTTP timeout in seconds. Bulk-prompt features
    /// (bank match against 150+150 candidates) need 40-60s; the default
    /// provider config is tuned for short prompts (8s).</summary>
    public int? TimeoutSecondsOverride { get; init; }

    /// <summary>When true, force a provider call even if the local model
    /// is above the confidence threshold. Used by admin "ขอความเห็น AI"
    /// buttons in the UI.</summary>
    public bool ForceProviderCall { get; init; }

    /// <summary>When true, skip cache lookup. Admin retry button uses this
    /// to get a fresh opinion.</summary>
    public bool BypassCache { get; init; }

    /// <summary>When true the feature returns a free-form plan / structured
    /// JSON document (e.g. bulk bank reconciliation) rather than the standard
    /// primaryAnswer/alternatives shape. The orchestrator then skips the
    /// "primary answer or alternatives" schema check and surfaces the provider
    /// content verbatim in <see cref="AiResponse.RawResponseJson"/> as Success —
    /// the caller is responsible for parsing it. Without this, plan responses
    /// were wrongly rejected as "Schema mismatch" and fell back to local.</summary>
    public bool RawPlanResponse { get; init; }
}

/// <summary>
/// What every call site receives back. NEVER null. When AI is down /
/// disabled / over-budget / hallucinated invalid JSON / etc., Status
/// reflects that AND PrimaryAnswer falls back to LocalPrimaryAnswer.
/// Callers can use PrimaryAnswer unconditionally.
/// </summary>
public sealed record AiResponse
{
    public required AiCallStatus Status { get; init; }

    /// <summary>The answer the orchestrator recommends — always set when
    /// the caller supplied a LocalPrimaryAnswer. AI's pick when AI ran
    /// successfully and passed compliance verification; otherwise the
    /// local pick. Caller uses this unconditionally.</summary>
    public string? PrimaryAnswer { get; init; }
    public decimal? Confidence { get; init; }

    /// <summary>Other candidates the AI (or local fallback) ranks.
    /// Surfaced as "or maybe..." options in the UI dropdown.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();

    /// <summary>Risk flags from the AI — e.g. "amount is 12x larger than
    /// vendor's monthly average", "tax invoice number format unusual for
    /// this vendor". Shown to the user before they confirm.</summary>
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    /// <summary>Thai-compliance flags caught by either the AI or the
    /// post-AI RdComplianceValidator. Shown prominently — these can
    /// block document approval depending on the call site.</summary>
    public IReadOnlyList<string> ComplianceFlags { get; init; } = Array.Empty<string>();

    /// <summary>AI's reasoning trace (1-3 sentences). Shown on hover /
    /// expanded panel — helps the user decide whether to trust the
    /// suggestion. Empty when AI didn't run.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Concrete next actions the AI proposes — "create
    /// Contact for this tax ID", "split into 2 payment vouchers",
    /// "request VAT receipt from vendor". Surfaced as action buttons
    /// when the call site supports it.</summary>
    public IReadOnlyList<string> SuggestedActions { get; init; } = Array.Empty<string>();

    /// <summary>True when the AI provider was actually consulted (and
    /// returned a parseable response). False = fell back to local
    /// (Status tells you which fallback path).</summary>
    public bool UsedAi { get; init; }
    public bool UsedCache { get; init; }

    /// <summary>FK to the AiSuggestionFeedback row written for this call.
    /// UI passes this back to /api/.../ai-feedback/{id}/record when the
    /// user confirms their choice so the training set captures the
    /// ground-truth label.</summary>
    public Guid? FeedbackId { get; init; }

    public int? LatencyMs { get; init; }
    public string? ProviderModel { get; init; }
    public string? RawResponseJson { get; init; }
}
