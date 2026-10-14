using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.ImportDataReview"/> (ตรวจข้อมูลทั้งชุดก่อนนำเข้า) — รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.6
///
/// <para><b>สมอง</b>: ห่อ <see cref="ImportReviewHeuristics.Review"/> (กติกาที่ refactor ไปอยู่บน <c>ImportValueNormalizer</c> ตัวเดียวกับที่ลงข้อมูลจริง) แล้วตอบ
/// <b>รูปเดียวกับครู</b> ทาง <see cref="LocalPrediction.StructuredJson"/> (normalizations · fuzzy_duplicates · quality_flags · field_validation · batch_patterns ·
/// overall_quality_score · summary) ⇒ ปิด provider ทุกตัวแล้วผลตรวจยังขึ้นครบ (kill-switch ข้อ 5) · cold start = กติกาล้วน (ข้อ 3)</para>
///
/// <para><b>เรียนรู้</b> (<see cref="LoadFromFeedbackAsync"/>): จากการรับ/ปฏิเสธคำแนะนำของผู้ใช้ (<c>ImportAiAugmenter.RecordReviewChoiceAsync</c> ⇒
/// UserChosenAnswer = <see cref="ChoiceJson"/>) — คำแนะนำ "ชนิดที่ไม่บล็อก" (การจัดรูปแบบ · ค่าติดลบ · วันที่ไกล) ของ field ใดที่บริษัทนี้ปฏิเสธ
/// ≥ 3 ครั้งและ ≥ 80% ⇒ เลิกเสนอ · ปัญหาที่มีผลทางกฎหมาย/ข้อมูลเสีย (เลขภาษีผิด · ช่องบังคับว่าง · วันที่อ่านไม่ได้) <b>ไม่ถูกปิดด้วยการเรียนรู้เด็ดขาด</b></para>
///
/// <para>ความมั่นใจคงที่ <see cref="RuleConfidence"/> (ต่ำกว่า Hybrid 0.85) ⇒ AI ยังตรวจเพิ่มเมื่อเปิดอยู่ · นักเรียนคือตาข่ายเมื่อ AI ไม่อยู่</para>
/// </summary>
public sealed class ImportDataReviewDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.ImportDataReview;
    public string Version { get; private set; } = "ImportReview-v1";

    /// <summary>กติกาตอบได้เสมอ ⇒ พร้อมตั้งแต่วินาทีแรก (cold-start)</summary>
    public bool IsReady => true;

    private const decimal RuleConfidence = 0.60m;
    private const int SuppressMinRejects = 3;
    private const int FeedbackScan = 500;

    /// <summary>รหัสคำแนะนำที่ "ปิดด้วยการเรียนรู้ได้" — การจัดรูปแบบ + ข้อสังเกตที่ไม่บล็อก · รหัสอื่น (เลขภาษีผิด/ช่องบังคับว่าง/อ่านค่าไม่ได้) ปิดไม่ได้</summary>
    internal static readonly IReadOnlySet<string> SuppressibleCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "thai-digits", "thousand-separator", "number-format", "date-format", "date-buddhist-year", "taxid-format", "flag-format",
        "branch-format", "date-far-future", "number-negative",
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<ImportDataReviewDistillationModel> _logger;
    private readonly Dictionary<(Guid CompanyId, string Entity), HashSet<(string Field, string Code)>> _suppressed = new();
    private readonly object _lock = new();

    public ImportDataReviewDistillationModel(IServiceProvider services, ILogger<ImportDataReviewDistillationModel> logger)
    {
        _services = services;
        _logger = logger;
    }

    // ═══════════════════════ คำตอบของผู้ใช้ (รูปมาตรฐาน) ═══════════════════════

    private sealed record ChoiceItem(string Field, string Code);
    private sealed record Choice(IReadOnlyList<ChoiceItem> Accepted, IReadOnlyList<ChoiceItem> Rejected);

    /// <summary>คำตอบรูปมาตรฐานของผู้ใช้ — <c>{"accepted":[{field,code}],"rejected":[…]}</c> เรียง+ไม่ซ้ำ (ทั้งการบันทึกและ PrimaryAnswer ของนักเรียนใช้ตัวนี้)</summary>
    public static string ChoiceJson(IEnumerable<ImportReviewChoice> accepted, IEnumerable<ImportReviewChoice> rejected)
    {
        static List<ChoiceItem> Norm(IEnumerable<ImportReviewChoice> xs) => xs
            .Where(x => !string.IsNullOrWhiteSpace(x.Field) && !string.IsNullOrWhiteSpace(x.Code))
            .Select(x => new ChoiceItem(x.Field.Trim(), x.Code.Trim()))
            .Distinct()
            .OrderBy(x => x.Field, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        return Services.Import.ImportJson.Serialize(new Choice(Norm(accepted), Norm(rejected)));
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var featureKey = nameof(AiFeatureKey.ImportDataReview);
            var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
                .Where(f => f.CompanyId == companyId && f.FeatureKey == featureKey && f.UserChosenAnswer != null)
                .OrderByDescending(f => f.CreatedAt)
                .Take(FeedbackScan)
                .Select(f => new { f.PromptJson, f.UserChosenAnswer })
                .ToListAsync(ct);
            var built = BuildSuppressions(rows.Select(r => (r.PromptJson, r.UserChosenAnswer)));
            lock (_lock)
            {
                foreach (var k in _suppressed.Keys.Where(k => k.CompanyId == companyId).ToList()) _suppressed.Remove(k);
                foreach (var (entity, set) in built) _suppressed[(companyId, entity)] = set;
                Version = $"ImportReview-v{DateTime.UtcNow:yyyyMMddHHmm}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImportDataReviewDistillationModel: โหลดข้อมูลฝึกไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    /// <summary>pure: แถว (prompt, คำตอบผู้ใช้) → ชุด (field, code) ที่ถูกปฏิเสธเป็นประจำต่อชนิดข้อมูล — แยกออกมาให้เทสต์ round-trip ได้</summary>
    internal static Dictionary<string, HashSet<(string Field, string Code)>> BuildSuppressions(IEnumerable<(string? PromptJson, string? Chosen)> rows)
    {
        var tally = new Dictionary<(string Entity, string Field, string Code), (int Acc, int Rej)>();
        foreach (var (prompt, chosen) in rows)
        {
            var entity = ReadEntity(prompt);
            if (entity == null || string.IsNullOrWhiteSpace(chosen)) continue;
            Choice? c;
            try { c = Services.Import.ImportJson.Deserialize<Choice>(chosen); }
            catch (JsonException) { continue; }
            if (c == null) continue;
            foreach (var a in c.Accepted ?? Array.Empty<ChoiceItem>())
            {
                var k = (entity, a.Field, a.Code);
                var t = tally.GetValueOrDefault(k);
                tally[k] = (t.Acc + 1, t.Rej);
            }
            foreach (var r in c.Rejected ?? Array.Empty<ChoiceItem>())
            {
                var k = (entity, r.Field, r.Code);
                var t = tally.GetValueOrDefault(k);
                tally[k] = (t.Acc, t.Rej + 1);
            }
        }
        var result = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);
        foreach (var ((entity, field, code), (acc, rej)) in tally)
        {
            if (!SuppressibleCodes.Contains(code) || rej < SuppressMinRejects || rej < 4 * acc) continue;
            if (!result.TryGetValue(entity, out var set)) result[entity] = set = new HashSet<(string, string)>();
            set.Add((field, code));
        }
        return result;
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Task.FromResult<LocalPrediction?>(null);
            var entity = Str(root, "entity_type") ?? "";
            var targets = new List<ImportPrompts.TargetField>();
            if (root.TryGetProperty("target_field_specs", out var tf) && tf.ValueKind == JsonValueKind.Array)
                foreach (var f in tf.EnumerateArray())
                {
                    var name = Str(f, "field_name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    targets.Add(new ImportPrompts.TargetField(name, Str(f, "display_name") ?? name, Str(f, "data_type") ?? "string",
                        f.TryGetProperty("is_required", out var rq) && rq.ValueKind == JsonValueKind.True, null));
                }
            var cols = new List<string>();
            if (root.TryGetProperty("mapped_columns", out var mc) && mc.ValueKind == JsonValueKind.Array)
                foreach (var c in mc.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())) cols.Add(c.GetString()!);
            var rows = new List<Dictionary<string, string?>>();
            if (root.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                {
                    var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    if (r.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                        foreach (var p in fields.EnumerateObject())
                            d[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null
                                : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                    rows.Add(d);
                }
            var existing = new List<ImportPrompts.ExistingEntityRef>();
            if (root.TryGetProperty("existing_entities_sample", out var ex) && ex.ValueKind == JsonValueKind.Array)
                foreach (var e in ex.EnumerateArray())
                {
                    var id = Str(e, "id");
                    if (id == null) continue;
                    existing.Add(new ImportPrompts.ExistingEntityRef(id, Str(e, "label") ?? "", Str(e, "key")));
                }

            var review = ImportReviewHeuristics.Review(entity, targets, cols, rows, existing);
            HashSet<(string Field, string Code)>? off;
            lock (_lock) _suppressed.TryGetValue((companyId, ImportExportPermissionScope.NormalizeEntityType(entity)), out off);
            if (off is { Count: > 0 })
                review = review with
                {
                    Normalizations = review.Normalizations.Where(n => n.Code == null || !off.Contains((n.Field, n.Code))).ToList(),
                    FieldValidations = review.FieldValidations.Where(v => v.Code == null || !off.Contains((v.Field, v.Code))).ToList(),
                };

            var json = ToTeacherJson(review);
            var primary = ChoiceJson(
                review.Normalizations.Where(n => n.Code != null).Select(n => new ImportReviewChoice(n.Field, n.Code!))
                    .Concat(review.FieldValidations.Where(v => v.Code != null).Select(v => new ImportReviewChoice(v.Field, v.Code!))),
                Array.Empty<ImportReviewChoice>());
            return Task.FromResult<LocalPrediction?>(new LocalPrediction(primary, RuleConfidence, Array.Empty<string>(), 0, Version)
            {
                StructuredJson = json,
            });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogDebug(e, "ImportDataReviewDistillationModel: อ่าน input ไม่ได้ — ปล่อยให้ครู/ผู้เรียกตอบ");
            return Task.FromResult<LocalPrediction?>(null);
        }
    }

    /// <summary>ผลตรวจ → JSON รูปเดียวกับที่ครูตอบ (<c>ImportPrompts.DataReviewSystem</c>) ⇒ <c>ImportAiAugmenter.ParseDataReviewResponse</c> อ่านได้ตรง</summary>
    internal static string ToTeacherJson(ImportAiReviewResult r) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["normalizations"] = r.Normalizations.Select(n => new Dictionary<string, object?>
        {
            ["rowIndex"] = n.RowIndex, ["field"] = n.Field, ["original"] = n.Original, ["normalized"] = n.Normalized, ["reason"] = n.Reason, ["code"] = n.Code,
        }).ToList(),
        ["fuzzy_duplicates"] = r.FuzzyDuplicates.Select(d => new Dictionary<string, object?>
        {
            ["rowIndex"] = d.RowIndex, ["incomingKey"] = d.IncomingKey, ["existingId"] = d.ExistingId, ["existingLabel"] = d.ExistingLabel,
            ["similarity"] = d.Similarity, ["reasoning"] = d.Reasoning,
        }).ToList(),
        ["quality_flags"] = r.QualityFlags.Select(q => new Dictionary<string, object?>
        {
            ["rowIndex"] = q.RowIndex, ["severity"] = q.Severity, ["message"] = q.Message,
        }).ToList(),
        ["field_validation"] = r.FieldValidations.Select(v => new Dictionary<string, object?>
        {
            ["rowIndex"] = v.RowIndex, ["field"] = v.Field, ["value"] = v.Value, ["issue"] = v.Issue, ["suggestedFix"] = v.SuggestedFix, ["code"] = v.Code,
        }).ToList(),
        ["batch_patterns"] = r.BatchPatterns,
        ["overall_quality_score"] = r.OverallQualityScore,
        ["summary"] = r.Summary,
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string? ReadEntity(string? promptJson)
    {
        if (string.IsNullOrWhiteSpace(promptJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            var e = Str(doc.RootElement, "entity_type");
            return e == null ? null : ImportExportPermissionScope.NormalizeEntityType(e);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement root, string prop)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
