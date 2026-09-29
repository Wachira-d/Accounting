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
/// ระบบจำไว้ให้ช่องทางเมื่อผู้นำเข้ามีสิทธิ์ · ข้อความบอกตามจริงผ่าน <see cref="MemoryClause"/>) · กำกวมแต่<b>ไม่มีผล</b> (ทุกวันที่ ≤ 12 และวัน=เดือน · ไม่มีเวลาที่ข้ามวัน) ⇒ ใช้ค่าตั้งต้นได้โดยไม่ถือเป็นหลักฐาน ·
/// ห้ามเดาเงียบ (DECISION_DOCTRINE §1: "ไม่รู้" ต้องเป็นค่า ไม่ใช่ค่าที่แต่งขึ้น)</para>
/// </summary>
public static class SettlementFileDecisions
{
    // ═══════════════ ถามครั้งเดียว + บอกความจริงเรื่อง "จำให้" (ฝ่ายค้าน I-2) ═══════════════

    /// <summary>
    /// ท้ายข้อความถามรูปแบบวันที่/เขตเวลา/จุลภาค — บอกตามจริงว่าค่าที่เลือกจะถูกจำไว้กับช่องทางไหม (ฝ่ายค้าน I-2: เดิมสัญญาว่า "ระบบจำไว้ให้ช่องทางนี้"
    /// ทุกครั้ง ทั้งที่ผู้มีแค่สิทธิ์นำเข้า/คีย์ API จำไม่ได้ ⇒ ถูกถามทุกไฟล์โดยไม่รู้เหตุ)
    /// </summary>
    public static string MemoryClause(SettlementParseContext? context)
    {
        if (context == null) return "(ถ้าผู้นำเข้ามีสิทธิ์ตั้งค่าช่องทาง ระบบจะจำค่าที่เลือกไว้ให้ช่องทางนี้)";
        if (context.WillRemember) return "(ระบบจะจำค่าที่เลือกไว้ให้ช่องทางนี้ — ไฟล์ครั้งหน้าไม่ต้องเลือกอีก)";
        var why = string.IsNullOrWhiteSpace(context.NotRememberedReason)
            ? "ไม่ได้ติ๊ก “จำการจับคู่คอลัมน์นี้ไว้กับช่องทาง”"
            : context.NotRememberedReason.Trim();
        return $"(ครั้งนี้ระบบจะไม่จำค่าที่เลือกไว้กับช่องทาง — {why} · ต้องเลือกทุกครั้งที่นำเข้า จนกว่าผู้มีสิทธิ์ตั้งค่าช่องทางจะนำเข้าด้วยค่านี้"
               + "โดยติ๊กจำไว้ หรือแก้การจับคู่ที่หน้าตั้งค่าช่องทาง)";
    }

