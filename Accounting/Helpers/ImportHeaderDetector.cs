using System.Text.RegularExpressions;
using Accounting.Models.DTOs.Import;
using Accounting.Services.Settlement.Adapters;

namespace Accounting.Helpers;

/// <summary>ผลหาแถวหัวตาราง — <see cref="HeaderRowIndex"/> 0-based ในแถวของไฟล์ · <see cref="Headers"/> = ชื่อคอลัมน์ (หัวสองชั้นรวมเป็นชื่อเดียวแล้ว)</summary>
/// <param name="Confidence">0–1 · &lt; 0.5 = เดา (หน้าเว็บให้ผู้ใช้เลือกแถวหัวตารางเองได้ — chip แก้ได้)</param>
/// <param name="Reason">เหตุผลภาษาไทย (แสดงเป็นหมายเหตุของ batch)</param>
public sealed record HeaderDetection(int HeaderRowIndex, int HeaderRowSpan, IReadOnlyList<string> Headers, decimal Confidence, string Reason);

/// <summary>
/// **หาแถวหัวตาราง + ตัดสินแถวยอดรวม — ตัวเดียวของทุกเส้นนำเข้า** (รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ไฟล์จริงของไทยไม่ได้ขึ้นต้นด้วยหัวตารางเสมอ: มีแถวชื่อรายงาน/ชื่อบริษัท/ช่วงวันที่ด้านบน (Express · PEAK · statement ธนาคาร) ·
/// หัวสองชั้นจากเซลล์ผสาน ("ยอดเงิน" ครอบ "เดบิต | เครดิต") · แถว "รวม"/"ยอดยกไป" ปนท้ายหรือกลางตาราง ⇒ ตัวนี้เลือกแถวที่ชื่อคอลัมน์ตรงกับ
/// field ปลายทางมากที่สุด (ชื่อ field · ป้ายไทย · คำพ้อง) ใน 30 แถวแรก และลองรวมสองแถวเป็นหัวเดียว</para>
///
/// <para><see cref="IsSummaryRow"/> = <b>เจ้าของเดียวของ regex แถวยอดรวม</b> (ย้ายจาก <c>BankCsvParser</c> — ทีม C เปลี่ยนให้เรียกตัวนี้) ·
/// ตัดสินจาก "ป้ายยอดรวมทั้งเซลล์" ไม่ใช่คำที่ปนในข้อความ ⇒ "ขนมรวมมิตร" · "Total care shampoo" ไม่ถูกตัด · ผู้เรียก (pipeline) <b>ไม่ทิ้งเงียบ</b>:
/// แถวที่เข้าข่ายถูกพักเป็น "ตัดออก" พร้อมเหตุผล ผู้ใช้นำกลับได้ (คำตัดสินข้อ 150)</para>
/// <para>G6: pure · ไม่ throw · ไม่ขึ้นกับ culture</para>
/// </summary>
public static class ImportHeaderDetector
{
    /// <summary>ดูแถวต้นไฟล์ไม่เกินเท่านี้</summary>
    public const int MaxScanRows = 30;

