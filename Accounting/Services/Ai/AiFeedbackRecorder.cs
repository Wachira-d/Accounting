using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Writes the per-call AiSuggestionFeedback row + updates AiUsageDaily
/// rollup. EVERY orchestrator path goes through here including the
/// failure / skipped paths — the row is the audit trail for "AI was
/// asked but said no / was unreachable" so the admin widget shows the
/// true call volume not just successes.
///
/// User-acceptance updates (UserChosenAnswer, UserAcceptedAi) are
/// written by the call-site via RecordUserChoiceAsync after the user
/// confirms in the UI.
/// </summary>
public interface IAiFeedbackRecorder
{
    Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct);
    Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct);

    /// <summary>Bulk-insert synthetic CHILD feedback rows (e.g. one per match in
    /// a bulk-bank-match plan) in a SINGLE SaveChanges instead of N round-trips.
    /// These carry zero tokens/cost (they are not real provider calls), so the
    /// daily usage rollup + budget cache are intentionally skipped. Returns the
    /// generated ids in the same order as the input; a row that fails to save
    /// yields Guid.Empty for the whole batch (best-effort — never throws).</summary>
    Task<IReadOnlyList<Guid>> RecordChildBatchAsync(IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct);
}

public sealed record AiFeedbackRecord(
    Guid CompanyId,
    AiFeatureKey FeatureKey,
    string PromptHash,
    string PromptJson,
    string? ResponseJson,
    string? AiPrimaryAnswer,
    decimal? AiConfidence,
    string? LocalModelAnswer,
    decimal? LocalModelConfidence,
    string? LocalModelVersion,
    string? SourceEntityType,
    Guid? SourceEntityId,
    AiCallStatus Status,
    AiProviderType ProviderUsed,
    string? ModelVersion,
    int? LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    Guid? CacheHitOfFeedbackId,
    string? ErrorMessage);

