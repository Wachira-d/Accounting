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

    // ══════════════ รอบ 228 — นำส่งเงินสมทบรายปี (TAX_PENALTY_AUDIT §3 ข้อ 2) ══════════════

    /// <summary>แบบนำส่งบนหน้านำส่งรวม — คีย์งวด = (ปีที่จ่ายค่าจ้าง, 12)</summary>
    public const string RemittanceType = "WcfAnnual";
    public const string PayableCode = "21816";
    public const string ExpenseCode = "54121";
    public const string LegalReference = "WCA-46";

    /// <summary>เงินเพิ่มเงินสมทบจ่ายช้า (พ.ร.บ.เงินทดแทน ม.46 · แก้ไขโดย พ.ร.บ.เงินทดแทน (ฉบับที่ 2) พ.ศ. 2561 — เดิม 3%) ร้อยละต่อเดือน · ไม่เกินเงินสมทบที่ค้าง ·
    /// ⚠️ ต้องยืนยันกับผู้ทำบัญชี: อัตรา 2% (หลังแก้ไข 2561) และการนับเศษของเดือน — <b>ข้อมูลเท่านั้น</b>: หน้านำส่งไม่เติมลงช่อง
    /// (<c>Suggestion.SurchargeNeedsConfirmation</c> · ฝ่ายค้าน S4)</summary>
    public const decimal LateSurchargePercentPerMonth = 2m;

    /// <summary>เงินสมทบทั้งปีของลูกจ้างหนึ่งคน — ค่าจ้างทั้งปีที่จ่ายจริง ไม่เกิน <see cref="AnnualWageCap"/> × อัตรา (ม.44) ·
    /// ต่างจากผลรวมรายเดือนเมื่อค่าจ้างไม่สม่ำเสมอ (เดือนที่เกิน 20,000 ถูกตัดรายเดือน แต่ทั้งปียังไม่ถึงเพดาน)</summary>
    public static decimal AnnualContribution(decimal annualWage, decimal ratePercent)
    {
        if (ratePercent <= 0m) return 0m;
        return Math.Round(Math.Min(Math.Max(0m, annualWage), AnnualWageCap) * ratePercent / 100m,
            2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ฐานค่าจ้างของงวดที่ไม่มีค่าจ้างเก็บไว้ (แถวก่อนรอบ 228) — ถอดจากเงินสมทบที่คิดไว้ (ฐานรายเดือนที่ตัดเพดานแล้ว) ·
    /// อัตรา ≤ 0 = 0</summary>
    public static decimal WageFromContribution(decimal contribution, decimal ratePercent)
        => ratePercent <= 0m ? 0m : Math.Round(Math.Max(0m, contribution) * 100m / ratePercent, 2, MidpointRounding.AwayFromZero);

    /// <summary>วันครบกำหนด (ก่อนเลื่อนวันหยุด) ของ "เงินสมทบตามค่าจ้างจริง" ปี <paramref name="contributionYear"/> —
    /// แจ้งค่าจ้างจริง (กท.20ก) + จ่ายส่วนที่ขาดภายในสิ้นเดือนกุมภาพันธ์ของปีถัดไป · ผู้เรียกคือ <see cref="TaxFilingDeadline"/> เท่านั้น ·
    /// ⚠️ เงินสมทบเบื้องต้น (ประมาณการ) ครบกำหนดภายในมกราคมของปีนั้นเอง — ระบบยังไม่รองรับการจ่ายประมาณการ (ต้องยืนยัน)</summary>
    internal static DateTime AnnualDueRaw(int contributionYear) => new DateTime(contributionYear + 1, 3, 1).AddDays(-1);

    /// <summary>เงินเพิ่ม ม.46 ที่เสนอ — <see cref="LateSurchargePercentPerMonth"/> ต่อเดือน (นับเดือนด้วย <see cref="RevenueCodeSurcharge.MonthsLate"/>:
    /// เศษของเดือนนับเป็นหนึ่งเดือน) · ไม่เกินเงินสมทบที่ค้าง · ทันกำหนด/ไม่มียอด ⇒ 0</summary>
    public static (decimal Amount, int Months, bool Capped) LateSurcharge(decimal unpaid, DateTime dueDate, DateTime payDate)
    {
        if (unpaid <= 0m || payDate.Date <= dueDate.Date) return (0m, 0, false);
        var months = RevenueCodeSurcharge.MonthsLate(dueDate, payDate);
        var raw = Math.Round(unpaid * LateSurchargePercentPerMonth / 100m * months, 2, MidpointRounding.AwayFromZero);
        return raw > unpaid ? (unpaid, months, true) : (raw, months, false);
    }

    /// <summary>แผนบรรทัด JE ตอนนำส่งเงินสมทบ (pure · Dr = Cr ทุกกรณี) —
    /// Dr 21816 เท่าที่ยังตั้งค้างจ่ายอยู่ · ส่วนที่เกินค้างจ่าย (เงินสมทบรายปีมากกว่าผลรวมรายเดือน) Dr ค่าใช้จ่าย 54121 ·
    /// การจ่ายที่ปิดยอดทั้งปีแล้วยังเหลือค้างจ่าย (ตั้งรายเดือนเกินรายปี) ⇒ Dr 21816 / Cr 54121 ส่วนเกินคืน</summary>
    /// <summary>คำอธิบายบรรทัดค่าใช้จ่ายที่หักได้ใน JE นำส่ง — <b>ห้ามมี</b> <see cref="LatePaymentPenaltyPolicy.Section65Ter6Marker"/> (ตัวบวกกลับ §65 ตรี(6) นับเฉพาะบรรทัดที่มีเครื่องหมาย)</summary>
    public const string TrueUpLineDescription = "เงินสมทบกองทุนเงินทดแทนรายปีส่วนที่เกินตั้งค้างจ่ายรายเดือน (เพดาน 240,000/คน/ปี)";
    public const string OverAccrualLineDescription = "กลับค้างจ่ายกองทุนเงินทดแทนที่ตั้งรายเดือนเกินเงินสมทบรายปี";

    public sealed record PaymentPlan(decimal DrPayable, decimal DrExpenseTrueUp, decimal ReverseOverAccrual)
    {
        public decimal Cash => DrPayable + DrExpenseTrueUp;
    }

    public static PaymentPlan Plan(decimal annualContribution, decimal accruedInPayroll, decimal remittedBefore, decimal amountNow)
    {
        var accruedLeft = Math.Max(0m, accruedInPayroll - Math.Max(0m, remittedBefore));
        var amt = Math.Max(0m, amountNow);
        var drPayable = Math.Min(amt, accruedLeft);
        var trueUp = amt - drPayable;
        var closesYear = remittedBefore + amt >= annualContribution - 0.005m;
        var overAccrual = closesYear ? Math.Max(0m, accruedLeft - drPayable) : 0m;
        return new PaymentPlan(Math.Round(drPayable, 2, MidpointRounding.AwayFromZero),
            Math.Round(trueUp, 2, MidpointRounding.AwayFromZero),
            Math.Round(overAccrual, 2, MidpointRounding.AwayFromZero));
    }
}
