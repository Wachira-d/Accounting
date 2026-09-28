using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>ลำดับวัน/เดือนของวันที่แบบตัวเลข</summary>
public enum SettlementDateOrder
{
    /// <summary>ยังไม่รู้ — ตัดสินจากทั้งไฟล์ (<see cref="SettlementValueParser.DetectDateOrder"/>)</summary>
    Auto = 0,
    /// <summary>วัน/เดือน/ปี (รายงานไทยส่วนใหญ่)</summary>
    DayMonthYear = 1,
    /// <summary>เดือน/วัน/ปี</summary>
    MonthDayYear = 2,
}

/// <summary>
/// **แปลงค่าในไฟล์ settlement เป็นยอดเงิน/วันที่ — pure · ไม่ขึ้นกับ culture ของเครื่อง**
///
/// <para>ยอดเงิน: คั่นหลักพัน , · วงเล็บ = ติดลบ <c>(1,234.50)</c> · ลบท้าย <c>1,234.50-</c> · เครื่องหมายลบยูนิโค้ด ·
/// สัญลักษณ์/หน่วยเงิน (฿ · THB · บาท) · เลขไทย ๐–๙ · ค่าว่าง/"-" = ไม่มีค่า (null) · อ่านไม่ได้ = false (ผู้เรียกล้มดังทั้งไฟล์)</para>
/// <para>วันที่: dd/MM/yyyy · d-M-yyyy · yyyy-MM-dd (+ เวลา/ISO) · ชื่อเดือนอังกฤษ/ไทยย่อ · <b>ปี ≥ 2400 = พ.ศ. → ลบ 543</b> ·
/// ปี 2 หลัก = กำกวม (พ.ศ./ค.ศ.) ⇒ อ่านไม่ได้ (ไม่เดา) · คืนวันที่ตามปฏิทินไทย ณ 00:00 Kind=Utc
/// (ตรงกับที่ <c>SettlementBatchMath.Plan</c> ใช้ <c>.Date</c> แบ่งใบขายสรุปรายวัน)</para>
/// </summary>
public static class SettlementValueParser
{
    private static readonly Regex NumericCore = new(@"^\d{1,3}(,\d{3})+(\.\d+)?$|^\d+(\.\d+)?$|^\.\d+$", RegexOptions.Compiled);

