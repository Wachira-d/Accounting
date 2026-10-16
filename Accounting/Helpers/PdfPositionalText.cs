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
/// <param name="Font">ชื่อฟอนต์ (PdfPig <c>FontName</c>) — ทีม OCR 13: ตัดตัวหนาที่วาดทับเฉพาะฟอนต์เดียวกัน · แปลง PUA ไทยเฉพาะฟอนต์ที่วาดอักษรไทยบนหน้านี้
/// (ไอคอนฟอนต์ใช้ช่วง U+F700–F71A ซ้อนกับ PUA ไทย) · null = ไม่รู้ (ทุก glyph ที่ไม่รู้ฟอนต์ถือเป็นฟอนต์เดียวกัน)</param>
public readonly record struct PdfGlyph(string Text, double Left, double Right, double Baseline, double Size, bool Horizontal = true, string? Font = null);

/// <summary>ผลประกอบหนึ่งหน้า + เหตุผลเมื่อประกอบไม่ได้ (ทีม OCR 13 · F2 #7 — ทางถอยไป <c>page.Text</c> ต้องบอกเหตุ ไม่ใช่เงียบ)</summary>
/// <param name="Text">ข้อความตามพิกัด · null = ประกอบไม่ได้ ⇒ ผู้เรียกใช้ <c>page.Text</c></param>
/// <param name="FallbackReason">เหตุที่ประกอบไม่ได้ (ไม่มี glyph · หน้าหมุน) · null เมื่อประกอบได้</param>
/// <param name="PuaDropped">จำนวนอักขระ Private Use ที่ถูกตัดออกจากข้อความ (ไอคอนฟอนต์ · ฟอนต์สัญลักษณ์ไม่มี ToUnicode · PUA ของฟอนต์ที่ไม่มีอักษรไทย) — บอกใน trace</param>
public readonly record struct PdfPageLayout(string? Text, string? FallbackReason, int PuaDropped);

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
/// <item>แปลงอักขระเฉพาะฟอนต์เป็น Unicode ปกติ (<c>NormalizeGlyphText</c> — PUA ไทยผ่าน <see cref="ThaiTextNormalizer.MapThaiPua"/> ตัวเดียว
/// <b>เฉพาะฟอนต์ที่วาดอักษรไทยบนหน้านี้</b> · PUA อื่น/ฟอนต์ที่ไม่มีอักษรไทย (ไอคอนฟอนต์ FontAwesome ใช้ U+F700–F71A ซ้อนกัน) ไม่ถูกแปลงเป็นไทย — ตัดออกและนับใน
/// <see cref="PdfPageLayout.PuaDropped"/>)</item>
/// <item>ตัด glyph ซ้ำที่วาดทับตำแหน่งเดิม (ตัวหนาแบบวาดสองรอบ — กฟภ. วาด "4,272.00" / "19,381.50" ซ้ำ) — อักขระเดียวกัน ฟอนต์เดียวกัน ห่างไม่เกิน
/// <see cref="DuplicateTolerance"/> × ขนาด และไม่เกินครึ่งความกว้างของตัวเอง (ทีม OCR 13: ตัวเลขแคบมาก "2,000.00" ไม่ถูกกินเป็น "2,00.0")</item>
/// <item>เลขในช่องตีกรอบ (ทีม OCR 13 · 50 ทวิ / แบบฟอร์มกรมสรรพากร "0 - 1 0 7 5 - …") — ตัวเลขทีละตัว + ขีด ระยะห่างสม่ำเสมอ ครบ 13 หลัก ⇒ ต่อกลับเป็นก้อนเดียว
/// "0-1075-38000-53-3" (<c>DigitBoxRuns</c>)</item>
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

    /// <summary>ตัวคั่นช่อง — <b>tab</b> (ทีม OCR 13 · ฝ่ายค้าน OCR 10 P1-4)
    /// <para>เดิมสองช่องว่าง ⇒ <see cref="ThaiTextNormalizer.Normalize"/> (ตัวที่ตัวอ่านหัวใบทุกตัวเรียกก่อน — กติกาของ Tesseract) ตัดช่องว่างระหว่างอักษรไทยสองตัวทิ้ง
    /// และยุบช่องว่างที่เหลือเป็นหนึ่ง ⇒ หัวใบหลายคอลัมน์ต่อกัน ("หจก.แอม แฮปปี้เนส  เลขที่/ No. …" ⇒ "หจก.แอมแฮปปี้เนสเลขที่/ No. …") ⇒ ชื่อ/ที่อยู่กลืนคอลัมน์ขวา
    /// (e-Tax TXE05202609T004679 · ใบ CRC ไทวัสดุ) · tab ไม่ถูก Normalize แตะ (กติกาทั้งสองข้อแตะเฉพาะ ' ') ⇒ ตัวคั่นคอลัมน์รอดถึงตัวอ่านทุกตัวโดยไม่ต้องแก้
    /// Normalize ที่ทุกเส้น OCR ใช้ · ตัวอ่านป้าย-ค่าใช้ <c>\s</c>/<c>[ \t]</c> อยู่แล้ว · ตัวอ่านชื่อ/ที่อยู่หยุดที่ tab (<see cref="ThaiCompanyName"/> · ตัวอ่านที่อยู่)
    /// · ตัวแบ่งช่องตาราง (<see cref="RawTextLineSplitter"/>) รับ tab อยู่แล้ว</para></summary>
    public const string ColumnSeparator = "\t";

    /// <summary>หน้าที่ glyph แนวนอนน้อยกว่าสัดส่วนนี้ = หน้าหมุน/วาดแปลก ⇒ คืน null ให้ผู้เรียกใช้ข้อความเดิม (ไม่เดาลำดับ)</summary>
    public const double MinHorizontalShare = 0.5;

    private readonly record struct Cluster(string Text, double Left, double Right, double Baseline, double Size, int Order);

    /// <summary>ประกอบข้อความหนึ่งหน้า — คืน null เมื่อไม่มี glyph ที่ใช้ได้หรือหน้าส่วนใหญ่ไม่ใช่แนวนอน (ผู้เรียกถอยไปใช้ <c>page.Text</c>)</summary>
    public static string? Build(IReadOnlyList<PdfGlyph>? glyphs) => Layout(glyphs).Text;

    /// <summary>ประกอบข้อความหนึ่งหน้าพร้อมเหตุผลเมื่อประกอบไม่ได้ + จำนวน PUA ที่ตัด (ผู้เรียกบันทึกลงเหตุผลของตัวสกัด ⇒ trace ของสแกน)</summary>
    public static PdfPageLayout Layout(IReadOnlyList<PdfGlyph>? glyphs)
    {
        if (glyphs == null || glyphs.Count == 0) return new(null, "ไม่มี glyph บนหน้า", 0);
        var usable = 0;
        var horizontal = 0;
        foreach (var g in glyphs)
        {
            if (!Usable(g)) continue;
            usable++;
            if (g.Horizontal) horizontal++;
        }
        if (usable == 0) return new(null, "ไม่มี glyph ที่มีพิกัด/ขนาดใช้ได้", 0);
        if (horizontal < usable * MinHorizontalShare)
            return new(null, string.Create(CultureInfo.InvariantCulture, $"หน้าหมุน/แนวตั้ง (glyph แนวนอน {horizontal}/{usable})"), 0);

        // ฟอนต์ที่วาดอักษรไทยบนหน้านี้ — PUA ไทย (U+F700–F71A) แปลงเฉพาะฟอนต์กลุ่มนี้ (ไอคอนฟอนต์ไม่มีอักษรไทย)
        var thaiFonts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in glyphs)
            if (Usable(g) && g.Text.Any(IsThai)) thaiFonts.Add(g.Font ?? "");

        // 1–3) แปลงอักขระ · ตัด glyph ที่วาดทับ · ติดวรรณยุกต์กับ glyph ก่อนหน้า (ลำดับ stream) · glyph ที่หมุนเก็บไว้ต่อท้ายตามลำดับ stream
        var clusters = new List<Cluster>(glyphs.Count);
        var rotated = new StringBuilder();
        var seen = new Dictionary<(string Text, string Font), List<(double X, double Y)>>();
        var puaDropped = 0;
        foreach (var g in glyphs)
        {
            if (!Usable(g)) continue;
            var mapThai = thaiFonts.Contains(g.Font ?? "");
            var text = NormalizeGlyphText(g.Text, mapThai);
            puaDropped += DroppedPua(g.Text, mapThai);
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
        if (clusters.Count == 0)
            return rotated.Length == 0 ? new(null, "ไม่มีอักขระที่เป็นข้อความ (ไอคอน/ช่องว่างล้วน)", puaDropped) : new(rotated.ToString(), null, puaDropped);

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
            var cells = line.OrderBy(c => c.Left).ThenBy(c => c.Order).ToList();
            var boxJoin = DigitBoxRuns(cells);
            for (var k = 0; k < cells.Count; k++)
            {
                var c = cells[k];
                if (lineSb.Length > 0 && !boxJoin[k])
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
        return sb.Length == 0 ? new(null, "ไม่มีอักขระที่เป็นข้อความ", puaDropped) : new(sb.ToString().TrimEnd('\n'), null, puaDropped);
    }

    /// <summary>จำนวนหลักของเลขในช่องตีกรอบที่ต่อกลับ — เลขผู้เสียภาษี/บัตรประชาชน 13 หลัก (แบบฟอร์ม 50 ทวิ / ภ.ง.ด. ตีกรอบทีละหลัก)</summary>
    public const int DigitBoxLength = 13;

    /// <summary>
    /// **เลขในช่องตีกรอบ** (ทีม OCR 13 · ฝ่ายค้าน OCR 10 P1-1) — หนังสือรับรอง 50 ทวิ ที่ระบบเราสร้าง (HeadlessChrome) และแบบฟอร์มกรมสรรพากรวาดเลข 13 หลัก
    /// ทีละหลักในช่อง ห่างกันเท่า ๆ กัน (วัดจริง 50 ทวิ ขนาด 9.8: ระยะหลักถึงหลัก 12.0 · ช่องว่างระหว่างหลัก 0.66 × ขนาด &gt; <see cref="WordGap"/>) ⇒
    /// ข้อความตามพิกัดได้ "0 - 1 0 7 5 - 3 8 0 0 0 - 5 3 - 3" ⇒ ตัวอ่านเลขผู้เสียภาษีไม่เห็นเลข (เดิม page.Text ได้ "0-1075-38000-53-3")
    ///
    /// <para>คืนธง "ต่อกับตัวก่อนหน้าโดยไม่เว้นวรรค" ต่อ cluster (เรียงซ้าย→ขวาแล้ว) — เฉพาะช่วงที่: ทุกตัวเป็นตัวเลขหนึ่งหลักหรือขีด · ไม่มีช่องห่างเกิน
    /// <see cref="ColumnGap"/> · ขึ้นต้นและจบด้วยตัวเลข · ตัวเลข<b>ครบ <see cref="DigitBoxLength"/> หลักพอดี</b> · ระยะหลักถึงหลัก (ไม่ข้ามขีด) สม่ำเสมอ
    /// (0.75–2 เท่าของค่ากลาง) · ไม่มีขีดติดกันสองตัว — ไม่ตรวจ checksum ที่นี่ (ตัวประกอบไม่ตัดสินค่า · ตัวอ่านเลขผู้เสียภาษีตรวจ mod-11 เอง)</para>
    /// </summary>
    private static bool[] DigitBoxRuns(IReadOnlyList<Cluster> cells)
    {
        var join = new bool[cells.Count];
        var i = 0;
        while (i < cells.Count)
        {
            if (!IsBoxChar(cells[i].Text)) { i++; continue; }
            var j = i;
            while (j + 1 < cells.Count && IsBoxChar(cells[j + 1].Text)
                   && cells[j + 1].Left - cells[j].Right <= ColumnGap * Math.Max(cells[j].Size, cells[j + 1].Size))
                j++;
            if (IsDigitBoxRun(cells, i, j))
                for (var k = i + 1; k <= j; k++) join[k] = true;
            i = j + 1;
        }
        return join;
    }

    private static bool IsBoxChar(string t) => t.Length == 1 && (char.IsAsciiDigit(t[0]) || t[0] == '-');

    private static bool IsDigitBoxRun(IReadOnlyList<Cluster> cells, int from, int to)
    {
        if (!char.IsAsciiDigit(cells[from].Text[0]) || !char.IsAsciiDigit(cells[to].Text[0])) return false;
        var digits = 0;
        var pitches = new List<double>();
        for (var k = from; k <= to; k++)
        {
            var isDigit = char.IsAsciiDigit(cells[k].Text[0]);
            if (isDigit) digits++;
            else if (cells[k - 1].Text[0] == '-') return false;   // ขีดติดกันสองตัว = ไม่ใช่เลขตีกรอบ (k > from เสมอ เพราะตัวแรกเป็นตัวเลข)
            if (isDigit && k > from && char.IsAsciiDigit(cells[k - 1].Text[0])) pitches.Add(cells[k].Left - cells[k - 1].Left);
        }
        if (digits != DigitBoxLength || pitches.Count == 0) return false;
        var sorted = pitches.OrderBy(p => p).ToList();
        var median = sorted[sorted.Count / 2];
        if (median <= 0) return false;
        return pitches.All(p => p >= 0.75 * median && p <= 2.0 * median);
    }

    private static bool IsThai(char ch) => ch >= '\u0E00' && ch <= '\u0E7F';

    /// <summary>จำนวนอักขระ PUA ของ glyph ที่จะถูกตัด (ไม่ได้แปลงเป็นไทย)</summary>
    private static int DroppedPua(string text, bool mapThaiPua)
        => text.Count(ch => IsPrivateUse(ch) && !(mapThaiPua && ch >= '\uF700' && ch <= '\uF71A'));

    /// <summary>แปลงข้อความ glyph เป็น Unicode ปกติ — PUA วรรณยุกต์/สระของฟอนต์ไทย (ตารางเดียว <see cref="ThaiTextNormalizer.MapThaiPua"/>) ·
    /// PUA อื่น (ไอคอนฟอนต์ เช่น โทรศัพท์/ลูกโลกของใบพรามี่) ไม่ใช่ข้อความ ⇒ ตัดทิ้ง</summary>
    internal static string NormalizeGlyphText(string? text) => NormalizeGlyphText(text, true);

    /// <param name="text">ข้อความของ glyph</param>
    /// <param name="mapThaiPua">ฟอนต์ของ glyph วาดอักษรไทยบนหน้านี้ (ทีม OCR 13) — false ⇒ PUA ทุกตัวถูกตัด ไม่ถูกแปลงเป็นไทย
    /// (FontAwesome 5 วาดไอคอนที่ U+F700 ⇒ เดิมกลายเป็น "ฐ" · U+F70A ⇒ ไม้เอกลอย)</param>
    internal static string NormalizeGlyphText(string? text, bool mapThaiPua)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var mapped = mapThaiPua ? ThaiTextNormalizer.MapThaiPua(text) : text;
        // ทีม OCR 13: อักขระควบคุม (U+0000 ฯลฯ — glyph ที่ฟอนต์ไม่มี ToUnicode · PDF จาก HeadlessChrome ให้วรรณยุกต์เป็น "\0") ไม่ใช่ข้อความ ·
        // เดิมค้างในข้อความ ⇒ "ใ\0บกำกับภาษี" (เรียงตาม x แล้วแทรกกลางคำ) ⇒ ชนิดเอกสารหลุด · JSON ของตัวแตกบรรทัดพัง
        if (mapped.Any(ch => IsPrivateUse(ch) || char.IsControl(ch)))
            mapped = new string(mapped.Where(ch => !IsPrivateUse(ch) && !char.IsControl(ch)).ToArray());
        return mapped;
    }

    /// <summary>นิคหิต+สระอา (U+0E4D U+0E32) ⇒ สระอำ (U+0E33) — ตาเห็นตัวเดียวกัน · NFC ไม่รวมให้</summary>
    /// <remarks>\u0E17\u0E35\u0E21 OCR 13: \u0E1F\u0E2D\u0E19\u0E15\u0E4C\u0E1A\u0E32\u0E07\u0E15\u0E31\u0E27\u0E40\u0E01\u0E47\u0E1A\u0E2A\u0E23\u0E30\u0E2D\u0E33\u0E41\u0E25\u0E49\u0E27\u0E15\u0E32\u0E21\u0E14\u0E49\u0E27\u0E22\u0E2A\u0E23\u0E30\u0E2D\u0E32\u0E0B\u0E49\u0E33 ("\u0E08\u0E33\u0E32\u0E01\u0E31\u0E14" \u00B7 "\u0E15\u0E33\u0E32\u0E1A\u0E25" \u2014 \u0E43\u0E1A BS2026100001) \u2014 \u0E20\u0E32\u0E29\u0E32\u0E44\u0E17\u0E22\u0E44\u0E21\u0E48\u0E21\u0E35 \u0E33\u0E32 \u21D2 \u0E40\u0E2B\u0E25\u0E37\u0E2D \u0E33 \u0E15\u0E31\u0E27\u0E40\u0E14\u0E35\u0E22\u0E27</remarks>
    private static string ComposeSaraAm(string text)
        => text.Replace("\u0E4D\u0E32", "\u0E33", StringComparison.Ordinal).Replace("\u0E33\u0E32", "\u0E33", StringComparison.Ordinal);

    private static bool IsPrivateUse(char ch) => ch >= '\uE000' && ch <= '\uF8FF';

    private static bool Usable(PdfGlyph g)
        => !string.IsNullOrEmpty(g.Text) && g.Size > 0 && double.IsFinite(g.Left) && double.IsFinite(g.Right) && double.IsFinite(g.Baseline);

    private static bool IsMark(string text)
    {
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) return false;
        return true;
    }

    /// <summary>glyph ซ้ำที่วาดทับ — อักขระเดียวกัน <b>ฟอนต์เดียวกัน</b> · ห่างไม่เกิน <see cref="DuplicateTolerance"/> × ขนาด และ (เมื่อ glyph มีความกว้าง)
    /// ไม่เกินครึ่งความกว้างของตัวเอง — ตัวเลขที่ advance แคบมาก (0.09 em) ตัวถัดไปอยู่ห่างเท่าความกว้าง ⇒ ไม่ใช่ตัวซ้ำ (ทีม OCR 13 · ฝ่ายค้าน OCR 10 P2:
    /// เดิม 0.1 × ขนาดล้วน ⇒ "2,000.00" กลายเป็น "2,00.0") · ตัวหนาที่วาดทับจริงอยู่ตำแหน่งเดียวกันเป๊ะ (กฟภ. ห่าง 0.00 · ฟอนต์เดียวกัน)</summary>
    private static bool IsDuplicate(Dictionary<(string Text, string Font), List<(double X, double Y)>> seen, string text, PdfGlyph g)
    {
        var tol = DuplicateTolerance * g.Size;
        var advance = g.Right - g.Left;
        if (advance > 0) tol = Math.Min(tol, 0.5 * advance);
        var key = (text, g.Font ?? "");
        if (seen.TryGetValue(key, out var list))
        {
            foreach (var (x, y) in list)
                if (Math.Abs(x - g.Left) <= tol && Math.Abs(y - g.Baseline) <= tol) return true;
            list.Add((g.Left, g.Baseline));
        }
        else seen[key] = new List<(double, double)> { (g.Left, g.Baseline) };
        return false;
    }
}
