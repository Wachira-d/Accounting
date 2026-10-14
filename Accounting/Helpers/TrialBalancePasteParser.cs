using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>หนึ่งบรรทัดงบทดลองที่อ่านได้จากข้อความที่วาง (เดบิต/เครดิตเป็นยอดบวกเสมอ)</summary>
public sealed record TrialBalancePasteRow(int LineNo, string Code, string? Name, decimal Debit, decimal Credit);

/// <summary>บรรทัดที่ข้าม + เหตุผลภาษาไทย (ผู้ใช้เห็นทุกบรรทัด — ห้ามหายเงียบ)</summary>
public sealed record TrialBalancePasteSkip(int LineNo, string Text, string Reason);

public sealed record TrialBalancePasteResult(
    IReadOnlyList<TrialBalancePasteRow> Rows,
    IReadOnlyList<TrialBalancePasteSkip> Skipped,
    string Delimiter);

/// <summary>
/// **อ่านงบทดลองที่ผู้ใช้วางจาก Excel/โปรแกรมเดิม** (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §1 แถว 5) — แทน <c>split(/[\t|,]/)</c> +
/// <c>parseFloat</c> ในหน้าเว็บ ซึ่งตัด "1,234.50" เป็นสองช่องแล้วอ่านได้ 1 (ยอดเพี้ยนเงียบ) · server parse (F2 ข้อ 5)
///
/// <para>ตัวคั่น: แท็บ (วางจาก Excel) &gt; ขีดตั้ง | &gt; ตัวตัดสินกลาง <see cref="ImportTabularReader.DetectDelimiter"/> (, ;) · ค่าในเครื่องหมายคำพูดอ่านด้วย
/// <see cref="ImportTabularReader.ReadCsv"/> ตัวเดียวกับไฟล์ · ยอดเงินผ่าน <see cref="ImportValueNormalizer.Amount"/> ตัวเดียว (คั่นหลักพัน · วงเล็บ = ติดลบ ·
/// เลขไทย · ฿/บาท) · CSV ที่ไม่ใส่คำพูดแต่ยอดมีคั่นหลักพัน ("1110,เงินสด,1,234.50,0") ⇒ รวมกลุ่ม 3 หลักกลับเป็นยอดเดียวเฉพาะเมื่อคอลัมน์เกิน</para>
/// <para>รูปคอลัมน์: <c>รหัส · ชื่อ · เดบิต · เครดิต</c> หรือ <c>รหัส · ชื่อ · ยอด</c> (บวก = เดบิต · ลบ/วงเล็บ = เครดิต) · เดบิตติดลบ = ย้ายไปเครดิต ·
/// แถวหัวตาราง (ไม่มียอดที่อ่านได้) · แถวยอดรวม ("รวม"/"Total"/"ยอดรวม") · แถวว่าง ⇒ ข้ามพร้อมเหตุผล</para>
/// <para>G6: pure · ไม่ขึ้นกับ culture</para>
/// </summary>
public static class TrialBalancePasteParser
{
    private static readonly Regex ThreeDigitGroup = new(@"^\d{3}(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex LeadingDigits = new(@"^[\(\-]?\d{1,3}$", RegexOptions.Compiled);
    private static readonly string[] TotalWords = { "รวม", "ยอดรวม", "รวมทั้งสิ้น", "total", "grand total", "sum" };

    public static TrialBalancePasteResult Parse(string? text)
    {
        var rows = new List<TrialBalancePasteRow>();
        var skipped = new List<TrialBalancePasteSkip>();
        if (string.IsNullOrWhiteSpace(text)) return new(rows, skipped, "");

        var body = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string delimiter;
        List<IReadOnlyList<string>> table;
        if (body.Contains('\t'))
        {
            delimiter = "tab";
            table = ImportTabularReader.ReadCsv(body).ToList();
        }
        else if (body.Contains('|'))
        {
            delimiter = "|";
            table = body.Split('\n').Select(l => (IReadOnlyList<string>)l.Split('|').Select(c => c.Trim()).ToList()).ToList();
        }
        else
        {
            delimiter = ImportTabularReader.DetectDelimiter(body).ToString();
            table = ImportTabularReader.ReadCsv(body).ToList();
        }

        for (var i = 0; i < table.Count; i++)
        {
            var lineNo = i + 1;
            var cells = table[i].Select(c => c.Trim()).ToList();
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            var raw = string.Join(" | ", cells);
            if (cells.Count == 0 || cells.All(c => c.Length == 0)) continue;   // แถวว่าง — ไม่ต้องรายงาน

            if (cells.Count > 4 && delimiter == ",") cells = MergeThousandGroups(cells);

            var code = cells[0];
            var name = cells.Count > 1 ? cells[1] : null;
            if (IsTotalRow(code, name))
            {
                skipped.Add(new(lineNo, raw, "แถวยอดรวม — ระบบรวมยอดให้เอง"));
                continue;
            }
            if (code.Length == 0)
            {
                skipped.Add(new(lineNo, raw, "ไม่มีรหัสบัญชีในคอลัมน์แรก — เติมรหัส หรือลบแถวนี้"));
                continue;
            }
            if (cells.Count < 3)
            {
                skipped.Add(new(lineNo, raw, "ไม่มียอดเงิน (บัญชีหัวข้อหรือยอดศูนย์) — รูปคอลัมน์คือ รหัส · ชื่อ · ยอด หรือ รหัส · ชื่อ · เดบิต · เครดิต"));
                continue;
            }
            if (cells.Count > 4)
            {
                skipped.Add(new(lineNo, raw, $"มี {cells.Count} คอลัมน์ (เกิน 4) — ใส่ยอดในเครื่องหมายคำพูด เช่น \"1,234.50\" หรือคัดลอกจาก Excel (คั่นด้วยแท็บ)"));
                continue;
            }

            var a = ImportValueNormalizer.Amount(cells[2]);
            var b = cells.Count > 3 ? ImportValueNormalizer.Amount(cells[3]) : null;
            if (!a.Ok || (b != null && !b.Ok))
            {
                // แถวหัวตาราง ("รหัส · ชื่อบัญชี · เดบิต · เครดิต") = ไม่มียอดที่อ่านได้ทั้งแถว
                var isHeader = i == FirstNonEmptyIndex(table) && !a.Ok && (b == null || !b.Ok);
                skipped.Add(new(lineNo, raw, isHeader
                    ? "แถวหัวตาราง"
                    : $"อ่านยอดเงินไม่ได้ ({(a.Ok ? b!.Message : a.Message)})"));
                continue;
            }

            decimal debit, credit;
            if (b == null)
            {
                var v = a.Value ?? 0m;
                debit = v >= 0 ? v : 0m;
                credit = v < 0 ? -v : 0m;
            }
            else
            {
                debit = a.Value ?? 0m;
                credit = b.Value ?? 0m;
                // เดบิตติดลบ = เครดิต (โปรแกรมเดิมบางตัวพิมพ์ยอดคงเหลือติดลบในคอลัมน์เดบิต) และกลับกัน
                if (debit < 0) { credit += -debit; debit = 0m; }
                if (credit < 0) { debit += -credit; credit = 0m; }
            }
            rows.Add(new(lineNo, code, string.IsNullOrWhiteSpace(name) ? null : name, debit, credit));
        }
        return new(rows, skipped, delimiter);
    }

    private static int FirstNonEmptyIndex(List<IReadOnlyList<string>> table)
    {
        for (var i = 0; i < table.Count; i++)
            if (table[i].Any(c => c.Trim().Length > 0)) return i;
        return -1;
    }

    private static bool IsTotalRow(string code, string? name)
    {
        static bool Hit(string? s) => !string.IsNullOrWhiteSpace(s)
            && TotalWords.Any(w => string.Equals(s.Trim(), w, StringComparison.OrdinalIgnoreCase));
        // "รวมมิตร" ไม่ใช่แถวยอดรวม — เทียบทั้งคำเท่านั้น
        return (Hit(code) && (name == null || name.Length == 0 || Hit(name) || !char.IsAsciiDigit(code[0])))
            || (code.Length == 0 && Hit(name));
    }

    /// <summary>"1" "234.50" ⇒ "1,234.50" — เฉพาะกลุ่ม 3 หลักที่ตามหลังเลข 1–3 หลัก (คอลัมน์ยอด ไม่แตะรหัส/ชื่อ)</summary>
    private static List<string> MergeThousandGroups(List<string> cells)
    {
        var outCells = new List<string> { cells[0] };
        if (cells.Count > 1) outCells.Add(cells[1]);
        for (var k = 2; k < cells.Count; k++)
        {
            var c = cells[k];
            var prev = outCells.Count > 2 ? outCells[^1] : null;
            if (prev != null && ThreeDigitGroup.IsMatch(c)
                && (LeadingDigits.IsMatch(prev) || Regex.IsMatch(prev, @"^[\(\-]?\d{1,3}(,\d{3})+$")))
            {
                outCells[^1] = prev + "," + c;
                continue;
            }
            outCells.Add(c);
        }
        return outCells;
    }
}
