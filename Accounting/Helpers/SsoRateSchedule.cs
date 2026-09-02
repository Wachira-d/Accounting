namespace Accounting.Helpers;

/// <summary>
/// เพดานค่าจ้างประกันสังคม (มาตรา 33) ตามพระราชกฤษฎีกาปรับเพดานแบบขั้นบันได:
///   • ถึงสิ้นปี 2025 (พ.ศ. 2568): เพดาน 15,000 → สมทบสูงสุด 750/เดือน
///   • 2026–2028 (พ.ศ. 2569–2571): เพดาน 17,500 → สูงสุด 875/เดือน
///   • 2029–2031 (พ.ศ. 2572–2574): เพดาน 20,000 → สูงสุด 1,000/เดือน
///   • 2032+    (พ.ศ. 2575 เป็นต้นไป): เพดาน 23,000 → สูงสุด 1,150/เดือน
/// อัตราสมทบลูกจ้าง/นายจ้างคงที่ 5% (ยกเว้นประกาศลดชั่วคราวเป็นรายงวด —
/// override ได้ผ่านตาราง SsoYearConfigs ต่อบริษัทต่อปี)
/// ค่าในนี้คือ DEFAULT เมื่อบริษัทไม่ได้ตั้ง override; กฎหมายเปลี่ยนอีก
/// ก็เพิ่ม case ที่นี่ หรือผู้ใช้ตั้งค่าเองได้ทันทีโดยไม่ต้องรอ release.
/// </summary>
public static class SsoRateSchedule
{
    /// <summary>Default (ceiling, employeeRate) for a CE year. Buddhist-era
    /// years (≥ 2400) are normalised first so callers can pass either.</summary>
    public static (decimal WageCeiling, decimal Rate) GetDefault(int year)
    {
        var y = year > 2400 ? year - 543 : year;
        return y switch
        {
            >= 2032 => (23_000m, 0.05m),
            >= 2029 => (20_000m, 0.05m),
            >= 2026 => (17_500m, 0.05m),
            _ => (15_000m, 0.05m),
        };
    }

    /// <summary>Max monthly contribution for the year (= ceiling × rate).</summary>
    public static decimal GetMaxContribution(int year)
    {
        var (ceiling, rate) = GetDefault(year);
        return Math.Round(ceiling * rate, 2);
    }

    // ===== ช่วงเดือนที่ override มีผล =====
    // ⚠️ ประกาศลดอัตราสมทบของไทยออกเป็น **ช่วงเดือน** เสมอ (เช่น ลดเหลือ 1%
    // เดือน พ.ค.–ก.ค. 2563, 2.5% เดือน ม.ค.–ก.พ. 2565) ไม่ใช่ทั้งปี — เดิม
    // SsoYearConfig เก็บอัตราเดียวต่อปี ⇒ ผู้ใช้ต้องแก้แถวเดิมกลางปี ซึ่ง
    // **เปลี่ยนอัตราของเดือนที่ยื่นไปแล้วย้อนหลังด้วย** (ไฟล์ สปส.1-10 ที่
    // สร้างใหม่จะไม่ตรงกับที่ยื่นจริง) จึงต้องเก็บเป็นช่วงเดือนตั้งแต่ต้น

    /// <summary>ทำให้ช่วงเดือนอยู่ในกรอบ 1–12 และเรียงถูกทาง
    /// (null/0 = ทั้งปี — แถวเก่าก่อนมีคอลัมน์นี้ต้องแปลว่า "ทั้งปี" เหมือนเดิม)</summary>
    public static (int From, int To) NormalizeRange(int? fromMonth, int? toMonth)
    {
        var f = fromMonth is >= 1 and <= 12 ? fromMonth.Value : 1;
        var t = toMonth is >= 1 and <= 12 ? toMonth.Value : 12;
        return f <= t ? (f, t) : (t, f);
    }

    /// <summary>เดือนนี้อยู่ในช่วงที่ override มีผลหรือไม่</summary>
    public static bool CoversMonth(int? fromMonth, int? toMonth, int month)
    {
        var (f, t) = NormalizeRange(fromMonth, toMonth);
        return month >= f && month <= t;
    }

    /// <summary>สองช่วงทับกันไหม</summary>
    public static bool RangesOverlap(int? aFrom, int? aTo, int? bFrom, int? bTo)
    {
        var (af, at) = NormalizeRange(aFrom, aTo);
        var (bf, bt) = NormalizeRange(bFrom, bTo);
        return af <= bt && bf <= at;
    }

    /// <summary>ความกว้างของช่วง (จำนวนเดือน) — ใช้เป็นลำดับความ**จำเพาะ**
    ///
    /// <para>กติกา: เมื่อหลายแถวครอบเดือนเดียวกัน **ช่วงที่แคบกว่าชนะ** —
    /// ตรงกับรูปที่กฎหมายออกจริง: อัตราปกติทั้งปี (1–12) + ประกาศลดชั่วคราว
    /// เฉพาะบางเดือน (เช่น 5–7) ⇒ ผู้ใช้ตั้งสองแถวได้โดยไม่ต้องตัดปีเป็นสามท่อน
    /// และผลลัพธ์ไม่ขึ้นกับลำดับแถว (ห้าม "ใครมาก่อนชนะ")</para>
    ///
    /// <para>ที่ยัง**ห้าม**คือสองช่วง**กว้างเท่ากัน**ที่ทับกัน (เช่น 1–6 กับ 4–9)
    /// — ตัวนั้นไม่มีเกณฑ์ตัดสิน ต้องให้ผู้ใช้แก้เอง</para></summary>
    public static int SpanWidth(int? fromMonth, int? toMonth)
    {
        var (f, t) = NormalizeRange(fromMonth, toMonth);
        return t - f + 1;
    }

    /// <summary>ช่วงสองอันนี้ "กำกวม" ไหม — ทับกันและกว้างเท่ากัน</summary>
    public static bool RangesAmbiguous(int? aFrom, int? aTo, int? bFrom, int? bTo)
        => RangesOverlap(aFrom, aTo, bFrom, bTo)
           && SpanWidth(aFrom, aTo) == SpanWidth(bFrom, bTo);
}
