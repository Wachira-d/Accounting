using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **รูปแบบ "เลขอ้างอิงการจอง" ที่ด่านมัดจำรู้จัก — ตัวตั้งตัวเดียว** (ทีม W10 2026-10-26 · R-56 · หลัก ก "ทำเป็นค่าตั้ง")
///
/// <para><b>ที่มา:</b> ด่าน <see cref="BookingDepositDrawdown"/> ผูก JE กับการจองด้วยรูปแบบตายตัว 2 รูป (<c>RES-…</c> · "การจอง #…") —
/// มัดจำที่ระบบต้นทางลงด้วยเลขอ้างอิงรูปแบบอื่น (เช่น <c>BK-77001</c>) ด่านมองไม่เห็นเครดิต ⇒ ปล่อยผ่าน (G3 ตั้งใจ) ⇒ ตัดมัดจำที่ยกเลิกแล้วได้โดยไม่มีใครรู้
/// ⇒ ให้เจ้าของ<b>ตั้งค่า</b>รูปแบบเพิ่มต่อคีย์ integration (<c>ExternalIntegration.BookingRefPatternsJson</c> = JSON array ของ regex) ·
/// <see cref="BookingDepositLedger"/> อ่านรวมทุกคีย์ของบริษัท (ตัวเดียว) · ค่าเริ่มต้น = 2 รูปเดิมเสมอ (ผลเทสต์เดิม 0 เปลี่ยน) · ไม่รู้จักยังปล่อยผ่าน + รายงานตรวจข้อมูล</para>
///
/// <para><b>กติกาของ regex ที่รับ:</b> ต้องมีกลุ่มชื่อ <c>key</c> (เลขการจองเต็มตัว — เทียบตรงตัวตามเดิม) · คอมไพล์ผ่าน · ไม่จับข้อความว่าง (regex ที่จับทุกอย่าง = ด่านเพี้ยนทั้งบริษัท) ·
/// ยาว ≤ <see cref="MaxLength"/> · ไม่เกิน <see cref="MaxPatterns"/> รูปต่อคีย์ · ใช้ match timeout กัน regex ระเบิด (ReDoS) — ผิดข้อใด ⇒ ปฏิเสธพร้อมข้อความไทย (ไม่บันทึก)</para>
/// </summary>
public static class BookingRefPattern
{
    /// <summary>ชื่อกลุ่มที่ต้องมีในทุก pattern — ค่าที่จับได้คือ "เลขการจอง" (เทียบตรงตัว)</summary>
    public const string KeyGroup = "key";
    public const int MaxPatterns = 20;
    public const int MaxLength = 200;
    /// <summary>รหัสกฎตอนปฏิเสธค่าตั้งที่ผิด (HTTP 400)</summary>
    public const string RuleCode = "INT-BOOKING-REF-PATTERN";
    /// <summary>เพดานเวลาต่อการ match — regex ที่ผู้ใช้ตั้งเองต้องไม่ทำให้ด่านค้าง</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>ส่วนท้าย "เลขเต็ม" ที่ทั้งสองรูปเริ่มต้นใช้ร่วม — 3 หลักขึ้นไป · ต่อด้วย -ตัวเลข ได้หลายช่วง (RES-101-2609-0001 ⇒ 101-2609-0001) · ไม่หยุดกลางเลข</summary>
    public const string KeyTail = @"(?<" + KeyGroup + @">\d{3,}(?:-\d+)*)(?![\d])";

    /// <summary>รูปเริ่มต้น 2 รูป (ตัวเดิมของ <see cref="BookingDepositDrawdown"/> ก่อน R-56 — ห้ามเปลี่ยนความหมาย: เทสต์เดิมต้องเท่าเดิมทุกตัว)</summary>
    public static readonly IReadOnlyList<string> DefaultSources = new[]
    {
        @"(?<![A-Za-z0-9])RES-?" + KeyTail,
        @"(?:การจอง|เลขจอง|booking|reservation)\s*(?:no\.?|เลขที่)?\s*#\s*" + KeyTail,
    };

    /// <summary>รูปเริ่มต้นที่คอมไพล์แล้ว — ใช้เมื่อไม่มีค่าตั้ง (เทสต์ pure ทุกตัว)</summary>
    public static readonly IReadOnlyList<Regex> Default = DefaultSources.Select(Compile).ToList();

