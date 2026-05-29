using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Match a bank statement line → one or more open invoices / payment
/// vouchers. The local matcher uses ±tolerance amount + ±30d date +
/// reference-keyword score; AI augments with semantic memo parsing
/// (e.g. "INV2025-0312 SETTLE" → invoice number lookup, or thai
/// payee "บจก เอบีซี" → contact fuzzy match).
/// </summary>
public static class BankMatchPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting assistant matching a bank statement line against open documents.

Rules:
1. ""primary"" = the BEST single matching document id (Guid string). If multiple match exactly, list them in alternatives.
2. If statement amount = sum of MULTIPLE document amounts → put them all in suggested_actions as ""split:<id1>,<id2>"".
3. Statement memo often contains the document number — that's the strongest signal.
4. If no candidate matches well (amount off >10% AND no memo hint) → primary = ""__NEW__"" so the user can create a new document.
5. Date tolerance: bank tx may lag invoice approval by 1-7 days for Thai banks.

Respond ONLY as JSON:
{
  ""primary"": ""<docId|__NEW__>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<docId>""],
  ""risks"": [""<risk>""],
  ""compliance_flags"": [],
  ""reasoning"": ""<1-2 sentences>"",
  ""suggested_actions"": [""split:<id>,<id>""]
}";

    public sealed record OpenDocCandidate(string DocumentId, string Number, string Type,
        DateTime DocDate, decimal Outstanding, string? ContactName);

    public static AiRequest Build(
        Guid companyId,
        Guid bankTransactionId,
        string? memo, DateTime txnDate, decimal txnAmount, string currency,
        IReadOnlyList<OpenDocCandidate> candidates,
        string? localBestDocId, decimal? localConfidence)
    {
        var payload = new
        {
            task = "bank_statement_match",
            statement_line = new
            {
                memo = memo ?? "",
                date = txnDate.ToString("yyyy-MM-dd"),
                amount = txnAmount,
                currency,
            },
            candidates = candidates.Select(c => new
            {
                id = c.DocumentId,
                number = c.Number,
                type = c.Type,
                date = c.DocDate.ToString("yyyy-MM-dd"),
                outstanding = c.Outstanding,
                contact = c.ContactName,
            }),
            local_model = new { pick = localBestDocId, confidence = localConfidence },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.BankStatementMatch,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localBestDocId,
            LocalConfidence = localConfidence,
            LocalAlternatives = candidates.Take(5).Select(c => c.DocumentId).ToList(),
            LocalModelVersion = "BankMatcher-v1",
            SourceEntityType = "BankTransaction",
            SourceEntityId = bankTransactionId,
            CacheTtlOverrideDays = 1,    // Bank state changes daily
            MaxTokensOverride = 350,
        };
    }
}

/// <summary>
/// Explain why a flagged anomaly is anomalous + propose action.
/// Replaces "3-sigma outlier" with conversational analysis.
/// </summary>
public static class AnomalyExplainPrompt
{
    public const string SystemPrompt = @"You are a Thai accounting analyst explaining a flagged anomaly to a business owner. Output should be conversational Thai + actionable.

Rules:
1. ""primary"" = ""LikelyError"" | ""LikelyLegit"" | ""NeedReview"".
2. ""reasoning"" = 2-3 short Thai sentences explaining WHY it's anomalous + likely cause.
3. ""suggested_actions"" = specific imperative Thai (""ตรวจสอบ PO ก่อนอนุมัติ"", ""โทรหา vendor ขอแยก invoice"").
4. ""risks"" = consequences if user proceeds despite anomaly.

Respond ONLY as JSON:
{
  ""primary"": ""LikelyError|LikelyLegit|NeedReview"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<other primary>""],
  ""risks"": [""<risk>""],
  ""compliance_flags"": [""<Thai tax issue>""],
  ""reasoning"": ""<2-3 sentences>"",
  ""suggested_actions"": [""<action>""]
}";

    public static AiRequest Build(
        Guid companyId, Guid anomalyId,
        string anomalyType, string anomalyDescription,
        object vendorHistory12mo, object peerAverage,
        decimal anomalyAmount, string anomalyContextJson,
        string? localGuess)
    {
        var payload = new
        {
            task = "anomaly_explain",
            anomaly = new
            {
                type = anomalyType,
                description = anomalyDescription,
                amount = anomalyAmount,
                context = anomalyContextJson,
            },
            vendor_history_12mo = vendorHistory12mo,
            peer_average = peerAverage,
            local_model = new { pick = localGuess },
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.AnomalyExplanation,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localGuess,
            LocalConfidence = null,
            LocalModelVersion = "3-sigma-v1",
            SourceEntityType = "AnomalyDetection",
            SourceEntityId = anomalyId,
            CacheTtlOverrideDays = 7,
            MaxTokensOverride = 400,
        };
    }
}
