using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Services.Helpers;

/// <summary>
/// Robust Thai bank CSV statement parser.
/// Handles formats from BBL, KBank, SCB, KTB, BAY, TTB, etc.
/// Auto-detects delimiter, header row, column layout, date format, and amount format.
/// </summary>
public static class BankCsvParser
{
    /// <summary>Column index map for a Thai bank CSV statement.</summary>
    public record ColumnMap(
        int DateCol,
        int TimeCol,
        int DescriptionCol,
        int WithdrawalCol,
        int DepositCol,
        int BalanceCol,
        int AmountCol,
        int ChannelCol,
        int DetailsCol,
        int ReferenceCol);

    /// <summary>A parsed transaction row.</summary>
    public record Row(
        DateTime Date,
        string Description,
        decimal Withdrawal,
        decimal Deposit,
        decimal Balance,
        string? Reference,
        string? Channel);

    /// <summary>
    /// Parse the entire CSV. Returns the parsed rows and any skipped row diagnostics.
    /// </summary>
    public static (List<Row> rows, List<string> skipped) ParseStatement(byte[] fileBytes)
    {
        // Try UTF-8 first (with BOM detection); fallback to Windows-874 (TIS-620)
        string content = TryDecode(fileBytes);
        var rawRows = ParseCsvCells(content);
        return ParseRows(rawRows, "CSV");
    }

    /// <summary>
    /// Shared row-loop used by both the CSV and the Excel entry points. Takes a
    /// pre-parsed 2D cell grid, detects the header row + column layout, then
    /// extracts one Row per data line. Performs a two-pass date scan first so
    /// the day/month order is locked across the entire file — otherwise a
    /// statement that uses MM/DD/YYYY would silently flip to DD/MM/YYYY on
    /// dates where the day happens to be ≤ 12 (the bug that turned an April
    /// statement into a Sep/Oct/Nov/Dec spread).
    /// </summary>
    public static (List<Row> rows, List<string> skipped) ParseRows(List<List<string>> rawRows, string sourceLabel = "ไฟล์")
    {
        if (rawRows.Count < 2)
            throw new ArgumentException($"{sourceLabel}ต้องมีหัวตารางและข้อมูลอย่างน้อย 1 รายการ");

        var (headerIdx, map) = DetectHeader(rawRows);
        if (headerIdx < 0)
            throw new ArgumentException(
                $"ไม่พบหัวคอลัมน์ที่รองรับใน{sourceLabel} " +
                "(ต้องมีคอลัมน์ที่มีคำว่า: วันที่/Date, ฝาก/Deposit หรือ ถอน/Withdrawal, ยอดคงเหลือ/Balance)");

        // ── Pass 1: collect every date string in the file and detect the
        // canonical day/month order so all rows parse with the same orientation.
        var dateStrings = new List<string>();
        for (int i = headerIdx + 1; i < rawRows.Count; i++)
        {
            var cols = rawRows[i];
            if (map.DateCol >= 0 && map.DateCol < cols.Count)
            {
                var s = cols[map.DateCol]?.Trim().Trim('"') ?? "";
                if (!string.IsNullOrWhiteSpace(s)) dateStrings.Add(s);
            }
        }
        var dateOrder = DetectDateOrder(dateStrings);

        var rows = new List<Row>();
        var skipped = new List<string>();

        for (int i = headerIdx + 1; i < rawRows.Count; i++)
        {
            var cols = rawRows[i];
            if (cols.Count == 0 || cols.All(string.IsNullOrWhiteSpace)) continue;

            // Skip footer/summary rows
            var rawLine = string.Join("|", cols);
            if (Regex.IsMatch(rawLine,
                @"(รวมยอด|รวมเงิน|รวมรายการ|ยอดยกมา|ยอดยกไป|รวมทั้งสิ้น|ยอดถอน|ยอดฝาก|^total|^subtotal|grand total|brought forward|carried forward)",
                RegexOptions.IgnoreCase))
                continue;

            string Get(int idx) => idx >= 0 && idx < cols.Count ? cols[idx].Trim().Trim('"') : "";

            var dateStr = Get(map.DateCol);
            if (string.IsNullOrWhiteSpace(dateStr)) continue;

            if (!TryParseFlexibleDate(dateStr, dateOrder, out var date))
            {
                skipped.Add($"แถว {i + 1}: รูปแบบวันที่ไม่ถูกต้อง '{dateStr}'");
                continue;
            }

            // Optional time column
            if (map.TimeCol >= 0)
            {
                var timeStr = Get(map.TimeCol);
                if (TimeSpan.TryParse(timeStr, out var t))
                    date = date.Date + t;
            }

            var deposit = ParseAmount(Get(map.DepositCol));
            var withdrawal = ParseAmount(Get(map.WithdrawalCol));
            var balance = ParseAmount(Get(map.BalanceCol));

            // Single signed-amount column (some banks)
            if (deposit == 0 && withdrawal == 0 && map.AmountCol >= 0)
            {
                var amt = ParseAmount(Get(map.AmountCol));
                if (amt > 0) deposit = amt;
                else if (amt < 0) withdrawal = -amt;
            }

            if (deposit == 0 && withdrawal == 0)
            {
                skipped.Add($"แถว {i + 1}: ไม่มีจำนวนเงิน");
                continue;
            }

            var descParts = new List<string>();
            if (map.DescriptionCol >= 0) descParts.Add(Get(map.DescriptionCol));
            if (map.DetailsCol >= 0) descParts.Add(Get(map.DetailsCol));
            var description = string.Join(" | ", descParts.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(description)) description = "(ไม่มีรายละเอียด)";

            var reference = map.ReferenceCol >= 0 ? Get(map.ReferenceCol) : null;
            var channel = map.ChannelCol >= 0 ? Get(map.ChannelCol) : null;

            rows.Add(new Row(date, description, withdrawal, deposit, balance,
                string.IsNullOrWhiteSpace(reference) ? null : reference,
                string.IsNullOrWhiteSpace(channel) ? null : channel));
        }

        return (rows, skipped);
    }

