using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ทำไมคำตอบของ provider อ่านเป็นรูป canonical ไม่ได้ — ใช้เขียน <c>ErrorMessage</c> ที่ระบุสาเหตุจริง
/// (เดิมทุกกรณีรวมเป็น "Schema mismatch" ข้อความเดียว ⇒ หน้าแอดมินไล่สาเหตุไม่ได้เลย)</summary>
public enum AiParseProblem
{
    None = 0,
    /// <summary>เนื้อหาว่าง</summary>
    Empty,
    /// <summary>ไม่มี JSON object ให้อ่าน (ร้อยแก้ว/markdown ล้วน)</summary>
    NotJson,
    /// <summary>JSON ขาดกลางทาง — เกือบทุกครั้งคือ <c>max_tokens</c> หมด (finish_reason=length)</summary>
    Truncated,
    /// <summary>JSON ถูกรูปแต่ไม่มีช่องคำตอบ (primary/alternatives) ในชื่อที่รู้จัก</summary>
    NoPrimary,
    /// <summary>มีช่อง primary แต่เป็น null/ว่าง — โมเดล "ไม่ตอบ" อย่างตั้งใจ</summary>
    NullPrimary,
}

/// <summary>ผลอ่านคำตอบรูป canonical (primary/confidence/alternatives/…) ของทุก feature ที่ไม่ใช่ RawPlanResponse</summary>
public sealed record AiParsedFeatureResponse(
    string? PrimaryAnswer, decimal? Confidence,
    IReadOnlyList<string> Alternatives, IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags, string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    AiParseProblem Problem,
    // Salvaged = คำตอบกู้มาจาก JSON ที่ขาดกลางทาง (อ่านได้เฉพาะช่องที่ปิดครบ) — ห้ามเก็บลงแคช
    bool Salvaged,
    // TopLevelKeys = ชื่อ key ระดับบนสุดที่เห็น — ใส่ในข้อความ error ของ NoPrimary เพื่อไล่ว่าโมเดลตอบโครงอะไรมา
    IReadOnlyList<string> TopLevelKeys)
{
    /// <summary>มีคำตอบให้ใช้ (ด่านเดียวกับเดิมของ orchestrator: primary หรือ alternatives อย่างน้อยหนึ่ง)</summary>
    public bool HasAnswer => PrimaryAnswer != null || Alternatives.Count > 0;
}

/// <summary>
/// **ตัวอ่านคำตอบรูป canonical ตัวเดียวของ AI pipeline** (ทีม H · 2026-10-22) — ย้ายจาก
/// <c>AiOrchestrator.TryParseFeatureResponse</c> (private) มาเป็น pure helper ให้เทสต์ได้จาก raw content จริง
///
/// <para><b>ที่มา</b>: หน้าแอดมิน (20/10/2569) ขึ้น InvalidResponse "Schema mismatch: no primary answer or alternatives"
/// ที่ DocumentTypeClassification · WhtCategoryInference · ApprovalWarningFixSuggestion — prompt ทั้งสามขอ <c>"primary"</c>
/// ตรงกับตัวอ่านอยู่แล้ว สาเหตุร่วมคือ <b>เพดาน max_tokens ต่ำ</b> (200 · 280 · 400) กับคำอธิบายภาษาไทย ⇒ JSON ขาดกลางทาง ⇒
/// <c>JsonDocument.Parse</c> โยน ⇒ ตัวอ่านเดิมกลืนแล้วตอบ "ไม่มีคำตอบ" ซึ่งรายงานเป็น schema mismatch (ไม่บอกว่าขาด)
/// · feature ที่ไม่ตั้งเพดาน (GL · ผู้ขาย) ใช้ค่าตั้ง 1,024 จึงผ่าน — สอดคล้องกับ log</para>
///
/// <para><b>สิ่งที่รับ (ทุกข้อมีเทสต์จาก raw content จริงใน <c>AiFeatureResponseParserTests</c>)</b>:
/// markdown code fence · ร้อยแก้วนำ/ตาม JSON · ราก array หนึ่งตัว · ชื่อ key หลายแบบ (snake/camel/ตัวพิมพ์) ·
/// ห่อหนึ่งชั้น (<c>{"result":{…}}</c>) · primary เป็นตัวเลข (รหัสบัญชี) / object (<c>{"code":…}</c>) / array ·
/// confidence เป็นสตริง/เปอร์เซ็นต์มีเครื่องหมาย % · JSON ขาดกลางทาง ⇒ กู้เฉพาะช่องที่<b>ปิดครบแล้ว</b> (ไม่เดาส่วนที่ขาด)</para>
///
/// <para><b>สิ่งที่ไม่ทำ (หลักการ 3)</b>: ไม่แต่งคำตอบ — primary ที่เป็น null คือ "ไม่ตอบ" เว้นแต่ feature ประกาศความหมายไว้เอง
/// (<c>AiRequest.NullPrimaryAnswer</c> เช่น WHT: null = "None" ไม่ต้องหัก) · ด่านกันมั่วของผู้เรียก (∈ ผัง/candidate/enum) ยังทำงานเหมือนเดิม</para>
/// </summary>
public static class AiFeatureResponseParser
{
    /// <summary>เพดาน output ขั้นต่ำของคำขอรูป canonical — JSON 7 ช่อง + เหตุผลภาษาไทย 1–2 ประโยค ≈ 250–450 token
    /// (ไทย ≈ 1 token ต่อ 1–2 ตัวอักษร) · ต่ำกว่านี้ = JSON ขาดกลางทางเป็นประจำ (ต้นเหตุของ log 20/10/2569)</summary>
    public const int MinCanonicalOutputTokens = 600;

