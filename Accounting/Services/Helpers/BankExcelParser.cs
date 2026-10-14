using System.Globalization;
using Accounting.Helpers;
using MiniExcelLibs;

namespace Accounting.Services.Helpers;

/// <summary>
/// Excel (.xlsx) bank-statement reader. Uses MiniExcel — a single
/// self-contained MIT-licensed library with ZERO transitive dependencies,
/// chosen so the production publish pipeline can't drop a satellite DLL.
///
/// Each row of the sheet is materialised as a List&lt;string&gt; in
/// alphabetical column order (A, B, C, …) and handed to
/// <see cref="BankCsvParser.ParseRows"/> which already knows how to find
/// the header row, classify date order across the whole file, and skip
/// summary / brought-forward rows.
///
/// Key correctness detail: real DateTime cells are rendered in Excel
/// display order (M/d/yyyy) — NOT ISO — so the downstream date parser
/// can interpret them consistently with text dates in the same file via
/// the DetectDateOrder pass. This is what keeps KBank-style statements
/// (whose underlying serial encodes day-of-month in the MONTH slot)
/// from being parsed three months off.
/// </summary>
public static class BankExcelParser
{
    /// <summary>ผลอ่าน workbook — ชีตที่ใช้ + ชีตทั้งหมด (หน้าจอ/ข้อความบอกผู้ใช้ว่าอ่านจากชีตไหน)</summary>
    public sealed record Result(List<BankCsvParser.Row> Rows, List<string> Skipped, string? SheetName, IReadOnlyList<string> SheetNames);

