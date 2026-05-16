using System.Globalization;
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
    public static (List<BankCsvParser.Row> rows, List<string> skipped) ParseStatement(byte[] fileBytes)
    {
        if (fileBytes == null || fileBytes.Length == 0)
            throw new ArgumentException("ไฟล์ Excel ว่างเปล่า");

        List<IDictionary<string, object?>> raw;
        try
        {
            using var stream = new MemoryStream(fileBytes);
            // MiniExcel returns one row per sheet line; useHeaderRow=false so
            // we get raw cells indexed by column letter ("A", "B", "C", …).
            // Cast each to IDictionary so we can iterate by key.
            raw = MiniExcel.Query(stream, useHeaderRow: false)
                .Cast<IDictionary<string, object?>>()
                .ToList();
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"อ่านไฟล์ Excel ไม่สำเร็จ — ตรวจสอบว่าเป็น .xlsx ที่ไม่ถูกตั้งรหัสผ่าน ({ex.Message})", ex);
        }

        if (raw.Count == 0)
            throw new ArgumentException("ไฟล์ Excel ไม่มีข้อมูล");

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
        int colCount = Math.Max(maxColIndex + 1, 1);

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

        return BankCsvParser.ParseRows(cells, "ไฟล์ Excel");
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