    /// <summary>เพดาน output ที่ส่งให้ provider จริง — ไม่ตั้ง = ใช้ค่าตั้งของ provider (null) · รูป canonical ไม่ต่ำกว่า
    /// <see cref="MinCanonicalOutputTokens"/> · RawPlanResponse (bulk) คงตามที่ผู้สร้าง prompt คำนวณมา</summary>
    public static int? EffectiveMaxTokens(int? requested, bool rawPlanResponse)
        => requested is int r && !rawPlanResponse ? Math.Max(r, MinCanonicalOutputTokens) : requested;

    private static readonly string[] PrimaryKeys =
    {
        "primary", "primaryanswer", "answer", "value", "result", "choice", "pick", "selected",
        "code", "category", "documenttype", "targetdocumenttype", "targetdoctype", "doctype",
        "incomecode", "whtcode", "accountcode", "contactid",
    };
    private static readonly string[] WrapperKeys =
        { "result", "response", "data", "output", "suggestion", "classification" };
    // ("primary"/"answer" ที่เป็น object ไม่ใช่ห่อ — อ่านค่าข้างในด้วย InnerValueKeys โดย confidence/เหตุผลยังอ่านจากราก)
    private static readonly string[] ConfidenceKeys = { "confidence", "conf", "score", "probability" };
    private static readonly string[] AlternativeKeys = { "alternatives", "candidates", "alternates", "otheroptions" };
    private static readonly string[] RiskKeys = { "risks", "warnings" };
    private static readonly string[] ComplianceKeys = { "complianceflags", "thaicomplianceflags", "compliance" };
    private static readonly string[] ReasoningKeys = { "reasoning", "rationale", "explanation", "reason" };
    private static readonly string[] ActionKeys = { "suggestedactions", "actions" };
    /// <summary>ค่าใน object ที่ใช้เป็นคำตอบเมื่อ primary ถูกส่งมาเป็น object (<c>{"primary":{"code":"54430","name":"…"}}</c>)</summary>
    private static readonly string[] InnerValueKeys = { "code", "value", "id", "answer", "type", "name" };

    public static AiParsedFeatureResponse Parse(string? content, string? nullPrimaryMeans = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return Fail(AiParseProblem.Empty);

        var text = StripFence(content.Trim());
        // ราก array ([{…}]) รับเฉพาะเมื่อขึ้นต้นด้วย '[' จริง — ไม่งั้น "[หมายเหตุ] {…}" จะถูกอ่านผิดก้อน
        var isArray = text.StartsWith('[');
        var start = isArray ? 0 : text.IndexOf('{');
        if (start < 0) return Fail(AiParseProblem.NotJson);
        var end = isArray ? text.LastIndexOf(']') : text.LastIndexOf('}');
        var candidate = end > start ? text[start..(end + 1)] : text[start..];

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(candidate); }
        catch (JsonException)
        {
            // ช่วงจาก '{' แรกถึง '}' สุดท้ายอาจตัดกลาง (ปีกกาปิดตัวสุดท้ายเป็นของ object ข้างใน) — ลองทั้งก้อนที่เหลือ
            if (LooksTruncated(text[start..])) return Salvage(text[start..]);
            return Fail(AiParseProblem.NotJson);
        }

        using (doc)
        {
            var root = doc!.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var first = root.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                if (first.ValueKind != JsonValueKind.Object) return Fail(AiParseProblem.NoPrimary);
                root = first;
            }
            if (root.ValueKind != JsonValueKind.Object) return Fail(AiParseProblem.NotJson);

