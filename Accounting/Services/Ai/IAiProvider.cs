using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai;

/// <summary>
/// Provider-agnostic chat-completion contract. One implementation per
/// vendor (DeepSeek, OpenAI, Anthropic, Gemini, LocalLlama). Each
/// implementation translates the canonical AiRequest into its native
/// HTTP wire format and decodes the native response back into
/// AiProviderRawResponse.
///
/// Implementations MUST:
///   • Honour cancellation tokens (call site sets the orchestrator
///     timeout via CancellationTokenSource).
///   • Return AiProviderRawResponse with Success=false rather than
///     throwing on HTTP / parse errors — the orchestrator decides
///     whether to retry or fall through to local.
///   • Populate TokenUsage when the provider returns it; null is OK.
/// </summary>
public interface IAiProvider
{
    AiProviderType Kind { get; }

    /// <summary>Send the canonical request, return the raw provider
    /// response. Caller is the AiOrchestrator — no other code calls
    /// this directly so cross-feature concerns (cache, budget, feedback)
    /// stay in the orchestrator.</summary>
    Task<AiProviderRawResponse> CompleteAsync(
        AiRequest request,
        AiProviderConfig config,
        CancellationToken ct);

    /// <summary>Quick health check — used by the admin "test connection"
    /// button. Should be a tiny, throw-away prompt that returns fast.
    /// Returns (true, null) on success, (false, reason) on failure.</summary>
    Task<(bool ok, string? error)> TestConnectionAsync(
        AiProviderConfig config,
        CancellationToken ct);
}

/// <summary>
/// Raw provider response after JSON parsing. Provider-specific. The
/// orchestrator further parses Content as the per-feature response
/// schema (also JSON, since we always ask for JSON output).
/// </summary>
public sealed record AiProviderRawResponse
{
    public required bool Success { get; init; }

    /// <summary>The content text returned by the model. Expected to be
    /// JSON when Success=true (we always set response_format=json_object).
    /// Empty string when Success=false.</summary>
    public string Content { get; init; } = "";

    /// <summary>Reported model used — e.g. "deepseek-chat-2025-01-20".
    /// Preserved on the feedback row so historic predictions can be
    /// attributed to the specific model version that produced them.</summary>
    public string? ModelVersion { get; init; }

    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }

    /// <summary>Set when Success=false. Human-readable, propagated to
    /// AiSuggestionFeedback.ErrorMessage for admin diagnostics.</summary>
    public string? Error { get; init; }

    /// <summary>HTTP status from the provider when known. Used by the
    /// orchestrator to decide retry policy (5xx → retry once, 4xx →
    /// don't retry).</summary>
    public int? HttpStatus { get; init; }
}
