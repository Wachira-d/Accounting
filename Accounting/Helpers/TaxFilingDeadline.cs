using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กำหนดเวลายื่นแบบรายเดือน — **ตารางเดียวของทั้งระบบ**
///
/// <para><b>ที่มา</b>: เรพนี้เคยมีตาราง 3 ชุด (<c>StatutoryRemittanceService.DueDates</c> ·
/// <c>TaxCalendarService</c> · <c>TaxComplianceChecker.DeadlineFor</c>) —
/// เลขวันของสองตัวแรกถูกตามกฎหมาย แต่ตัวที่สามเหมา <b>ภ.พ.36 ไปรวมกับ ภ.พ.30</b>
/// ⇒ ได้วันที่ 23 แทนที่จะเป็น 15 = <b>เตือนช้ากว่ากำหนดจริง 8 วัน</b> ·
/// และปฏิทินภาษีไม่มี <b>ภ.ง.ด.54</b> อยู่ในลิสต์เลย ⇒ เดือนที่ต้องยื่น ม.70
/// ไม่มีอะไรเตือน</para>
///
/// <para><b>ฐานกฎหมาย</b>
/// <list type="bullet">
/// <item>ภ.พ.30 — ป.รัษฎากร §83 วรรคสอง: ภายในวันที่ 15 ของเดือนถัดไป</item>
/// <item>ภ.พ.36 — §83/6: ภายใน 7 วันนับแต่วันสิ้นเดือนที่จ่าย</item>
/// <item>ภ.ง.ด.1/3/53 — §52 · §59 + ท.ป.4/2528: ภายใน 7 วันนับแต่วันสิ้นเดือน</item>
/// <item>ภ.ง.ด.54 — §70: ภายใน 7 วันนับแต่วันสิ้นเดือนที่จ่าย</item>
/// <item>สปส.1-10 — พ.ร.บ.ประกันสังคม 2533 §47: ภายในวันที่ 15 ของเดือนถัดไป</item>
/// <item>เลื่อนวันหยุด — <b>ป.พ.พ. §193/8</b>: วันสุดท้ายตรงวันหยุด ให้นับวันทำการถัดไป</item>
/// </list></para>
/// </summary>
public static class TaxFilingDeadline
{
    /// <summary>
    /// มาตรการขยายเวลายื่นผ่านอินเทอร์เน็ต (ประกาศกระทรวงการคลังฯ) — <b>ชั่วคราว
    /// และมีวันหมดอายุ</b> (CLAUDE.md §F ระบุว่าขยายถึง 31 ม.ค. 2570)
    ///
    /// <para>จงใจ <b>ไม่</b> ผูกวันหมดอายุไว้ในโค้ด: ถ้าใส่วันตัดแล้วมาตรการถูกต่ออายุ
    /// ระบบจะขึ้น "เลยกำหนด" ให้งวดที่ยื่นทันจริง — คำเตือนที่ฟ้องของถูกคือการปิดด่าน
    /// (กฎ CLAUDE.md). ให้จดไว้ที่เดียวตรงนี้แล้วทบทวนเมื่อประกาศเปลี่ยน</para>
    /// </summary>
    public const int EFilingExtraDays = 8;

    /// <summary>วันครบกำหนดยื่น "แบบกระดาษ" ของเดือนถัดจากงวด (ก่อนเลื่อนวันหยุด)</summary>
    private static int PaperDay(string remittanceType) => remittanceType switch
    {
        "VatPp30" => 15,       // §83 วรรคสอง
        "SsoSps110" => 15,     // พ.ร.บ.ประกันสังคม §47
        // สปส.6-09 แจ้งสิ้นสุดความเป็นผู้ประกันตน — วันที่ 15 ของเดือนถัดจากเดือนที่ออก
        // (เดิม PayrollService คิดเองว่า `new DateTime(y,m,15).AddMonths(1)` โดยไม่เลื่อน
        // วันหยุด = ตารางชุดที่ 5 ที่ checker มองไม่เห็นเพราะซ่อนในเมธอดชื่อ Terminate…)
        "SsoSps609" => 15,
        _ => 7,                // ภ.พ.36 §83/6 · ภ.ง.ด.ทุกตัว §52/§59/§70
    };

