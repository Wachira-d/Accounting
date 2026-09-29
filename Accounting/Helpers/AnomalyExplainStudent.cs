using System.Globalization;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงที่นักเรียนอ่านได้จาก payload ของ <c>AnomalyExplainPrompt.Build</c> (ตัวเดียวกับที่ครูได้)</summary>
/// <param name="Amount">ยอดของรายการผิดปกติ (<c>anomaly.amount</c>) · 0 = รายการที่ไม่ใช่ยอดเงิน (เช่น ใบซ้ำ)</param>
/// <param name="Series">ชุดยอดย้อนหลัง — <c>vendor_history_12mo.history</c> (เส้น ad-hoc) หรือ <c>recent_12mo</c></param>
/// <param name="MadZ">z-score ที่ตัวตรวจจับส่งมาเอง (<c>anomaly.mad_z_score</c>) — ใช้เมื่อไม่มีชุดยอดให้คำนวณ</param>
/// <param name="HistoryCount">จำนวนใบในสรุปประวัติคู่ค้า (<c>vendor_history_12mo.count</c> — เส้นที่บันทึกลงรายการ)</param>
/// <param name="LocalPick">คำตอบตามกติกาของผู้เรียก (<c>local_model.pick</c> — ระดับความรุนแรง Critical ⇒ LikelyError)</param>
public sealed record AnomalyPromptFacts(
    decimal Amount,
    IReadOnlyList<decimal> Series,
    decimal? MadZ,
    decimal? TypicalMin, decimal? TypicalMax,
    int? HistoryCount, decimal? HistoryMin, decimal? HistoryMax, decimal? HistoryMedian,
    string? LocalPick);

/// <summary>คำตอบของนักเรียน — รูปเดียวกับ JSON ที่ครูคืน (<c>primary · confidence · reasoning · suggested_actions · risks</c>)</summary>
public sealed record AnomalyStudentAnswer(
    string Primary, decimal Confidence, string Reasoning,
    IReadOnlyList<string> SuggestedActions, IReadOnlyList<string> Risks, string Basis)
{
    /// <summary>JSON รูปเดียวกับคำตอบครู — ส่งออกทาง <c>LocalPrediction.StructuredJson</c> ⇒ <c>AiResponse.RawResponseJson</c></summary>
    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["primary"] = Primary,
        ["confidence"] = Confidence,
        ["reasoning"] = Reasoning,
        ["suggested_actions"] = SuggestedActions,
        ["risks"] = Risks,
        ["basis"] = Basis,
    });
}

/// <summary>
/// **นักเรียนของ "อธิบายรายการผิดปกติ" (AiFeatureKey.AnomalyExplanation) — ตอบเป็นค่าในชุดเสมอ** (รอบ 200 ทีม RF · R200-X2)
///
/// <para>═══ ที่มา ═══ <c>AnomalyExplanationDistillationModel</c> เดิมอ่าน <c>root.amount</c>/<c>root.history</c> ขณะที่ prompt จริงส่ง
/// <c>{task, anomaly:{amount,…}, vendor_history_12mo, recent_12mo, local_model}</c> ⇒ คืน null ทุกครั้ง = นักเรียนไม่เคยตอบ · และต่อให้ตอบ
/// <c>PrimaryAnswer</c> ก็เป็น JSON <c>{"isAnomaly":…}</c> ที่ไม่อยู่ในชุด <c>LikelyError/LikelyLegit/NeedReview</c> ⇒ controller บันทึกไม่ได้ ⇒
/// ตอนปิด provider (kill-switch กฎเหล็ก #1 ข้อ 5) คอลัมน์ "AI ว่าอย่างไร" ว่างตลอด</para>
///
/// <para>═══ ลำดับหลักฐาน (DOCTRINE §1 — ใกล้ข้อมูลจริงก่อน) ═══
/// (1) ชุดยอดย้อนหลัง ≥ 3 ค่า ⇒ MAD z-score (ผู้เรียกคำนวณด้วย <c>AmountAnomalyDetector</c> ตัวเดียวกับชั้นตรวจจับ) ·
/// (2) z ที่ตัวตรวจจับส่งมา · (3) สรุปประวัติคู่ค้า/ช่วงปกติ (ต่ำสุด–สูงสุด–มัธยฐาน) · (4) กติการะดับความรุนแรงของผู้เรียก ·
/// (5) ไม่มีอะไรเลย ⇒ <c>NeedReview</c> (= "ไม่รู้ ให้คนตรวจ" — ทิศที่มองเห็น ไม่ใช่ LikelyLegit ที่เงียบ)</para>
///
/// <para>G6: pure · ไม่มี I/O · ไม่มีตัวเลขเงินที่ถูกเขียนลงบัญชี (เป็นคำอธิบายบนการ์ด)</para>
/// </summary>
public static class AnomalyExplainStudent
{
    /// <summary>เกณฑ์ outlier มาตรฐานของ MAD z (ตรงกับค่าเริ่มต้นของ <c>AmountAnomalyDetector.CheckModifiedZScore</c>)</summary>
    public const decimal ZThreshold = 3.5m;

