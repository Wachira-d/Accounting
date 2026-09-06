using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.OcrLineItemSplit"/> — "แตกบรรทัดจากข้อความล้วน"
///
/// ═══ ทำไมต้องมี (กฎเหล็ก #1 ข้อ 2 · feature parity) ═══
/// feature นี้เป็น <b>AiFeatureKey ตัวสุดท้ายในไปป์ไลน์ OCR ที่ยังไม่มี student</b>
/// ⇒ ปิด provider ทุกตัว (kill-switch ข้อ 5) แล้วเอกสารจากกระดาษที่ engine อ่านตาราง
/// ไม่ออกจะได้บรรทัดสรุปใบเดียวตลอดกาล ซึ่งขัดกฎเหล็ก #3 ข้อ 6 ตรง ๆ
///
/// ═══ สองชั้น (ชั้นล่างต้องตอบได้เสมอ = cold-start ไม่ว่างเปล่า) ═══
/// <list type="number">
/// <item><b>จำใบที่เคยยืนยันแล้ว</b> — บิลประจำ (ค่าไฟ/ค่าน้ำ/ค่าเช่า/ค่าบริการรายเดือน)
///   มีโครงข้อความ<b>เหมือนเดิมทุกงวด</b> ต่างแค่ตัวเลข ⇒ ใช้ลายนิ้วมือของข้อความที่
///   <b>ตัดตัวเลขออก</b> เป็นคีย์ แล้วคืนชุดบรรทัดที่ผู้ใช้เคยยืนยันของผู้ขายรายนั้น
///   (คำอธิบาย/หน่วยมาจากของเดิม · <b>ตัวเลขคิดใหม่จากยอดบนกระดาษใบนี้เสมอ</b> —
///   ห้ามคืนยอดของงวดก่อน ซึ่งจะเป็นการ "แต่งตัวเลข" ที่ไฟล์ CLAUDE.md ห้ามไว้)</item>
/// <item><b>กติกาแตกบรรทัด</b> (<see cref="Accounting.Helpers.RawTextLineSplitter"/>)
///   — ตอบได้ทันทีตั้งแต่ใบแรกของ tenant ใหม่ ไม่ต้องรอครูสอน</item>
/// </list>
///
/// <para>ทั้งสองชั้นถูกส่งต่อให้ <see cref="Accounting.Helpers.OcrLineSplitGuard"/>
/// (ด่านเดียวกับคำตอบของครู) ⇒ ผลรวมต้องตรงยอดบนกระดาษถึงจะกลายเป็นรายการทางบัญชี
/// "เดาผิด" จึงถูกปฏิเสธ ไม่ใช่ถูกบันทึก</para>
/// </summary>
public sealed class LineSplitDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.OcrLineItemSplit;
    public string Version { get; private set; } = "LineSplit-v1";

    /// <summary>ชั้นกติกาตอบได้เสมอ ⇒ พร้อมใช้ตั้งแต่วินาทีแรก (cold-start
    /// ตามกฎเหล็ก #1 ข้อ 3) — ไม่ผูกกับจำนวนแถวที่เรียนมา</summary>
    public bool IsReady => true;

    private readonly IServiceProvider _services;
    private readonly ILogger<LineSplitDistillationModel> _logger;

    /// <summary>(บริษัท, ลายนิ้วมือโครงข้อความ) → โครงบรรทัดที่ผู้ใช้ยืนยันแล้ว</summary>
    private readonly Dictionary<(Guid CompanyId, string Shape), LearnedShape> _byShape = new();
    private readonly object _lock = new();

    /// <summary>เพดานความมั่นใจของชั้น "จำได้" — ต่ำกว่า short-circuit ของ Hybrid (0.85)
    /// เพื่อไม่ให้นักเรียนไปปิดกั้นครูที่ยังทำงานได้ปกติ</summary>
    private const decimal RecalledConfidence = 0.80m;

    /// <summary>ความมั่นใจของชั้นกติกา — ตั้งใจให้ต่ำ: มันคือ "ตาข่ายรอง"
    /// ไม่ใช่คำตอบที่ควรชนะครู</summary>
    private const decimal RuleConfidence = 0.55m;

    /// <summary>โครงบรรทัดที่เรียนมา — เก็บเฉพาะ "สิ่งที่คงที่ทุกงวด"
    /// (คำอธิบาย + หน่วย) · <b>ไม่เก็บจำนวนเงิน</b> เพราะเป็นค่าต่อใบ
    /// (บทเรียน `VendorKnownGoodCorrector`: ค่าที่เปลี่ยนทุกใบ ห้ามอยู่ถังเดียว
    /// กับค่าที่คงที่ — ไม่งั้นใบใหม่ถูกทับด้วยตัวเลขของใบก่อน)</summary>
    private sealed record LearnedShape(IReadOnlyList<(string Description, string? Unit)> Lines, int Samples);

    public LineSplitDistillationModel(IServiceProvider services, ILogger<LineSplitDistillationModel> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

            // ความจริงที่ยืนยันแล้ว = สแกนที่ถูกใช้สร้างเอกสารจริง (ผู้ใช้ตรวจแล้วกดสร้าง)
            // — แหล่งเดียวกับ OcrFullReviewDistillationModel เพื่อไม่ให้มีนิยาม
            // "ยืนยันแล้ว" สองชุด
            var rows = await db.Set<OcrScanResult>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && r.CreatedDocumentId != null
                    && r.RawTextContent != null
                    && r.ExtractedItemsJson != null)
                .OrderByDescending(r => r.CreatedAt)
                .Take(1000)
                .Select(r => new { r.RawTextContent, r.ExtractedItemsJson })
                .ToListAsync(ct);

            var grouped = new Dictionary<(Guid, string), LearnedShape>();
            foreach (var r in rows)
            {
                var shape = ShapeFingerprint(r.RawTextContent);
                if (shape.Length == 0) continue;
                var lines = ReadConfirmedLines(r.ExtractedItemsJson);
                if (lines.Count == 0) continue;

                var key = (companyId, shape);
                if (grouped.TryGetValue(key, out var prev))
                    // แถวเรียงใหม่→เก่า ⇒ ตัวแรกคือล่าสุด เก็บโครงล่าสุดไว้ นับตัวอย่างเพิ่ม
                    grouped[key] = prev with { Samples = prev.Samples + 1 };
                else
                    grouped[key] = new LearnedShape(lines, 1);
            }

            lock (_lock)
            {
                foreach (var k in _byShape.Keys.Where(k => k.CompanyId == companyId).ToList())
                    _byShape.Remove(k);
                foreach (var (k, v) in grouped) _byShape[k] = v;
                Version = $"LineSplit-v{DateTime.UtcNow:yyyyMMddHHmm}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LineSplitDistillationModel: โหลดข้อมูลฝึกไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            var rawText = Str(root, "ocr_raw_text");
            if (string.IsNullOrWhiteSpace(rawText)) return Task.FromResult<LocalPrediction?>(null);

            // ยอดเป้าหมายบนกระดาษ **ใบนี้** — ตัวเลขทุกตัวที่คืนออกไปต้องมาจากตรงนี้
            decimal? target = null;
            if (root.TryGetProperty("known_totals", out var kt) && kt.ValueKind == JsonValueKind.Object)
                target = Num(kt, "sub_total") ?? Num(kt, "total_amount");

            // ── ชั้นที่ 1: เคยยืนยันโครงนี้มาแล้วกี่ครั้ง ─────────────────────
            var shape = ShapeFingerprint(rawText);
            LearnedShape? hit = null;
            if (shape.Length > 0)
                lock (_lock) { _byShape.TryGetValue((companyId, shape), out hit); }

            if (hit != null && target is > 0m)
            {
                var json = BuildFromShape(hit.Lines, target.Value);
                if (json != null)
                {
                    var conf = Math.Min(RecalledConfidence, 0.60m + 0.05m * hit.Samples);
                    return Task.FromResult<LocalPrediction?>(new LocalPrediction(
                        "ok", conf, Array.Empty<string>(), hit.Samples, Version)
                    {
                        StructuredJson = json,
                    });
                }
            }

            // ── ชั้นที่ 2: กติกาแตกบรรทัด (ตอบได้ตั้งแต่ใบแรก) ────────────────
            var ruleJson = Accounting.Helpers.RawTextLineSplitter.SplitToJson(rawText);
            if (string.IsNullOrWhiteSpace(ruleJson)) return Task.FromResult<LocalPrediction?>(null);

            return Task.FromResult<LocalPrediction?>(new LocalPrediction(
                "ok", RuleConfidence, Array.Empty<string>(), 0, Version)
            {
                StructuredJson = ruleJson,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LineSplitDistillationModel: อ่าน input ไม่ได้ — ปล่อยให้ครูตอบ");
            return Task.FromResult<LocalPrediction?>(null);
        }
    }

    /// <summary>ประกอบ JSON จากโครงที่จำได้ โดยกระจาย <paramref name="target"/> ตามสัดส่วนเท่ากัน
    /// แล้วยัดเศษที่บรรทัดสุดท้าย ⇒ Σ = ยอดบนกระดาษ<b>เป๊ะ</b> (ด่าน OcrLineSplitGuard ผ่าน)
    ///
    /// <para>คืน <c>null</c> เมื่อโครงมีบรรทัดเดียว — บรรทัดเดียว = ไม่ได้ "แตก" อะไรเลย
    /// ปล่อยให้ชั้นกติกา/ครูลองดูดีกว่า</para></summary>
    private static string? BuildFromShape(IReadOnlyList<(string Description, string? Unit)> lines, decimal target)
    {
        if (lines.Count < 2 || lines.Count > Accounting.Helpers.RawTextLineSplitter.MaxLines) return null;
        var sb = new StringBuilder("{\"lines\":[");
        decimal assigned = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var share = i == lines.Count - 1
                ? target - assigned
                : Math.Round(target / lines.Count, 2, MidpointRounding.AwayFromZero);
            assigned += share;
            if (i > 0) sb.Append(',');
            sb.Append("{\"description\":").Append(JsonEncode(lines[i].Description))
              .Append(",\"quantity\":1,\"unit\":")
              .Append(lines[i].Unit == null ? "null" : JsonEncode(lines[i].Unit!))
              .Append(",\"unit_price\":").Append(share.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"amount\":").Append(share.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>อ่านชุดบรรทัดที่ผู้ใช้ยืนยัน (<c>OcrScanResult.ExtractedItemsJson</c>)
    /// เอาเฉพาะ<b>คำอธิบาย + หน่วย</b> — ตัวเลขเป็นค่าต่อใบ ห้ามเรียน</summary>
    private static List<(string, string?)> ReadConfirmedLines(string? itemsJson)
    {
        var result = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(itemsJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var d = Str(el, "Description") ?? Str(el, "description");
                if (string.IsNullOrWhiteSpace(d)) continue;
                result.Add((d.Trim(), Str(el, "Unit") ?? Str(el, "unit")));
                if (result.Count >= Accounting.Helpers.RawTextLineSplitter.MaxLines) break;
            }
        }
        catch (JsonException) { result.Clear(); }
        return result;
    }

    /// <summary>ลายนิ้วมือ "โครง" ของข้อความ — <b>ตัดตัวเลขและช่องว่างทิ้ง</b>
    /// เพื่อให้บิลประจำที่ต่างกันแค่ตัวเลข/งวด ได้คีย์เดียวกัน
    ///
    /// <para>คืนสตริงว่างเมื่อข้อความสั้นเกินจะเป็นลายนิ้วมือที่มีความหมาย —
    /// คีย์ที่กว้างเกินไปจะจับใบคนละใบมาชนกัน (บทเรียน threshold: ต้องคิดว่า
    /// "ต่างกันแค่ไหนถึงจะตก" ที่ความยาวจริงของข้อมูลชนิดนั้น)</para></summary>
    internal static string ShapeFingerprint(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "";
        var sb = new StringBuilder(rawText.Length);
        foreach (var ch in rawText)
        {
            if (char.IsDigit(ch) || char.IsWhiteSpace(ch)) continue;
            if (ch == ',' || ch == '.' || ch == '-' || ch == '/') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        var normalized = sb.ToString();
        if (normalized.Length < 80) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string JsonEncode(string s) => JsonSerializer.Serialize(s);

    private static string? Str(JsonElement root, string prop)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static decimal? Num(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDecimal(out var d) ? d : null;
}
