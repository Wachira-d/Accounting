using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;

namespace Accounting.Services.Import;

/// <param name="TemplateId">แม่แบบที่ผู้ใช้เลือก · null = หาเองจาก signature ของชุดหัว</param>
/// <param name="AllowAi">false = local ล้วน (worker/ไฟล์ใหญ่/ผู้ใช้ปิด) — ผลทุกชั้นใต้ AI เหมือนเดิม</param>
/// <param name="UserAssignments">✋ คอลัมน์ที่ผู้ใช้เลือกแล้ว (remap) — ชนะทุกชั้น</param>
public sealed record ImportColumnMatchOptions(
    Guid? BatchId = null,
    Guid? TemplateId = null,
    string? SourceSystem = null,
    bool AllowAi = true,
    IReadOnlyList<ImportColumnAssignment>? UserAssignments = null);

/// <summary>ผลจับคู่คอลัมน์ของทั้งไฟล์ — เก็บ <see cref="AiFeedbackId"/>/<see cref="UsedAi"/> ลง <c>SmartImportSession.ColumnMatchAiFeedbackId/ColumnMatchUsedAi</c>
/// แล้วปิดลูปตอนยืนยันด้วย <see cref="IImportColumnMatchService.RecordConfirmedAsync"/></summary>
public sealed record ImportColumnMatchOutcome(
    ImportColumnPlan Plan,
    string HeaderSignature,
    Guid? TemplateId,
    ImportTemplateRefDto? Template,
    Guid? AiFeedbackId,
    bool UsedAi);

/// <summary>
/// **จับคู่คอลัมน์ทั้งไฟล์ — ทางเข้าเดียวของทุกผู้เรียก** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.3/§2.6) — pipeline (ทีม B: Analyze/Remap) ·
/// <c>AiSuggestionController.SuggestImportMapping</c> · หน้าอื่นที่อยากแนะนำการจับคู่
///
/// <para>ลำดับ: แม่แบบ (ตาม TemplateId หรือ signature) + คู่ที่เรียนไว้ (<see cref="IImportMappingTemplateStore.GetLearnedHeadersAsync"/>) →
/// <see cref="ImportColumnMapper.Resolve"/> ชั้น local → คอลัมน์ที่ยังไม่มั่นใจเท่านั้นถาม <see cref="IImportAiAugmenter.SuggestColumnMappingsWithFeedbackAsync"/>
/// (orchestrator · นักเรียนก่อน · ด่านคำตอบ) → Resolve อีกรอบพร้อมคำตอบชั้น AI (เติมเฉพาะช่องว่าง) · AI ปิด/ล่ม = ผลชั้น local ครบ (kill-switch)</para>
/// </summary>
public interface IImportColumnMatchService
{
    Task<ImportColumnMatchOutcome> MapAsync(Guid companyId, string entityType, IReadOnlyList<ImportColumnInput> columns,
        ImportColumnMatchOptions options, CancellationToken ct = default);

    /// <summary>ผู้ใช้ยืนยัน (commit) / แก้ (remap) การจับคู่ ⇒ <see cref="IImportMappingTemplateStore.RecordColumnMatchChoiceAsync"/> + ให้นักเรียนเรียนทันที
    /// (ไม่ต้องรองานกลางคืน) · <paramref name="feedbackId"/> null = ไม่มีอะไรต้องปิด (false)</summary>
    Task<bool> RecordConfirmedAsync(Guid companyId, Guid? feedbackId, IReadOnlyList<ImportColumnPlanDto> finalColumns, CancellationToken ct = default);
}

public sealed class ImportColumnMatchService : IImportColumnMatchService
{
    private readonly IImportMappingTemplateStore _templates;
    private readonly IImportAiAugmenter _ai;
    private readonly IEnumerable<ILocalDistillationModel> _students;

    public ImportColumnMatchService(IImportMappingTemplateStore templates, IImportAiAugmenter ai, IEnumerable<ILocalDistillationModel> students)
    {
        _templates = templates;
        _ai = ai;
        _students = students;
    }

