using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
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
        CancellationToken ct = default,
        int? totalRowCount = null);

    // ══ รอบ 205 ทีม A (กฎเหล็ก #1 — ไม่ทิ้ง FeedbackId/UsedAi อีก) ══

    /// <summary>
    /// เหมือน <see cref="SuggestColumnMappingsAsync"/> แต่คืน <b>FeedbackId + UsedAi</b> (ผู้เรียกเก็บลง <c>SmartImportSession.ColumnMatchAiFeedbackId/
    /// ColumnMatchUsedAi</c> แล้วปิดลูปด้วย <c>IImportMappingTemplateStore.RecordColumnMatchChoiceAsync</c>) · คำตอบผ่านด่าน
    /// <c>ImportColumnMapper.GuardAiAnswers</c> แล้ว (field ∈ targets · ≥ 0.70 · คอลัมน์ที่ถามจริง) · AI ปิด ⇒ คำตอบนักเรียน (UsedAi=false)
    /// </summary>
    Task<ImportColumnAiResult> SuggestColumnMappingsWithFeedbackAsync(
        Guid companyId, Guid? sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<ImportPrompts.ColumnMatchInput> uncertainColumns,
        CancellationToken ct = default);

    /// <summary>
    /// หาบัญชีในผังบริษัทให้แถวนำเข้า (<c>ImportAccountMap</c> · 58) — ด่าน: คำตอบ ∈ <paramref name="allowedCodes"/> (รหัสผังของบริษัทนี้) ·
    /// ไม่ผ่าน/AI ปิด ⇒ คำตอบ local (<paramref name="localCode"/>) · ผู้เรียก: <c>ImportEntityResolver.ResolveAccountsAsync</c> (ทีม D) ·
    /// ผู้ใช้ยืนยันแถว ⇒ <see cref="RecordPickChoiceAsync"/>
    /// </summary>
    Task<ImportAiPick> SuggestAccountMapAsync(
        Guid companyId, Guid? batchId, string? sourceCode, string? sourceName, string? hint,
        IReadOnlyList<ImportPrompts.AccountCandidate> candidates, IReadOnlyCollection<string> allowedCodes,
        string? localCode, decimal? localConfidence, CancellationToken ct = default);

    /// <summary>
    /// ตัดสินแถวกำกวมว่าเป็นข้อมูลเดิมตัวไหนหรือรายการใหม่ (<c>ImportEntityMatch</c> · 59) — ด่าน: คำตอบ ∈ candidate ids ∪ {"NEW"} ·
    /// ผู้เรียก: <c>ImportEntityResolver.MatchAsync</c> (ทีม B) เฉพาะ top-2 ห่าง &lt; 0.05 และไม่มีเลขภาษี · ผลของ AI = <b>แนะนำ</b> เท่านั้น
    /// (ห้าม AutoApply · คำตัดสินข้อ 151 (5)) · ผู้ใช้ยืนยันแถว ⇒ <see cref="RecordPickChoiceAsync"/>
    /// </summary>
    Task<ImportAiPick> ResolveEntityAsync(
        Guid companyId, Guid? batchId, string entityType, string rowLabel, string? rowDetail,
        IReadOnlyList<ImportPrompts.EntityCandidate> candidates, string? localAnswer, decimal? localConfidence,
        CancellationToken ct = default);

    /// <summary>ปิดลูปคำตอบเดี่ยว (58/59) — ผู้ใช้ยืนยัน/แก้แถว ⇒ <c>RecordUserChoiceAsync(feedbackId, chosen, chosen == suggested)</c> ·
    /// feedback ต้องเป็นของบริษัทนี้ (ไม่ใช่ ⇒ false)</summary>
    Task<bool> RecordPickChoiceAsync(Guid companyId, Guid feedbackId, string chosenAnswer, string? suggestedAnswer, CancellationToken ct = default);

    /// <summary>ปิดลูปผลตรวจข้อมูล (<c>ImportDataReview</c> · 27) — ผู้ใช้รับ/ปฏิเสธคำแนะนำ ⇒ นักเรียนเรียนว่าคำเตือนชนิดไหนของ field ไหน
    /// บริษัทนี้ปฏิเสธเป็นประจำ (เลิกเตือนซ้ำ) · feedback ต้องเป็นของบริษัทนี้</summary>
    Task<bool> RecordReviewChoiceAsync(Guid companyId, Guid feedbackId, IReadOnlyList<ImportReviewChoice> accepted,
        IReadOnlyList<ImportReviewChoice> rejected, CancellationToken ct = default);
}

