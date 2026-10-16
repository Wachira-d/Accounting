using System.Globalization;
using System.Text;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Helpers;

/// <summary>อักขระหนึ่งตัวบนหน้า PDF พร้อมพิกัด (หน่วย point · แกน y ชี้ขึ้นแบบ PDF) — ข้อเท็จจริงที่ผู้เรียกถอดจาก PdfPig ส่งเข้ามา
/// (ตัวประกอบข้อความไม่รู้จัก PdfPig ⇒ เทสต์ด้วยพิกัดที่วัดจากกระดาษจริงได้)</summary>
/// <param name="Text">ข้อความของ glyph (มักหนึ่งอักขระ)</param>
/// <param name="Left">จุดเริ่มเส้นฐาน (StartBaseLine.X)</param>
/// <param name="Right">จุดจบเส้นฐาน (EndBaseLine.X) — วรรณยุกต์/สระบนไทยกว้าง 0 (Right = Left)</param>
/// <param name="Baseline">เส้นฐาน (StartBaseLine.Y)</param>
/// <param name="Size">ขนาดตัวอักษรที่วาดจริง (PointSize · ถ้าไม่มีใช้ FontSize)</param>
/// <param name="Horizontal">วางแนวนอนปกติ — glyph ที่หมุน (ตราประทับข้าง · ลายน้ำ) ไม่ถูกจัดแถวตามพิกัด (ต่อท้ายหน้าตามลำดับ stream)</param>
public readonly record struct PdfGlyph(string Text, double Left, double Right, double Baseline, double Size, bool Horizontal = true);

/// <summary>
/// **ประกอบข้อความหน้า PDF ตาม "ตำแหน่งบนกระดาษ" — ตัวตั้งตัวเดียวของเส้นข้อความ PDF** (ทีม OCR 10 · 2026-10-16)
///
/// <para>═══ ที่มา ═══ เดิม <c>PdfTextLayerExtractor</c> ใช้ PdfPig <c>page.Text</c> ซึ่งต่อ glyph ตาม<b>ลำดับใน content stream</b> ไม่ใช่ลำดับที่ตาเห็น ·
/// บิลค่าไฟจริง กฟภ. XH0712609004157: "หน่วยที่ใช้ 4272.0019,381.504,272.00…1,356.7120,738.21" (ช่องติดกัน ⇒ ยอดอ่านเป็น 689.92/48.29/738.21) ·
/// ป้ายกับค่าสลับคู่ ("เลขที่ (No.)516004632612" ทั้งที่บนกระดาษ เลขที่ (No.) = XH0712609004157) · สาขา "00000" ต่อท้ายรหัสไปรษณีย์ ("1090000000") ·
/// ใบ RE-20261000111 (พรามี่): แถวตาราง "1.00ชิ้น1,330.00293.007%1,037.00" ⇒ ตัวอ่านรายการไม่ได้บรรทัดเลย · วรรณยุกต์เป็นอักขระ Private Use</para>
///
/// <para>═══ วิธี (ทุกเกณฑ์เป็นสัดส่วนของขนาดตัวอักษร · ตั้งจากช่องว่างที่วัดบนกระดาษจริงสองใบ) ═══
/// <list type="number">
/// <item>แปลงอักขระเฉพาะฟอนต์เป็น Unicode ปกติ (<see cref="NormalizeGlyphText"/> — PUA ไทยผ่าน <see cref="ThaiTextNormalizer.MapThaiPua"/> ตัวเดียว · ไอคอนฟอนต์ทิ้ง)</item>
/// <item>ตัด glyph ซ้ำที่วาดทับตำแหน่งเดิม (ตัวหนาแบบวาดสองรอบ — กฟภ. วาด "4,272.00" / "19,381.50" ซ้ำ) — อักขระเดียวกัน ห่างไม่เกิน <see cref="DuplicateTolerance"/></item>
/// <item>วรรณยุกต์/สระบน-ล่าง (Unicode Mn) ติดกับ glyph ก่อนหน้าตามลำดับ stream — ไม่เรียงใหม่ตามพิกัด (กว้าง 0 · x ชนตัวถัดไป)</item>
/// <item>จัดแถวด้วยเส้นฐาน: ห่างจากเส้นฐานแรกของแถวไม่เกิน <see cref="LineTolerance"/> = แถวเดียวกัน · เรียงแถวบน→ล่าง · ในแถวเรียงซ้าย→ขวา</item>
/// <item>ช่องว่างระหว่าง glyph &gt; <see cref="WordGap"/> = เว้นวรรคหนึ่งตัว · &gt; <see cref="ColumnGap"/> = <see cref="ColumnSeparator"/> (ช่องตาราง/ป้าย-ค่า แยกกันเสมอ)</item>
/// <item>นิคหิต+สระอา ⇒ สระอำ (ตาเห็นตัวเดียวกัน · ฟอนต์บางตัวเก็บสองตัว — พรามี่ "จํากัด"/"สํานักงานใหญ่" ⇒ ตัวค้นคำหาไม่เจอ)</item>
/// </list></para>
///
/// <para><b>ไม่ใช่ตัวตัดสินค่า</b> — คืนข้อความเท่านั้น ตัวอ่านหัวใบ/ยอด/รายการเดิมทุกตัวอ่านต่อ · ไม่มี I/O · ไม่ throw ·
/// คืน null เมื่อประกอบไม่ได้ ⇒ ผู้เรียกใช้ <c>page.Text</c> เดิม (ไม่เดาลำดับ)</para>
/// </summary>
public static class PdfPositionalText
{
    /// <summary>glyph อักขระเดียวกันที่ห่างกันไม่เกินสัดส่วนนี้ของขนาดตัวอักษร (ทั้งแกน x และเส้นฐาน) = วาดทับ ⇒ เก็บตัวแรก</summary>
    public const double DuplicateTolerance = 0.1;

