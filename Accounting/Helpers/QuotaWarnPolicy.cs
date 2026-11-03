namespace Accounting.Helpers;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.6 · คำตัดสินข้อ 231-5) — <b>เกณฑ์เตือน "ใกล้เต็ม" ตัวเดียวของทุกทรัพยากร</b> (pure)
///
/// <para>เดิมฝัง 3 ชุด (เอกสาร 80 · หลอด usage.html 70/90 · โควตาเสริม 80) แอดมินตั้งไม่ได้ · ตอนนี้ค่าตั้ง <c>PlatformBillingSettings.QuotaWarnPercents</c>
/// (ตั้งต้น <see cref="DefaultPercents"/> = "80,90,100") เป็นตัวตั้ง — ผู้อ่านรับ <c>IReadOnlyList&lt;int&gt;</c> จาก <see cref="Parse"/> · ไม่ส่ง = ค่าตั้งต้น</para>
/// <list type="bullet">
/// <item><see cref="Severity"/> 0 ปกติ · 1 ใกล้เต็ม (ข้ามเกณฑ์ใดเกณฑ์หนึ่งที่ &lt; 100) · 2 เต็ม/เกิน (ใช้ ≥ เพดาน) — สัญญาเดิมของหน้าเว็บ (WarnLevel 0/1/2) คงอยู่</item>
/// <item><see cref="HighestCrossed"/> = เกณฑ์ % สูงสุดที่ใช้ไปถึงแล้ว (0 = ยังไม่ถึง) — ใช้เป็น "ระดับ" ของการแจ้งครั้งเดียวต่อ (บริษัท·ทรัพยากร·เกณฑ์·เดือน)</item>
/// </list>
/// </summary>
public static class QuotaWarnPolicy
{
    public const string DefaultPercents = "80,90,100";
    public const int MaxThresholds = 5;

    public static readonly IReadOnlyList<int> Default = Parse(DefaultPercents);

    /// <summary>อ่านรายการ % แบบผ่อนปรน (ค่าที่เก็บไว้แล้ว): ตัดตัวที่อ่านไม่ได้/นอก 1–100 · ไม่ซ้ำ · เรียงน้อยไปมาก · ไม่เกิน <see cref="MaxThresholds"/> · ว่างทั้งหมด = ค่าตั้งต้น</summary>
    public static IReadOnlyList<int> Parse(string? raw)
    {
        var list = (raw ?? "").Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, out var v) ? v : -1)
            .Where(v => v >= 1 && v <= 100)
            .Distinct().OrderBy(v => v).Take(MaxThresholds).ToList();
        if (list.Count == 0 && raw != DefaultPercents) return Parse(DefaultPercents);
        return list;
    }

    /// <summary>ตรวจค่าที่แอดมินกรอก (เข้ม) — คืนข้อความไทยเมื่อผิด · null = ผ่าน · <paramref name="normalized"/> = รูปที่จัดแล้ว</summary>
    public static string? Validate(string? raw, out string normalized)
    {
        normalized = "";
        var tokens = (raw ?? "").Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return "เกณฑ์เตือนโควตา: ต้องมีอย่างน้อย 1 ค่า (เช่น 80,90,100)";
        var list = new List<int>();
        foreach (var t in tokens)
        {
            if (!int.TryParse(t, out var v) || v < 1 || v > 100) return $"เกณฑ์เตือนโควตา: \"{t}\" ไม่ถูกต้อง — ใส่ตัวเลข 1–100 คั่นด้วยจุลภาค";
            if (!list.Contains(v)) list.Add(v);
        }
        if (list.Count > MaxThresholds) return $"เกณฑ์เตือนโควตา: ได้ไม่เกิน {MaxThresholds} ค่า";
        normalized = string.Join(",", list.OrderBy(v => v));
        return null;
    }

    /// <summary>% ที่ใช้ไป (ปัดลงเป็นจำนวนเต็ม · เพดาน ≤ 0 = ไม่จำกัด ⇒ 0)</summary>
    public static int PercentUsed(long used, long? limit)
    {
        if (limit is not long max || max <= 0) return 0;
        if (used <= 0) return 0;
        var p = used * 100L / max;
        return p > int.MaxValue ? int.MaxValue : (int)p;
    }

    /// <summary>เกณฑ์ % สูงสุดที่ใช้ไปถึงแล้ว · 0 = ยังไม่ถึงเกณฑ์ใด · ไม่จำกัด = 0 · ใช้ ≥ เพดาน ⇒ 100 (เต็มต้องนับเป็นเต็มแม้รายการไม่มี 100) ·
    /// ผู้อ่านภายนอกใช้ผ่าน <see cref="Crossed"/> (ข้ามจาก 0 = ค่านี้) / <see cref="DueAfterWarned"/></summary>
    private static int HighestCrossed(long used, long? limit, IReadOnlyList<int>? percents = null)
    {
        var th = percents is { Count: > 0 } ? percents : Default;
        if (limit is not long max || max <= 0) return 0;
        if (used >= max) return 100;   // ฝ่ายค้านรอบ 231 N4: เต็ม = 100 เสมอ (ไม่ใช่เกณฑ์สูงสุดในรายการ — รายการ "70,90" เต็มต้องเป็น 100 ไม่ใช่ 90)
        var pct = PercentUsed(used, limit);
        var crossed = 0;
        foreach (var t in th) if (pct >= t && t < 100) crossed = t;
        return crossed;
    }

    /// <summary>0 ปกติ · 1 ใกล้เต็ม · 2 เต็ม/เกิน — สัญญาเดิมของหลอด/ป้ายบนหน้าเว็บ (ไม่จำกัด = 0)</summary>
    public static int Severity(long used, long? limit, IReadOnlyList<int>? percents = null)
    {
        if (limit is not long max) return 0;
        if (max <= 0 || used >= max) return 2;
        return HighestCrossed(used, limit, percents) > 0 ? 1 : 0;
    }

    /// <summary>เกณฑ์ที่<b>เพิ่งข้าม</b> (ก่อน → หลัง) · 0 = ไม่ข้ามเกณฑ์ใหม่ — ตัวนับเพิ่มทางเดียวทั้งเดือน ⇒ แต่ละเกณฑ์ถูกแจ้งครั้งเดียว</summary>
    public static int Crossed(long usedBefore, long? limitBefore, long usedAfter, long? limitAfter, IReadOnlyList<int>? percents = null)
    {
        var after = HighestCrossed(usedAfter, limitAfter, percents);
        return after > HighestCrossed(usedBefore, limitBefore, percents) ? after : 0;
    }

    /// <summary>เกณฑ์ที่ต้องแจ้งเมื่อ "เคยแจ้งไปแล้วถึง <paramref name="warnedPercent"/>" (งานรายวันของ Capacity — ค่าลดได้ จึงใช้เครื่องหมายสูงสุดต่อเดือน) · 0 = ไม่ต้องแจ้ง</summary>
    public static int DueAfterWarned(long used, long? limit, int warnedPercent, IReadOnlyList<int>? percents = null)
    {
        var now = HighestCrossed(used, limit, percents);
        return now > warnedPercent ? now : 0;
    }

    /// <summary>หัวข้อแจ้งเตือน (ทุกทรัพยากร) — ข้อความเนื้อหาผู้เรียกประกอบเอง (เอกสาร: <c>DocumentQuotaPolicy.CrossedMessage</c> · งานรายวัน: <c>UsageSnapshotService</c>)</summary>
    public static string Title(string label, int percent) => percent >= 100 ? $"โควตา{label}เดือนนี้เต็มแล้ว" : $"โควตา{label}ใกล้เต็ม ({percent}%)";
}