    /// <summary>
    /// อ่าน statement จาก .xlsx — <paramref name="sheetName"/> ระบุ = ชีตนั้น (ไม่มีในไฟล์ ⇒ ล้มดังพร้อมรายชื่อชีต · ตัวอ่านกลาง) ·
    /// ไม่ระบุ = <b>ชีตแรกที่หาหัวตาราง statement เจอ</b> (ไฟล์ธนาคารหลายแห่งมีชีตปก/สรุปนำหน้า — เดิมอ่านชีตแรกอย่างเดียวแล้วล้มว่า
    /// "ไม่พบหัวคอลัมน์") · รายชื่อชีตจาก <see cref="ImportTabularReader.ListSheets"/> (ตัวเดียวกับศูนย์นำเข้า)
    /// </summary>
    public static Result ParseWorkbook(byte[] fileBytes, string? sheetName = null)
    {
        if (fileBytes == null || fileBytes.Length == 0)
            throw new ArgumentException("ไฟล์ Excel ว่างเปล่า");

        var sheets = ImportTabularReader.ListSheets(fileBytes);
        if (!string.IsNullOrWhiteSpace(sheetName))
        {
            var chosen = sheets.FirstOrDefault(sh => string.Equals(sh, sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ImportFormatException("sheet-not-found",
                    $"ไม่พบชีต \"{sheetName}\" ในไฟล์ — ชีตที่มี: {string.Join(", ", sheets)} · เลือกชีตใหม่แล้วนำเข้าอีกครั้ง");
            var (rows, skipped) = BankCsvParser.ParseRows(ReadGrid(fileBytes, chosen), $"ชีต \"{chosen}\"");
            return new Result(rows, skipped, chosen, sheets);
        }

        // ไม่ระบุชีต: ชีตแรกที่มีหัวตาราง statement — ชีตที่ไม่มีหัวตารางข้ามไป · ไม่มีชีตไหนเลย ⇒ ข้อความของชีตแรก (เห็นสิ่งที่ระบบเห็น)
        ArgumentException? firstError = null;
        foreach (var sh in sheets.Count > 0 ? sheets : new List<string> { "" })
        {
            var grid = ReadGrid(fileBytes, sh.Length == 0 ? null : sh);
            if (grid.Count == 0) continue;
            if (BankCsvParser.DetectHeader(grid).headerIdx < 0)
            {
                try { BankCsvParser.ParseRows(grid, sheets.Count > 1 ? $"ชีต \"{sh}\"" : "ไฟล์ Excel"); }
                catch (ArgumentException ex) { firstError ??= ex; }
                continue;
            }
            var (rows, skipped) = BankCsvParser.ParseRows(grid, sheets.Count > 1 ? $"ชีต \"{sh}\"" : "ไฟล์ Excel");
            return new Result(rows, skipped, sh.Length == 0 ? null : sh, sheets);
        }
        throw firstError ?? new ArgumentException("ไฟล์ Excel ไม่มีข้อมูล");
    }

    /// <summary>ชีตเดียวเป็นตารางข้อความ (เรียงคอลัมน์ A, B, C … สี่เหลี่ยมผืนผ้า) — วันที่ในเซลล์วันที่แสดงแบบ M/d/yyyy (ดูคำอธิบายคลาส)</summary>
    private static List<List<string>> ReadGrid(byte[] fileBytes, string? sheetName)
    {
        List<IDictionary<string, object>> raw;
        try
        {
            using var stream = new MemoryStream(fileBytes);
            // MiniExcel returns one row per sheet line; useHeaderRow=false so
            // we get raw cells indexed by column letter ("A", "B", "C", …).
            // Each row is an ExpandoObject which implements
            // IDictionary<string, object> (non-nullable value) — match that
            // exact generic shape or Cast throws InvalidCastException.
            raw = MiniExcel.Query(stream, useHeaderRow: false, sheetName: sheetName)
                .Cast<IDictionary<string, object>>()
                .Take(ImportTabularReader.MaxRows + 1)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"อ่านไฟล์ Excel ไม่สำเร็จ — ตรวจสอบว่าเป็น .xlsx ที่ไม่ถูกตั้งรหัสผ่าน ({ex.Message})", ex);
        }
        if (raw.Count > ImportTabularReader.MaxRows)
            throw new ImportFormatException("too-many-rows",
                $"ไฟล์มีมากกว่า {ImportTabularReader.MaxRows:N0} แถว — แบ่งไฟล์ (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์");

        // Find the maximum column letter actually used across all rows so the
        // grid is rectangular for the downstream parser. MiniExcel keys are
        // always Excel column letters (A..Z, AA..AZ, …).
        int maxColIndex = 0;
        foreach (var row in raw)
        {
            foreach (var key in row.Keys)
            {
                var idx = ColumnLetterToIndex(key);
                if (idx > maxColIndex) maxColIndex = idx;
            }
        }
        int colCount = Math.Min(Math.Max(maxColIndex + 1, 1), ImportTabularReader.MaxColumns);

        var cells = new List<List<string>>(raw.Count);
        foreach (var row in raw)
        {
            var line = new List<string>(colCount);
            for (int c = 0; c < colCount; c++)
            {
                var key = IndexToColumnLetter(c);
                row.TryGetValue(key, out var v);
                line.Add(StringifyValue(v));
            }
            cells.Add(line);
        }
        return cells;
    }

    private static string StringifyValue(object? v)
    {
        if (v == null) return "";
        switch (v)
        {
            case DateTime dt:
                // Render in Excel display order (M/d/yyyy) so the downstream
                // DetectDateOrder pass (which also reads text dates) can
                // classify the file consistently — see class doc above.
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("M/d/yyyy", CultureInfo.InvariantCulture)
                    : dt.ToString("M/d/yyyy H:mm:ss", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString(@"hh\:mm\:ss");
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case bool b:
                return b ? "TRUE" : "FALSE";
            default:
                return v.ToString() ?? "";
        }
    }

    private static int ColumnLetterToIndex(string letters)
    {
        // "A" → 0, "B" → 1, …, "Z" → 25, "AA" → 26
        int n = 0;
        foreach (var ch in letters)
        {
            if (ch < 'A' || ch > 'Z') return n;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }

    private static string IndexToColumnLetter(int index)
    {
        // 0 → "A", 25 → "Z", 26 → "AA"
        var letters = new List<char>();
        index++;
        while (index > 0)
        {
            index--;
            letters.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return new string(letters.ToArray());
    }
}
