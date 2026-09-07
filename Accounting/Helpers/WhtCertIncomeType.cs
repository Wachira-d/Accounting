using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **อ่าน "ประเภทเงินได้ ม.40" จากหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ)** (pure, ไม่มี I/O)
///
/// ═══ ทำไมตัวเดิมคืน null แทบทุกใบ (ผลตรวจ 2026-09-06 · T1-08) ═══
/// ตัวเดิมสแกน<b>ข้อความทั้งใบ</b>แล้วยอมตอบเฉพาะเมื่อเจอ "กลุ่มเดียว" — แต่แบบ
/// 50 ทวิ เป็น<b>ฟอร์มพิมพ์สำเร็จที่พิมพ์ทุกประเภทเป็นรายการให้ติ๊ก</b>
/// (1. เงินเดือน 40(1) · 2. ค่าธรรมเนียม/ค่านายหน้า 40(2) · 3. ค่าแห่งลิขสิทธิ์ 40(3) ·
/// 4.(ก) ดอกเบี้ย · 5. ค่าเช่า · 6. วิชาชีพอิสระ · 7. ค่ารับเหมา · 8. อื่น ๆ)
/// ⇒ **ทุกกลุ่มเจอเสมอ** ⇒ <c>hits.Count</c> ไม่มีวันเป็น 1 ⇒ คืน <c>null</c> ทุกใบ
/// ⇒ <c>CheckRate</c> ที่คอมเมนต์บอกว่า "ป้อนให้ตรวจอัตราตาม ท.ป.4/2528" ไม่มีข้อมูล
/// ให้ตรวจเลยสักใบ (ญาติของ "เงื่อนไขที่เป็นจริงไม่ได้เลย")
///
/// <para>กติกาที่ถูก: บนฟอร์มติ๊ก สิ่งที่บอกประเภทคือ <b>แถวที่มีจำนวนเงิน</b>
/// ไม่ใช่คำที่ปรากฏ — ฟอร์มพิมพ์ยอดเฉพาะแถวที่ใช้จริง</para>
/// </summary>
public static class WhtCertIncomeType
{
    /// <summary>(รหัสที่คืน, คำบ่งชี้) — รหัสต้องอยู่ในรูปที่ <c>CheckRate</c> อ่านออก</summary>
    private static readonly (string Code, string[] Markers)[] Families =
    {
        ("40(1) เงินเดือน",      new[] { "40(1)", "เงินเดือน", "ค่าจ้าง" }),
        ("40(2) ค่านายหน้า",     new[] { "40(2)", "ค่านายหน้า", "ค่าธรรมเนียม", "คอมมิชชั่น", "คอมมิชชัน" }),
        ("40(3) ค่าสิทธิ",       new[] { "40(3)", "ค่าแห่งลิขสิทธิ์", "ค่าสิทธิ", "royalty" }),
        ("40(4)(ก) ดอกเบี้ย",    new[] { "40(4)(ก)", "ดอกเบี้ย" }),
        ("40(4)(ข) เงินปันผล",   new[] { "40(4)(ข)", "เงินปันผล", "dividend" }),
        ("40(5) ค่าเช่า",        new[] { "40(5)", "ค่าเช่า" }),
        ("40(6) วิชาชีพอิสระ",   new[] { "40(6)", "วิชาชีพอิสระ" }),
        ("40(7) ค่ารับเหมา",     new[] { "40(7)", "รับเหมา" }),
        ("40(8) ค่าโฆษณา",       new[] { "ค่าโฆษณา" }),
        ("40(8) ค่าขนส่ง",       new[] { "ค่าขนส่ง" }),
        ("40(8) ค่าบริการ",      new[] { "40(8)", "ค่าบริการ", "ค่าจ้างทำของ" }),
    };

    private static readonly Regex Money = new(
        @"(?<!\d)(?<amt>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2})(?!\d)", RegexOptions.Compiled);

    /// <summary>ค่าเผื่อเมื่อเทียบยอดบนแถวกับฐานเงินได้ของใบ (บาท)</summary>
    private const decimal AmountTolerance = 1m;

    /// <param name="rawText">ข้อความทั้งใบ</param>
    /// <param name="incomeAmount">ฐานเงินได้ของใบนี้ (ยอดก่อน VAT) — ใช้ชี้ขาดเมื่อ
    /// มีหลายแถวที่มีตัวเลข · ส่ง <c>null</c> ได้ถ้ายังไม่รู้</param>
    /// <returns>รหัสประเภทเงินได้ หรือ <c>null</c> เมื่อ<b>ตัดสินไม่ได้</b>
    /// (ห้ามเดา — ประเภทเงินได้เป็นตัวกำหนดอัตราและแบบยื่น)</returns>
    public static string? Infer(string? rawText, decimal? incomeAmount = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // ── ชั้นที่ 1: แถวที่ "มีจำนวนเงิน" คือแถวที่ถูกใช้จริง ──
        var withAmount = new List<(string Code, decimal Amount)>();
        foreach (var line in rawText.Split('\n'))
        {
            var compact = line.Replace(" ", "").ToLowerInvariant();
            if (compact.Length == 0) continue;
            var code = MatchFamily(compact);
            if (code == null) continue;
            var m = Money.Match(line);
            if (!m.Success) continue;
            if (!decimal.TryParse(m.Groups["amt"].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var amt) || amt <= 0m) continue;
            withAmount.Add((code, amt));
        }

        var distinct = withAmount.Select(x => x.Code).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1) return distinct[0];
        if (distinct.Count > 1 && incomeAmount is > 0m)
        {
            // หลายแถวมีตัวเลข → แถวที่ยอดตรงกับฐานเงินได้ของใบคือแถวจริง
            var exact = withAmount
                .Where(x => Math.Abs(x.Amount - incomeAmount.Value) <= AmountTolerance)
                .Select(x => x.Code).Distinct(StringComparer.Ordinal).ToList();
            if (exact.Count == 1) return exact[0];
        }

        // ── ชั้นที่ 2: ใบที่ไม่ใช่ฟอร์มติ๊ก (ใบกำกับ/ใบแจ้งหนี้ที่มีบรรทัดหัก) ──
        // ยอมตอบเมื่อ**ทั้งใบพูดถึงกลุ่มเดียว**เท่านั้น — พฤติกรรมเดิม
        var whole = rawText.Replace(" ", "").ToLowerInvariant();
        var hits = Families.Where(f => f.Markers.Any(mk => whole.Contains(mk, StringComparison.Ordinal)))
            .Select(f => f.Code).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>กลุ่มของบรรทัดนี้ — คืน <c>null</c> เมื่อบรรทัดพูดถึงหลายกลุ่ม
    /// (หัวตาราง/ข้อความอธิบาย) เพราะบรรทัดแบบนั้นชี้อะไรไม่ได้</summary>
    private static string? MatchFamily(string compactLowerLine)
    {
        string? found = null;
        foreach (var (code, markers) in Families)
        {
            if (!markers.Any(mk => compactLowerLine.Contains(mk, StringComparison.Ordinal))) continue;
            if (found != null && found != code) return null;   // บรรทัดเดียวชนหลายกลุ่ม
            found = code;
        }
        return found;
    }
}
