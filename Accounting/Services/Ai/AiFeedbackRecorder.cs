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
        try
        {
            var row = new AiSuggestionFeedback
            {
                CompanyId = record.CompanyId,
                FeatureKey = record.FeatureKey.ToString(),
                PromptHash = record.PromptHash,
                PromptJson = record.PromptJson,
                ResponseJson = record.ResponseJson,
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
            // Recording failure must NEVER bring the call site down —
            // worst case the admin widget under-counts.
            _logger.LogError(ex, "AiFeedback record failed for {Feature}", record.FeatureKey);
            return Guid.Empty;
        }
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiFeedback user-choice update failed for {Id}", feedbackId);
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
