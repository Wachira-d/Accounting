using ClosedXML.Excel;

namespace Accounting.Services.Helpers;

/// <summary>
/// Excel (.xlsx) bank-statement reader. Reads the first non-empty worksheet
/// (banks usually put the transaction list on sheet 1; if a cover sheet is
/// present we skip it), normalises every cell to a string preserving the
/// underlying value (numbers, dates, formulas), then hands the 2D grid to
/// <see cref="BankCsvParser.ParseRows"/> for header detection + row parsing.
///
/// Dates need special care: ClosedXML stores Excel dates as DateTime, but
/// some bank exports paste dates as text (e.g. "04/12/2568"). We render
/// genuine DateTime cells as ISO yyyy-MM-dd so the downstream date parser
/// always gets an unambiguous string, while text cells pass through verbatim
/// for DetectDateOrder to inspect.
/// </summary>
public static class BankExcelParser
{
    public static (List<BankCsvParser.Row> rows, List<string> skipped) ParseStatement(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        // Pick the first worksheet that has data in it. Some banks ship a
        // cover sheet with only the account header — we keep scanning until
        // we find one with at least a few populated rows.
        IXLWorksheet? sheet = null;
        foreach (var ws in workbook.Worksheets)
        {
            if (!ws.RangeUsed()?.RangeAddress?.IsValid ?? true) continue;
            var used = ws.RangeUsed();
            if (used == null) continue;
            if (used.RowCount() >= 2)
            {
                sheet = ws;
                break;
            }
        }
        sheet ??= workbook.Worksheets.FirstOrDefault()
            ?? throw new ArgumentException("ไฟล์ Excel ว่างเปล่า");

        var cells = new List<List<string>>();
        var usedRange = sheet.RangeUsed();
        if (usedRange == null)
            throw new ArgumentException("ไฟล์ Excel ไม่มีข้อมูล");

        int lastCol = usedRange.LastColumn().ColumnNumber();
        foreach (var row in usedRange.Rows())
        {
            var line = new List<string>(lastCol);
            for (int c = 1; c <= lastCol; c++)
            {
                var cell = row.Cell(c);
                line.Add(StringifyCell(cell));
            }
            cells.Add(line);
        }

        return BankCsvParser.ParseRows(cells, "ไฟล์ Excel");
    }

    private static string StringifyCell(IXLCell cell)
    {
        if (cell == null || cell.IsEmpty()) return "";
        try
        {
            // Real DateTime cells → ISO so the date parser doesn't have to
            // guess DD/MM vs MM/DD. Time-of-day is preserved.
            if (cell.DataType == XLDataType.DateTime)
            {
                var dt = cell.GetDateTime();
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd")
                    : dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            // Numbers — return invariant string so amount parsing isn't fooled
            // by a thousand separator the user's locale would render.
            if (cell.DataType == XLDataType.Number)
                return cell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Booleans / errors / blanks fall through to the raw value text.
            var v = cell.GetFormattedString();
            return v ?? "";
        }
        catch
        {
            // ClosedXML can throw on formula cells whose cached value isn't
            // available — fall back to the raw text to keep the import going.
            return cell.GetString() ?? "";
        }
    }
}