    /// <summary>เส้นฐานห่างไม่เกินสัดส่วนนี้ของขนาดตัวอักษร (ตัวที่เล็กกว่า) = แถวเดียวกัน · วัดจริง: กฟภ. แถวที่ใกล้สุดห่าง 0.45 (เลขที่ (No.) กับที่อยู่ผู้ขาย —
    /// คนละแถวบนกระดาษ) · พรามี่ ป้าย "ผู้ขาย :" กับกล่องเลขที่เอกสาร ห่าง 0.42 (คนละแถวบนกระดาษ) ⇒ 0.35 ต่ำกว่าทั้งสอง</summary>
    public const double LineTolerance = 0.35;

    /// <summary>ช่องว่างเกินสัดส่วนนี้ = เว้นวรรค · วัดจริง: ภายในคำ ≤ 0.06 · เว้นวรรคหนึ่งตัว 0.22 (กฟภ. 14pt) / 0.24 (พรามี่ 9pt) ⇒ 0.14</summary>
    public const double WordGap = 0.14;

    /// <summary>ช่องว่างเกินสัดส่วนนี้ = ขึ้นช่องใหม่ (ช่องตาราง · ป้ายกับค่า) · วัดจริง: ช่องว่างในข้อความที่พิมพ์ติดกันมากสุด 0.96 (กฟภ.) / 0.91 (พรามี่) ·
    /// ช่องตาราง/ป้าย-ค่าที่น้อยสุด 1.57 (กฟภ.) / 1.41 (พรามี่) ⇒ 1.2 อยู่กลางช่องว่างของทั้งสองใบ</summary>
    public const double ColumnGap = 1.2;

    /// <summary>ตัวคั่นช่อง — สองช่องว่าง (ตัวอ่านหลายตัวรับเฉพาะ ' ' ไม่รับ tab · <see cref="ThaiTextNormalizer.Normalize"/> ยุบเหลือหนึ่งช่อง
    /// ⇒ ตัวอ่านที่อ่านข้อความ normalize ยังเห็นช่องคั่น · ช่องคั่นระหว่างอักษรไทยสองตัวถูก Normalize ตัดทิ้ง เหมือนเว้นวรรคเดิม)</summary>
    public const string ColumnSeparator = "  ";

    /// <summary>หน้าที่ glyph แนวนอนน้อยกว่าสัดส่วนนี้ = หน้าหมุน/วาดแปลก ⇒ คืน null ให้ผู้เรียกใช้ข้อความเดิม (ไม่เดาลำดับ)</summary>
    public const double MinHorizontalShare = 0.5;

    private readonly record struct Cluster(string Text, double Left, double Right, double Baseline, double Size, int Order);