    public async Task<ImportColumnMatchOutcome> MapAsync(Guid companyId, string entityType, IReadOnlyList<ImportColumnInput> columns,
        ImportColumnMatchOptions options, CancellationToken ct = default)
    {
        var targets = ImportSchemas.For(entityType)
            ?? throw new BusinessRuleException($"ยังไม่รองรับการนำเข้าข้อมูลชนิด \"{entityType}\" — เลือกชนิดข้อมูลจากรายการ");
        var signature = ImportHeaderSignature.Compute(columns.Select(c => c.Header));

        ImportTemplateRefDto? tref = null;
        IReadOnlyList<ImportTemplateColumnDto>? templateCols = null;
        Guid? templateId = options.TemplateId;
        if (templateId == null)
            templateId = (await _templates.FindAsync(companyId, entityType, signature, ct))?.Id;
        if (templateId is { } tid)
        {
            var t = await _templates.GetAsync(companyId, tid, ct);
            // แม่แบบของชนิดอื่น (ผู้ใช้เลือกผิด) ไม่ถูกใช้ — ไม่ใช่ล้ม: ชั้นอื่นจับคู่ต่อได้
            if (t != null && ImportExportPermissionScope.NormalizeEntityType(t.EntityType) == ImportExportPermissionScope.NormalizeEntityType(entityType))
            {
                templateCols = t.Columns;
                tref = new ImportTemplateRefDto(t.Id, t.Name, t.ConfirmedByUser, t.AutoCommitWhenClean, t.UseCount);
            }
            else templateId = null;
        }
        var learned = await _templates.GetLearnedHeadersAsync(companyId, entityType, ct);

        var request = new ImportColumnMapRequest(entityType, targets, columns, templateCols, learned,
            UserAssignments: options.UserAssignments, SourceSystem: options.SourceSystem);
        var plan = ImportColumnMapper.Resolve(request);
        if (!options.AllowAi || plan.Uncertain.Count == 0)
            return new ImportColumnMatchOutcome(plan, signature, templateId, tref, null, false);

        // ชั้น AI — เฉพาะคอลัมน์ที่ชั้น local ยังไม่มั่นใจ · ชั้นสูงกว่าไม่ถูกทับ (Resolve เติมเฉพาะช่องว่าง)
        var asked = plan.Uncertain.Select(c => new ImportPrompts.ColumnMatchInput(c.SourceIndex, c.Header,
            c.SampleValues.Where(v => !string.IsNullOrWhiteSpace(v)).Take(5).ToList(),
            plan.Columns.FirstOrDefault(p => p.SourceIndex == c.SourceIndex)?.TargetField,
            (double)(plan.Columns.FirstOrDefault(p => p.SourceIndex == c.SourceIndex)?.Confidence ?? 0m))).ToList();
        var ai = await _ai.SuggestColumnMappingsWithFeedbackAsync(companyId, options.BatchId, entityType,
            ImportPrompts.ToTargetFields(targets), asked, ct);
        if (ai.Suggestions.Count == 0)
            return new ImportColumnMatchOutcome(plan, signature, templateId, tref, ai.FeedbackId, false);

        var final = ImportColumnMapper.Resolve(request with
        {
            AiAnswers = ai.Suggestions.Select(s => new ImportColumnAiAnswer(s.SourceIndex, s.TargetField, s.Confidence, s.Reasoning, ai.UsedAi)).ToList(),
        });
        return new ImportColumnMatchOutcome(final, signature, templateId, tref, ai.FeedbackId, final.UsedAi);
    }

    public async Task<bool> RecordConfirmedAsync(Guid companyId, Guid? feedbackId, IReadOnlyList<ImportColumnPlanDto> finalColumns,
        CancellationToken ct = default)
    {
        if (feedbackId is not { } fid || fid == Guid.Empty) return false;
        var ok = await _templates.RecordColumnMatchChoiceAsync(companyId, fid, finalColumns, ct);
        if (!ok) return false;
        // "train ไปเลย" — นักเรียนเห็นคู่ที่เพิ่งยืนยันทันที (singleton · โหลดเฉพาะบริษัทนี้)
        foreach (var s in _students.Where(s => s.FeatureKey == AiFeatureKey.ImportColumnMatch))
            await s.LoadFromFeedbackAsync(companyId, ct);
        return true;
    }
}