    /// <summary>ป้ายแถวยอดรวม/ยกมา-ยกไป — ทั้งเซลล์ (ตามด้วยตัวเลข/เครื่องหมาย/คำประกอบสั้น ๆ ได้) · เรียงคำยาวก่อนคำสั้น</summary>
    private static readonly Regex SummaryLabel = new(
        @"^(?:ยอดรวมทั้งสิ้น|รวมทั้งสิ้น|ยอดรวม|รวมยอด|รวมเงิน|รวมรายการ|ยอดยกมา|ยอดยกไป|ยอดถอน|ยอดฝาก|รวม|grand\s*total|sub\s*-?\s*total|total|brought\s+forward|carried\s+forward|balance\s+[bc]/f)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>คำที่ตามหลังป้ายยอดรวมได้โดยยังเป็นแถวยอดรวม ("รวม 15 รายการ" · "รวมทั้งสิ้น (บาท)")</summary>
    private static readonly HashSet<string> SummaryTailWords = new(StringComparer.OrdinalIgnoreCase)
        { "", "รายการ", "บาท", "ทั้งสิ้น", "items", "item", "rows", "baht", "thb" };

    /// <summary>
    /// แถวนี้เป็นแถวยอดรวม/ยกมา/ยกไปของรายงาน (ไม่ใช่ข้อมูล) — มีเซลล์ที่<b>ทั้งเซลล์</b>เป็นป้ายยอดรวม (เช่น "รวม" · "รวมทั้งสิ้น" · "Grand Total:" ·
    /// "รวม 15 รายการ" · "ยอดยกมา") · คำที่ขึ้นต้นเหมือนแต่เป็นชื่อจริง ("รวมมิตร" · "Total care") ไม่ใช่ · แถวว่างทั้งแถว = ไม่ใช่ (ผู้เรียกข้ามเอง)
    /// </summary>
    public static bool IsSummaryRow(IReadOnlyList<string?> cells)
    {
        foreach (var c in cells)
        {
            var s = (c ?? "").Trim().Trim('"').Trim();
            if (s.Length == 0 || s.Length > 60) continue;
            var m = SummaryLabel.Match(s);
            if (!m.Success) continue;
            var rest = m.Groups["rest"].Value;
            // ป้ายต้องจบที่ขอบคำ: ตัวถัดไปเป็นตัวอักษร ⇒ เป็นคำอื่น ("รวมมิตร" · "Totally")
            if (rest.Length > 0 && char.IsLetter(rest[0])) continue;
            var tail = new string(rest.Where(ch => char.IsLetter(ch) || char.GetUnicodeCategory(ch)
                is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark).ToArray());
            if (SummaryTailWords.Contains(tail)) return true;
        }
        return false;
    }

    /// <summary>หัวคอลัมน์รูปเทียบ — ตัวพิมพ์เล็ก · ตัดช่องว่าง/ขีด/ขีดล่าง/จุด/วงเล็บ/ดอกจัน/โคลอน · เลขไทยเป็นอารบิก</summary>
    public static string NormalizeHeader(string? header)
    {
        var s = SettlementValueParser.ThaiDigitsToAscii(header ?? "").ToLowerInvariant();
        return new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch is not ('_' or '-' or '.' or '(' or ')' or '*' or ':' or '/' or '\\' or '"' or '\'')).ToArray());
    }

    /// <summary>
    /// ชื่อคอลัมน์จากแถวหัวตาราง <paramref name="index"/> กว้าง <paramref name="span"/> แถว — หัวสองชั้น: ช่องบนที่ว่าง (เซลล์ผสาน) ใช้ค่าซ้ายสุดที่มี
    /// แล้วต่อด้วยช่องล่าง ("ยอดเงิน" + "เดบิต" ⇒ "ยอดเงิน เดบิต") · ช่องว่างทั้งสองชั้น = "คอลัมน์ N"
    /// </summary>
    public static IReadOnlyList<string> MergeHeaders(IReadOnlyList<IReadOnlyList<string>> rows, int index, int span)
    {
        if (index < 0 || index >= rows.Count) return Array.Empty<string>();
        span = Math.Clamp(span, 1, Math.Min(2, rows.Count - index));
        var width = 0;
        for (var r = index; r < index + span; r++) width = Math.Max(width, rows[r].Count);
        var result = new List<string>(width);
        string carry = "";
        for (var j = 0; j < width; j++)
        {
            var top = Cell(rows[index], j);
            if (span == 1)
            {
                result.Add(top.Length > 0 ? top : $"คอลัมน์ {j + 1}");
                continue;
            }
            var bottom = Cell(rows[index + 1], j);
            if (top.Length > 0) carry = top;
            var upper = top.Length > 0 ? top : (bottom.Length > 0 ? carry : "");
            var name = string.Join(" ", new[] { upper, bottom }.Where(x => x.Length > 0).Distinct());
            result.Add(name.Length > 0 ? name : $"คอลัมน์ {j + 1}");
        }
        return result;
    }

    /// <summary>
    /// หาแถวหัวตารางใน <see cref="MaxScanRows"/> แถวแรก — คะแนน = จำนวนคอลัมน์ที่ชื่อตรง field ปลายทาง (ชื่อ field · ป้าย · คำพ้องใน
    /// <paramref name="synonyms"/>) · ลองรวมสองแถวเมื่อได้คะแนนมากกว่า · ไม่มีแถวไหนตรงเลย ⇒ แถวแรกที่เป็นข้อความหลายช่อง (ความมั่นใจต่ำ — ผู้ใช้แก้ได้)
    /// </summary>
    /// <param name="synonyms">field → คำพ้อง (ชื่อคอลัมน์ที่โปรแกรมอื่น/ผู้ใช้มักตั้ง) · null = ใช้แค่ชื่อ field + ป้าย</param>
    public static HeaderDetection Detect(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<ImportFieldDto> targets,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms = null)
    {
        var scan = rows.Take(MaxScanRows).ToList();
        if (scan.Count == 0) return new HeaderDetection(0, 1, Array.Empty<string>(), 0m, "ไฟล์ไม่มีแถวข้อมูล");

        var vocab = Vocabulary(targets, synonyms);
        var best = (Index: -1, Span: 1, Score: 0m);
        for (var i = 0; i < scan.Count; i++)
        {
            if (NonEmpty(scan[i]) < 2) continue;   // แถวชื่อรายงาน/ชื่อบริษัท (ช่องเดียว)
            var one = Score(MergeHeaders(scan, i, 1), vocab);
            if (one > best.Score) best = (i, 1, one);
            if (i + 1 < scan.Count && LooksLikeText(scan[i + 1]))
            {
                var two = Score(MergeHeaders(scan, i, 2), vocab);
                if (two > best.Score && two > one) best = (i, 2, two);
            }
        }

        if (best.Index >= 0)
        {
            var headers = MergeHeaders(scan, best.Index, best.Span);
            var denom = Math.Max(1, Math.Min(headers.Count(h => !h.StartsWith("คอลัมน์ ", StringComparison.Ordinal)), Math.Max(1, targets.Count)));
            var conf = Math.Round(Math.Clamp(best.Score / denom, 0m, 1m), 4, MidpointRounding.AwayFromZero);
            var skipped = best.Index > 0 ? $" · ข้าม {best.Index} แถวบนสุด (ชื่อรายงาน/ช่วงวันที่)" : "";
            var merged = best.Span == 2 ? " · หัวตารางสองชั้นรวมเป็นชื่อเดียว" : "";
            return new HeaderDetection(best.Index, best.Span, headers, conf,
                $"หัวตารางอยู่แถวที่ {best.Index + 1} (ชื่อคอลัมน์ตรงกับข้อมูลปลายทาง {best.Score:0.#} ช่อง){skipped}{merged}");
        }

        for (var i = 0; i < scan.Count; i++)
        {
            if (NonEmpty(scan[i]) >= 2 && LooksLikeText(scan[i]))
                return new HeaderDetection(i, 1, MergeHeaders(scan, i, 1), 0.3m,
                    $"ไม่พบชื่อคอลัมน์ที่ระบบรู้จัก — เดาว่าแถวที่ {i + 1} เป็นหัวตาราง (ตรวจและจับคู่คอลัมน์เอง หรือเลือกแถวหัวตารางใหม่)");
        }
        return new HeaderDetection(0, 1, MergeHeaders(scan, 0, 1), 0m,
            "ไม่พบแถวหัวตาราง — ใช้แถวแรกเป็นหัวตาราง (เลือกแถวหัวตารางใหม่ได้ถ้าไม่ถูก)");
    }

    // ───────────────────────── ภายใน ─────────────────────────

    private sealed record Term(string Field, string Text, decimal Weight);

    private static List<Term> Vocabulary(IReadOnlyList<ImportFieldDto> targets, IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms)
    {
        var terms = new List<Term>();
        foreach (var f in targets)
        {
            terms.Add(new Term(f.Field, NormalizeHeader(f.Field), 1m));
            terms.Add(new Term(f.Field, NormalizeHeader(f.Label), 1m));
            if (synonyms != null && synonyms.TryGetValue(f.Field, out var al))
                foreach (var a in al) terms.Add(new Term(f.Field, NormalizeHeader(a), 0.9m));
        }
        return terms.Where(t => t.Text.Length > 0).ToList();
    }

    /// <summary>คะแนนของชุดหัวตาราง — ตรงทั้งคำ = น้ำหนักเต็ม · มีคำ ≥ 3 ตัวอักษรอยู่ในชื่อ = 0.6 · หนึ่ง field นับครั้งเดียว</summary>
    private static decimal Score(IReadOnlyList<string> headers, List<Term> vocab)
    {
        var used = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            var n = NormalizeHeader(h);
            if (n.Length == 0) continue;
            string? bestField = null;
            var bestW = 0m;
            foreach (var t in vocab)
            {
                var w = n == t.Text ? t.Weight
                    : t.Text.Length >= 3 && n.Length >= 3 && (n.Contains(t.Text, StringComparison.Ordinal) || t.Text.Contains(n, StringComparison.Ordinal)) ? 0.6m
                    : 0m;
                if (w > bestW) { bestW = w; bestField = t.Field; }
            }
            if (bestField != null && (!used.TryGetValue(bestField, out var prev) || prev < bestW)) used[bestField] = bestW;
        }
        return used.Values.Sum();
    }

    private static string Cell(IReadOnlyList<string> row, int j) => j < row.Count ? (row[j] ?? "").Trim() : "";

    private static int NonEmpty(IReadOnlyList<string> row) => row.Count(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>แถวที่ส่วนใหญ่เป็นข้อความ (ไม่ใช่ตัวเลข/วันที่) — ลักษณะของหัวตาราง</summary>
    private static bool LooksLikeText(IReadOnlyList<string> row)
    {
        var cells = row.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (cells.Count == 0) return false;
        var text = cells.Count(c => !(ImportValueNormalizer.Number(c).Value.HasValue || ImportValueNormalizer.Amount(c).Value.HasValue
            || ImportValueNormalizer.Date(c).Value.HasValue));
        return text * 10 >= cells.Count * 6;
    }
}