    /// <summary>
    /// **ตัดสินลำดับวัน/เดือน + เขตเวลาของไฟล์ในรอบเดียว** (ฝ่ายค้าน I-2 · เดิมถามทีละเรื่อง ⇒ ไฟล์ที่กำกวมทั้งคู่ต้องนำเข้า 3 ครั้ง) —
    /// กำกวมเรื่องใดเรื่องหนึ่งหรือทั้งคู่ ⇒ <see cref="SettlementFormatException"/> ครั้งเดียวที่ถามทุกเรื่องพร้อมกัน
    /// (<c>date-order-ambiguous</c> · <c>timezone-ambiguous</c> · ทั้งคู่ <c>date-order-timezone-ambiguous</c>) ·
    /// รูปแบบวันที่ที่ตั้ง/จำไว้ขัดกับวันที่ในไฟล์ ⇒ ล้มดังทันที (<c>date-order-conflict</c> — ฝ่ายค้าน I-10)
    /// </summary>
    /// <param name="dates">(เลขแถว, ค่าดิบ) ของคอลัมน์วันที่ทั้งไฟล์ (ไม่รวมแถวสรุป)</param>
    public static (SettlementFileDecision<SettlementDateOrder> Order, SettlementFileDecision<SettlementFileTimeZone> Zone) DecideDates(
        SettlementDateOrder savedOrder, SettlementFileTimeZone savedZone, string? header, IReadOnlyList<(int RowNo, string? Raw)> dates,
        SettlementParseContext? context)
    {
        var order = OrderCore(savedOrder, dates, header, context?.PeriodFrom, context?.PeriodTo, out var orderQuestion);
        SettlementDateOrder? decidedOrder = order is SettlementFileDecision<SettlementDateOrder> od ? od.Value : null;
        var zone = ZoneCore(savedZone, header, dates, decidedOrder, out var zoneQuestion);
        if (order is SettlementFileDecision<SettlementDateOrder> o && zone is SettlementFileDecision<SettlementFileTimeZone> z) return (o, z);
        var code = orderQuestion != null && zoneQuestion != null ? "date-order-timezone-ambiguous"
            : orderQuestion != null ? "date-order-ambiguous" : "timezone-ambiguous";
        var asks = new[] { orderQuestion, zoneQuestion }.Where(q => q != null).ToList();
        var pick = orderQuestion != null && zoneQuestion != null
            ? "เลือกทั้ง “รูปแบบวันที่” และ “เขตเวลาในไฟล์” ในขั้นจับคู่คอลัมน์"
            : orderQuestion != null
                ? "เลือก “รูปแบบวันที่” ในขั้นจับคู่คอลัมน์ หรือระบุช่วงวันที่ของรอบโอนในหัวรอบโอน"
                : "เลือก “เขตเวลาในไฟล์” ในขั้นจับคู่คอลัมน์";
        throw new SettlementFormatException(code,
            string.Join(" · ", asks) + $" — {pick} {MemoryClause(context)} แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
    }

    // ═══════════════ ลำดับวัน/เดือน (R-B9) ═══════════════

    /// <summary>
    /// ลำดับวัน/เดือนของไฟล์: ค่าที่ช่องทางจำไว้/ผู้ใช้เลือก ⇒ ใช้เลย (ขัดกับวันที่ในไฟล์ ⇒ ล้มดัง <c>date-order-conflict</c>) ·
    /// มีวันที่ส่วนใดเกิน 12 ⇒ พิสูจน์ได้ · ทุกวันที่ไม่ขึ้นกับลำดับ ⇒ วัน/เดือน (ไม่มีผล) ·
    /// ช่วงวันที่ของรอบโอนที่ผู้ใช้ระบุ (<paramref name="periodFrom"/>–<paramref name="periodTo"/>) รับได้แบบเดียว ⇒ พิสูจน์ได้ ·
    /// ไม่งั้น ⇒ <see cref="SettlementFormatException"/> <c>date-order-ambiguous</c> (ตัวอ่านไฟล์ใช้ <see cref="DecideDates"/> ที่ถามพร้อมเขตเวลา)
    /// </summary>
    /// <param name="dates">(เลขแถว, ค่าดิบ) ของคอลัมน์วันที่ทั้งไฟล์</param>
    /// <param name="periodFrom">วันแรกของรอบโอน (วันตามปฏิทินไทย) — null = ไม่ระบุ</param>
    /// <param name="periodTo">วันสุดท้ายของรอบโอน — null = ไม่ระบุ</param>
    public static SettlementFileDecision<SettlementDateOrder> DecideDateOrder(SettlementDateOrder saved,
        IReadOnlyList<(int RowNo, string? Raw)> dates, string? header, DateTime? periodFrom, DateTime? periodTo)
    {
        var d = OrderCore(saved, dates, header, periodFrom, periodTo, out var question);
        if (d is SettlementFileDecision<SettlementDateOrder> decided) return decided;
        throw new SettlementFormatException("date-order-ambiguous",
            $"{question} — เลือก “รูปแบบวันที่” ในขั้นจับคู่คอลัมน์ หรือระบุช่วงวันที่ของรอบโอนในหัวรอบโอน แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
    }

    private static SettlementFileDecision<SettlementDateOrder>? OrderCore(SettlementDateOrder saved,
        IReadOnlyList<(int RowNo, string? Raw)> dates, string? header, DateTime? periodFrom, DateTime? periodTo, out string? question)
    {
        question = null;
        var evidence = SettlementValueParser.DetectDateOrder(dates.Select(d => d.Raw));
        if (saved != SettlementDateOrder.Auto)
        {
            // I-10: ค่าที่จำไว้ (จากไฟล์/ช่วงวันที่ครั้งก่อน) ขัดกับวันที่ในไฟล์นี้ ⇒ บอกว่าเพราะค่าที่จำไว้ + ทางล้าง
            // (เดิมข้ามการตรวจไฟล์ทั้งหมดแล้วล้มเป็น "อ่านเป็นวันที่ไม่ได้ (ปี 2 หลัก…)" ที่ชี้เหตุผิด)
            if (evidence != SettlementDateOrder.Auto && evidence != saved)
            {
                var (row, raw) = dates.First(x => SettlementValueParser.NumericDayMonthParts(x.Raw) is (int a, int b)
                                                  && (evidence == SettlementDateOrder.DayMonthYear ? a > 12 : b > 12));
                throw new SettlementFormatException("date-order-conflict",
                    $"การจับคู่คอลัมน์ของช่องทางนี้ตั้ง “รูปแบบวันที่” เป็น {OrderLabel(saved)} (ค่าที่ระบบจำไว้จากไฟล์/ช่วงวันที่ครั้งก่อน หรือที่เลือกไว้) "
                    + $"แต่ไฟล์นี้เป็น {OrderLabel(evidence)} — แถว {row} คอลัมน์ “{header}” = “{raw}” อ่านแบบ {OrderLabel(saved)} ไม่ได้ · "
                    + $"ถ้ารูปแบบรายงานของแพลตฟอร์มเปลี่ยน ให้เปลี่ยน “รูปแบบวันที่” ในขั้นจับคู่คอลัมน์เป็น {OrderLabel(evidence)} หรือ “อัตโนมัติ” "
                    + "(ล้างค่าที่จำไว้) แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
            }
            return new(saved, false);
        }
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

        var (r0, raw0) = sensitive[0];
        SettlementValueParser.TryParseDate(raw0, SettlementDateOrder.DayMonthYear, out var asDmy);
        SettlementValueParser.TryParseDate(raw0, SettlementDateOrder.MonthDayYear, out var asMdy);
        question = $"วันที่ในคอลัมน์ “{header}” อ่านได้สองแบบ เช่น แถว {r0} “{raw0}” = {Iso(asDmy)} (วัน/เดือน/ปี) หรือ {Iso(asMdy)} (เดือน/วัน/ปี) "
                   + $"และทั้งไฟล์ไม่มีวันที่ใดที่เกิน 12 ให้ตัดสิน ({sensitive.Count:N0} แถวได้ผลต่างกัน)";
        return null;
    }

    /// <summary>
    /// ลำดับวัน/เดือนที่<b>ตัวอ่านก่อนรอบ 200</b>อาจใช้กับไฟล์นี้ (ฝ่ายค้าน I-1 — คิดคีย์กันซ้ำรุ่นก่อน): ลำดับที่ตัดสินได้วันนี้เสมอ +
    /// วัน/เดือน เมื่อไฟล์ไม่มีหลักฐาน (ตัวอ่านเดิมเดา วัน/เดือน เงียบ ๆ เมื่อการจับคู่เป็น Auto) · ไม่ซ้ำ
    /// </summary>
    public static IReadOnlyList<SettlementDateOrder> LegacyReadOrders(SettlementDateOrder decided, IReadOnlyList<(int RowNo, string? Raw)> dates)
    {
        var orders = new List<SettlementDateOrder> { decided };
        SettlementDateOrder evidence;
        try { evidence = SettlementValueParser.DetectDateOrder(dates.Select(d => d.Raw)); }
        catch (SettlementFormatException) { evidence = decided; }      // ปนกันสองแบบ = ตัวอ่านเดิมก็อ่านทั้งไฟล์ไม่ได้ (ไม่มีคีย์เดิมให้เทียบ)
        if (evidence == SettlementDateOrder.Auto && decided != SettlementDateOrder.DayMonthYear)
            orders.Add(SettlementDateOrder.DayMonthYear);
        return orders;
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
    /// เขตเวลาของไฟล์: ค่าที่ช่องทางจำไว้/ผู้ใช้เลือก ⇒ ใช้เลย (ชนะหัวคอลัมน์ — คำประกาศไม่ใช่หลักฐาน) · หัวคอลัมน์ประกาศ ⇒ พิสูจน์ได้ ·
    /// ไม่มีแถวที่วันที่ขึ้นกับเขตเวลา (ไม่มีเวลา · มี offset ในตัว · เวลาไม่ข้ามวัน) ⇒ เวลาไทย (ไม่มีผล) · ไม่งั้น ⇒ <see cref="SettlementFormatException"/>
    /// <c>timezone-ambiguous</c> (เดิมถือเป็นเวลาไทยเงียบ ๆ ⇒ รายงาน UTC ขาย 30 ก.ย. 18:00 UTC = 1 ต.ค. ของไทย ตกเดือนภาษีผิด)
    /// </summary>
    public static SettlementFileDecision<SettlementFileTimeZone> DecideTimeZone(SettlementFileTimeZone saved, string? header,
        IReadOnlyList<(int RowNo, string? Raw)> dates, SettlementDateOrder order)
    {
        var d = ZoneCore(saved, header, dates, order, out var question);
        if (d is SettlementFileDecision<SettlementFileTimeZone> decided) return decided;
        throw new SettlementFormatException("timezone-ambiguous",
            $"{question} — เลือก “เขตเวลาในไฟล์” ในขั้นจับคู่คอลัมน์ แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)");
    }

    /// <param name="order">ลำดับวัน/เดือนที่ตัดสินแล้ว — null = ยังกำกวม (ตรวจว่าข้ามวันไหมทั้งสองแบบ — ส่วนเวลาไม่ขึ้นกับลำดับวัน/เดือน)</param>
    private static SettlementFileDecision<SettlementFileTimeZone>? ZoneCore(SettlementFileTimeZone saved, string? header,
        IReadOnlyList<(int RowNo, string? Raw)> dates, SettlementDateOrder? order, out string? question)
    {
        question = null;
        // ค่าที่ตั้งไว้ชนะหัวคอลัมน์ — หัวคอลัมน์เป็นแค่ "คำประกาศ" ไม่ใช่หลักฐานทางคณิต (ผู้ใช้ที่รู้ว่าหัวคอลัมน์ผิดต้องมีทางไปต่อ ไม่ใช่ทางตัน) ·
        // ต่างจากลำดับวัน/เดือนที่ไฟล์พิสูจน์ได้ว่าค่าที่จำไว้อ่านไม่ได้ (date-order-conflict)
        if (saved != SettlementFileTimeZone.Auto) return new(saved, false);
        if (TimeZoneFromHeader(header) is SettlementFileTimeZone proven) return new(proven, true);
        var sensitive = dates.Where(d => order is SettlementDateOrder o
            ? SettlementValueParser.DependsOnZone(d.Raw, o)
            : SettlementValueParser.DependsOnZone(d.Raw, SettlementDateOrder.DayMonthYear)
              || SettlementValueParser.DependsOnZone(d.Raw, SettlementDateOrder.MonthDayYear)).ToList();
        if (sensitive.Count == 0) return new(SettlementFileTimeZone.Bangkok, false);
        var (row, raw) = sensitive[0];
        question = $"คอลัมน์วันที่ “{header}” มีเวลา (เช่น แถว {row} “{raw}”) แต่ไฟล์ไม่บอกว่าเป็นเวลาไทยหรือ UTC — {sensitive.Count:N0} รายการจะตกคนละวัน "
                   + "(อาจคนละเดือนภาษี) ขึ้นกับคำตอบ";
        return null;
    }

    // ═══════════════ แถวสรุป (R-B11 · DECISIONS ข้อ 39) ═══════════════

    /// <summary>คำขึ้นต้นของแถวสรุปยอด</summary>
    private static readonly Regex TotalRow = new(
        @"^(?:รวม|ยอดรวม|ยอดสุทธิ|สรุป|total|subtotal|sub total|grand total|net total|sum|summary)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// แถวนี้<b>พิสูจน์ได้</b>ว่าเป็นแถวสรุป/แถวรวม (ไม่ใช่รายการ) ไหม — มีเลขอ้างอิง ⇒ ไม่ใช่เสมอ · ช่องแรกขึ้นต้นคำสรุป ("รวม/Total/ยอดสุทธิ") ⇒ ใช่ ·
    /// <b>แบบกว้าง</b> (DECISIONS ข้อ 39 · ฝ่ายค้าน I-7): ไม่มีวันที่ (ไฟล์ที่ไม่มีคอลัมน์วันที่ก็นับ — แถวจริงตามข้อ 39 ต้อง "มีวันที่") หรือ ยอดทุกคอลัมน์เท่าผลรวมของแถวที่มีเลข
    /// (<paramref name="totalsIdRows"/> — <see cref="TotalsIdRows"/>) ⇒ ใช่ · แถวไม่มีเลขที่มีวันที่และยอดไม่ใช่ผลรวม = <b>รายการจริง</b>
    /// (แถวปรับปรุง/แคมเปญ · xlsx ที่ merge เซลล์เลขออเดอร์) ⇒ ผู้อ่านนำเข้าพร้อมคำเตือน · เดิมข้ามทุกแถวที่ไม่มีเลข ⇒ ยอดจริงหายแล้วถูกพาไปลง
    /// "ปรับปรุงอื่น" (VAT/WHT หาย) · <b>แบบยาว</b>: ไม่มีทั้งป้ายประเภทและวันที่ ⇒ ใช่ — แถวไม่มีเลขที่มีป้าย/วันที่ (ค่าธรรมเนียมถอนเงิน) ยังเป็นรายการ
    /// </summary>
    /// <param name="totalsIdRows">ยอดทุกคอลัมน์ของแถวนี้เท่าผลรวมของแถวที่มีเลข (<see cref="TotalsIdRows"/>) — ใช้กับแบบกว้าง</param>
    public static bool IsSummaryRow(SettlementFileLayout layout, string? txnId, string? orderId, string? typeLabel, string? date, string? firstCell,
        bool totalsIdRows)
    {
        if (!string.IsNullOrWhiteSpace(txnId) || !string.IsNullOrWhiteSpace(orderId)) return false;
        if (TotalRow.IsMatch((firstCell ?? "").Trim())) return true;
        if (layout == SettlementFileLayout.Wide)
            return string.IsNullOrWhiteSpace(date) || totalsIdRows;
        return string.IsNullOrWhiteSpace(typeLabel) && string.IsNullOrWhiteSpace(date);
    }

    /// <summary>
    /// ยอดของแถว (ทีละคอลัมน์ยอดเงิน · ค่าดิบในไฟล์) เท่ากับผลรวมของคอลัมน์เดียวกันจากแถวที่มีเลขอ้างอิงทุกคอลัมน์ไหม — หลักฐานว่าเป็นแถวรวม (DECISIONS ข้อ 39) ·
    /// ต้องมีแถวที่มีเลข ≥ 2 แถว (มีแถวเดียว ⇒ แถวสินค้าแถวที่สองของออเดอร์เดียวกันที่ยอดเท่ากันก็ "เท่าผลรวม" — ห้ามถือเป็นหลักฐาน) ·
    /// แถวที่ยอดเป็นศูนย์/ว่างทั้งแถว ⇒ ไม่ใช่
    /// </summary>
    /// <param name="row">ยอดของแถวที่ตรวจ ทีละคอลัมน์ (null = ว่าง = 0)</param>
    /// <param name="idRows">ยอดของแถวที่มีเลขอ้างอิง ทีละคอลัมน์ลำดับเดียวกัน (null = ว่าง = 0)</param>
    public static bool TotalsIdRows(IReadOnlyList<decimal?> row, IReadOnlyList<IReadOnlyList<decimal?>> idRows)
    {
        if (idRows.Count < 2 || row.Count == 0 || row.All(v => (v ?? 0m) == 0m)) return false;
        for (var c = 0; c < row.Count; c++)
        {
            var col = c;
            var sum = idRows.Sum(r => col < r.Count ? r[col] ?? 0m : 0m);
            if ((row[col] ?? 0m) != sum) return false;
        }
        return true;
    }

    /// <summary>
    /// ไฟล์แบบกว้างที่คอลัมน์เลขออเดอร์/เลขรายการ<b>ว่างทุกแถว</b> ⇒ ข้อความล้มดังที่ชี้เหตุจริง (ฝ่ายค้าน I-6: เดิมทุกแถวกลายเป็น "แถวสรุป" แล้วล้มด้วย
    /// "ไม่พบรายการที่มียอดเงิน — ตรวจการจับคู่คอลัมน์ยอดเงิน" ซึ่งชี้ผิดคอลัมน์) · มีเลขอย่างน้อยหนึ่งแถว/แบบยาว/ไม่มีแถว ⇒ null
    /// </summary>
    public static string? WideIdColumnsEmpty(SettlementFileLayout layout, string? orderHeader, string? txnHeader,
        IReadOnlyList<(string? TxnId, string? OrderId)> rows)
    {
        if (layout != SettlementFileLayout.Wide || rows.Count == 0) return null;
        if (rows.Any(r => !string.IsNullOrWhiteSpace(r.TxnId) || !string.IsNullOrWhiteSpace(r.OrderId))) return null;
        var cols = new[] { orderHeader, txnHeader }.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => $"“{h}”").ToList();
        return $"คอลัมน์เลขออเดอร์/เลขรายการ {string.Join(" และ ", cols)} ว่างทุกแถวของไฟล์ ({rows.Count:N0} แถว) — ไฟล์แบบกว้างต้องมีเลขออเดอร์ทุกแถว "
               + "(ใช้กันนำเข้าซ้ำและจับคู่ใบขาย) · น่าจะจับคู่คอลัมน์เลขออเดอร์ผิดคอลัมน์ ตรวจในขั้นจับคู่คอลัมน์แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)";
    }

    // ═══════════════ จุลภาคทศนิยม (R-B10) ═══════════════

    /// <summary>
    /// ไฟล์ CSV ที่คั่นคอลัมน์ด้วย <c>;</c> (สัญญาณรูปแบบยุโรป — จุลภาค = ทศนิยม) และมียอดรูป "1,500" (ไม่มีจุดทศนิยม) โดยไม่มียอดใดพิสูจน์ว่า
    /// ใช้จุลภาคคั่นหลักพัน ("1,500.25") ⇒ คืนข้อความล้มดัง (1,500 หรือ 1.5 — ตัดสินแทนไม่ได้) · ผู้ใช้ยืนยันแล้วว่าจุลภาค = คั่นหลักพัน
    /// (<paramref name="commaIsThousands"/> · ฝ่ายค้าน I-5) ⇒ null · ไม่งั้น null
    /// </summary>
    public static string? DecimalCommaAmbiguity(char? csvDelimiter, IReadOnlyList<(int RowNo, string Header, string? Raw)> amounts,
        bool commaIsThousands = false, SettlementParseContext? context = null)
    {
        if (csvDelimiter != ';' || commaIsThousands) return null;
        if (amounts.Any(a => SettlementValueParser.IsCommaGroupedWithDecimal(a.Raw))) return null;
        var hit = amounts.FirstOrDefault(a => SettlementValueParser.IsCommaGroupedWithoutDecimal(a.Raw));
        if (hit.Raw == null) return null;
        // I-5: ทางหลัก = ยืนยันความหมายของจุลภาค หรือ .xlsx — เดิมทางแรกคือ "ส่งออกให้ใช้จุดทศนิยม" ซึ่งทำไม่ได้กับไฟล์ที่ยอดเป็นบาทเต็ม
        return $"ไฟล์คั่นคอลัมน์ด้วย ; (รูปแบบที่ใช้จุลภาคเป็นจุดทศนิยม) และยอดในแถว {hit.RowNo} คอลัมน์ “{hit.Header}” = “{hit.Raw}” "
               + "อ่านได้ทั้ง 1,500 และ 1.5 — ถ้าจุลภาคในไฟล์นี้คือคั่นหลักพัน (ยอดเป็นบาทเต็ม) ให้ติ๊ก “จุลภาคในยอด = คั่นหลักพัน” ในขั้นจับคู่คอลัมน์ "
               + $"{MemoryClause(context)} · หรือเปิดไฟล์แล้วบันทึกเป็น .xlsx แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า)";
    }

    private static string OrderLabel(SettlementDateOrder o) => o == SettlementDateOrder.MonthDayYear ? "เดือน/วัน/ปี" : "วัน/เดือน/ปี";

    private static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(อ่านไม่ได้)";
}
