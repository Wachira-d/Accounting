using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Services.Settlement.Adapters;

namespace Accounting.Services.Helpers;

/// <summary>
/// Robust Thai bank CSV statement parser.
/// Handles formats from BBL, KBank, SCB, KTB, BAY, TTB, etc.
/// Auto-detects delimiter, header row, column layout, date format, and amount format.
///
/// <para>รอบ 205 ทีม C: ถอดรหัส + แยก CSV ผ่านตัวอ่านกลาง <see cref="ImportTabularReader"/> (UTF-8 เข้มงวด → UTF-16 BOM → Windows-874/TIS-620 ·
/// เดิม <c>TryDecode</c> คอมเมนต์อ้างว่ามี Windows-874 แต่โค้ดถอด UTF-8 แบบหลวม ⇒ ไฟล์ Express/KBank รุ่นเก่ากลายเป็น U+FFFD ทั้งหัวตาราง) ·
/// คอลัมน์ยอดคงเหลือไม่บังคับ (ไม่มี = นำเข้าได้ + คำเตือนจากตัวเขียน) · ยอดเงินผ่าน <see cref="SignedAmount"/> (วงเล็บ/ลบท้าย/CR-DR)</para>
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

    /// <summary>A parsed transaction row. Balance เป็น null เมื่อช่องว่าง/อ่านไม่ได้
    /// — แยกจาก 0.00 จริง เพื่อไม่ให้ snapshot ยอดธนาคารโดนเขียนทับด้วยศูนย์ปลอม</summary>
    /// <param name="ExternalId">รหัสรายการของธนาคาร (OFX FITID) — กันนำเข้าซ้ำตรงตัว</param>
    /// <param name="Payee">ชื่อผู้โอน/ผู้รับ (OFX NAME)</param>
    /// <param name="RowNumber">เลขแถวในไฟล์ (1-based)</param>
    public record Row(
        DateTime Date,
        string Description,
        decimal Withdrawal,
        decimal Deposit,
        decimal? Balance,
        string? Reference,
        string? Channel,
        string? ExternalId = null,
        string? Payee = null,
        int RowNumber = 0);

    /// <summary>
    /// Parse the entire CSV. Returns the parsed rows and any skipped row diagnostics.
    /// </summary>
    public static (List<Row> rows, List<string> skipped) ParseStatement(byte[] fileBytes)
    {
        // ตัวถอดรหัสกลาง: UTF-8 เข้มงวด (มี/ไม่มี BOM) · UTF-16 BOM · Windows-874 (TIS-620) · ถอดไม่ได้ ⇒ ImportFormatException ข้อความไทย
        string content = ImportTabularReader.Decode(fileBytes);
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
            throw new ArgumentException($"{sourceLabel}ต้องมีหัวตารางและข้อมูลอย่างน้อย 1 รายการ (พบ {rawRows.Count} แถว)");

        var (headerIdx, map) = DetectHeader(rawRows);
        if (headerIdx < 0)
        {
            // Build a helpful diagnostic: surface the first 3 rows so the operator
            // can see what the parser saw — typical fixes are (a) merged-cell
            // header that ClosedXML couldn't split, (b) a cover sheet with text
            // banners, (c) extra rows above the real table.
            var preview = string.Join("\n", rawRows.Take(3).Select((r, idx) =>
                $"   แถว {idx + 1}: " + string.Join(" | ", r.Take(8).Select(c => string.IsNullOrWhiteSpace(c) ? "(ว่าง)" : c.Trim()))));
            throw new ArgumentException(
                $"ไม่พบหัวคอลัมน์ที่รองรับใน{sourceLabel} — ต้องมีคอลัมน์ที่มีคำว่า: " +
                "วันที่/Date และ ฝาก/Deposit หรือ ถอน/Withdrawal หรือ จำนวนเงิน/Amount (ยอดคงเหลือ/Balance ไม่บังคับ).\n" +
                $"สิ่งที่ระบบเห็น (3 แถวแรก):\n{preview}\n" +
                "💡 ลองลบแถวบนๆ ที่เป็นคำอธิบายธนาคารทิ้ง หรือ ตัด merged cells ออกก่อนนำเข้า");
        }

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
            if (IsSummaryRow(cols)) continue;

            string Get(int idx) => idx >= 0 && idx < cols.Count ? cols[idx].Trim().Trim('"') : "";

            var dateStr = Get(map.DateCol);
            if (string.IsNullOrWhiteSpace(dateStr)) continue;

            if (!TryParseFlexibleDate(dateStr, dateOrder, out var date))
            {
                skipped.Add($"แถว {i + 1}: รูปแบบวันที่ไม่ถูกต้อง '{dateStr}'");
                continue;
            }

            // Optional time column (invariant — ไม่ขึ้นกับ culture ของเครื่อง)
            if (map.TimeCol >= 0)
            {
                var timeStr = Get(map.TimeCol);
                if (TimeSpan.TryParse(SettlementValueParser.ThaiDigitsToAscii(timeStr), CultureInfo.InvariantCulture, out var t))
                    date = date.Date + t;
            }

            // ยอดเงินที่อ่านไม่ได้ ≠ ศูนย์ — บอกผู้ใช้ว่าแถวไหน ค่าอะไร (เดิมกลายเป็น 0 แล้วถูกตัดเงียบ ๆ ว่า "ไม่มีจำนวนเงิน")
            string? badAmount = null;
            decimal Amt(int col)
            {
                var raw = Get(col);
                var v = SignedAmount(raw);
                if (!v.Ok) badAmount ??= raw;
                return v.Value ?? 0m;
            }
            // ช่องถอนเป็นเงินออกเสมอ (บางธนาคารใส่เครื่องหมายลบในช่องถอน) — ตัวเขียนตัดสินทิศจาก ฝาก − |ถอน|
            var deposit = Amt(map.DepositCol);
            var withdrawal = Math.Abs(Amt(map.WithdrawalCol));
            var balanceCell = SignedAmount(Get(map.BalanceCol));
            var balance = balanceCell.Ok ? balanceCell.Value : null;

            // Single signed-amount column (some banks) — "1,000.00 CR" = เงินเข้า · "1,000.00 DR" = เงินออก
            if (deposit == 0 && withdrawal == 0 && map.AmountCol >= 0)
            {
                var amt = Amt(map.AmountCol);
                if (amt > 0) deposit = amt;
                else if (amt < 0) withdrawal = -amt;
            }

            if (deposit == 0 && withdrawal == 0)
            {
                skipped.Add(badAmount != null
                    ? $"แถว {i + 1}: อ่านจำนวนเงิน '{badAmount}' ไม่ได้"
                    : $"แถว {i + 1}: ไม่มีจำนวนเงิน");
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
                string.IsNullOrWhiteSpace(channel) ? null : channel,
                RowNumber: i + 1));
        }

        return (rows, skipped);
    }

    /// <summary>
    /// แถวสรุป/ยอดยกมา/ยอดรวมท้ายตาราง — ไม่ใช่รายการ (ตัดออก) · "รวมมิตร" (ชื่อร้าน) ต้องไม่ถูกตัด: จับเฉพาะคำสรุปเต็มคำ
    /// <para>รอบ 205 ทีม B: ส่งต่อไป <see cref="Accounting.Helpers.ImportHeaderDetector.IsSummaryRow"/> — owner เดียวของกติกาแถวยอดรวมทุกเส้นนำเข้า
    /// (ตัดสินจากป้ายทั้งเซลล์ ไม่ใช่คำที่ปนในข้อความ)</para>
    /// </summary>
    internal static bool IsSummaryRow(IReadOnlyList<string> cells)
        => Accounting.Helpers.ImportHeaderDetector.IsSummaryRow(cells);

    /// <summary>
    /// Parse CSV/TSV content into rows of cells — ตัวแยกกลาง <see cref="ImportTabularReader.ReadCsv"/> (ตัวคั่น , ; tab · คำพูดข้ามบรรทัด) ·
    /// ตัดบรรทัดว่างนำหน้า (ให้ตัวหาตัวคั่นเห็นบรรทัดจริง) และแถวว่างทั้งแถวออก
    /// </summary>
    public static List<List<string>> ParseCsvCells(string content)
    {
        if (content.Length > 0 && content[0] == '\uFEFF') content = content[1..];
        content = content.TrimStart('\r', '\n');
        return ImportTabularReader.ReadCsv(content)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .Select(r => r.ToList())
            .ToList();
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

        // แถวหัวตาราง = แถวแรกที่มี วันที่ + คอลัมน์เงิน · ชอบแถวที่มียอดคงเหลือด้วยก่อน (กันแถวคำอธิบายด้านบนที่บังเอิญมีคำว่า
        // "วันที่ … จำนวนเงิน" ถูกเลือก) · ไม่มีแถวไหนมียอดคงเหลือ ⇒ ใช้แถวแรกที่มีวันที่ + คอลัมน์เงิน (statement แบบไม่มียอด — รอบ 205 ทีม C)
        (int, ColumnMap)? withoutBalance = null;
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
            if (dateCol >= 0 && hasFlowCol && withoutBalance == null)
                withoutBalance = (r, new ColumnMap(dateCol, timeCol, descCol, wdCol, depCol, -1, amtCol, chCol, detCol, refCol));
        }

        return withoutBalance ?? (-1, new ColumnMap(-1, -1, -1, -1, -1, -1, -1, -1, -1, -1));
    }

    /// <summary>Day-first vs month-first orientation for slash/dash-separated dates.</summary>
    public enum DateOrder { Unknown, DayFirst, MonthFirst }

    /// <summary>
    /// Inspect every date string from the file to decide whether the format is
    /// DD/MM (Thai/European, the default) or MM/DD (US). Two-stage classifier:
    ///
    ///   Stage 1 — unambiguous component check. If any first component is &gt; 12
    ///   the file must be DD/MM; if any second component is &gt; 12 it must be
    ///   MM/DD. Conflicting signals fall through to stage 2.
    ///
    ///   Stage 2 — frequency analysis. A monthly bank statement covers a
    ///   single calendar month, so one of the two components stays constant
    ///   while the other ranges over the day-of-month. If the FIRST slot is
    ///   constant across rows but the SECOND varies, the file is MM/DD; the
    ///   constant must be the month. The mirror image flags DD/MM. This
    ///   catches the bug where every row has day ≤ 12 (e.g. a statement of
    ///   "04/01, 04/02, ..., 04/12" which has no &gt;12 signal at all).
    ///
    /// Last resort is the Thai-bank default of DD/MM.
    /// </summary>
    public static DateOrder DetectDateOrder(IEnumerable<string> dateStrings)
    {
        var slashRx = new Regex(@"^\s*(\d{1,2})[/\-\.](\d{1,2})[/\-\.](\d{2,4})\s*$");
        bool sawDayFirstSignal = false;
        bool sawMonthFirstSignal = false;
        var firsts = new HashSet<int>();
        var seconds = new HashSet<int>();
        int sampleCount = 0;

        foreach (var s in dateStrings)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var m = slashRx.Match(s);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var a)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var b)) continue;

            if (a > 12 && b <= 12) sawDayFirstSignal = true;
            else if (b > 12 && a <= 12) sawMonthFirstSignal = true;

            firsts.Add(a);
            seconds.Add(b);
            sampleCount++;
        }

        // Stage 1 — unambiguous signals win.
        if (sawMonthFirstSignal && !sawDayFirstSignal) return DateOrder.MonthFirst;
        if (sawDayFirstSignal && !sawMonthFirstSignal) return DateOrder.DayFirst;

        // Stage 2 — constant-month heuristic. Need a meaningful sample size
        // (a 2-row file shouldn't be classified by this) and a clear pattern:
        // exactly one of the two components is constant across all rows.
        if (sampleCount >= 4)
        {
            var firstConstant = firsts.Count == 1;
            var secondConstant = seconds.Count == 1;
            if (firstConstant && !secondConstant) return DateOrder.MonthFirst;
            if (secondConstant && !firstConstant) return DateOrder.DayFirst;
        }

        // No signal at all (or both signals present in a corrupt file) — fall
        // back to the Thai-bank export convention.
        return DateOrder.DayFirst;
    }

    public static bool TryParseFlexibleDate(string s, out DateTime date)
        => TryParseFlexibleDate(s, DateOrder.DayFirst, out date);

    public static bool TryParseFlexibleDate(string s, DateOrder order, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = SettlementValueParser.ThaiDigitsToAscii(s).Trim();

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

    /// <summary>
    /// **ยอดเงินในไฟล์ statement — ตัวเดียวของงานธนาคาร** (ไฟล์ CSV/Excel · ศูนย์นำเข้า <c>BankStatementImportTarget</c>) — แกนกลางมอบให้
    /// <see cref="ImportValueNormalizer.Amount"/> (คั่นหลักพัน · วงเล็บ/ลบท้าย = ติดลบ · ฿/บาท · เลขไทย) แล้วเติมเฉพาะเรื่องของธนาคาร:
    /// "CR"/"DR" ท้าย/หน้าค่า = เงินเข้า/เงินออก (เดิมตัด CR/DR ทิ้งเฉย ๆ ⇒ "500.00 DR" กลายเป็นเงินเข้า) · เครื่องหมายสองชั้นที่ขัดกัน
    /// ("(500.00) CR") = อ่านไม่ได้ (ไม่เดาว่าชั้นไหนชนะ)
    /// </summary>
    public static ImportValue<decimal?> SignedAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(null, true);
        var s = raw.Trim();
        if (s is "-" or "–" or "—") return new(null, true);
        var m = CrDrPattern.Match(s);
        if (!m.Success) return ImportValueNormalizer.Amount(s);
        var core = m.Groups["v1"].Success ? m.Groups["v1"].Value : m.Groups["v2"].Value;
        var marker = (m.Groups["m1"].Success ? m.Groups["m1"].Value : m.Groups["m2"].Value).ToUpperInvariant();
        var inner = ImportValueNormalizer.Amount(core);
        if (!inner.Ok) return inner;
        if (inner.Value == null) return new(null, false, "amount-invalid", $"อ่านจำนวนเงิน \"{s}\" ไม่ได้");
        if (inner.Value < 0)
            return new(null, false, "amount-sign-conflict",
                $"จำนวนเงิน \"{s}\" มีทั้งเครื่องหมายลบและ {marker} — แก้ให้เหลือแบบเดียว");
        return new(marker == "DR" ? -inner.Value : inner.Value, true);
    }

    private static readonly Regex CrDrPattern = new(
        @"^(?:(?<v1>.*?[0-9).])\s*(?<m1>CR|DR)\.?|(?<m2>CR|DR)\.?\s*(?<v2>[0-9(+\-.].*))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
