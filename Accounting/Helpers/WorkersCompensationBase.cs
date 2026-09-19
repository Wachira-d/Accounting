using System;

namespace Accounting.Helpers;

/// <summary>
/// กองทุนเงินทดแทน (พ.ร.บ.เงินทดแทน พ.ศ. 2537 · แบบ กท.20ก) — **ตัวตั้งตัวเดียว**
/// ของเพดานฐานค่าจ้าง · กรอบอัตรา · และการปัดเศษ
///
/// <para>═══ บั๊กที่ปิด (D6-6 · ยืนยันเองที่ <c>PayrollService.cs:1899-1902</c>) ═══
/// <list type="number">
/// <item><b>ปัดเศษแบบธนาคาร</b> — <c>Math.Round(x, 2)</c> ไม่ระบุ
/// <see cref="MidpointRounding.AwayFromZero"/> ⇒ .005 ปัดลงบ้างขึ้นบ้างตามเลขคู่/คี่
/// (กฎเหล็ก #4 E บังคับ AwayFromZero ทุกจุดที่เป็นเงิน)</item>
/// <item><b>ฐานไม่เฉลี่ยตามวันที่เป็นลูกจ้างจริง</b> — ใช้ <c>emp.BaseSalary</c>
/// เต็มเดือน ⇒ คนเข้างาน 25 ก.ย. หรือลาออกวันที่ 3 ถูกคิดสมทบ**เต็มเดือน**
/// ทั้งที่ค่าจ้างที่จ่ายจริงเป็นเศษเดือน · ฐานของ ปกส. ข้างกันแก้ไปแล้วรอบ D-S3
/// (<c>proratedBaseSalary</c>) เหลือกองทุนเงินทดแทนที่ยังใช้ยอดเต็ม = สองฐาน
/// ที่ควรเป็นตัวเดียวกันตาม ม.5 กลับเล่าคนละเรื่อง</item>
/// <item><b>เพดาน 20,000 เป็น literal ใน <c>PayrollService</c></b> และกรอบอัตรา
/// 0.2–1.0% เป็น literal ใน <c>SettingsService.cs:188-190</c> — ตารางกฎหมาย
/// สองสำเนาคนละไฟล์ (F2 ข้อ 4)</item>
/// </list></para>
///
/// <para>═══ ทิศของความเสียหาย (G5) ═══ คิดฐานเกิน = นายจ้างจ่ายเกิน แต่
/// **เห็นได้จากใบเสร็จ/รายงานประจำปี** · คิดฐานขาด = สปส. ประเมินย้อนหลัง
/// พร้อมเงินเพิ่ม ซึ่งมองไม่เห็นจนถึงวันตรวจ ⇒ ที่นี่ไม่ใช่การเลือกทิศ แต่เป็น
/// การให้ฐาน**ตรงกับค่าจ้างที่จ่ายจริง** ซึ่งเป็นสิ่งที่กฎหมายถามหาอยู่แล้ว</para>
/// </summary>
public static class WorkersCompensationBase
{
    /// <summary>เพดานค่าจ้างต่อคนต่อปีที่ใช้คำนวณเงินสมทบ (พ.ร.บ.เงินทดแทน §44)</summary>
    public const decimal AnnualWageCap = 240_000m;

    /// <summary>เพดานต่อเดือน = <see cref="AnnualWageCap"/> ÷ 12 — ระบบคิดสมทบ
    /// รายเดือน จึงบังคับเพดานรายเดือนแทนการสะสมทั้งปี
    ///
    /// <para>⚠️ ข้อจำกัดที่รู้ตัว: คนที่เงินเดือนไม่สม่ำเสมอ (เช่น 10,000 สิบเอ็ด
    /// เดือน + โบนัสเดือนเดียว 200,000) จะถูกคิดต่างจากการสะสมทั้งปีเล็กน้อย —
    /// ถ้าจะทำให้ตรงเป๊ะต้องเก็บยอดสะสมรายคนต่อปี ซึ่งเป็นงานคนละก้อน
    /// (จดไว้ที่นี่ ไม่ใช่ในโค้ดที่เรียก)</para></summary>
    public const decimal MonthlyWageCap = 20_000m;   // = AnnualWageCap / 12 (มีเทสต์ล็อกไว้)

    /// <summary>กรอบอัตราเงินสมทบตามประเภทกิจการ (0.2% – 1.0%) — นายจ้างฝ่ายเดียว</summary>
    public const decimal MinRatePercent = 0.2m;
    public const decimal MaxRatePercent = 1.0m;

    /// <summary>อัตรานี้อยู่ในกรอบกฎหมายไหม — ผู้เรียกที่จะ**ปฏิเสธการบันทึก**
    /// ต้องใช้ตัวนี้ ไม่ใช่เขียน <c>&gt;= 0.2 &amp;&amp; &lt;= 1.0</c> เอง</summary>
    public static bool IsRateInRange(decimal ratePercent)
        => ratePercent >= MinRatePercent && ratePercent <= MaxRatePercent;

    /// <summary>ฐานค่าจ้างที่ใช้คิดสมทบของงวดหนึ่ง = ค่าจ้างที่จ่ายจริงในงวดนั้น
    /// (เฉลี่ยตามวันที่เป็นลูกจ้างแล้ว) แต่ไม่เกินเพดานรายเดือน · ติดลบ → 0
    ///
    /// <para><c>private</c> โดยเจตนา — ผู้เรียกต้องได้ "เงินสมทบ" ไม่ใช่ "ฐาน"
    /// เพื่อไม่ให้มีใครเอาฐานไปคูณอัตราเองแล้วปัดเศษคนละแบบ</para></summary>
    private static decimal MonthlyBase(decimal wagePaidThisPeriod)
        => Math.Min(Math.Max(0m, wagePaidThisPeriod), MonthlyWageCap);

    /// <summary>เงินสมทบของงวดหนึ่ง — ปัด 2 ตำแหน่งแบบ AwayFromZero
    ///
    /// <para>⚠️ **คิดตามอัตราที่ส่งมาเสมอ แม้อยู่นอกกรอบ 0.2–1.0%** — เดิมเคย
    /// เขียนให้คืน 0 เมื่ออัตรานอกกรอบ แล้วถอนออกเพราะมันคือ "นำส่งขาดแบบเงียบ"
    /// ซึ่งเป็นทิศที่ความเสียหายมองไม่เห็นจนถึงวันที่ สปส. ประเมินย้อนหลัง (G5)
    /// ⇒ ที่ที่ต้องปฏิเสธคือ**หน้าตั้งค่า** ผ่าน <see cref="IsRateInRange"/>
    /// ซึ่งผู้ใช้เห็นทันทีและแก้ได้ ไม่ใช่กลางเครื่องคำนวณเงินเดือน</para></summary>
    public static decimal Contribution(decimal wagePaidThisPeriod, decimal ratePercent)
    {
        if (ratePercent <= 0m) return 0m;
        return Math.Round(MonthlyBase(wagePaidThisPeriod) * ratePercent / 100m,
            2, MidpointRounding.AwayFromZero);
    }
}
