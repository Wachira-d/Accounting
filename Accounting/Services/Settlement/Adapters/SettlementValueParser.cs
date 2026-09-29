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

/// <summary>เขตเวลาของ "วันที่+เวลา" ในไฟล์ที่ไม่มี offset กำกับ (review198-B R-B8 · ทีม I รอบ 200)</summary>
/// <remarks>ค่าที่มี offset ในตัว (<c>Z</c> · <c>+07:00</c> · <c>GMT+7</c>) ใช้ offset นั้นเสมอ ไม่ว่าตั้งค่าอะไร ·
/// วันที่ที่ไม่มีเวลา = วันตามปฏิทิน (ไม่แปลงเขตเวลา)</remarks>
public enum SettlementFileTimeZone
{
    /// <summary>ยังไม่รู้ — ตัดสินจากหัวคอลัมน์/ข้อมูลทั้งไฟล์ (<see cref="SettlementFileDecisions.DecideTimeZone"/>) · ตัดสินไม่ได้และมีผล ⇒ ล้มดังให้ผู้ใช้เลือก</summary>
    Auto = 0,
    /// <summary>เวลาไทย (UTC+7)</summary>
    Bangkok = 1,
    /// <summary>เวลา UTC — แปลงเป็นวันที่ไทยก่อนใช้ (18:00 UTC = 01:00 วันถัดไปของไทย)</summary>
    Utc = 2,
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
    /// <remarks>R-B10 (ทีม I รอบ 200): เครื่องหมายลบสองชั้นที่ขัดกันเอง <c>(-100)</c> · <c>-100-</c> · <c>(+100)</c> = อ่านไม่ได้ (เดิม "(-100)" ได้ +100
    /// เพราะลบสองชั้นหักล้างกัน) · ขีดลบยูนิโค้ดทุกแบบ (− ‐ ‑ ‒ – ﹣ －) = เครื่องหมายลบ · หน่วยเงินอยู่นอกวงเล็บได้ (<c>฿(1,234.00)</c>)</remarks>
    public static bool TryParseAmount(string? raw, out decimal? value)
    {
        value = null;
        if (raw == null) return true;
        var s = ThaiDigitsToAscii(raw).Trim();
        if (s.Length == 0 || s == "-" || s == "—" || s == "–") return true;

        s = NormalizeMinus(s);
        foreach (var unit in new[] { "THB", "thb", "บาท", "฿", "$" })
            s = s.Replace(unit, "", StringComparison.Ordinal);
        s = s.Replace(" ", "", StringComparison.Ordinal).Replace("\u00A0", "", StringComparison.Ordinal)
            .Replace("\u202F", "", StringComparison.Ordinal);
        var paren = false;
        if (s.StartsWith('(') && s.EndsWith(')')) { paren = true; s = s[1..^1]; }
        var lead = false;
        var plus = false;
        if (s.StartsWith('-')) { lead = true; s = s[1..]; }
        else if (s.StartsWith('+')) { plus = true; s = s[1..]; }
        var trail = false;
        if (s.EndsWith('-')) { trail = true; s = s[..^1]; }
        // R-B10: เครื่องหมายมากกว่าหนึ่งชั้น ("(-100)" · "-100-" · "(+100)") = ไฟล์บอกสองอย่างพร้อมกัน ⇒ ไม่เดาว่าชั้นไหนชนะ
        if ((paren ? 1 : 0) + (lead ? 1 : 0) + (plus ? 1 : 0) + (trail ? 1 : 0) > 1) return false;
        var negative = paren || lead || trail;
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

    /// <summary>ขีดลบ/ขีดกลางยูนิโค้ดทุกแบบที่ Excel/PDF/เว็บใส่แทนเครื่องหมายลบ → "-" (R-B10)</summary>
    internal static string NormalizeMinus(string s)
    {
        foreach (var ch in "\u2212\u2010\u2011\u2012\u2013\uFE63\uFF0D")
            s = s.Replace(ch, '-');
        return s;
    }

    private static readonly Regex GroupedNoDecimal = new(@"^\d{1,3}(,\d{3})+$", RegexOptions.Compiled);
    private static readonly Regex GroupedWithDecimal = new(@"^\d{1,3}(,\d{3})+\.\d+$", RegexOptions.Compiled);

    /// <summary>ยอดที่มีแต่จุลภาคคั่นกลุ่ม 3 หลักโดยไม่มีจุดทศนิยม ("1,500") — ในไฟล์รูปแบบยุโรปคือ 1.5 ไม่ใช่ 1,500 (R-B10)</summary>
    internal static bool IsCommaGroupedWithoutDecimal(string? raw)
        => raw != null && GroupedNoDecimal.IsMatch(AmountDigits(raw));

    /// <summary>ยอดที่พิสูจน์ว่าไฟล์ใช้จุลภาคคั่นหลักพัน + จุดทศนิยม ("1,500.25") — หลักฐานว่าไฟล์ไม่ใช่รูปแบบยุโรป</summary>
    internal static bool IsCommaGroupedWithDecimal(string? raw)
        => raw != null && GroupedWithDecimal.IsMatch(AmountDigits(raw));

    private static string AmountDigits(string raw) => Regex.Replace(ThaiDigitsToAscii(raw), @"[^0-9,.]", "");

    private static readonly Regex ExcelExponent = new(@"^[+\-]?\d(?:\.\d+)?[eE][+\-]\d{1,3}$", RegexOptions.Compiled);

    /// <summary>
    /// **เลขอ้างอิง (เลขรายการ/ออเดอร์/รอบโอน) ที่เสียหลักไปแล้วเพราะผ่านตัวเลขของ Excel** (review198-B R-B7 · ทีม I รอบ 200)
    /// <para>Excel เก็บตัวเลขได้ 15 หลัก — เลขออเดอร์ 16–20 หลักในเซลล์ชนิดตัวเลขกลายเป็น <c>1.2345678901234568E+17</c> (xlsx) หรือ
    /// <c>1.23457E+17</c> (CSV ที่ Excel บันทึก) · หลักท้ายหายแล้วกู้ไม่ได้ ⇒ เลขต่างกันชนเป็นค่าเดียว (คีย์กันซ้ำทิ้งรายการจริง) และจับคู่
    /// <c>Document.Reference</c> ไม่เจอ ⇒ ต้องล้มดัง ไม่ใช่เก็บเลขที่ผิด · รูปที่จับ = scientific แบบที่ Excel/.NET พิมพ์ (หลักเดียวหน้าจุด +
    /// เลขชี้กำลังมีเครื่องหมาย) — เลขอ้างอิงตัวอักษรปนตัวเลขอย่าง "5E10" ไม่ถูกจับ</para>
    /// </summary>
    public static bool IdLostPrecision(string? raw)
        => raw != null && ExcelExponent.IsMatch(raw.Trim());

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

    private static readonly Regex NumericDate = new(@"^(\d{1,4})[/\-.](\d{1,2})[/\-.](\d{1,4})(?:[ T](.*))?$", RegexOptions.Compiled);
    private static readonly Regex NamedDate = new(@"^(\d{1,2})[\s\-]+([^\s\-\d]+)[\s\-,]+(\d{2,4})(?:\s+(.*))?$", RegexOptions.Compiled);
    private static readonly Regex NamedDateMonthFirst = new(@"^([^\s\-\d,]+)[\s\-]+(\d{1,2}),?[\s\-]+(\d{2,4})(?:\s+(.*))?$", RegexOptions.Compiled);
    // ส่วนเวลาท้ายวันที่: 18:30 · 18:30:05.123 · 6:30 PM · 18:30 น. · + เขตเวลาในตัว (Z · UTC · GMT · +07:00 · +0700 · GMT+7)
    private static readonly Regex TimeTail = new(
        @"^(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,]\d{1,7})?)?[ ]*(am|pm|น\.)?[ ]*(z|utc|gmt|(?:utc|gmt)?[ ]*[+\-][ ]*\d{1,2}(?::?\d{2})?)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LooksLikeTime = new(@"\d:\d", RegexOptions.Compiled);
    private static readonly TimeSpan BangkokOffset = TimeSpan.FromHours(7);

    /// <summary>ส่วนของวันที่ที่อ่านได้ (ยังไม่แปลงเขตเวลา · ปียังอาจเป็น พ.ศ.)</summary>
    /// <param name="UnreadTime">ท้ายวันที่มีรูปเวลา ("18:30…") แต่อ่านไม่ได้ — แปลงเขตเวลาไม่ได้</param>
    private readonly record struct DateParts(int Year, int Month, int Day, TimeSpan? Time, TimeSpan? Offset, bool UnreadTime);

    /// <summary>ส่วน "a/b" ของวันที่ตัวเลขที่ไม่ขึ้นต้นด้วยปี — ป้อน <see cref="DetectDateOrder"/></summary>
    public static (int A, int B)? NumericDayMonthParts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = NumericDate.Match(ThaiDigitsToAscii(raw).Trim());
        if (!m.Success || m.Groups[1].Value.Length > 2) return null;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <b>หลักฐาน</b>ลำดับวัน/เดือนจากทั้งไฟล์: มีส่วนแรก &gt; 12 ⇒ วัน/เดือน · มีส่วนที่สอง &gt; 12 ⇒ เดือน/วัน · ขัดกันเอง ⇒ ล้มดัง ·
    /// <b>ไม่มีหลักฐาน ⇒ <see cref="SettlementDateOrder.Auto"/></b> (review198-B R-B9 · ทีม I รอบ 200 — เดิมคืน วัน/เดือน เงียบ ๆ ⇒ ไฟล์ เดือน/วัน
    /// ช่วงสั้น 1–12 สลับวันกับเดือน 5 ก.ย. → 9 พ.ค. โดยไม่มีคำเตือน) · ตัวตัดสินขั้นสุดท้าย = <see cref="SettlementFileDecisions.DecideDateOrder"/>
    /// </summary>
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
        return monthFirst ? SettlementDateOrder.MonthDayYear
            : dayFirst ? SettlementDateOrder.DayMonthYear
            : SettlementDateOrder.Auto;
    }

    /// <summary>วันที่ตัวเลขนี้อ่านได้สองแบบจริง (ทั้งสองส่วน ≤ 12 และไม่เท่ากัน — "01/09/2026" ✓ · "05/05/2026" ✗ · "13/09/2026" ✗)</summary>
    public static bool IsOrderSensitive(string? raw)
        => NumericDayMonthParts(raw) is (int a, int b) && a <= 12 && b <= 12 && a != b;

    /// <summary>อ่านวันที่ (เวลาไม่มี offset = เวลาไทย) — ดูเมธอดที่รับเขตเวลา</summary>
    public static bool TryParseDate(string? raw, SettlementDateOrder order, out DateTime? value)
        => TryParseDate(raw, order, SettlementFileTimeZone.Bangkok, out value);

    /// <summary>
    /// อ่านวันที่ — คืนวันที่ตามปฏิทินไทย 00:00 Kind=Utc · ปี พ.ศ. แปลงเป็น ค.ศ. · ปี 2 หลัก/ค่าพัง = false
    /// <para>เขตเวลา (review198-B R-B8): มี offset ในค่า ⇒ ใช้ offset นั้น · มีเวลาแต่ไม่มี offset ⇒ ตาม <paramref name="zone"/>
    /// (<see cref="SettlementFileTimeZone.Utc"/> = บวก 7 ชม. ก่อนตัดวัน · <c>Auto</c>/<c>Bangkok</c> = วันตามที่เขียน — ผู้เรียกต้องตัดสิน
    /// <c>Auto</c> ก่อนด้วย <see cref="SettlementFileDecisions.DecideTimeZone"/>) · ไม่มีเวลา = วันตามปฏิทิน (ไม่แปลง) ·
    /// เวลา UTC ที่อ่านรูปไม่ออก ⇒ false (แปลงไม่ได้ ห้ามเดา)</para>
    /// </summary>
    public static bool TryParseDate(string? raw, SettlementDateOrder order, SettlementFileTimeZone zone, out DateTime? value)
    {
        value = null;
        if (raw == null) return true;
        var s = ThaiDigitsToAscii(raw).Trim();
        if (s.Length == 0 || s == "-") return true;
        if (IsoWithOffset(s) is DateTimeOffset dto)
        {
            var bkk = dto.ToOffset(BangkokOffset);
            return Make(bkk.Year, bkk.Month, bkk.Day, out value);
        }
        return TryReadParts(s, order, out var p) && Resolve(p, zone, out value);
    }

    /// <summary>
    /// ค่านี้ตกคนละวันระหว่าง "เวลาไทย" กับ "UTC" ไหม (มีเวลา · ไม่มี offset ในตัว · เวลาทำให้ข้ามวัน เช่น 18:00 UTC = 01:00 วันถัดไปของไทย) —
    /// ป้อน <see cref="SettlementFileDecisions.DecideTimeZone"/> (R-B8)
    /// </summary>
    public static bool DependsOnZone(string? raw, SettlementDateOrder order)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = ThaiDigitsToAscii(raw).Trim();
        if (IsoWithOffset(s) != null || !TryReadParts(s, order, out var p) || p.Offset != null) return false;
        if (p.UnreadTime) return true;
        if (p.Time == null) return false;
        return Resolve(p, SettlementFileTimeZone.Bangkok, out var asBkk) && Resolve(p, SettlementFileTimeZone.Utc, out var asUtc)
               && asBkk != asUtc;
    }

