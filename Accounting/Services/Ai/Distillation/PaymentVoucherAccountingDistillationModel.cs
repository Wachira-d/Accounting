using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.PaymentVoucherAccountingSuggestion"/> — รอบ 201 ทีม AI · A-AI4 (H-6)
///
/// ═══ ของเดิมพังตรงไหน ═══
/// feature นี้มี <b>สองรูปคำถาม</b>: รายบรรทัด (คำตอบเดี่ยว = รหัสผัง) กับทั้งใบ (<c>BulkPvAccountingPrompt</c> ·
/// คำตอบเป็นโครง <c>{"lines":[…]}</c>) แต่ลงทะเบียนเป็น <c>GenericFeedbackDistillationModel</c> ที่ตอบได้แต่คำตอบเดี่ยว ⇒
/// ปิด provider แล้วกด "AI แนะนำผังบัญชี" บนใบสำคัญจ่าย นักเรียนตอบรหัสยอดฮิตของบริษัท (tier-2) ตัวเดียว →
/// <c>ParseBulkPvResponse</c> แปลเป็นโครงไม่ได้ → "เติมให้ 0/N บรรทัด" + คำเตือน "AI ตอบกลับ JSON ไม่ valid"
/// ทั้งที่ไม่เคยเรียก AI (ผู้ใช้เข้าใจว่า AI เสีย) — กฎเหล็ก #1 ข้อ 2 เขียนเองว่า output แบบ bulk ต้องเป็น bespoke
///
/// ═══ วิธีตอบ ═══
/// • คำถามรายบรรทัด → ส่งต่อให้นักเรียน generic ตัวใน (พฤติกรรมเดิมทุกประการ)
/// • คำถามทั้งใบ → แตกเป็นคำถามรายบรรทัด<b>รูปเดียวกับแถว feedback ลูก</b>ที่ <c>DocumentAiAugmenter</c> บันทึกไว้
///   (<see cref="BuildPerLineInputJson"/> ตัวเดียว — กุญแจฝั่งเขียนกับฝั่งอ่านต้องตรงกัน ไม่งั้นคลังเขียนแล้วไม่มีใครอ่าน)
///   แล้วประกอบ <see cref="LocalPrediction.StructuredJson"/> รูปเดียวกับที่ AI คืน · ความมั่นใจรวม = ต่ำสุดของทุกบรรทัด
///   (ตอบไม่ครบทุกบรรทัด = 0 ⇒ ไม่มีวัน short-circuit ข้ามครู)
/// </summary>
public sealed class PaymentVoucherAccountingDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.PaymentVoucherAccountingSuggestion;
    public string Version => _inner.Version;
    public bool IsReady => _inner.IsReady;

    private readonly GenericFeedbackDistillationModel _inner;

    public PaymentVoucherAccountingDistillationModel(IServiceProvider services,
        ILogger<GenericFeedbackDistillationModel> innerLogger)
    {
        _inner = new GenericFeedbackDistillationModel(
            AiFeatureKey.PaymentVoucherAccountingSuggestion, services, innerLogger);
    }

    public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
        => _inner.LoadFromFeedbackAsync(companyId, ct);

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
        => PredictWithAsync(companyId, inputJson, (cid, json, c) => _inner.PredictAsync(cid, json, c), ct);

    /// <summary>คำถามรายบรรทัด 1 ข้อ — <b>รูปเดียว</b>ที่ทั้งแถว feedback ลูกของ bulk PV และนักเรียนใช้
    /// (เปลี่ยนรูปนี้ = ทุกอย่างที่เรียนไว้กลายเป็นกุญแจที่ไม่มีใครอ่าน)</summary>
    public static string BuildPerLineInputJson(string? vendorName, string? vendorTaxId, string? vendorIndustry,
        string? description, decimal amount)
        => JsonSerializer.Serialize(new
        {
            vendor = new { name = vendorName, tax_id = vendorTaxId, industry = vendorIndustry },
            line = new { description, amount },
        });

    /// <summary>แกนของการตอบ — แยกตัวทำนายรายบรรทัดออกมาให้เทสต์ใส่ของปลอมได้</summary>
    internal static async Task<LocalPrediction?> PredictWithAsync(Guid companyId, string inputJson,
        Func<Guid, string, CancellationToken, Task<LocalPrediction?>> perLine, CancellationToken ct)
    {
        if (!TryParseBulk(inputJson, out var vendorName, out var vendorTaxId, out var vendorIndustry, out var lines))
            return await perLine(companyId, inputJson, ct);
        if (lines.Count == 0) return null;

        var answered = new List<(string LineId, LocalPrediction P)>();
        foreach (var l in lines)
        {
            var p = await perLine(companyId,
                BuildPerLineInputJson(vendorName, vendorTaxId, vendorIndustry, l.Description, l.Amount), ct);
            if (p != null && !string.IsNullOrWhiteSpace(p.PrimaryAnswer)) answered.Add((l.LineId, p));
        }
        if (answered.Count == 0) return null;

        var complete = answered.Count == lines.Count;
        var structured = JsonSerializer.Serialize(new
        {
            lines = answered.Select(a => new
            {
                lineId = a.LineId,
                accountCode = a.P.PrimaryAnswer,
                confidence = a.P.Confidence,
                alternatives = a.P.Alternatives,
                reasoning = $"⚙️ ระบบ (โมเดลในบ้าน) — จากคำตอบที่บริษัทนี้ยืนยันไว้ {a.P.SupportingSamples} ครั้ง",
            }),
            cross_line_observations = Array.Empty<string>(),
            warnings = complete
                ? Array.Empty<string>()
                : new[] { $"โมเดลในบ้านตอบได้ {answered.Count}/{lines.Count} บรรทัด — บรรทัดที่เหลือยังไม่มีประวัติให้เรียน" },
        });
        return new LocalPrediction(
            PrimaryAnswer: answered[0].P.PrimaryAnswer,
            // ตอบไม่ครบ = 0 ⇒ orchestrator ไม่ short-circuit (ยังถามครูเมื่อครูพร้อม) · ครบ = บรรทัดที่อ่อนที่สุดเป็นตัวตัดสิน
            Confidence: complete ? answered.Min(a => a.P.Confidence) : 0m,
            Alternatives: Array.Empty<string>(),
            SupportingSamples: answered.Sum(a => a.P.SupportingSamples),
            ModelVersion: answered[0].P.ModelVersion + "-bulk")
        {
            StructuredJson = structured,
        };
    }

    private sealed record BulkLine(string LineId, string? Description, decimal Amount);

    /// <summary>คำถามทั้งใบของ <c>BulkPvAccountingPrompt</c> (task = bulk_pv_line_accounting) — false = คำถามรายบรรทัด</summary>
    private static bool TryParseBulk(string inputJson, out string? vendorName, out string? vendorTaxId,
        out string? vendorIndustry, out List<BulkLine> lines)
    {
        vendorName = vendorTaxId = vendorIndustry = null;
        lines = new List<BulkLine>();
        if (string.IsNullOrWhiteSpace(inputJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.String
                || task.GetString() != "bulk_pv_line_accounting")
                return false;
            if (root.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                vendorName = Str(v, "Name");
                vendorTaxId = Str(v, "TaxId");
                vendorIndustry = Str(v, "Industry");
            }
            if (root.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var id = Str(el, "line_id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var amount = el.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number
                        && a.TryGetDecimal(out var d) ? d : 0m;
                    lines.Add(new BulkLine(id!, Str(el, "description"), amount));
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
