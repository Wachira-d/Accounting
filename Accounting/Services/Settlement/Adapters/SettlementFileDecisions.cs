using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>ผลการตัดสินค่าหนึ่งของไฟล์ — <paramref name="Proven"/> = ไฟล์/หัวรอบโอนพิสูจน์ได้ (ระบบจำให้ช่องทาง) · false = ค่าที่ผู้ใช้ตั้งไว้แล้ว
/// หรือค่าตั้งต้นที่<b>ไม่มีผลต่อผลลัพธ์</b> (ห้ามจำ — ไม่ใช่หลักฐาน)</summary>
public readonly record struct SettlementFileDecision<T>(T Value, bool Proven);

/// <summary>
/// **ตัวตัดสินระดับ "ทั้งไฟล์" ของตัวอ่าน settlement — pure** (review198-B R-B8 · R-B9 · R-B10 · R-B11 · ทีม I รอบ 200)
///
/// <para>═══ ทำไมอยู่ใต้ Adapters ═══ ความรู้เรื่องรูปไฟล์ (หัวคอลัมน์ · ลำดับวัน/เดือน · เขตเวลา · แถวสรุป) ต้องอยู่ชั้น adapter เท่านั้น
/// (<c>tools/settlement_adapter_boundary_check.py</c> · report-S2 §3) — ชั้น service ได้แค่ "แถวที่อ่านแล้ว"</para>
///
/// <para>═══ หลักเดียวทุกตัว ═══ ไฟล์กำกวมและความกำกวม<b>มีผลต่อตัวเลข/วันที่</b> ⇒ ล้มดังทั้งไฟล์พร้อมทางไปต่อ (เลือกค่าที่หน้าจับคู่คอลัมน์ —
/// ระบบจำไว้ให้ช่องทาง) · กำกวมแต่<b>ไม่มีผล</b> (ทุกวันที่ ≤ 12 และวัน=เดือน · ไม่มีเวลาที่ข้ามวัน) ⇒ ใช้ค่าตั้งต้นได้โดยไม่ถือเป็นหลักฐาน ·
/// ห้ามเดาเงียบ (DECISION_DOCTRINE §1: "ไม่รู้" ต้องเป็นค่า ไม่ใช่ค่าที่แต่งขึ้น)</para>
/// </summary>
public static class SettlementFileDecisions
{
    // ═══════════════ ลำดับวัน/เดือน (R-B9) ═══════════════

    /// <summary>
    /// ลำดับวัน/เดือนของไฟล์: ค่าที่ช่องทางจำไว้/ผู้ใช้เลือก ⇒ ใช้เลย · มีวันที่ส่วนใดเกิน 12 ⇒ พิสูจน์ได้ · ทุกวันที่ไม่ขึ้นกับลำดับ ⇒ วัน/เดือน (ไม่มีผล) ·
    /// ช่วงวันที่ของรอบโอนที่ผู้ใช้ระบุ (<paramref name="periodFrom"/>–<paramref name="periodTo"/>) รับได้แบบเดียว ⇒ พิสูจน์ได้ ·
    /// ไม่งั้น ⇒ <see cref="SettlementFormatException"/> <c>date-order-ambiguous</c>
    /// </summary>
    /// <param name="dates">(เลขแถว, ค่าดิบ) ของคอลัมน์วันที่ทั้งไฟล์</param>
    /// <param name="periodFrom">วันแรกของรอบโอน (วันตามปฏิทินไทย) — null = ไม่ระบุ</param>
    /// <param name="periodTo">วันสุดท้ายของรอบโอน — null = ไม่ระบุ</param>
    public static SettlementFileDecision<SettlementDateOrder> DecideDateOrder(SettlementDateOrder saved,
        IReadOnlyList<(int RowNo, string? Raw)> dates, string? header, DateTime? periodFrom, DateTime? periodTo)
    {
        if (saved != SettlementDateOrder.Auto) return new(saved, false);
        var evidence = SettlementValueParser.DetectDateOrder(dates.Select(d => d.Raw));
        if (evidence != SettlementDateOrder.Auto) return new(evidence, true);
        var sensitive = dates.Where(d => SettlementValueParser.IsOrderSensitive(d.Raw)).ToList();
        if (sensitive.Count == 0) return new(SettlementDateOrder.DayMonthYear, false);

        if (periodFrom != null || periodTo != null)
        {
            bool Fits(SettlementDateOrder o) => sensitive.All(d =>
                SettlementValueParser.TryParseDate(d.Raw, o, out var v) && v is DateTime day
                && (periodFrom == null || day.Date >= periodFrom.Value.Date)
                && (periodTo == null || day.Date <= periodTo.Value.Date));
            var dmy = Fits(SettlementDateOrder.DayMonthYear);
            var mdy = Fits(SettlementDateOrder.MonthDayYear);
            if (dmy != mdy) return new(dmy ? SettlementDateOrder.DayMonthYear : SettlementDateOrder.MonthDayYear, true);
        }

        var (row, raw) = sensitive[0];
        SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, out var asDmy);
        SettlementValueParser.TryParseDate(raw, SettlementDateOrder.MonthDayYear, out var asMdy);
        throw new SettlementFormatException("date-order-ambiguous",
            $"วันที่ในคอลัมน์ \"{header}\" อ่านได้สองแบบ เช่น แถว {row} \"{raw}\" = {Iso(asDmy)} (วัน/เดือน/ปี) หรือ {Iso(asMdy)} (เดือน/วัน/ปี) "
            + $"และทั้งไฟล์ไม่มีวันที่ใดที่เกิน 12 ให้ตัดสิน ({sensitive.Count:N0} แถวได้ผลต่างกัน) — เลือก \"รูปแบบวันที่\" ในขั้นจับคู่คอลัมน์ "
            + "(ระบบจำไว้ให้ช่องทางนี้) หรือระบุช่วงวันที่ของรอบโอนในหัวรอบโอน แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
    }