            var topKeys = root.EnumerateObject().Select(p => p.Name).ToList();
            var body = FindBody(root);

            var (primary, primaryKeyPresent) = ReadPrimary(body);
            var problem = AiParseProblem.None;
            if (primary == null && primaryKeyPresent)
            {
                if (nullPrimaryMeans != null) primary = nullPrimaryMeans;
                else problem = AiParseProblem.NullPrimary;
            }

            var alternatives = ReadList(body, AlternativeKeys);
            if (primary == null && alternatives.Count == 0 && problem == AiParseProblem.None)
                problem = AiParseProblem.NoPrimary;

            return new AiParsedFeatureResponse(
                primary, ReadDecimal(body, ConfidenceKeys), alternatives,
                ReadList(body, RiskKeys), ReadList(body, ComplianceKeys),
                ReadString(body, ReasoningKeys), ReadList(body, ActionKeys),
                problem, Salvaged: false, topKeys);
        }
    }

    /// <summary>ข้อความ <c>ErrorMessage</c> ของแถว InvalidResponse — ระบุสาเหตุจริง + finish_reason/token ถ้ามี</summary>
    public static string Describe(AiParsedFeatureResponse parsed, string? finishReason, int? outputTokens, int? maxTokens)
    {
        var tokenInfo = outputTokens is int o
            ? $" · output {o}{(maxTokens is int m ? "/" + m : "")} tokens"
            : "";
        var fr = string.IsNullOrEmpty(finishReason) ? "" : $" · finish_reason={finishReason}";
        var truncatedByLimit = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        return parsed.Problem switch
        {
            AiParseProblem.Empty => "Empty response" + fr + tokenInfo,
            AiParseProblem.Truncated => "Truncated JSON: คำตอบขาดกลางทาง (max_tokens หมด?)" + fr + tokenInfo,
            AiParseProblem.NotJson when truncatedByLimit => "Truncated: คำตอบขาดกลางทาง" + fr + tokenInfo,
            AiParseProblem.NotJson => "Not JSON: ไม่มี JSON object ในคำตอบ" + fr + tokenInfo,
            AiParseProblem.NullPrimary => "Null primary: AI ตอบ primary = null (ไม่มีคำตอบ)" + fr + tokenInfo,
            _ => "Schema mismatch: no primary answer or alternatives (keys: "
                 + (parsed.TopLevelKeys.Count == 0 ? "-" : string.Join(", ", parsed.TopLevelKeys.Take(8))) + ")"
                 + fr + tokenInfo,
        };
    }

    // ─────────────────────────────────────────────────────────────────────

    private static AiParsedFeatureResponse Fail(AiParseProblem p) => new(
        null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null,
        Array.Empty<string>(), p, false, Array.Empty<string>());

    private static string Norm(string key) => key.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static bool TryGet(JsonElement obj, string normKey, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (Norm(p.Name) == normKey) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var nl = s.IndexOf('\n');
        s = nl > 0 ? s[(nl + 1)..] : s.TrimStart('`');
        var close = s.LastIndexOf("```", StringComparison.Ordinal);
        if (close >= 0) s = s[..close];
        return s.Trim();
    }

    /// <summary>object ที่ถือคำตอบจริง — รากเอง หรือห่อหนึ่งชั้น (<c>{"result":{"primary":…}}</c>)</summary>
    private static JsonElement FindBody(JsonElement root)
    {
        if (HasScalarPrimary(root)) return root;
        foreach (var w in WrapperKeys)
            if (TryGet(root, w, out var inner) && inner.ValueKind == JsonValueKind.Object && HasScalarPrimary(inner))
                return inner;
        var props = root.EnumerateObject().ToList();
        if (props.Count == 1 && props[0].Value.ValueKind == JsonValueKind.Object) return props[0].Value;
        return root;
    }

    private static bool HasScalarPrimary(JsonElement obj)
    {
        foreach (var k in PrimaryKeys)
            if (TryGet(obj, k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    or JsonValueKind.Null or JsonValueKind.Array)
                return true;
        return false;
    }

    /// <summary>(คำตอบ, มี key primary อยู่จริงไหม) — key ที่มีแต่ค่าว่าง/null = ไม่ตอบอย่างตั้งใจ</summary>
    private static (string? Value, bool KeyPresent) ReadPrimary(JsonElement body)
    {
        var present = false;
        foreach (var k in PrimaryKeys)
        {
            if (!TryGet(body, k, out var v)) continue;
            present = true;
            var s = Scalar(v);
            if (s == null && v.ValueKind == JsonValueKind.Object)
                foreach (var ik in InnerValueKeys)
                    if (TryGet(v, ik, out var iv) && (s = Scalar(iv)) != null) break;
            if (s == null && v.ValueKind == JsonValueKind.Array)
                s = v.EnumerateArray().Select(Scalar).FirstOrDefault(x => x != null);
            if (s != null) return (s, true);
        }
        return (null, present);
    }

    private static string? Scalar(JsonElement v)
    {
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return s;
    }

    private static string? ReadString(JsonElement body, string[] keys)
    {
        foreach (var k in keys)
            if (TryGet(body, k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static decimal? ReadDecimal(JsonElement body, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!TryGet(body, k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String) return ParseConfidenceText(v.GetString());
        }
        return null;
    }

    /// <summary>"0.8" → 0.8 · "85%" → 0.85 (มีเครื่องหมาย % = เปอร์เซ็นต์ชัดเจน) · "85" ไม่แปลง (ด่าน [0,1] ของผู้เรียกตัดสิน)</summary>
    private static decimal? ParseConfidenceText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        var pct = t.EndsWith('%');
        if (pct) t = t[..^1].Trim();
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return null;
        return pct ? Math.Round(d / 100m, 4, MidpointRounding.AwayFromZero) : d;
    }

    private static IReadOnlyList<string> ReadList(JsonElement body, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!TryGet(body, k, out var v) || v.ValueKind != JsonValueKind.Array) continue;
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) { var s = item.GetString(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s); }
                else if (item.ValueKind == JsonValueKind.Number) list.Add(item.GetRawText());
                else if (item.ValueKind == JsonValueKind.Object) list.Add(item.GetRawText());   // structured alt — caller parses
            }
            return list;
        }
        return Array.Empty<string>();
    }

    // ── JSON ขาดกลางทาง ─────────────────────────────────────────────────

    /// <summary>เริ่มด้วย '{' แล้วปีกกา/วงเล็บยังไม่ปิดครบ (นับนอกสตริง) หรือจบกลางสตริง</summary>
    internal static bool LooksTruncated(string s)
    {
        if (s.Length == 0 || s[0] != '{') return false;
        int depth = 0; bool inStr = false, esc = false;
        foreach (var ch in s)
        {
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') inStr = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']') depth--;
        }
        return inStr || depth > 0;
    }

    private static readonly Regex PrimaryStringRe = new(
        "\"(?:primary|primary_answer|primaryAnswer|answer)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PrimaryNumberRe = new(
        "\"(?:primary|primary_answer|primaryAnswer|answer)\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)\\s*[,}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConfidenceRe = new(
        "\"confidence\"\\s*:\\s*\"?(\\d+(?:\\.\\d+)?%?)\"?\\s*[,}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AlternativesRe = new(
        "\"alternatives\"\\s*:\\s*\\[([^\\]]*)\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedRe = new("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

    /// <summary>กู้เฉพาะช่องที่ <b>ปิดครบแล้ว</b> (สตริงมีเครื่องหมายคำพูดปิด · ตัวเลขตามด้วย , หรือ }) — ส่วนที่ขาดไม่เดา</summary>
    private static AiParsedFeatureResponse Salvage(string s)
    {
        string? primary = null;
        var m = PrimaryStringRe.Match(s);
        if (m.Success) primary = Unescape(m.Groups[1].Value).Trim();
        else if (PrimaryNumberRe.Match(s) is { Success: true } mn) primary = mn.Groups[1].Value;
        if (string.IsNullOrEmpty(primary) || primary.Equals("null", StringComparison.OrdinalIgnoreCase))
            primary = null;
        // primary = null ในก้อนที่ขาด **ไม่**แปลงเป็นความหมายของ feature (เช่น WHT "None") — ไม่รู้ว่าตั้งใจหรือถูกตัด (หลักการ 3)

        decimal? conf = null;
        if (ConfidenceRe.Match(s) is { Success: true } mc) conf = ParseConfidenceText(mc.Groups[1].Value);

        var alts = new List<string>();
        if (AlternativesRe.Match(s) is { Success: true } ma)
            foreach (Match q in QuotedRe.Matches(ma.Groups[1].Value))
            {
                var v = Unescape(q.Groups[1].Value).Trim();
                if (v.Length > 0) alts.Add(v);
            }

        return new AiParsedFeatureResponse(primary, conf, alts, Array.Empty<string>(), Array.Empty<string>(),
            null, Array.Empty<string>(), AiParseProblem.Truncated, Salvaged: primary != null || alts.Count > 0,
            Array.Empty<string>());
    }

    private static string Unescape(string jsonStringBody)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + jsonStringBody + "\"") ?? jsonStringBody; }
        catch (JsonException) { return jsonStringBody; }
    }
}