/// <summary>ผลชั้น AI ของการจับคู่คอลัมน์ — <see cref="UsedAi"/> = เรียก AI จริง (ป้าย 🤖) · <see cref="FromLocalModel"/> = นักเรียนตอบแทน</summary>
public sealed record ImportColumnAiResult(
    IReadOnlyList<ImportColumnAiSuggestion> Suggestions,
    Guid? FeedbackId,
    bool UsedAi,
    bool FromLocalModel,
    int Rejected);

/// <summary>คำตอบเดี่ยวที่ผ่านด่านแล้ว (58/59) — <see cref="Answer"/> null = ไม่มีคำตอบที่ใช้ได้ (ผู้เรียกใช้ local/ให้ผู้ใช้เลือก) ·
/// <see cref="Rejected"/> = AI ตอบนอกชุดที่อนุญาต (ถูกปัดตก — ผู้เรียกไม่ต้องทำอะไร แค่ไม่ใช้)</summary>
public sealed record ImportAiPick(
    string? Answer,
    decimal Confidence,
    bool UsedAi,
    bool FromLocalModel,
    Guid? FeedbackId,
    string? Reasoning,
    bool Rejected,
    IReadOnlyList<string> Alternatives);

/// <summary>หนึ่งคำแนะนำของผลตรวจข้อมูลที่ผู้ใช้รับ/ปฏิเสธ — <see cref="Code"/> = <see cref="ImportFieldValidation.Code"/>/<see cref="ImportNormalization.Code"/></summary>
public sealed record ImportReviewChoice(string Field, string Code);

public sealed record ImportColumnAiSuggestion(
    int SourceIndex,
    string? TargetField,
    decimal Confidence,
    string? Reasoning);

/// <summary>ผลตรวจข้อมูลทั้งชุด — <see cref="FeedbackId"/> (รอบ 205 ทีม A) = แถว feedback ของ ImportDataReview (ปิดลูปด้วย
/// <c>RecordReviewChoiceAsync</c>) · <see cref="UsedAi"/> true เฉพาะเมื่อ AI ตอบจริง</summary>
public sealed record ImportAiReviewResult(
    bool UsedAi,
    decimal? OverallQualityScore,
    string? Summary,
    IReadOnlyList<ImportNormalization> Normalizations,
    IReadOnlyList<ImportFuzzyDuplicate> FuzzyDuplicates,
    IReadOnlyList<ImportQualityFlag> QualityFlags,
    IReadOnlyList<ImportFieldValidation> FieldValidations,
    IReadOnlyList<string> BatchPatterns,
    Guid? FeedbackId = null);

/// <param name="Code">รหัสคงที่ของชนิดการแปลง (รอบ 205 — นักเรียนเรียนการรับ/ปฏิเสธต่อ field+code) · null = จากครูที่ไม่ส่งรหัส</param>
public sealed record ImportNormalization(
    int RowIndex, string Field, string? Original, string? Normalized, string? Reason, string? Code = null);

public sealed record ImportFuzzyDuplicate(
    int RowIndex, string? IncomingKey, string? ExistingId, string? ExistingLabel,
    decimal Similarity, string? Reasoning);

public sealed record ImportQualityFlag(
    int RowIndex, string Severity, string Message);

/// <param name="Code">รหัสคงที่ของปัญหา (เช่น <c>taxid-invalid</c> · <c>required-missing</c>) — ตัวเดียวกับ ImportValueNormalizer</param>
public sealed record ImportFieldValidation(
    int RowIndex, string Field, string? Value, string Issue, string? SuggestedFix, string? Code = null);

public class ImportAiAugmenter : IImportAiAugmenter
{
    private readonly AccountingDbContext _db;
    private readonly IAiOrchestrator _orchestrator;
    private readonly ILogger<ImportAiAugmenter> _logger;
    private readonly IAiFeedbackRecorder? _feedback;