public class AiFeedbackRecorder : IAiFeedbackRecorder
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AiFeedbackRecorder> _logger;

    public AiFeedbackRecorder(AccountingDbContext db, ILogger<AiFeedbackRecorder> logger)
    { _db = db; _logger = logger; }

    public async Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct)
    {
        AiSuggestionFeedback? row = null;
        try
        {
            row = new AiSuggestionFeedback
            {
                CompanyId = record.CompanyId,
                FeatureKey = record.FeatureKey.ToString(),
                PromptHash = record.PromptHash,
                // Both columns are jsonb in Postgres — any non-JSON string here
                // throws 22P02 and leaves the entity stuck in the change tracker,
                // which then poisons every later SaveChanges in the request
                // (including AuditMiddleware's, observed in production). Coerce
                // anything that isn't already valid JSON into {"raw":"..."} so
                // the row always saves.
                PromptJson = CoerceJsonNonNull(record.PromptJson),
                ResponseJson = CoerceJsonNullable(record.ResponseJson),
                AiPrimaryAnswer = record.AiPrimaryAnswer,
                AiConfidence = record.AiConfidence,
                LocalModelAnswer = record.LocalModelAnswer,
                LocalModelConfidence = record.LocalModelConfidence,
                LocalModelVersion = record.LocalModelVersion,
                SourceEntityType = record.SourceEntityType,
                SourceEntityId = record.SourceEntityId,
                Status = record.Status,
                ProviderUsed = record.ProviderUsed,
                ModelVersion = record.ModelVersion,
                LatencyMs = record.LatencyMs,
                InputTokens = record.InputTokens,
                OutputTokens = record.OutputTokens,
                CostUsd = record.CostUsd,
                CacheHitOfFeedbackId = record.CacheHitOfFeedbackId,
                ErrorMessage = record.ErrorMessage,
            };
            _db.AiSuggestionFeedbacks.Add(row);
            await _db.SaveChangesAsync(ct);

            // Bump the daily rollup. Tolerant upsert — if two parallel
            // calls race we accept the small over-count rather than
            // serialising every AI call through a row lock.
            await UpsertDailyRollupAsync(record, ct);

            // Invalidate budget guard's 30s cache so the next call
            // reflects this call's contribution.
            AiBudgetGuard.InvalidateCache();
            return row.Id;
        }
        catch (Exception ex)
        {
            // Recording failure must NEVER bring the call site down — worst
            // case the admin widget under-counts. DETACH the failed entity
            // so it doesn't sit Added in the change tracker and re-throw on
            // the next SaveChangesAsync in this scope (e.g. AuditMiddleware).
            if (row != null)
            {
                try { _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* nothing else to do */ }
            }
            _logger.LogError(ex, "AiFeedback record failed for {Feature}", record.FeatureKey);
            return Guid.Empty;
        }
    }

    public async Task<IReadOnlyList<Guid>> RecordChildBatchAsync(
        IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return Array.Empty<Guid>();
        var rows = new List<AiSuggestionFeedback>(records.Count);
        try
        {
            foreach (var record in records)
            {
                var row = new AiSuggestionFeedback
                {
                    CompanyId = record.CompanyId,
                    FeatureKey = record.FeatureKey.ToString(),
                    PromptHash = record.PromptHash,
                    PromptJson = CoerceJsonNonNull(record.PromptJson),
                    ResponseJson = CoerceJsonNullable(record.ResponseJson),
                    AiPrimaryAnswer = record.AiPrimaryAnswer,
                    AiConfidence = record.AiConfidence,
                    LocalModelAnswer = record.LocalModelAnswer,
                    LocalModelConfidence = record.LocalModelConfidence,
                    LocalModelVersion = record.LocalModelVersion,
                    SourceEntityType = record.SourceEntityType,
                    SourceEntityId = record.SourceEntityId,
                    Status = record.Status,
                    ProviderUsed = record.ProviderUsed,
                    ModelVersion = record.ModelVersion,
                    LatencyMs = record.LatencyMs,
                    InputTokens = record.InputTokens,
                    OutputTokens = record.OutputTokens,
                    CostUsd = record.CostUsd,
                    CacheHitOfFeedbackId = record.CacheHitOfFeedbackId,
                    ErrorMessage = record.ErrorMessage,
                };
                _db.AiSuggestionFeedbacks.Add(row);
                rows.Add(row);
            }
            // ONE round-trip for the whole batch (vs N). No rollup / budget
            // bump — these synthetic child rows carry zero tokens + cost.
            await _db.SaveChangesAsync(ct);
            return rows.Select(r => r.Id).ToList();
        }
        catch (Exception ex)
        {
            foreach (var r in rows)
            {
                try { _db.Entry(r).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* nothing else to do */ }
            }
            _logger.LogWarning(ex, "AiFeedback child batch failed ({Count} rows)", records.Count);
            return records.Select(_ => Guid.Empty).ToList();
        }
    }

    /// <summary>Return s when it parses as JSON; otherwise wrap it as a JSON
    /// object {"raw":"..."} so the jsonb column accepts it. Used because the
    /// AI provider's raw text isn't always JSON-formatted but the column is
    /// jsonb-typed; without this we hit Postgres 22P02 and the failed entity
    /// poisons later SaveChanges in the same request.</summary>
    private static string CoerceJsonNonNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return s; }
        catch { return System.Text.Json.JsonSerializer.Serialize(new { raw = s }); }
    }

    private static string? CoerceJsonNullable(string? s)
    {
        if (s == null) return null;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return s; }
        catch { return System.Text.Json.JsonSerializer.Serialize(new { raw = s }); }
    }

    public async Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct)
    {
        if (feedbackId == Guid.Empty) return;
        try
        {
            var row = await _db.AiSuggestionFeedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct);
            if (row == null) return;
            row.UserChosenAnswer = chosenAnswer;
            row.UserChosenAt = DateTime.UtcNow;
            row.UserAcceptedAi = acceptedAi;
            await _db.SaveChangesAsync(ct);

            // ── ONLINE LEARNING (train ไปเลย) ───────────────────────────
            // The moment a user confirms or overrides, fold the ground
            // truth into AiSuggestionMemory so the very next suggestion for
            // the same input returns the learned answer — no nightly job,
            // no manual "train" click. Keyed by the feedback row's
            // PromptHash, which suggestion endpoints set to a stable
            // business key (contactId, normalised name, …). Skipped when
            // the key is empty (legacy rows) or the answer is blank.
            await LearnInlineAsync(row.CompanyId, row.FeatureKey, row.PromptHash, chosenAnswer, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiFeedback user-choice update failed for {Id}", feedbackId);
        }
    }

    /// <summary>Upsert the per-(company, feature, input) learned answer.
    /// First confirmation already counts — a single explicit user choice
    /// for an exact input is authoritative, so the model "learns ไปเลย".
    /// A persistent override (Override &gt; Accept) flips the stored answer
    /// to the new value. Confidence = Accept / (Accept + Override).</summary>
    private async Task LearnInlineAsync(Guid companyId, string featureKey, string inputKey, string chosenAnswer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputKey) || string.IsNullOrWhiteSpace(chosenAnswer)) return;
        if (inputKey.Length > 256) inputKey = inputKey[..256];
        try
        {
            var mem = await _db.AiSuggestionMemories.FirstOrDefaultAsync(
                m => m.CompanyId == companyId && m.FeatureKey == featureKey && m.InputKey == inputKey, ct);
            if (mem == null)
            {
                mem = new AiSuggestionMemory
                {
                    CompanyId = companyId, FeatureKey = featureKey, InputKey = inputKey,
                    LearnedAnswer = chosenAnswer, AcceptCount = 1, OverrideCount = 0,
                    Confidence = 1m, LastLearnedAt = DateTime.UtcNow,
                };
                _db.AiSuggestionMemories.Add(mem);
            }
            else if (string.Equals(mem.LearnedAnswer, chosenAnswer, StringComparison.Ordinal))
            {
                mem.AcceptCount++;
            }
            else
            {
                mem.OverrideCount++;
                // A challenger that has now out-voted the incumbent takes over.
                if (mem.OverrideCount > mem.AcceptCount)
                {
                    mem.LearnedAnswer = chosenAnswer;
                    mem.AcceptCount = 1;
                    mem.OverrideCount = 0;
                }
            }
            var total = mem.AcceptCount + mem.OverrideCount;
            mem.Confidence = total > 0 ? Math.Round((decimal)mem.AcceptCount / total, 4) : 1m;
            mem.LastLearnedAt = DateTime.UtcNow;
            mem.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online-learning upsert failed feature={Feature} key={Key}", featureKey, inputKey);
        }
    }

    private async Task UpsertDailyRollupAsync(AiFeedbackRecord record, CancellationToken ct)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var featureKey = record.FeatureKey.ToString();
            var row = await _db.AiUsageDailies.FirstOrDefaultAsync(
                u => u.UsageDate == today && u.ProviderType == record.ProviderUsed && u.FeatureKey == featureKey, ct);
            if (row == null)
            {
                row = new AiUsageDaily
                {
                    UsageDate = today,
                    ProviderType = record.ProviderUsed,
                    FeatureKey = featureKey,
                };
                _db.AiUsageDailies.Add(row);
            }
            row.CallsAttempted++;
            switch (record.Status)
            {
                case AiCallStatus.Success: row.CallsSuccessful++; break;
                case AiCallStatus.Cached: row.CallsCached++; break;
                case AiCallStatus.Failed:
                case AiCallStatus.InvalidResponse: row.CallsFailed++; break;
                case AiCallStatus.BudgetExceeded: row.CallsBudgetBlocked++; break;
            }
            row.InputTokensTotal += record.InputTokens ?? 0;
            row.OutputTokensTotal += record.OutputTokens ?? 0;
            row.CostUsdTotal += record.CostUsd ?? 0m;
            if (record.LatencyMs.HasValue && row.CallsSuccessful > 0)
            {
                row.AvgLatencyMs = (int)(((long)row.AvgLatencyMs * (row.CallsSuccessful - 1) + record.LatencyMs.Value)
                                          / row.CallsSuccessful);
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiUsageDaily upsert failed (non-fatal)");
        }
    }
}