    /// <summary>ยื่นผ่านอินเทอร์เน็ตได้ไหม — <b>สปส. ไม่อยู่ในมาตรการของกระทรวงการคลัง</b>
    /// (คนละหน่วยงาน) จึงไม่ได้ +8 วัน</summary>
    private static bool HasEFilingExtension(string remittanceType)
        => !remittanceType.StartsWith("Sso", StringComparison.Ordinal);

    /// <summary>
    /// (กระดาษ, e-Filing) ของงวด <paramref name="year"/>/<paramref name="month"/>
    /// — เลื่อนพ้นวันหยุดแล้วทั้งคู่
    ///
    /// <para>⚠️ เลื่อนเฉพาะ <b>เสาร์/อาทิตย์</b> เพราะเรพนี้ยังไม่มีตารางวันหยุดราชการ
    /// (เป็น backlog ใน SYSTEM_REVIEW) ⇒ งวดที่วันครบกำหนดตรงวันหยุดนักขัตฤกษ์จะยัง
    /// ขึ้น "เลยกำหนด" เร็วไป 1 วัน — ทิศนี้ปลอดภัยกว่าการเลื่อนเกินจริง แต่ยังไม่จบ</para>
    /// </summary>
    public static (DateTime Paper, DateTime EFiling) For(string remittanceType, int year, int month)
    {
        var next = new DateTime(year, month, 1).AddMonths(1);
        var day = Math.Min(PaperDay(remittanceType), DateTime.DaysInMonth(next.Year, next.Month));
        var paperRaw = new DateTime(next.Year, next.Month, day);

        // ⚠️ e-Filing นับจากวันครบกำหนด **ก่อนเลื่อน** — เดิมปฏิทินภาษีเลื่อนกระดาษ
        // ก่อนแล้วค่อย +8 ⇒ งวดที่วันที่ 7 ตรงเสาร์ ได้ e-Filing วันที่ 17 ซึ่ง
        // **ช้ากว่าที่กฎหมายให้ 2 วัน** (กำหนดจริงคือวันที่ 15 แล้วค่อยเลื่อน)
        var eFilingRaw = HasEFilingExtension(remittanceType)
            ? paperRaw.AddDays(EFilingExtraDays)
            : paperRaw;

        return (RollToBusinessDay(paperRaw), RollToBusinessDay(eFilingRaw));
    }

    /// <summary>ป.พ.พ. §193/8 — เลื่อน<b>ไปข้างหน้า</b>เท่านั้น ห้ามถอยหลัง</summary>
    public static DateTime RollToBusinessDay(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(2),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    /// <summary>map จาก <see cref="TaxType"/> → คีย์ของตารางนี้ · null = ไม่ใช่แบบรายเดือน</summary>
    public static string? KeyOf(TaxType type) => type switch
    {
        TaxType.VAT => "VatPp30",
        TaxType.VatPp36 => "VatPp36",
        TaxType.WithholdingTax1 => "WhtPnd1",
        TaxType.WithholdingTax3 => "WhtPnd3",
        TaxType.WithholdingTax53 => "WhtPnd53",
        TaxType.WithholdingTax54 => "WhtPnd54",
        TaxType.SocialSecurity => "SsoSps110",
        _ => null,
    };

    /// <summary>กำหนดยื่น e-Filing ของ <see cref="TaxType"/> — null เมื่อไม่ใช่แบบรายเดือน</summary>
    public static DateTime? EFilingFor(TaxType type, int year, int month)
    {
        if (year < 2018 || month is < 1 or > 12) return null;
        var key = KeyOf(type);
        return key == null ? null : For(key, year, month).EFiling;
    }
}