    public ImportAiAugmenter(AccountingDbContext db, IAiOrchestrator orchestrator,
        ILogger<ImportAiAugmenter> logger, IAiFeedbackRecorder? feedback = null)
    { _db = db; _orchestrator = orchestrator; _logger = logger; _feedback = feedback; }

    /// <summary>เส้นเดิม (Smart Import ของ ImportExportService · ทีม B ย้ายไป <see cref="SuggestColumnMappingsWithFeedbackAsync"/>) —
    /// คืนเฉพาะคำแนะนำที่ผ่านด่านแล้ว</summary>
    public async Task<List<ImportColumnAiSuggestion>> SuggestColumnMappingsAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<ImportPrompts.ColumnMatchInput> uncertainColumns,
        CancellationToken ct = default)
        => (await SuggestColumnMappingsWithFeedbackAsync(companyId, sessionId, entityType, targets, uncertainColumns, ct))
            .Suggestions.ToList();

    public async Task<ImportColumnAiResult> SuggestColumnMappingsWithFeedbackAsync(
        Guid companyId, Guid? sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<ImportPrompts.ColumnMatchInput> uncertainColumns,
        CancellationToken ct = default)
    {
        if (uncertainColumns.Count == 0) return new(Array.Empty<ImportColumnAiSuggestion>(), null, false, false, 0);
        try
        {
            var req = ImportPrompts.BuildColumnMatch(companyId, sessionId, entityType, targets, uncertainColumns);
            var resp = await _orchestrator.AskAsync(req, ct);
            if (string.IsNullOrWhiteSpace(resp.RawResponseJson))
                return new(Array.Empty<ImportColumnAiSuggestion>(), resp.FeedbackId, false, resp.FromLocalModel, 0);
            var raw = ParseColumnMatchResponse(resp.RawResponseJson!);
            // ด่าน anti-hallucination ตัวเดียวกับ ImportColumnMapper (field ∈ targets · ≥ 0.70 · คอลัมน์ที่ถามจริง · ไม่ชนกัน)
            var targetDtos = targets.Select(t => new Models.DTOs.Import.ImportFieldDto(t.FieldName, t.DisplayName,
                Accounting.Helpers.ImportSchemas.ParseFieldType(t.DataType, t.FieldName), t.IsRequired, t.Description)).ToList();
            var guarded = Accounting.Helpers.ImportColumnMapper.GuardAiAnswers(
                raw.Select(r => new Accounting.Helpers.ImportColumnAiAnswer(r.SourceIndex, r.TargetField, r.Confidence, r.Reasoning, resp.UsedAi)),
                targetDtos, uncertainColumns.Select(c => c.SourceIndex));
            var rejected = raw.Count(r => r.TargetField != null) - guarded.Count;
            return new(guarded.Select(g => new ImportColumnAiSuggestion(g.SourceIndex, g.TargetField, g.Confidence, g.Reasoning)).ToList(),
                resp.FeedbackId, resp.UsedAi && guarded.Count > 0, resp.FromLocalModel, Math.Max(0, rejected));
        }
        catch (Exception ex)
        {
            // orchestrator ไม่ throw อยู่แล้ว — ที่นี่คือ parse/ด่านพัง: ไม่ทำให้การนำเข้าล้ม (ชั้น local ตัดสินไปแล้ว) แต่ไม่ใส่คำตอบมั่ว
            _logger.LogWarning(ex, "Import column-match AI augmenter failed");
            return new(Array.Empty<ImportColumnAiSuggestion>(), null, false, false, 0);
        }
    }

    public async Task<ImportAiPick> SuggestAccountMapAsync(
        Guid companyId, Guid? batchId, string? sourceCode, string? sourceName, string? hint,
        IReadOnlyList<ImportPrompts.AccountCandidate> candidates, IReadOnlyCollection<string> allowedCodes,
        string? localCode, decimal? localConfidence, CancellationToken ct = default)
    {
        var allowed = new HashSet<string>(allowedCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var local = localCode != null && allowed.Contains(localCode.Trim()) ? localCode.Trim() : null;
        try
        {
            var resp = await _orchestrator.AskAsync(ImportPrompts.BuildAccountMap(companyId, batchId, sourceCode, sourceName, hint,
                candidates, local, localConfidence), ct);
            return GuardPick(resp, allowed, local, localConfidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import account-map AI augmenter failed");
            return new(local, localConfidence ?? 0m, false, false, null, null, false, Array.Empty<string>());
        }
    }

    public async Task<ImportAiPick> ResolveEntityAsync(
        Guid companyId, Guid? batchId, string entityType, string rowLabel, string? rowDetail,
        IReadOnlyList<ImportPrompts.EntityCandidate> candidates, string? localAnswer, decimal? localConfidence,
        CancellationToken ct = default)
    {
        var ids = new HashSet<string>(candidates.Select(c => c.Id.Trim()), StringComparer.OrdinalIgnoreCase) { ImportPrompts.EntityMatchNew };
        var local = localAnswer != null && ids.Contains(localAnswer.Trim()) ? localAnswer.Trim() : null;
        try
        {
            var resp = await _orchestrator.AskAsync(ImportPrompts.BuildEntityMatch(companyId, batchId, entityType, rowLabel, rowDetail,
                candidates, local, localConfidence), ct);
            return GuardPick(resp, ids, local, localConfidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import entity-match AI augmenter failed");
            return new(local, localConfidence ?? 0m, false, false, null, null, false, Array.Empty<string>());
        }
    }

    /// <summary>
    /// ด่านคำตอบเดี่ยว (58/59) — คำตอบต้องอยู่ใน <paramref name="allowed"/> (รหัสผังบริษัท / candidate ids ∪ NEW) · นอกชุด = hallucination ⇒
    /// ปัดตก (<see cref="ImportAiPick.Rejected"/>) แล้วคืนคำตอบ local · <see cref="ImportAiPick.UsedAi"/> = เรียก AI จริงและคำตอบที่คืนคือของ AI
    /// </summary>
    internal static ImportAiPick GuardPick(AiResponse resp, IReadOnlySet<string> allowed, string? local, decimal? localConfidence)
    {
        string? Canon(string? a)
        {
            if (string.IsNullOrWhiteSpace(a)) return null;
            var t = a.Trim();
            return allowed.FirstOrDefault(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase));
        }
        var alts = resp.Alternatives.Select(Canon).Where(a => a != null).Select(a => a!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var answer = Canon(resp.PrimaryAnswer);
        if (answer != null)
            return new(answer, resp.Confidence ?? localConfidence ?? 0m, resp.UsedAi, resp.FromLocalModel, resp.FeedbackId,
                resp.Reasoning, false, alts);
        return new(local, localConfidence ?? 0m, false, resp.FromLocalModel, resp.FeedbackId, null,
            Rejected: !string.IsNullOrWhiteSpace(resp.PrimaryAnswer) && resp.UsedAi, alts);
    }

    public async Task<bool> RecordPickChoiceAsync(Guid companyId, Guid feedbackId, string chosenAnswer, string? suggestedAnswer,
        CancellationToken ct = default)
    {
        if (_feedback == null || feedbackId == Guid.Empty || string.IsNullOrWhiteSpace(chosenAnswer)) return false;
        var accountMap = nameof(AiFeatureKey.ImportAccountMap);
        var entityMatch = nameof(AiFeatureKey.ImportEntityMatch);
        var ok = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .AnyAsync(f => f.Id == feedbackId && f.CompanyId == companyId && (f.FeatureKey == accountMap || f.FeatureKey == entityMatch), ct);
        if (!ok) return false;
        var chosen = chosenAnswer.Trim();
        await _feedback.RecordUserChoiceAsync(feedbackId, chosen,
            acceptedAi: suggestedAnswer != null && string.Equals(chosen, suggestedAnswer.Trim(), StringComparison.OrdinalIgnoreCase),
            ct, UserChoiceSource.Explicit);
        return true;
    }

    public async Task<bool> RecordReviewChoiceAsync(Guid companyId, Guid feedbackId, IReadOnlyList<ImportReviewChoice> accepted,
        IReadOnlyList<ImportReviewChoice> rejected, CancellationToken ct = default)
    {
        if (_feedback == null || feedbackId == Guid.Empty || (accepted.Count == 0 && rejected.Count == 0)) return false;
        var dataReview = nameof(AiFeatureKey.ImportDataReview);
        var ok = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .AnyAsync(f => f.Id == feedbackId && f.CompanyId == companyId && f.FeatureKey == dataReview, ct);
        if (!ok) return false;
        var chosen = Distillation.ImportDataReviewDistillationModel.ChoiceJson(accepted, rejected);
        // "รับทุกข้อ" = เห็นด้วยกับผลตรวจ · มีข้อที่ปฏิเสธ = ไม่รับทั้งชุด (นักเรียนเรียนรายข้อจาก JSON อีกชั้น)
        await _feedback.RecordUserChoiceAsync(feedbackId, chosen, acceptedAi: rejected.Count == 0, ct, UserChoiceSource.Explicit);
        return true;
    }

    public async Task<ImportAiReviewResult> ReviewImportAsync(
        Guid companyId, Guid sessionId, string entityType,
        IReadOnlyList<ImportPrompts.TargetField> targets,
        IReadOnlyList<string> mappedColumnOrder,
        IReadOnlyList<Dictionary<string, string?>> sampleRows,
        IReadOnlyList<ImportPrompts.ExistingEntityRef> existingSlice,
        CancellationToken ct = default,
        int? totalRowCount = null)
    {
        try
        {
            var req = ImportPrompts.BuildDataReview(companyId, sessionId, entityType,
                targets, mappedColumnOrder, sampleRows, existingSlice, totalRowCount);
            var resp = await _orchestrator.AskAsync(req, ct);
            // ไม่มีคำตอบจาก provider (ปิด AI / เกินงบ / timeout / feature disabled)
            // → ใช้ตัวตรวจ rule-based แทน ห้ามคืน list ว่าง (กฎเหล็ก #1: local ต้อง
            // ทดแทนได้ 100% และ UI ต้องแยกออกว่า "ไม่มีปัญหา" ≠ "AI ไม่ทำงาน")
            // รอบ 205: ปกติเส้นนี้ได้คำตอบของนักเรียน ImportDataReviewDistillationModel (รูปเดียวกับครู) ผ่าน RawResponseJson แล้ว ·
            // FeedbackId ติดไปกับผลเสมอ (ไม่ทิ้ง) ⇒ ผู้เรียกปิดลูปด้วย RecordReviewChoiceAsync
            if (string.IsNullOrWhiteSpace(resp.RawResponseJson))
                return ImportReviewHeuristics.Review(
                    entityType, targets, mappedColumnOrder, sampleRows, existingSlice) with { FeedbackId = resp.FeedbackId };
            return GuardReview(ParseDataReviewResponse(resp.RawResponseJson!, usedAi: resp.UsedAi), sampleRows.Count, mappedColumnOrder,
                existingSlice.Select(e => e.Id)) with { FeedbackId = resp.FeedbackId };
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

    /// <summary>
    /// ด่านผลตรวจ (กฎเหล็ก #1 anti-hallucination · รอบ 205) — ข้อที่อ้างแถวนอกตัวอย่าง · field ที่ไม่ได้จับคู่ · ข้อมูลเดิมที่ไม่ได้ส่งให้ดู ⇒ ทิ้ง
    /// (AI แต่งแถว/field/id ขึ้นมา = ชี้ผู้ใช้ไปแก้ของที่ไม่มีจริง) · ใช้กับทั้งคำตอบครูและนักเรียน (นักเรียนผ่านเสมอ)
    /// </summary>
    internal static ImportAiReviewResult GuardReview(ImportAiReviewResult r, int rowCount, IReadOnlyList<string> mappedColumns,
        IEnumerable<string> existingIds)
    {
        var cols = new HashSet<string>(mappedColumns, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        bool Row(int i) => i >= 0 && i < rowCount;
        return r with
        {
            Normalizations = r.Normalizations.Where(n => Row(n.RowIndex) && cols.Contains(n.Field)).ToList(),
            FuzzyDuplicates = r.FuzzyDuplicates.Where(d => Row(d.RowIndex) && d.ExistingId != null && ids.Contains(d.ExistingId)).ToList(),
            QualityFlags = r.QualityFlags.Where(q => Row(q.RowIndex)).ToList(),
            FieldValidations = r.FieldValidations.Where(v => Row(v.RowIndex) && cols.Contains(v.Field)).ToList(),
        };
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
                    ReadString(el, "reason"),
                    ReadString(el, "code"))),
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
                    ReadString(el, "suggestedFix"),
                    ReadString(el, "code"))),
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