    private static string TryDecode(byte[] bytes)
    {
        // Detect UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // Detect UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        // Detect UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // Default UTF-8 (lenient — invalid bytes become replacement chars)
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Parse CSV/TSV content into rows of cells.
    /// Auto-detects delimiter (comma/tab/semicolon) and properly handles quoted values.
    /// </summary>
    public static List<List<string>> ParseCsvCells(string content)
    {
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];

        var firstLine = content.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var delimiter = ',';
        if (firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ','))
            delimiter = '\t';
        else if (firstLine.Count(c => c == ';') > firstLine.Count(c => c == ','))
            delimiter = ';';

        var rows = new List<List<string>>();
        var current = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == delimiter) { current.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { /* ignore */ }
                else if (c == '\n')
                {
                    current.Add(cell.ToString());
                    cell.Clear();
                    if (current.Any(s => !string.IsNullOrWhiteSpace(s))) rows.Add(current);
                    current = new List<string>();
                }
                else cell.Append(c);
            }
        }

        if (cell.Length > 0 || current.Count > 0)
        {
            current.Add(cell.ToString());
            if (current.Any(s => !string.IsNullOrWhiteSpace(s))) rows.Add(current);
        }

        return rows;
    }

    public static (int headerIdx, ColumnMap map) DetectHeader(List<List<string>> rows)
    {
        var datePatterns = new[] { "วันที่", "date", "posting date" };
        var timePatterns = new[] { "เวลา", "time" };
        var depositPatterns = new[] { "ฝากเงิน", "ฝาก", "deposit", "credit", "เครดิต", "เงินเข้า", "money in" };
        var withdrawalPatterns = new[] { "ถอนเงิน", "ถอน", "withdrawal", "withdraw", "debit", "เดบิต", "เงินออก", "money out" };
        var balancePatterns = new[] { "ยอดคงเหลือ", "คงเหลือ", "balance" };
        var amountPatterns = new[] { "จำนวนเงิน", "amount", "value", "มูลค่า" };
        var descPatterns = new[] { "รายการ", "description", "transaction type" };
        var channelPatterns = new[] { "ช่องทาง", "channel", "service" };
        var detailsPatterns = new[] { "รายละเอียด", "details", "remark", "memo", "notes" };
        var refPatterns = new[] { "เลขอ้างอิง", "reference", "ref no", "ref id", "ref." };

        bool Matches(string cell, string[] patterns)
        {
            var c = (cell ?? "").Trim().ToLowerInvariant();
            return patterns.Any(p => c.Contains(p.ToLowerInvariant()));
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var cols = rows[r];
            int dateCol = -1, timeCol = -1, depCol = -1, wdCol = -1, balCol = -1, amtCol = -1;
            int descCol = -1, chCol = -1, detCol = -1, refCol = -1;

            for (int c = 0; c < cols.Count; c++)
            {
                var cell = cols[c] ?? "";
                if (dateCol < 0 && Matches(cell, datePatterns)) dateCol = c;
                else if (timeCol < 0 && Matches(cell, timePatterns)) timeCol = c;
                else if (wdCol < 0 && Matches(cell, withdrawalPatterns)) wdCol = c;
                else if (depCol < 0 && Matches(cell, depositPatterns)) depCol = c;
                else if (balCol < 0 && Matches(cell, balancePatterns)) balCol = c;
                else if (amtCol < 0 && Matches(cell, amountPatterns)) amtCol = c;
                else if (descCol < 0 && Matches(cell, descPatterns)) descCol = c;
                else if (chCol < 0 && Matches(cell, channelPatterns)) chCol = c;
                else if (refCol < 0 && Matches(cell, refPatterns)) refCol = c;
                else if (detCol < 0 && Matches(cell, detailsPatterns)) detCol = c;
            }

            var hasFlowCol = depCol >= 0 || wdCol >= 0 || amtCol >= 0;
            if (dateCol >= 0 && hasFlowCol && balCol >= 0)
            {
                return (r, new ColumnMap(
                    dateCol, timeCol, descCol, wdCol, depCol, balCol, amtCol, chCol, detCol, refCol));
            }
        }

        return (-1, new ColumnMap(-1, -1, -1, -1, -1, -1, -1, -1, -1, -1));
    }

    /// <summary>Day-first vs month-first orientation for slash/dash-separated dates.</summary>
    public enum DateOrder { Unknown, DayFirst, MonthFirst }

    /// <summary>
    /// Inspect every date string from the file to decide whether the format is
    /// DD/MM (Thai/European, the default) or MM/DD (US). The classification is:
    ///   - If any first component is > 12, the file is DD/MM (the first slot
    ///     can only be a day).
    ///   - If any first component is ≤ 12 AND the second component is > 12,
    ///     the file is MM/DD.
    ///   - When every row is ambiguous (both ≤ 12), default to DD/MM — that's
    ///     the format Thai banks (BBL/KBank/SCB/KTB/BAY/TTB) export.
    /// Same comparison is applied to dash- and dot-separated dates. ISO
    /// (yyyy-MM-dd) and Thai-month-name strings ignore this and parse natively.
    /// </summary>
    public static DateOrder DetectDateOrder(IEnumerable<string> dateStrings)
    {
        var slashRx = new Regex(@"^\s*(\d{1,2})[/\-\.](\d{1,2})[/\-\.](\d{2,4})\s*$");
        bool sawDayFirstSignal = false;
        bool sawMonthFirstSignal = false;
        foreach (var s in dateStrings)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var m = slashRx.Match(s);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var a)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var b)) continue;
            if (a > 12 && b <= 12) sawDayFirstSignal = true;
            else if (b > 12 && a <= 12) sawMonthFirstSignal = true;
            // a == b or both > 12 → ambiguous / invalid; ignore.
        }
        if (sawMonthFirstSignal && !sawDayFirstSignal) return DateOrder.MonthFirst;
        if (sawDayFirstSignal && !sawMonthFirstSignal) return DateOrder.DayFirst;
        // Both signals present (corrupt file) or no signal at all — default to
        // DD/MM since Thai bank exports overwhelmingly use that layout.
        return DateOrder.DayFirst;
    }

    public static bool TryParseFlexibleDate(string s, out DateTime date)
        => TryParseFlexibleDate(s, DateOrder.DayFirst, out date);

    public static bool TryParseFlexibleDate(string s, DateOrder order, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();

        // ISO formats are unambiguous — try them first regardless of order.
        var isoFormats = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd" };
        var thaiTextFormats = new[]
        {
            "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
            "dd-MMM-yyyy", "d-MMM-yyyy"
        };

        var dayFirst = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy",
            "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy",
            "dd.MM.yyyy", "d.M.yyyy"
        };
        var monthFirst = new[]
        {
            "MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy",
            "MM-dd-yyyy", "M-d-yyyy", "MM-dd-yy", "M-d-yy",
            "MM.dd.yyyy", "M.d.yyyy"
        };

        var cultures = new[]
        {
            CultureInfo.InvariantCulture,
            new CultureInfo("th-TH"),
            new CultureInfo("en-US")
        };

        // Order matters: try ISO first, then the orientation chosen for the
        // whole file, then the opposite as a last resort (for sites that mix
        // a single row in a foreign format — rare but harmless to attempt).
        var ordered = order == DateOrder.MonthFirst
            ? isoFormats.Concat(monthFirst).Concat(dayFirst).Concat(thaiTextFormats)
            : isoFormats.Concat(dayFirst).Concat(monthFirst).Concat(thaiTextFormats);

        foreach (var fmt in ordered)
        {
            foreach (var ci in cultures)
            {
                if (DateTime.TryParseExact(s, fmt, ci, DateTimeStyles.AssumeLocal, out date))
                {
                    if (date.Year > 2400) date = date.AddYears(-543);
                    return true;
                }
            }
        }

        // Final fallback: culture-driven free parse. Forces the right culture
        // based on the detected order so US-style strings like "4/12/2025" get
        // parsed as April 12, not April 4.
        var fallbackCulture = order == DateOrder.MonthFirst
            ? new CultureInfo("en-US")
            : new CultureInfo("th-TH");
        if (DateTime.TryParse(s, fallbackCulture, DateTimeStyles.AssumeLocal, out date))
        {
            if (date.Year > 2400) date = date.AddYears(-543);
            return true;
        }

        return false;
    }

    public static decimal ParseAmount(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s == "-" || s == "–" || s == "—") return 0;

        var clean = Regex.Replace(s, @"[฿$€£¥\s,]", "");

        var negative = false;
        if (clean.StartsWith('(') && clean.EndsWith(')'))
        {
            negative = true;
            clean = clean[1..^1];
        }

        if (clean.EndsWith('-'))
        {
            negative = true;
            clean = clean[..^1];
        }

        clean = Regex.Replace(clean, @"(CR|DR|cr|dr)$", "");

        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return negative ? -v : v;

        return 0;
    }
}
