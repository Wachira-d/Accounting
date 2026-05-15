using ClosedXML.Excel;
using System.Globalization;

namespace Accounting.Services.Helpers;

/// <summary>
/// Excel (.xlsx) bank-statement reader. Picks the first worksheet that has
/// a meaningful number of rows (banks sometimes ship a cover sheet first),
/// flattens every cell to a string preserving its underlying value, then
/// hands the 2D grid to <see cref="BankCsvParser.ParseRows"/>.
///
/// Key correctness detail: real DateTime cells are rendered ISO
/// (yyyy-MM-dd) so the downstream date parser never has to guess DD/MM
/// vs MM/DD on them. Numeric cells are returned in invariant format so a
/// thousand separator from the user's locale doesn't poison the amount
/// parser. Text cells flow through unchanged and get the same date-order
/// detection treatment as a CSV file.
/// </summary>
public static class BankExcelParser
{
    public static (List<BankCsvParser.Row> rows, List<string> skipped) ParseStatement(byte[] fileBytes)
    {
        if (fileBytes == null || fileBytes.Length == 0)
            throw new ArgumentException("ไฟล์ Excel ว่างเปล่า");

        using var stream = new MemoryStream(fileBytes);
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"อ่านไฟล์ Excel ไม่สำเร็จ — ตรวจสอบว่าเป็น .xlsx ที่ไม่ถูกตั้งรหัสผ่าน ({ex.Message})", ex);
        }

        try
        {
            // Pick the first worksheet with at least 2 rows of data.
            IXLWorksheet? sheet = null;
            foreach (var ws in workbook.Worksheets)
            {
                IXLRange? r;
                try { r = ws.RangeUsed(); }
                catch { r = null; }
                if (r == null) continue;
                if (r.RowCount() >= 2) { sheet = ws; break; }
            }
            sheet ??= workbook.Worksheets.FirstOrDefault();
            if (sheet == null) throw new ArgumentException("ไฟล์ Excel ไม่มี worksheet");

            var used = sheet.RangeUsed() ?? throw new ArgumentException("ไฟล์ Excel ไม่มีข้อมูล");

            int firstRow = used.FirstRow().RowNumber();
            int lastRow = used.LastRow().RowNumber();
            int firstCol = used.FirstColumn().ColumnNumber();
            int lastCol = used.LastColumn().ColumnNumber();

            var cells = new List<List<string>>();
            for (int r = firstRow; r <= lastRow; r++)
            {
                var line = new List<string>(lastCol - firstCol + 1);
                for (int c = firstCol; c <= lastCol; c++)
                {
                    line.Add(StringifyCell(sheet.Cell(r, c)));
                }
                cells.Add(line);
            }

            return BankCsvParser.ParseRows(cells, "ไฟล์ Excel");
        }
        finally
        {
            workbook.Dispose();
        }
    }

    private static string StringifyCell(IXLCell cell)
    {
        if (cell == null) return "";
        try
        {
            if (cell.IsEmpty()) return "";
        }
        catch { return ""; }

        try
        {
            // Real DateTime cells → ISO so the date parser doesn't have to
            // guess DD/MM vs MM/DD. Time-of-day preserved when present.
            if (cell.DataType == XLDataType.DateTime)
            {
                var dt = cell.GetDateTime();
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            // Numbers → invariant string so amount parsing isn't fooled by the
            // user's locale thousand separator.
            if (cell.DataType == XLDataType.Number)
            {
                try { return cell.GetDouble().ToString("R", CultureInfo.InvariantCulture); }
                catch { return cell.GetString() ?? ""; }
            }
            // Boolean / TimeSpan / Text → use the formatted display string when
            // available, otherwise the raw value.
            try { return cell.GetFormattedString() ?? ""; }
            catch { return cell.GetString() ?? ""; }
        }
        catch
        {
            // Formula cells whose cached value isn't materialised (rare on bank
            // exports but possible with custom templates) — degrade to raw text.
            try { return cell.GetString() ?? ""; }
            catch { return ""; }
        }
    }
}