    /// <summary>อ่านยอดเงิน — <paramref name="value"/> = null เมื่อช่องว่าง/"-" · คืน false เมื่อมีข้อความที่ไม่ใช่ตัวเลข</summary>
    public static bool TryParseAmount(string? raw, out decimal? value)
    {
        value = null;
        if (raw == null) return true;
        var s = ThaiDigitsToAscii(raw).Trim();
        if (s.Length == 0 || s == "-" || s == "—" || s == "–") return true;

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')')) { negative = true; s = s[1..^1].Trim(); }
        s = s.Replace('−', '-').Replace('–', '-');
        foreach (var unit in new[] { "THB", "thb", "บาท", "฿", "$" })
            s = s.Replace(unit, "", StringComparison.Ordinal);
        s = s.Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (s.StartsWith('-')) { negative = !negative; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (s.EndsWith('-')) { negative = !negative; s = s[..^1]; }
        if (s.Length == 0) return false;

        // รูปแบบวิทยาศาสตร์จาก Excel (1.5E-05) — ยอมเฉพาะเมื่อแปลงแล้วเป็นทศนิยมปกติ
        if (s.Contains('E') || s.Contains('e'))
        {
            if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sci)) return false;
            value = Math.Round(negative ? -sci : sci, 2, MidpointRounding.AwayFromZero);
            return true;
        }
        if (!NumericCore.IsMatch(s)) return false;
        if (!decimal.TryParse(s.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var d)) return false;
        // ยอดในไฟล์ของแพลตฟอร์มเป็นสตางค์ 2 ตำแหน่ง · ค่าจาก Excel (double) อาจมีเศษทศนิยมลอย ⇒ ปัดแบบ AwayFromZero (CLAUDE.md §E)
        value = Math.Round(negative ? -d : d, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static readonly Dictionary<string, int> MonthNames = BuildMonthNames();

    private static Dictionary<string, int> BuildMonthNames()
    {
        var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var en = new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        var enFull = new[] { "january", "february", "march", "april", "may", "june", "july", "august", "september",
            "october", "november", "december" };
        var thShort = new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };
        var thFull = new[] { "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
            "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };
        for (var i = 0; i < 12; i++)
        {
            m[en[i]] = i + 1; m[enFull[i]] = i + 1; m[thShort[i]] = i + 1; m[thFull[i]] = i + 1;
            m[thShort[i].Replace(".", "", StringComparison.Ordinal)] = i + 1;
        }
        m["sept"] = 9;
        return m;
    }

    private static readonly Regex NumericDate = new(@"^(\d{1,4})[/\-.](\d{1,2})[/\-.](\d{1,4})(?:[ T].*)?$", RegexOptions.Compiled);
    private static readonly Regex NamedDate = new(@"^(\d{1,2})[\s\-]+([^\s\-\d]+)[\s\-,]+(\d{2,4})(?:\s.*)?$", RegexOptions.Compiled);
    private static readonly Regex NamedDateMonthFirst = new(@"^([^\s\-\d,]+)[\s\-]+(\d{1,2}),?[\s\-]+(\d{2,4})(?:\s.*)?$", RegexOptions.Compiled);

    /// <summary>ส่วน "a/b" ของวันที่ตัวเลขที่ไม่ขึ้นต้นด้วยปี — ป้อน <see cref="DetectDateOrder"/></summary>
    public static (int A, int B)? NumericDayMonthParts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = NumericDate.Match(ThaiDigitsToAscii(raw).Trim());
        if (!m.Success || m.Groups[1].Value.Length > 2) return null;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>ตัดสินลำดับวัน/เดือนจากทั้งไฟล์: มีส่วนแรก &gt; 12 ⇒ วัน/เดือน · มีส่วนที่สอง &gt; 12 ⇒ เดือน/วัน ·
    /// ขัดกันเอง ⇒ ล้มดัง · ไม่มีหลักฐาน ⇒ วัน/เดือน (รายงานไทย)</summary>
    public static SettlementDateOrder DetectDateOrder(IEnumerable<string?> raws)
    {
        bool dayFirst = false, monthFirst = false;
        foreach (var r in raws)
        {
            if (NumericDayMonthParts(r) is not (int a, int b)) continue;
            if (a > 12) dayFirst = true;
            if (b > 12) monthFirst = true;
        }
        if (dayFirst && monthFirst)
            throw new SettlementFormatException("date-order",
                "วันที่ในไฟล์ปนกันระหว่าง วัน/เดือน และ เดือน/วัน — ระบุรูปแบบวันที่ในการจับคู่คอลัมน์ (dateOrder) แล้วนำเข้าใหม่");
        return monthFirst ? SettlementDateOrder.MonthDayYear : SettlementDateOrder.DayMonthYear;
    }

    /// <summary>อ่านวันที่ — คืนวันที่ตามปฏิทินไทย 00:00 Kind=Utc · ปี พ.ศ. แปลงเป็น ค.ศ. · ปี 2 หลัก/ค่าพัง = false</summary>
    public static bool TryParseDate(string? raw, SettlementDateOrder order, out DateTime? value)
    {
        value = null;
        if (raw == null) return true;
        var s = ThaiDigitsToAscii(raw).Trim();
        if (s.Length == 0 || s == "-") return true;

        // ISO 8601 ที่มีเขตเวลา (Z / +07:00) — แปลงเป็นวันที่กรุงเทพ
        if (s.Length > 10 && (s[10] == 'T' || s[10] == ' ') && s[4] == '-'
            && (s.EndsWith('Z') || Regex.IsMatch(s, @"[+\-]\d{2}:?\d{2}$"))
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            var bkk = dto.ToOffset(TimeSpan.FromHours(7));
            return Make(bkk.Year, bkk.Month, bkk.Day, out value);
        }

        var m = NumericDate.Match(s);
        if (m.Success)
        {
            var g1 = m.Groups[1].Value; var g2 = m.Groups[2].Value; var g3 = m.Groups[3].Value;
            int p1 = int.Parse(g1, CultureInfo.InvariantCulture), p2 = int.Parse(g2, CultureInfo.InvariantCulture),
                p3 = int.Parse(g3, CultureInfo.InvariantCulture);
            if (g1.Length == 4) return g3.Length <= 2 && Make(p1, p2, p3, out value);           // yyyy-MM-dd
            if (g3.Length != 4) return false;                                                    // ปี 2 หลัก = กำกวม
            return order == SettlementDateOrder.MonthDayYear ? Make(p3, p1, p2, out value) : Make(p3, p2, p1, out value);
        }

        var n = NamedDate.Match(s);
        if (n.Success && MonthNames.TryGetValue(n.Groups[2].Value.Trim(), out var mon1) && n.Groups[3].Value.Length == 4)
            return Make(int.Parse(n.Groups[3].Value, CultureInfo.InvariantCulture), mon1,
                int.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture), out value);
        var n2 = NamedDateMonthFirst.Match(s);
        if (n2.Success && MonthNames.TryGetValue(n2.Groups[1].Value.Trim(), out var mon2) && n2.Groups[3].Value.Length == 4)
            return Make(int.Parse(n2.Groups[3].Value, CultureInfo.InvariantCulture), mon2,
                int.Parse(n2.Groups[2].Value, CultureInfo.InvariantCulture), out value);

        // Excel serial (วันที่ที่เซลล์ไม่ได้จัดรูปแบบ) — ช่วงปี 2000–2100 เท่านั้น กันเลขอื่นกลายเป็นวันที่
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial >= 36526 && serial <= 73051)
        {
            var d = DateTime.FromOADate(serial);
            return Make(d.Year, d.Month, d.Day, out value);
        }
        return false;
    }

    private static bool Make(int year, int month, int day, out DateTime? value)
    {
        value = null;
        if (year >= 2400) year -= 543;                   // พ.ศ. → ค.ศ.
        if (year < 1900 || year > 2200 || month < 1 || month > 12 || day < 1) return false;
        if (day > DateTime.DaysInMonth(year, month)) return false;
        value = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    /// <summary>เลขไทย ๐–๙ → 0–9</summary>
    public static string ThaiDigitsToAscii(string s)
    {
        if (s.IndexOfAny("๐๑๒๓๔๕๖๗๘๙".ToCharArray()) < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch >= '๐' && ch <= '๙' ? (char)('0' + (ch - '๐')) : ch);
        return sb.ToString();
    }
}
