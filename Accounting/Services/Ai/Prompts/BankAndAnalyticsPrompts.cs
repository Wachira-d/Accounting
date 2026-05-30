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
        DateTime DocDate, decimal Outstanding, string? ContactName,
        // Enriched fields — supplied by the caller when available so AI
        // can break amount/date ties using counterparty intelligence.
        string? ContactTaxId = null,
        int? PriorPaymentsFromSameContact = null,
        int? AvgPaymentDelayDays = null,
        string? TypicalMemoPattern = null);

    /// <summary>Extract canonical hints from the bank memo before sending
    /// to the AI — invoice number patterns, reference codes, contact
    /// name fragments. Letting the orchestrator parse these locally
    /// rather than burning AI tokens on regex-able structure.</summary>
    public static IReadOnlyList<string> ExtractMemoHints(string? memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return Array.Empty<string>();
        var hints = new List<string>();
        // Common Thai invoice prefixes
        var docNumPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:INV|TXN|REF|PV|RV|BIL|TAX|IV)[-_/]?\d{4,}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in docNumPattern.Matches(memo))
            hints.Add($"docNumber:{m.Value}");
        // Bare 6-12 digit reference (could be doc number or settlement id)
        var numPattern = new System.Text.RegularExpressions.Regex(@"\b\d{6,12}\b");
        foreach (System.Text.RegularExpressions.Match m in numPattern.Matches(memo).Cast<System.Text.RegularExpressions.Match>().Take(3))
            hints.Add($"refNumber:{m.Value}");
        // Common Thai counterparty designators
        if (memo.Contains("บจก", StringComparison.OrdinalIgnoreCase)
            || memo.Contains("บมจ", StringComparison.OrdinalIgnoreCase)
            || memo.Contains("หจก", StringComparison.OrdinalIgnoreCase))
            hints.Add("hasJuristicMarker:true");
        // Transfer / payment verb markers
        if (memo.Contains("transfer", StringComparison.OrdinalIgnoreCase)
            || memo.Contains("โอน", StringComparison.OrdinalIgnoreCase)) hints.Add("verb:transfer");
        if (memo.Contains("payment", StringComparison.OrdinalIgnoreCase)
            || memo.Contains("ชำระ", StringComparison.OrdinalIgnoreCase)) hints.Add("verb:payment");
        if (memo.Contains("refund", StringComparison.OrdinalIgnoreCase)
            || memo.Contains("คืน", StringComparison.OrdinalIgnoreCase)) hints.Add("verb:refund");
        return hints;
    }

    public static AiRequest Build(
        Guid companyId,
        Guid bankTransactionId,
        string? memo, DateTime txnDate, decimal txnAmount, string currency,
        IReadOnlyList<OpenDocCandidate> candidates,
        string? localBestDocId, decimal? localConfidence,
        // New optional caller context — past 90-day payment velocity
        // statistics by contact. Lets AI reason about "this counter-
        // party usually pays 50k a week — 12k looks like a partial".
        object? recentPaymentVelocity = null,
        IReadOnlyList<string>? bankAccountHistoricalPatterns = null)
    {
        var payload = new
        {
            task = "bank_statement_match",
            statement_line = new
            {
                memo = memo ?? "",
                memo_hints = ExtractMemoHints(memo),   // pre-parsed structure
                date = txnDate.ToString("yyyy-MM-dd"),
                day_of_week = txnDate.DayOfWeek.ToString(),
                amount = txnAmount,
                currency,
            },
            // Richer candidate cards — tax id (for juristic-marker
            // disambiguation), prior payment cadence per contact, the
            // typical memo prefix this contact uses so AI can pattern-
            // match across "INV-2025-…" vs "PAY 2025-…".
            candidates = candidates.Select(c => new
            {
                id = c.DocumentId,
                number = c.Number,
                type = c.Type,
                date = c.DocDate.ToString("yyyy-MM-dd"),
                days_old = (txnDate - c.DocDate).Days,
                outstanding = c.Outstanding,
                amount_match_pct = txnAmount > 0
                    ? Math.Round(100m * (1m - Math.Abs(c.Outstanding - txnAmount) / txnAmount), 1)
                    : 0m,
                contact = c.ContactName,
                contact_tax_id = c.ContactTaxId,
                contact_prior_payment_count = c.PriorPaymentsFromSameContact,
                contact_avg_delay_days = c.AvgPaymentDelayDays,
                contact_typical_memo = c.TypicalMemoPattern,
            }),
            recent_velocity = recentPaymentVelocity,
            bank_account_patterns = bankAccountHistoricalPatterns ?? Array.Empty<string>(),
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
            LocalModelVersion = "BankMatcher-v2",
            SourceEntityType = "BankTransaction",
            SourceEntityId = bankTransactionId,
            CacheTtlOverrideDays = 1,    // Bank state changes daily
            MaxTokensOverride = 450,
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
        string? localGuess,
        // New optional enrichments — when supplied, they unlock much
        // sharper explanations because AI can ground its reasoning in
        // calendar effects ("ปกติเดือนธันวาคมยอดสูงเพราะโบนัส") and in
        // trend slope ("ค่อย ๆ เพิ่มขึ้นทุกเดือน — ไม่ใช่ outlier
        // เดี่ยว แต่เป็น regime shift").
        object? seasonality = null,
        object? trend = null,
        IReadOnlyList<decimal>? recentMonthlyTotals = null,
        decimal? madZScore = null,
        decimal? typicalMin = null, decimal? typicalMax = null)
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
                // Promote the detector's numerical signal so AI doesn't
                // have to re-derive it from the raw history series.
                mad_z_score = madZScore,
                typical_range = (typicalMin.HasValue && typicalMax.HasValue)
                    ? new { min = typicalMin, max = typicalMax } : null,
            },
            vendor_history_12mo = vendorHistory12mo,
            peer_average = peerAverage,
            // Calendar context — typical Thai SME bills spike at month-
            // end (salary, rent) and year-end (bonus, tax). Telling AI
            // the current month's seasonal multiplier prevents
            // misclassifying a legit December spike as anomalous.
            seasonality = seasonality,
            trend = trend,
            recent_12mo = recentMonthlyTotals,
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
            LocalModelVersion = "MAD-v2",
            SourceEntityType = "AnomalyDetection",
            SourceEntityId = anomalyId,
            CacheTtlOverrideDays = 7,
            MaxTokensOverride = 450,
        };
    }
}
