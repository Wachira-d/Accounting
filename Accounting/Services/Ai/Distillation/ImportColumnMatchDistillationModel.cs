using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Import;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.ImportColumnMatch"/> (จับคู่หัวคอลัมน์ไฟล์นำเข้า → field ปลายทาง) — รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.6
///
/// <para><b>สมอง</b>: <see cref="ImportColumnMapper.Resolve"/> ชั้น local ทั้งหมด (คำพ้อง <see cref="ImportFieldSynonymSeed"/> · โปรไฟล์โปรแกรมต้นทาง
/// <see cref="ImportSourceSeed"/> · คู่ที่บริษัทยืนยันแล้ว · รูปข้อมูล) บนคำถามรูปเดียวกับครู (<c>ImportPrompts.BuildColumnMatch</c>) แล้วตอบ
/// <b>รูปเดียวกับครู</b> <c>{"mappings":[{sourceIndex,targetField,confidence,reasoning}]}</c> ทาง <see cref="LocalPrediction.StructuredJson"/> ·
/// <see cref="LocalPrediction.PrimaryAnswer"/> = <see cref="ImportColumnMapper.CanonicalAnswer"/> (รูปเดียวกับ UserChosenAnswer ⇒ สถิติความแม่นจริง)</para>
///
/// <para><b>เรียนรู้</b> (<see cref="LoadFromFeedbackAsync"/>): <c>IImportMappingTemplateStore.GetLearnedHeadersAsync</c> — ที่มาเดียวกับชั้น 🧠 ของ pipeline
/// (แม่แบบบริษัท + การยืนยันการจับคู่ที่ <c>RecordColumnMatchChoiceAsync</c> ปิดลูปไว้ — รวมคำตอบของครูที่ผู้ใช้รับ = การกลั่น) ·
/// <b>cold start</b>: คำพ้อง + โปรไฟล์ ตอบได้ตั้งแต่ไฟล์แรกของบริษัทใหม่ (กฎเหล็ก #1 ข้อ 3)</para>
///
/// <para>ความมั่นใจรวม = ค่าเฉลี่ยต่อคอลัมน์ที่ถูกถาม (คอลัมน์ที่ตอบไม่ได้นับ 0) ⇒ ตอบครบด้วยคู่ที่เรียนไว้ ≥ 0.85 ⇒ Hybrid ไม่เรียก AI ·
/// ตอบไม่ได้สักคอลัมน์ ⇒ null (ไม่แต่งคำตอบ · F2 ข้อ 3)</para>
/// </summary>
public sealed class ImportColumnMatchDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.ImportColumnMatch;
    public string Version { get; private set; } = "ImportColumnMatch-v1";

    /// <summary>คำพ้อง/โปรไฟล์ตอบได้เสมอ ⇒ พร้อมตั้งแต่วินาทีแรก (cold-start)</summary>
    public bool IsReady => true;

    private readonly IServiceProvider _services;
    private readonly ILogger<ImportColumnMatchDistillationModel> _logger;
    private readonly Dictionary<Guid, IReadOnlyList<ImportLearnedHeader>> _learned = new();
    private readonly object _lock = new();

    public ImportColumnMatchDistillationModel(IServiceProvider services, ILogger<ImportColumnMatchDistillationModel> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IImportMappingTemplateStore>();
            var pairs = await store.GetLearnedHeadersAsync(companyId, null, ct);
            lock (_lock)
            {
                _learned[companyId] = pairs;
                Version = $"ImportColumnMatch-v{DateTime.UtcNow:yyyyMMddHHmm}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImportColumnMatchDistillationModel: โหลดข้อมูลฝึกไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        IReadOnlyList<ImportLearnedHeader> learned;
        lock (_lock) learned = _learned.TryGetValue(companyId, out var l) ? l : Array.Empty<ImportLearnedHeader>();
        return Task.FromResult(Predict(inputJson, learned, Version));
    }

    /// <summary>pure: คำถามรูปครู + คู่ที่เรียนไว้ → คำตอบรูปครู (แยกจาก DI ให้เทสต์ cold-start/round-trip ได้)</summary>
    internal static LocalPrediction? Predict(string inputJson, IReadOnlyList<ImportLearnedHeader> learned, string version)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var entity = Str(root, "entity_type") ?? "";
            var targets = new List<ImportFieldDto>();
            if (root.TryGetProperty("target_fields", out var tf) && tf.ValueKind == JsonValueKind.Array)
                foreach (var f in tf.EnumerateArray())
                {
                    var name = Str(f, "field_name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    targets.Add(new ImportFieldDto(name, Str(f, "display_name") ?? name, ImportSchemas.ParseFieldType(Str(f, "data_type"), name),
                        f.TryGetProperty("is_required", out var rq) && rq.ValueKind == JsonValueKind.True, Str(f, "description")));
                }
            var cols = new List<ImportColumnInput>();
            if (root.TryGetProperty("columns", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (!c.TryGetProperty("source_index", out var si) || !si.TryGetInt32(out var idx)) continue;
                    var samples = new List<string>();
                    if (c.TryGetProperty("sample_values", out var sv) && sv.ValueKind == JsonValueKind.Array)
                        foreach (var v in sv.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.String) samples.Add(v.GetString() ?? "");
                    cols.Add(new ImportColumnInput(idx, Str(c, "source_header") ?? "", samples));
                }
            if (targets.Count == 0 || cols.Count == 0) return null;

            var plan = ImportColumnMapper.Resolve(new ImportColumnMapRequest(entity, targets, cols, Learned: learned));
            var mapped = plan.Columns.Where(p => p.TargetField != null).ToList();
            if (mapped.Count == 0) return null;

            var json = JsonSerializer.Serialize(new
            {
                mappings = plan.Columns.Select(p => new
                {
                    sourceIndex = p.SourceIndex,
                    targetField = p.TargetField,
                    confidence = p.TargetField == null ? 0m : p.Confidence,
                    reasoning = p.TargetField == null ? "ระบบยังไม่รู้จักคอลัมน์นี้" : p.SourceLabel,
                }),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var confidence = Math.Round(plan.Columns.Sum(p => p.TargetField == null ? 0m : p.Confidence) / plan.Columns.Count, 4,
                MidpointRounding.AwayFromZero);
            var learnedHits = mapped.Count(p => p.Source == ImportMapSource.Learned);
            return new LocalPrediction(ImportColumnMapper.CanonicalAnswer(plan.Columns.Select(p => (p.SourceIndex, p.TargetField))),
                confidence, Array.Empty<string>(), learnedHits, version)
            {
                StructuredJson = json,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string prop)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