    /// <summary>ISO 8601 ที่มีเขตเวลา (Z / +07:00) — null = ไม่ใช่รูปนี้</summary>
    private static DateTimeOffset? IsoWithOffset(string s)
    {
        if (s.Length > 10 && (s[10] == 'T' || s[10] == ' ') && s[4] == '-'
            && (s.EndsWith('Z') || Regex.IsMatch(s, @"[+\-]\d{2}:?\d{2}$"))
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto;
        return null;
    }

    private static bool TryReadParts(string s, SettlementDateOrder order, out DateParts parts)
    {
        parts = default;
        var m = NumericDate.Match(s);
        if (m.Success)
        {
            var g1 = m.Groups[1].Value; var g2 = m.Groups[2].Value; var g3 = m.Groups[3].Value;
            int p1 = int.Parse(g1, CultureInfo.InvariantCulture), p2 = int.Parse(g2, CultureInfo.InvariantCulture),
                p3 = int.Parse(g3, CultureInfo.InvariantCulture);
            if (g1.Length == 4)                                                                   // yyyy-MM-dd
                return g3.Length <= 2 && WithTail(p1, p2, p3, m.Groups[4].Value, out parts);
            if (g3.Length != 4) return false;                                                    // ปี 2 หลัก = กำกวม
            return order == SettlementDateOrder.MonthDayYear
                ? WithTail(p3, p1, p2, m.Groups[4].Value, out parts)
                : WithTail(p3, p2, p1, m.Groups[4].Value, out parts);
        }

        var n = NamedDate.Match(s);
        if (n.Success && MonthNames.TryGetValue(n.Groups[2].Value.Trim(), out var mon1) && n.Groups[3].Value.Length == 4)
            return WithTail(int.Parse(n.Groups[3].Value, CultureInfo.InvariantCulture), mon1,
                int.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture), n.Groups[4].Value, out parts);
        var n2 = NamedDateMonthFirst.Match(s);
        if (n2.Success && MonthNames.TryGetValue(n2.Groups[1].Value.Trim(), out var mon2) && n2.Groups[3].Value.Length == 4)
            return WithTail(int.Parse(n2.Groups[3].Value, CultureInfo.InvariantCulture), mon2,
                int.Parse(n2.Groups[2].Value, CultureInfo.InvariantCulture), n2.Groups[4].Value, out parts);

        // Excel serial (วันที่ที่เซลล์ไม่ได้จัดรูปแบบ) — ช่วงปี 2000–2100 เท่านั้น กันเลขอื่นกลายเป็นวันที่ · เศษของวัน = เวลา
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial >= 36526 && serial <= 73051)
        {
            var dt = DateTime.FromOADate(serial);
            var tod = TimeSpan.FromSeconds(Math.Round(dt.TimeOfDay.TotalSeconds, MidpointRounding.AwayFromZero));
            TimeSpan? time = tod > TimeSpan.Zero ? tod : null;
            parts = new DateParts(dt.Year, dt.Month, dt.Day, time, null, false);
            return true;
        }
        return false;
    }

    /// <summary>ส่วนวันที่ + ส่วนท้าย (เวลา/เขตเวลา) · ท้ายที่ไม่ใช่เวลา ("(จันทร์)") ไม่มีผล · ท้ายรูปเวลาที่อ่านไม่ออก = <c>UnreadTime</c></summary>
    private static bool WithTail(int year, int month, int day, string? tail, out DateParts parts)
    {
        parts = new DateParts(year, month, day, null, null, false);
        var t = (tail ?? "").Trim();
        if (t.Length == 0) return true;
        var m = TimeTail.Match(t);
        if (!m.Success)
        {
            parts = parts with { UnreadTime = LooksLikeTime.IsMatch(t) };
            return true;
        }
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var ampm = m.Groups[4].Value.ToLowerInvariant();
        if (ampm == "am" || ampm == "pm")
        {
            if (h < 1 || h > 12) return false;
            h = ampm == "pm" ? (h % 12) + 12 : h % 12;
        }
        if (h > 23 || min > 59 || sec > 59) return false;
        TimeSpan? offset = null;
        if (m.Groups[5].Success && m.Groups[5].Value.Length > 0)
        {
            if (!TryParseOffset(m.Groups[5].Value, out var o)) return false;
            offset = o;
        }
        parts = parts with { Time = new TimeSpan(h, min, sec), Offset = offset };
        return true;
    }

    /// <summary>"Z" · "UTC" · "GMT" = 0 · "+07:00" · "+0700" · "+7" · "GMT+7" · "UTC-05:30" — อ่านไม่ได้ = false</summary>
    internal static bool TryParseOffset(string raw, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var z = raw.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (z.StartsWith("utc", StringComparison.Ordinal) || z.StartsWith("gmt", StringComparison.Ordinal)) z = z[3..];
        if (z.Length == 0 || z == "z") return true;
        var sign = z[0] == '-' ? -1 : z[0] == '+' ? 1 : 0;
        if (sign == 0) return false;
        var body = z[1..].Replace(":", "", StringComparison.Ordinal);
        if (body.Length == 0 || body.Length > 4 || !body.All(char.IsAsciiDigit)) return false;
        var hh = 0;
        var mm = 0;
        if (body.Length <= 2) hh = int.Parse(body, CultureInfo.InvariantCulture);
        else
        {
            hh = int.Parse(body[..^2], CultureInfo.InvariantCulture);
            mm = int.Parse(body[^2..], CultureInfo.InvariantCulture);
        }
        if (hh > 14 || mm > 59) return false;
        offset = TimeSpan.FromMinutes(sign * (hh * 60 + mm));
        return true;
    }

    /// <summary>ส่วนของวันที่ → วันตามปฏิทินไทย (แปลงเขตเวลาเมื่อมีเวลา และมี offset ในตัว หรือไฟล์เป็น UTC)</summary>
    private static bool Resolve(DateParts p, SettlementFileTimeZone zone, out DateTime? value)
    {
        value = null;
        if (p.Offset == null && zone != SettlementFileTimeZone.Utc) return Make(p.Year, p.Month, p.Day, out value);
        if (p.UnreadTime) return false;                                   // มีเวลาแต่อ่านไม่ออก — แปลงเขตเวลาไม่ได้ (ไม่เดา)
        if (p.Time is not TimeSpan t) return Make(p.Year, p.Month, p.Day, out value);
        if (!Make(p.Year, p.Month, p.Day, out var day) || day is not DateTime d0) return false;
        var bkk = d0 + t - (p.Offset ?? TimeSpan.Zero) + BangkokOffset;
        return Make(bkk.Year, bkk.Month, bkk.Day, out value);
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
