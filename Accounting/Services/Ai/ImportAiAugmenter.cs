using System.Text.Json;
using Accounting.Data;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// AI-side augmentation for the otherwise 100%-heuristic import pipeline.
/// Two responsibilities, both wrapping <see cref="IAiOrchestrator"/> so
/// budget cap / cache / fallback handling are inherited:
///
///   1. <see cref="SuggestColumnMappingsAsync"/> — re-rank uncertain
///      column → field mappings at upload time.
///   2. <see cref="ReviewImportAsync"/> — one-shot review of the whole
///      batch before commit (normalizations, fuzzy dups, quality
///      flags, semantic validation, batch patterns).
///
/// Both fail OPEN: on any exception or AI-unavailable response the
/// augmenter returns an empty result so the import can still proceed
/// using only the heuristic pipeline. We never break a working import
/// because AI was sick.
/// </summary>
public interface IImportAiAugmenter
{
    /// <summary>Re-rank uncertain column mappings. Caller passes only the
    /// columns whose heuristic confidence was low — passing everything
    /// would waste tokens on the 90% that the heuristic already nailed.
    /// Returns an empty list when AI is disabled / failed; caller falls
    /// back to the heuristic mapping silently.</summary>
    Task<List<ImportColumnAiSuggestion>> SuggestColumnMappingsAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<ImportPrompts.ColumnMatchInput> uncertainColumns,
        CancellationToken ct = default);

    /// <summary>Single AI call covering five concerns at once: type
    /// normalizations, fuzzy duplicates, per-row quality flags, semantic
    /// field validation, batch-level patterns. The combined surface is
    /// cheaper + more coherent than five independent calls because the
    /// underlying context (sample rows + target schema + existing-entity
    /// slice) is identical.</summary>
    Task<ImportAiReviewResult> ReviewImportAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<string> mappedColumnOrder,
        IReadOnlyList<Dictionary<string, string?>> sampleRows,
        IReadOnlyList<ImportPrompts.ExistingEntityRef> existingSlice,
        CancellationToken ct = default);
}

public sealed record ImportColumnAiSuggestion(
    int SourceIndex,
    string? TargetField,
    decimal Confidence,
    string? Reasoning);

public sealed record ImportAiReviewResult(
    bool UsedAi,
    decimal? OverallQualityScore,
    string? Summary,
    IReadOnlyList<ImportNormalization> Normalizations,
    IReadOnlyList<ImportFuzzyDuplicate> FuzzyDuplicates,
    IReadOnlyList<ImportQualityFlag> QualityFlags,
    IReadOnlyList<ImportFieldValidation> FieldValidations,
    IReadOnlyList<string> BatchPatterns);

public sealed record ImportNormalization(
    int RowIndex, string Field, string? Original, string? Normalized, string? Reason);

public sealed record ImportFuzzyDuplicate(
    int RowIndex, string? IncomingKey, string? ExistingId, string? ExistingLabel,
    decimal Similarity, string? Reasoning);

public sealed record ImportQualityFlag(
    int RowIndex, string Severity, string Message);

public sealed record ImportFieldValidation(
    int RowIndex, string Field, string? Value, string Issue, string? SuggestedFix);