    /// <summary>คอมไพล์ pattern หนึ่งรูป (ไม่ validate — ใช้ <see cref="Validate"/> ก่อนเมื่อมาจากผู้ใช้)</summary>
    public static Regex Compile(string source)
        => new(source, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>ตรวจ pattern หนึ่งรูปจากผู้ใช้ — null = ใช้ได้ · ไม่งั้นข้อความไทยบอกว่าผิดอะไร · pure</summary>
    public static string? Validate(string? source)
    {
        var s = (source ?? "").Trim();
        if (s.Length == 0) return "รูปแบบเลขอ้างอิงว่าง";
        if (s.Length > MaxLength) return $"รูปแบบเลขอ้างอิงยาวเกิน {MaxLength} ตัวอักษร: {Shorten(s)}";
        Regex rx;
        try { rx = Compile(s); }
        catch (ArgumentException ex) { return $"รูปแบบเลขอ้างอิง \"{Shorten(s)}\" ไม่ใช่ regex ที่ถูกต้อง: {ex.Message}"; }
        if (!rx.GetGroupNames().Contains(KeyGroup))
            return $"รูปแบบเลขอ้างอิง \"{Shorten(s)}\" ต้องมีกลุ่มชื่อ (?<{KeyGroup}>…) ครอบเลขการจอง เช่น BK-(?<{KeyGroup}>\\d{{4,}})";
        try
        {
            if (rx.IsMatch("")) return $"รูปแบบเลขอ้างอิง \"{Shorten(s)}\" จับข้อความว่างได้ — จะทำให้ทุกรายการถูกมองว่าอ้างการจอง";
            var probe = rx.Match("INV-2026-0001 ขายเงินสด 1,000.00 บาท");
            if (probe.Success && probe.Groups[KeyGroup].Value.Length == 0)
                return $"รูปแบบเลขอ้างอิง \"{Shorten(s)}\" จับได้แต่กลุ่ม {KeyGroup} ว่าง — เลขการจองต้องอยู่ในกลุ่มนี้";
        }
        catch (RegexMatchTimeoutException) { return $"รูปแบบเลขอ้างอิง \"{Shorten(s)}\" ใช้เวลาจับนานเกินไป (regex ซับซ้อนเกิน)"; }
        return null;
    }

    /// <summary>แปลง JSON array ของ pattern (ค่าที่เก็บในฐาน / ค่าที่ส่งมาจากฟอร์ม) → รายการสตริงที่ trim แล้ว · null/ว่าง ⇒ รายการว่าง ·
    /// JSON ผิดรูป ⇒ โยน <see cref="JsonException"/> (ผู้เรียกฝั่งบันทึกใช้ <see cref="ValidateAll(IReadOnlyList{string}?)"/> กับรายการตรง ๆ แทน)</summary>
    public static IReadOnlyList<string> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        var raw = JsonSerializer.Deserialize<List<string?>>(json) ?? new List<string?>();
        return raw.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>JSON ที่จะเก็บ — null เมื่อไม่มี pattern (คอลัมน์ว่าง = ใช้ค่าเริ่มต้นล้วน)</summary>
    public static string? ToJson(IReadOnlyList<string>? patterns)
    {
        var list = (patterns ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    /// <summary>ตรวจทั้งชุดก่อนบันทึก — null = ผ่าน · ไม่งั้นข้อความไทยของรูปแรกที่ผิด (ผู้ใช้แก้ทีละจุด) · ชุดว่าง = ผ่าน (กลับไปใช้ค่าเริ่มต้น)</summary>
    public static string? ValidateAll(IReadOnlyList<string>? patterns)
    {
        if (patterns == null || patterns.Count == 0) return null;
        var cleaned = patterns.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (cleaned.Count > MaxPatterns) return $"รูปแบบเลขอ้างอิงเกิน {MaxPatterns} รูปต่อคีย์ (ส่งมา {cleaned.Count})";
        foreach (var p in cleaned)
            if (Validate(p) is string err) return err;
        return null;
    }

    /// <summary>รวมค่าเริ่มต้น + ค่าตั้งของทุกคีย์ (JSON ต่อคีย์) เป็นชุดเดียวสำหรับด่าน · pattern ที่เก็บไว้แล้วแต่ผิด (แก้ฐานมือ) ⇒ ข้าม
    /// (ด่านแคบลงเท่าค่าเริ่มต้น = ปล่อยผ่านตาม G3 ไม่ใช่ล้ม) · ซ้ำกับค่าเริ่มต้น ⇒ ไม่เพิ่ม · pure</summary>
    public static IReadOnlyList<Regex> Merge(IEnumerable<string?> patternJsons)
    {
        var sources = new List<string>(DefaultSources);
        var result = new List<Regex>(Default);
        foreach (var json in patternJsons)
        {
            IReadOnlyList<string> parsed;
            try { parsed = ParseJson(json); }
            catch (JsonException) { continue; }
            foreach (var p in parsed)
            {
                if (sources.Contains(p, StringComparer.Ordinal) || Validate(p) != null) continue;
                sources.Add(p);
                result.Add(Compile(p));
            }
        }
        return result;
    }

    /// <summary>เลขการจองทุกตัวที่ข้อความอ้างตามชุด pattern (เลขเต็ม · ไม่ fuzzy · ลำดับตามที่พบ · ไม่ซ้ำ) · regex ที่ timeout ⇒ ข้ามรูปนั้น (ไม่ล้มด่าน) · pure</summary>
    public static IReadOnlyList<string> ExtractKeys(IReadOnlyList<Regex> patterns, params string?[] texts)
    {
        var keys = new List<string>();
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (var rx in patterns)
            {
                MatchCollection matches;
                try { matches = rx.Matches(t); }
                catch (RegexMatchTimeoutException) { continue; }
                foreach (Match m in matches)
                {
                    var k = m.Groups[KeyGroup].Value.Trim();
                    if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
                }
            }
        }
        return keys;
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