    /// <summary>ประกอบข้อความหนึ่งหน้า — คืน null เมื่อไม่มี glyph ที่ใช้ได้หรือหน้าส่วนใหญ่ไม่ใช่แนวนอน (ผู้เรียกถอยไปใช้ <c>page.Text</c>)</summary>
    public static string? Build(IReadOnlyList<PdfGlyph>? glyphs)
    {
        if (glyphs == null || glyphs.Count == 0) return null;
        var usable = 0;
        var horizontal = 0;
        foreach (var g in glyphs)
        {
            if (!Usable(g)) continue;
            usable++;
            if (g.Horizontal) horizontal++;
        }
        if (usable == 0 || horizontal < usable * MinHorizontalShare) return null;

        // 1–3) แปลงอักขระ · ตัด glyph ที่วาดทับ · ติดวรรณยุกต์กับ glyph ก่อนหน้า (ลำดับ stream) · glyph ที่หมุนเก็บไว้ต่อท้ายตามลำดับ stream
        var clusters = new List<Cluster>(glyphs.Count);
        var rotated = new StringBuilder();
        var seen = new Dictionary<string, List<(double X, double Y)>>(StringComparer.Ordinal);
        foreach (var g in glyphs)
        {
            if (!Usable(g)) continue;
            var text = NormalizeGlyphText(g.Text);
            if (text.Length == 0) continue;
            if (!g.Horizontal) { rotated.Append(text); continue; }
            if (string.IsNullOrWhiteSpace(text)) continue;   // ช่องว่างที่พิมพ์ = ช่องว่างระหว่างพิกัด (วัดจากพิกัด ไม่นับตัวอักษร)
            if (IsDuplicate(seen, text, g)) continue;
            if (IsMark(text) && clusters.Count > 0)
            {
                var prev = clusters[^1];
                if (Math.Abs(prev.Baseline - g.Baseline) <= LineTolerance * Math.Min(prev.Size, g.Size)
                    && g.Left >= prev.Left - g.Size && g.Left <= prev.Right + g.Size)
                {
                    clusters[^1] = prev with { Text = prev.Text + text };
                    continue;
                }
            }
            clusters.Add(new Cluster(text, g.Left, Math.Max(g.Left, g.Right), g.Baseline, g.Size, clusters.Count));
        }
        if (clusters.Count == 0) return rotated.Length == 0 ? null : rotated.ToString();

        // 4) จัดแถวด้วยเส้นฐาน (บน → ล่าง) · แถวใหม่เมื่อห่างจากเส้นฐานแรกของแถวเกินเกณฑ์
        var ordered = clusters.OrderByDescending(c => c.Baseline).ThenBy(c => c.Order).ToList();
        var lines = new List<List<Cluster>>();
        var anchor = 0.0;
        var minSize = 0.0;
        foreach (var c in ordered)
        {
            if (lines.Count > 0 && Math.Abs(anchor - c.Baseline) <= LineTolerance * Math.Min(minSize, c.Size))
            {
                lines[^1].Add(c);
                minSize = Math.Min(minSize, c.Size);
                continue;
            }
            lines.Add(new List<Cluster> { c });
            anchor = c.Baseline;
            minSize = c.Size;
        }

        // 5) ในแถวเรียงซ้าย → ขวา · ช่องว่างจากพิกัด
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var lineSb = new StringBuilder();
            var right = double.NegativeInfinity;
            var prevSize = 0.0;
            foreach (var c in line.OrderBy(c => c.Left).ThenBy(c => c.Order))
            {
                if (lineSb.Length > 0)
                {
                    var gap = c.Left - right;
                    var size = Math.Max(prevSize, c.Size);
                    if (gap > ColumnGap * size) lineSb.Append(ColumnSeparator);
                    else if (gap > WordGap * size) lineSb.Append(' ');
                }
                lineSb.Append(c.Text);
                right = Math.Max(right, c.Right);
                prevSize = c.Size;
            }
            var lineText = ComposeSaraAm(lineSb.ToString()).Trim();
            if (lineText.Length > 0) sb.Append(lineText).Append('\n');
        }
        if (rotated.Length > 0) sb.Append(ComposeSaraAm(rotated.ToString())).Append('\n');
        return sb.Length == 0 ? null : sb.ToString().TrimEnd('\n');
    }

    /// <summary>แปลงข้อความ glyph เป็น Unicode ปกติ — PUA วรรณยุกต์/สระของฟอนต์ไทย (ตารางเดียว <see cref="ThaiTextNormalizer.MapThaiPua"/>) ·
    /// PUA อื่น (ไอคอนฟอนต์ เช่น โทรศัพท์/ลูกโลกของใบพรามี่) ไม่ใช่ข้อความ ⇒ ตัดทิ้ง</summary>
    public static string NormalizeGlyphText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var mapped = ThaiTextNormalizer.MapThaiPua(text);
        if (mapped.Any(IsPrivateUse))
            mapped = new string(mapped.Where(ch => !IsPrivateUse(ch)).ToArray());
        return mapped;
    }

    /// <summary>นิคหิต+สระอา (U+0E4D U+0E32) ⇒ สระอำ (U+0E33) — ตาเห็นตัวเดียวกัน · NFC ไม่รวมให้</summary>
    public static string ComposeSaraAm(string text) => text.Replace("ํา", "ำ", StringComparison.Ordinal);

    private static bool IsPrivateUse(char ch) => ch >= '' && ch <= '';

    private static bool Usable(PdfGlyph g)
        => !string.IsNullOrEmpty(g.Text) && g.Size > 0 && double.IsFinite(g.Left) && double.IsFinite(g.Right) && double.IsFinite(g.Baseline);

    private static bool IsMark(string text)
    {
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) return false;
        return true;
    }

    private static bool IsDuplicate(Dictionary<string, List<(double X, double Y)>> seen, string text, PdfGlyph g)
    {
        var tol = DuplicateTolerance * g.Size;
        if (seen.TryGetValue(text, out var list))
        {
            foreach (var (x, y) in list)
                if (Math.Abs(x - g.Left) <= tol && Math.Abs(y - g.Baseline) <= tol) return true;
            list.Add((g.Left, g.Baseline));
        }
        else seen[text] = new List<(double, double)> { (g.Left, g.Baseline) };
        return false;
    }
}