    /// <summary>z เกินนี้ = ห่างจากประวัติมากจนน่าจะผิด (พิมพ์/อ่านทศนิยมผิด · บันทึกซ้ำ) — ตรงกับ severity High ของรุ่นเดิม</summary>
    public const decimal ZLikelyError = 5m;

    /// <summary>อ่านข้อเท็จจริงจาก payload ของ prompt จริง · JSON เสีย = <c>null</c> · ช่องที่ไม่มี = ว่าง/null (ไม่แต่งค่า)</summary>
    public static AnomalyPromptFacts? ReadPrompt(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            decimal amount = 0m;
            decimal? madZ = null, tMin = null, tMax = null;
            if (root.TryGetProperty("anomaly", out var an) && an.ValueKind == JsonValueKind.Object)
            {
                amount = Num(an, "amount") ?? 0m;
                madZ = Num(an, "mad_z_score");
                if (an.TryGetProperty("typical_range", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    tMin = Num(tr, "min");
                    tMax = Num(tr, "max");
                }
            }

            var series = new List<decimal>();
            int? hCount = null;
            decimal? hMin = null, hMax = null, hMed = null;
            if (root.TryGetProperty("vendor_history_12mo", out var vh) && vh.ValueKind == JsonValueKind.Object)
            {
                if (vh.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array)
                    AddPositive(series, h);
                var c = Num(vh, "count");
                hCount = c.HasValue ? (int)c.Value : null;
                hMin = Num(vh, "min");
                hMax = Num(vh, "max");
                hMed = Num(vh, "median");
            }
            if (series.Count < 3 && root.TryGetProperty("recent_12mo", out var rec) && rec.ValueKind == JsonValueKind.Array)
            {
                series.Clear();
                AddPositive(series, rec);
            }

            string? pick = null;
            if (root.TryGetProperty("local_model", out var lm) && lm.ValueKind == JsonValueKind.Object
                && lm.TryGetProperty("pick", out var p) && p.ValueKind == JsonValueKind.String)
                pick = p.GetString();

            return new AnomalyPromptFacts(amount, series, madZ, tMin, tMax, hCount, hMin, hMax, hMed, pick);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>ตัดสินคำตอบจากข้อเท็จจริง — <paramref name="seriesZ"/> = z ที่ผู้เรียกคำนวณจาก <c>Series</c> (null = คำนวณไม่ได้)</summary>
    public static AnomalyStudentAnswer Decide(AnomalyPromptFacts f, decimal? seriesZ)
    {
        var amt = f.Amount.ToString("N2", CultureInfo.InvariantCulture);

        // (1)(2) z-score — หลักฐานเชิงสถิติจากประวัติจริง
        var z = seriesZ ?? f.MadZ;
        if (z is { } zv)
        {
            var az = Math.Abs(zv);
            var dist = Math.Abs(az - ZThreshold);
            var conf = Math.Min(0.99m, 0.70m + 0.05m * dist);
            var zTxt = zv.ToString("0.0", CultureInfo.InvariantCulture);
            var basis = seriesZ.HasValue ? "mad_z_series" : "mad_z_detector";
            if (az > ZLikelyError)
                return new AnomalyStudentAnswer("LikelyError", conf,
                    $"ยอด {amt} ห่างจากประวัติ 12 เดือนมาก (MAD z = {zTxt}) — มักเกิดจากพิมพ์/อ่านจุดทศนิยมผิด หรือบันทึกซ้ำ",
                    Actions(zv, f.Amount), ErrorRisks, basis);
            if (az > ZThreshold)
                return new AnomalyStudentAnswer("NeedReview", conf,
                    $"ยอด {amt} {(zv > 0 ? "สูง" : "ต่ำ")}กว่าปกติของประวัติ (MAD z = {zTxt}) แต่ยังไม่ห่างพอจะสรุปว่าผิด — ควรตรวจเอกสารประกอบก่อนอนุมัติ",
                    Actions(zv, f.Amount), ReviewRisks, basis);
            return new AnomalyStudentAnswer("LikelyLegit", conf,
                $"ยอด {amt} อยู่ในช่วงปกติของประวัติ (MAD z = {zTxt}) — น่าจะเป็นรายการปกติ",
                Array.Empty<string>(), Array.Empty<string>(), basis);
        }

        // (3) สรุปประวัติ/ช่วงปกติ — ไม่มีชุดยอดให้คำนวณ z
        var lo = f.HistoryCount >= 3 ? f.HistoryMin : f.TypicalMin;
        var hi = f.HistoryCount >= 3 ? f.HistoryMax : f.TypicalMax;
        if (f.Amount > 0 && lo is > 0m && hi is > 0m && lo <= hi)
        {
            if (f.Amount >= lo && f.Amount <= hi)
                return new AnomalyStudentAnswer("LikelyLegit", 0.75m,
                    $"ยอด {amt} อยู่ระหว่างยอดต่ำสุด–สูงสุดของประวัติ — น่าจะเป็นรายการปกติ",
                    Array.Empty<string>(), Array.Empty<string>(), "history_range");
            var farOut = f.Amount >= hi.Value * 10m || f.Amount * 10m <= lo.Value;
            return farOut
                ? new AnomalyStudentAnswer("LikelyError", 0.75m,
                    $"ยอด {amt} ต่างจากช่วงประวัติเกิน 10 เท่า — ลักษณะของจุดทศนิยมผิดหรือพิมพ์เลขเกิน",
                    Actions(f.Amount > hi.Value ? 1m : -1m, f.Amount), ErrorRisks, "history_range")
                : new AnomalyStudentAnswer("NeedReview", 0.65m,
                    $"ยอด {amt} อยู่นอกช่วงต่ำสุด–สูงสุดของประวัติ — ควรตรวจเอกสารประกอบก่อนอนุมัติ",
                    Actions(f.Amount > hi.Value ? 1m : -1m, f.Amount), ReviewRisks, "history_range");
        }

        // (4) กติการะดับความรุนแรงของผู้เรียก
        var pick = AnomalyExplainVerdict.Normalize(f.LocalPick);
        if (pick != null)
            return new AnomalyStudentAnswer(pick, 0.60m,
                pick == "LikelyError"
                    ? "ตัวตรวจจับจัดรายการนี้เป็นระดับร้ายแรง แต่ไม่มีประวัติพอจะเทียบ — ควรตรวจกับเอกสารต้นฉบับ"
                    : "ไม่มีประวัติพอจะเทียบยอด — ควรให้ผู้รับผิดชอบตรวจรายการนี้",
                pick == "LikelyLegit" ? Array.Empty<string>() : new[] { "เปิดเอกสารต้นฉบับเทียบกับรายการที่บันทึก" },
                pick == "LikelyError" ? ErrorRisks : pick == "NeedReview" ? ReviewRisks : Array.Empty<string>(),
                "severity_rule");

        // (5) ไม่รู้ ⇒ ให้คนตรวจ
        return new AnomalyStudentAnswer("NeedReview", 0.50m,
            "ข้อมูลไม่พอจะสรุปว่ารายการนี้ผิดหรือปกติ — ควรให้ผู้รับผิดชอบตรวจ",
            new[] { "เปิดเอกสารต้นฉบับเทียบกับรายการที่บันทึก" }, ReviewRisks, "unknown");
    }

    /// <summary>อ่านคำอธิบายจาก JSON รูปคำตอบครู/นักเรียน (<c>reasoning · suggested_actions · risks</c>) — JSON เสีย/ว่าง = <c>null</c></summary>
    public static (string? Reasoning, IReadOnlyList<string> SuggestedActions, IReadOnlyList<string> Risks)? ReadStructured(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? reasoning = r.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
            return (reasoning, Strings(r, "suggested_actions"), Strings(r, "risks"));
        }
        catch (JsonException) { return null; }
    }

    private static readonly string[] ErrorRisks =
        { "ถ้าอนุมัติโดยไม่ตรวจ ยอดที่ผิดจะเข้าสมุดบัญชีและรายงานภาษีของเดือนนั้น" };
    private static readonly string[] ReviewRisks =
        { "ยอดที่ไม่ได้ตรวจอาจทำให้ต้นทุน/ภาษีซื้อของงวดคลาดเคลื่อน" };

    private static IReadOnlyList<string> Actions(decimal direction, decimal amount)
    {
        var a = new List<string>();
        if (direction > 0)
        {
            a.Add("ตรวจสอบใบกำกับภาษีว่ายอดตรงกับเอกสารกระดาษ");
            if (amount > 100_000m) a.Add("ยอดเกิน 100,000 — ตรวจสอบการอนุมัติระดับสูงขึ้น");
            a.Add("เช็คว่าเป็นค่าใช้จ่ายประจำหรือครั้งเดียว");
        }
        else
        {
            a.Add("ตรวจสอบว่าเป็นการชำระบางส่วนหรือไม่");
            a.Add("เช็คว่าจุดทศนิยมถูกต้อง (เช่น 1,234.50 กับ 12.3450)");
        }
        return a;
    }

    private static decimal? Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static void AddPositive(List<decimal> into, JsonElement arr)
    {
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var v) && v > 0) into.Add(v);
    }

    private static IReadOnlyList<string> Strings(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())) list.Add(el.GetString()!);
        return list;
    }
}