    // ═══════════════ เขตเวลา (R-B8) ═══════════════

    private static readonly Regex HeaderOffset = new(@"(?:utc|gmt)\s*([+\-])\s*(\d{1,2})(?::?(\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HeaderBareBangkokOffset = new(@"\+\s*0?7(?::?00)?(?!\d)", RegexOptions.Compiled);
    private static readonly Regex HeaderUtc = new(@"(?<![a-z])(?:utc|gmt)(?![a-z])|\(z\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HeaderBangkok = new(@"(?<![a-z])(?:ict|bangkok|thai time|th time)(?![a-z])|เวลาไทย|เวลาประเทศไทย",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// เขตเวลาที่หัวคอลัมน์วันที่ประกาศไว้ ("Created (UTC)" · "เวลา (GMT+7)" · "Time (ICT)") — null = หัวคอลัมน์ไม่บอก ·
    /// ประกาศเขตอื่น (เช่น GMT+8) ⇒ ล้มดัง <c>timezone-unsupported</c> (อ่านเป็นไทยหรือ UTC ก็ผิดทั้งคู่)
    /// </summary>
    public static SettlementFileTimeZone? TimeZoneFromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var m = HeaderOffset.Match(header);
        if (m.Success)
        {
            var hours = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var minutes = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            var total = (m.Groups[1].Value == "-" ? -1 : 1) * (hours * 60 + minutes);
            if (total == 7 * 60) return SettlementFileTimeZone.Bangkok;
            if (total == 0) return SettlementFileTimeZone.Utc;
            throw new SettlementFormatException("timezone-unsupported",
                $"คอลัมน์วันที่ \"{header}\" เป็นเวลาเขต {m.Value.Trim()} — ระบบอ่านได้เฉพาะเวลาไทย (UTC+7) หรือ UTC · ส่งออกรายงานจากแพลตฟอร์มเป็นเวลาไทย "
                + "แล้วนำเข้าใหม่ (อ่านผิดเขต = รายการตกคนละวัน/คนละเดือนภาษี)");
        }
        if (HeaderBareBangkokOffset.IsMatch(header) || HeaderBangkok.IsMatch(header)) return SettlementFileTimeZone.Bangkok;
        if (HeaderUtc.IsMatch(header)) return SettlementFileTimeZone.Utc;
        return null;
    }

    /// <summary>
    /// เขตเวลาของไฟล์: ค่าที่ช่องทางจำไว้/ผู้ใช้เลือก ⇒ ใช้เลย · หัวคอลัมน์ประกาศ ⇒ พิสูจน์ได้ · ไม่มีแถวที่วันที่ขึ้นกับเขตเวลา
    /// (ไม่มีเวลา · มี offset ในตัว · เวลาไม่ข้ามวัน) ⇒ เวลาไทย (ไม่มีผล) · ไม่งั้น ⇒ <see cref="SettlementFormatException"/> <c>timezone-ambiguous</c>
    /// (เดิมถือเป็นเวลาไทยเงียบ ๆ ⇒ รายงาน UTC ขาย 30 ก.ย. 18:00 UTC = 1 ต.ค. ของไทย ตกเดือนภาษีผิด)
    /// </summary>
    public static SettlementFileDecision<SettlementFileTimeZone> DecideTimeZone(SettlementFileTimeZone saved, string? header,
        IReadOnlyList<(int RowNo, string? Raw)> dates, SettlementDateOrder order)
    {
        if (saved != SettlementFileTimeZone.Auto) return new(saved, false);
        if (TimeZoneFromHeader(header) is SettlementFileTimeZone declared) return new(declared, true);
        var sensitive = dates.Where(d => SettlementValueParser.DependsOnZone(d.Raw, order)).ToList();
        if (sensitive.Count == 0) return new(SettlementFileTimeZone.Bangkok, false);
        var (row, raw) = sensitive[0];
        throw new SettlementFormatException("timezone-ambiguous",
            $"คอลัมน์วันที่ \"{header}\" มีเวลา (เช่น แถว {row} \"{raw}\") แต่ไฟล์ไม่บอกว่าเป็นเวลาไทยหรือ UTC — {sensitive.Count:N0} รายการจะตกคนละวัน "
            + "(อาจคนละเดือนภาษี) ขึ้นกับคำตอบ · เลือก \"เขตเวลาในไฟล์\" ในขั้นจับคู่คอลัมน์ (ระบบจำไว้ให้ช่องทางนี้) แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
    }

    // ═══════════════ แถวสรุป (R-B11) ═══════════════

    /// <summary>คำขึ้นต้นของแถวสรุปยอด — ใช้กับไฟล์แบบยาวเท่านั้น (แบบกว้างใช้กติกา "ไม่มีเลขอ้างอิง")</summary>
    private static readonly Regex TotalRow = new(
        @"^(?:รวม|ยอดรวม|ยอดสุทธิ|สรุป|total|subtotal|sub total|grand total|net total|sum|summary)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// แถวนี้เป็นแถวสรุป/แถวรวม (ไม่ใช่รายการ) ไหม — แบบกว้าง: <b>ไม่มีทั้งเลขออเดอร์และเลขรายการ</b> ⇒ ใช่เสมอ (1 แถว = 1 ออเดอร์ ·
    /// เดิมจับเฉพาะช่องแรกขึ้นต้น "รวม/Total" ⇒ แถว "ยอดสุทธิ"/ช่องแรกว่างถูกนำเข้าเป็นรายการ ยอดขาย/ค่าธรรมเนียมซ้ำสองเท่า) ·
    /// แบบยาว: ไม่มีเลขอ้างอิง และ (ช่องแรกขึ้นต้นคำสรุป หรือ ไม่มีทั้งป้ายประเภทและวันที่) — แถวไม่มีเลขที่มีป้าย/วันที่ (เช่น ค่าธรรมเนียมถอนเงิน)
    /// ยังเป็นรายการ
    /// </summary>
    public static bool IsSummaryRow(SettlementFileLayout layout, string? txnId, string? orderId, string? typeLabel, string? date, string? firstCell)
    {
        if (!string.IsNullOrWhiteSpace(txnId) || !string.IsNullOrWhiteSpace(orderId)) return false;
        if (layout == SettlementFileLayout.Wide) return true;
        return TotalRow.IsMatch((firstCell ?? "").Trim())
               || (string.IsNullOrWhiteSpace(typeLabel) && string.IsNullOrWhiteSpace(date));
    }

    // ═══════════════ จุลภาคทศนิยม (R-B10) ═══════════════

    /// <summary>
    /// ไฟล์ CSV ที่คั่นคอลัมน์ด้วย <c>;</c> (สัญญาณรูปแบบยุโรป — จุลภาค = ทศนิยม) และมียอดรูป "1,500" (ไม่มีจุดทศนิยม) โดยไม่มียอดใดพิสูจน์ว่า
    /// ใช้จุลภาคคั่นหลักพัน ("1,500.25") ⇒ คืนข้อความล้มดัง (1,500 หรือ 1.5 — ตัดสินแทนไม่ได้) · ไม่งั้น null
    /// </summary>
    public static string? DecimalCommaAmbiguity(char? csvDelimiter, IReadOnlyList<(int RowNo, string Header, string? Raw)> amounts)
    {
        if (csvDelimiter != ';') return null;
        if (amounts.Any(a => SettlementValueParser.IsCommaGroupedWithDecimal(a.Raw))) return null;
        var hit = amounts.FirstOrDefault(a => SettlementValueParser.IsCommaGroupedWithoutDecimal(a.Raw));
        if (hit.Raw == null) return null;
        return $"ไฟล์คั่นคอลัมน์ด้วย ; (รูปแบบที่ใช้จุลภาคเป็นจุดทศนิยม) และยอดในแถว {hit.RowNo} คอลัมน์ \"{hit.Header}\" = \"{hit.Raw}\" "
            + "อ่านได้ทั้ง 1,500 และ 1.5 — ส่งออกรายงานใหม่ให้ใช้จุด (.) เป็นทศนิยม หรือบันทึกเป็น .xlsx แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)";
    }

    private static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(อ่านไม่ได้)";
}