public class ImportAiAugmenter : IImportAiAugmenter
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<ImportAiAugmenter> _logger;

    public ImportAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        ILogger<ImportAiAugmenter> logger)
    { _db = db; _orchestrator = orchestrator; _logger = logger; }

    public async Task<List<ImportColumnAiSuggestion>> SuggestColumnMappingsAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<ImportPrompts.ColumnMatchInput> uncertainColumns,
        CancellationToken ct = default)
    {
        if (uncertainColumns.Count == 0) return new();
        try
        {
            var req = ImportPrompts.BuildColumnMatch(companyId, sessionId, entityType, targets, uncertainColumns);
            var resp = await _orchestrator.AskAsync(req, ct);
            if (string.IsNullOrWhiteSpace(resp.RawResponseJson))
                return new();
            return ParseColumnMatchResponse(resp.RawResponseJson!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import column-match AI augmenter failed");
            return new();
        }
    }

    public async Task<ImportAiReviewResult> ReviewImportAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<string> mappedColumnOrder,
        IReadOnlyList<Dictionary<string, string?>> sampleRows,
        IReadOnlyList<ImportPrompts.ExistingEntityRef> existingSlice,
        CancellationToken ct = default)
    {
        try
        {
            var req = ImportPrompts.BuildDataReview(companyId, sessionId, entityType,
                targets, mappedColumnOrder, sampleRows, existingSlice);
            var resp = await _orchestrator.AskAsync(req, ct);
            // ไม่มีคำตอบจาก provider (ปิด AI / เกินงบ / timeout / feature disabled)
            // → ใช้ตัวตรวจ rule-based แทน ห้ามคืน list ว่าง (กฎเหล็ก #1: local ต้อง
            // ทดแทนได้ 100% และ UI ต้องแยกออกว่า "ไม่มีปัญหา" ≠ "AI ไม่ทำงาน")
            if (string.IsNullOrWhiteSpace(resp.RawResponseJson))
                return ImportReviewHeuristics.Review(
                    entityType, targets, mappedColumnOrder, sampleRows, existingSlice);
            return ParseDataReviewResponse(resp.RawResponseJson!, usedAi: resp.UsedAi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import data-review AI augmenter failed — ใช้ตัวตรวจ rule-based แทน");
            try
            {
                return ImportReviewHeuristics.Review(
                    entityType, targets, mappedColumnOrder, sampleRows, existingSlice);
            }
            catch (Exception hex)
            {
                _logger.LogWarning(hex, "Import data-review heuristics failed");
                return EmptyResult(usedAi: false);
            }
        }
    }

    private static ImportAiReviewResult EmptyResult(bool usedAi) =>
        new(usedAi, null, null,
            Array.Empty<ImportNormalization>(),
            Array.Empty<ImportFuzzyDuplicate>(),
            Array.Empty<ImportQualityFlag>(),
            Array.Empty<ImportFieldValidation>(),
            Array.Empty<string>());

    private static List<ImportColumnAiSuggestion> ParseColumnMatchResponse(string raw)
    {
        // AI returns { "mappings": [ { sourceIndex, targetField, confidence, reasoning } ] }
        // — sanitize defensively so a slightly malformed item doesn't lose the whole batch.
        var result = new List<ImportColumnAiSuggestion>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("mappings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("sourceIndex", out var siEl) ||
                    !siEl.TryGetInt32(out var si)) continue;
                string? tf = item.TryGetProperty("targetField", out var tfEl) && tfEl.ValueKind == JsonValueKind.String ? tfEl.GetString() : null;
                decimal conf = item.TryGetProperty("confidence", out var cEl) && cEl.TryGetDecimal(out var cv) ? cv : 0m;
                string? reason = item.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
                result.Add(new ImportColumnAiSuggestion(si, tf, conf, reason));
            }
        }
        catch { /* malformed JSON — return what we parsed so far */ }
        return result;
    }

    private static ImportAiReviewResult ParseDataReviewResponse(string raw, bool usedAi)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new ImportAiReviewResult(
                usedAi,
                ReadDecimal(root, "overall_quality_score"),
                ReadString(root, "summary"),
                ReadArray(root, "normalizations", el => new ImportNormalization(
                    ReadInt(el, "rowIndex") ?? -1,
                    ReadString(el, "field") ?? "",
                    ReadString(el, "original"),
                    ReadString(el, "normalized"),
                    ReadString(el, "reason"))),
                ReadArray(root, "fuzzy_duplicates", el => new ImportFuzzyDuplicate(
                    ReadInt(el, "rowIndex") ?? -1,
                    ReadString(el, "incomingKey"),
                    ReadString(el, "existingId"),
                    ReadString(el, "existingLabel"),
                    ReadDecimal(el, "similarity") ?? 0m,
                    ReadString(el, "reasoning"))),
                ReadArray(root, "quality_flags", el => new ImportQualityFlag(
                    ReadInt(el, "rowIndex") ?? -1,
                    ReadString(el, "severity") ?? "info",
                    ReadString(el, "message") ?? "")),
                ReadArray(root, "field_validation", el => new ImportFieldValidation(
                    ReadInt(el, "rowIndex") ?? -1,
                    ReadString(el, "field") ?? "",
                    ReadString(el, "value"),
                    ReadString(el, "issue") ?? "",
                    ReadString(el, "suggestedFix"))),
                ReadStringArray(root, "batch_patterns"));
        }
        catch
        {
            return EmptyResult(usedAi: usedAi);
        }
    }

    private static List<T> ReadArray<T>(JsonElement root, string key, Func<JsonElement, T> map)
    {
        var list = new List<T>();
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
        {
            try { list.Add(map(el)); } catch { /* skip malformed item */ }
        }
        return list;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? ReadInt(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.TryGetInt32(out var v) ? v : null;

    private static decimal? ReadDecimal(JsonElement root, string key) =>
        root.TryGetProperty(key, out var el) && el.TryGetDecimal(out var v) ? v : null;
}
