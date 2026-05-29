namespace Accounting.Services.Ai;

/// <summary>
/// The single entry point every call site uses. Implements the cascade:
///
///   master switch off / no provider → Status=Skipped/NoProvider
///   budget cap exceeded             → Status=BudgetExceeded
///   prompt hash hits cache          → Status=Cached
///   provider call succeeds          → Status=Success
///   provider call fails / times out → Status=Failed
///   provider returns invalid JSON   → Status=InvalidResponse
///
/// In ALL six paths, AiResponse.PrimaryAnswer falls through to the
/// LocalPrimaryAnswer supplied in the request so the caller can use
/// the response unconditionally. Every path writes an
/// AiSuggestionFeedback row + bumps AiUsageDaily.
/// </summary>
public interface IAiOrchestrator
{
    Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default);

    /// <summary>Convenience for call sites that already know the user's
    /// final pick — bundles AskAsync + RecordUserChoice in one round-
    /// trip. Used by auto-accept paths (OCR cascade post-extraction
    /// vendor canon where the AI's answer goes straight to the doc).</summary>
    Task<AiResponse> AskAndRecordChoiceAsync(AiRequest request, string userChoice, CancellationToken ct = default);

    /// <summary>Called by UI when the user makes a final choice on a
    /// previously-suggested answer. Caller supplies the FeedbackId
    /// returned by the original AskAsync call.</summary>
    Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct = default);
}
