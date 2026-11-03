namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 — <b>ตัวตัดสินเดียวของ "วันหมดอายุที่ไม่มีจริง" (open-ended)</b> · pure (เทสต์ได้)
///
/// <para>ที่มา (บั๊กจริง): หน้า "แพ็กเกจและการชำระเงิน" แสดง "ใช้ได้ถึง 2 เม.ย. <b>2669</b> · ออกใบแจ้งหนี้ต่ออายุ 18 มี.ค. 2669" —
/// ไม่ใช่การ +543 ซ้ำ: ค่าในฐานคือ ค.ศ. 2126 (= <c>UtcNow.AddYears(100)</c> ที่สามจุดเขียนลง <c>Subscription.EndDate</c>
/// เป็นค่าแทน "ไม่หมดอายุ" ของแพ็กเกจฟรีถาวร/stub) แล้ว <c>Layout.date</c> (locale <c>th-TH</c>) แปลงเป็น พ.ศ. 2669 อย่างถูกต้อง ·
/// ค่าแทนนี้หลุดถึงผู้ใช้เมื่อ <c>IsPermanentFree</c> ถูกถอด (resync แม่แบบที่เลิกฟรีถาวร) หรือ stub ของ <c>CompanyService</c>
/// ที่ไม่เคยติดธง — ฝั่งอ่านทุกที่ตัดสินจากธงอย่างเดียว ไม่ได้มองค่าแทน</para>
///
/// <para>กติกา: <b>เปรียบเทียบเป็น ค.ศ. UTC เท่านั้น</b> (ห้าม +543 ก่อนเทียบ — พ.ศ. มีไว้แสดงบนจอ/แบบยื่นอย่างเดียว ผ่าน
/// <c>Layout.date</c>/<see cref="ThaiDate"/> ตัวเดียว) · ค่าแทน "ไม่หมดอายุ" เขียนผ่าน <see cref="OpenEnded"/> ตัวเดียว ·
/// ฝั่งอ่านทุกจอ (ผู้เช่า/แอดมิน) ถาม <see cref="IsOpenEnded"/> แล้วแสดง "ไม่หมดอายุ" และ<b>ไม่มี</b>วันออกใบต่ออายุ
/// แทนที่จะพิมพ์ปี 2669</para>
/// </summary>
public static class SubscriptionExpiry
{
    /// <summary>ค่าแทน "ไม่หมดอายุ" = ตอนนี้ + 100 ปี (ตัวเขียนเดียว — เดิมมี 3 สำเนา)</summary>
    public const int OpenEndedYears = 100;

    /// <summary>เกณฑ์ตัดสินว่า EndDate เป็นค่าแทน: ไกลกว่า 50 ปีจาก now — ไม่มีแพ็กเกจที่ขายจริงยาวขนาดนั้น (รายปีสูงสุด 1 ปี · ขยายทดลองใช้ถูกจำกัดโดย
    /// <c>TrialExtensionPolicy</c>) · ใช้ครึ่งหนึ่งของ <see cref="OpenEndedYears"/> เพื่อให้ค่าแทนที่เขียนไว้หลายสิบปีก่อนยังถูกจำแนกถูก</summary>
    public const int OpenEndedThresholdYears = 50;

    /// <summary>ค่าที่เขียนลง <c>EndDate</c> เมื่อแพ็กเกจไม่มีวันหมดอายุ (ฟรีถาวร / stub)</summary>
    public static DateTime OpenEnded(DateTime nowUtc) => nowUtc.AddYears(OpenEndedYears);

    /// <summary>EndDate นี้คือ "ไม่หมดอายุ" ไหม — ตัดสินจาก<b>ค่า</b> (ไม่ใช่ธง) เพื่อให้แถวที่ธงหลุดยังแสดงถูก · เทียบ UTC ค.ศ. ล้วน</summary>
    public static bool IsOpenEnded(DateTime endUtc, DateTime nowUtc)
        => endUtc >= ThresholdFrom(nowUtc);

    /// <summary>ขอบล่างของค่าแทน (สำหรับ projection ใน EF ที่เรียกเมธอดไม่ได้: <c>EndDate &gt;= ThresholdFrom(now)</c> = สูตรเดียวกับ <see cref="IsOpenEnded(DateTime, DateTime)"/>)</summary>
    public static DateTime ThresholdFrom(DateTime nowUtc) => nowUtc.AddYears(OpenEndedThresholdYears);

    /// <summary>ไม่หมดอายุ = ธงฟรีถาวร <b>หรือ</b> ค่าแทนในฐาน (ฝั่งอ่านทุกจอใช้ตัวนี้)</summary>
    public static bool IsOpenEnded(bool isPermanentFree, DateTime endUtc, DateTime nowUtc)
        => isPermanentFree || IsOpenEnded(endUtc, nowUtc);

    /// <summary>วันหมดอายุที่แสดงให้ผู้ใช้: null = "ไม่หมดอายุ" (หน้าเว็บห้ามพิมพ์ปี 2669)</summary>
    public static DateTime? DisplayEnd(bool isPermanentFree, DateTime endUtc, DateTime nowUtc)
        => IsOpenEnded(isPermanentFree, endUtc, nowUtc) ? null : endUtc;

    /// <summary>ฝ่ายค้าน 232-hub B1(ก): ปิดสถานะ "ไม่มีรอบ" เมื่อธงฟรีถาวรถูกถอด (resync แม่แบบ · เปลี่ยนแพ็กเกจ) — ตัวตั้งเดียว:
    /// EndDate = now + 1 รอบของ BillingCycle (<see cref="PlatformPlanPricing.MonthsOf"/> · รอบที่ไม่รู้จัก = 1 เดือน) · NextBillingDate = EndDate</summary>
    public static (DateTime EndDate, DateTime NextBillingDate) CloseOpenEnded(DateTime nowUtc, Models.Enums.BillingCycle cycle)
    {
        var months = PlatformPlanPricing.MonthsOf(cycle);
        var end = nowUtc.AddMonths(months > 0 ? months : 1);
        return (end, end);
    }

    /// <summary>ฝ่ายค้าน 232-hub B1(ข): แถวที่ต้องให้แอดมินตรวจ = แผนจ่ายเงิน (ไม่ใช่ FreeTrial · ธงฟรีถาวร false) แต่ EndDate เป็นค่าแทน — ไม่แก้เงียบ</summary>
    public static bool NeedsExpiryReview(bool isPermanentFree, Models.Enums.SubscriptionPlan plan, DateTime endUtc, DateTime nowUtc)
        => !isPermanentFree && plan != Models.Enums.SubscriptionPlan.FreeTrial && IsOpenEnded(endUtc, nowUtc);

    /// <summary>วันที่จะออกใบแจ้งหนี้ต่ออายุ (EndDate − leadDays): null เมื่อไม่หมดอายุ / ไม่ Active / ออกใบรอบนี้แล้ว / ขอยกเลิกสิ้นรอบ</summary>
    public static DateTime? NextRenewalInvoiceAt(bool isPermanentFree, DateTime endUtc, DateTime nowUtc, bool isActive,
        bool renewalAlreadyIssued, bool cancelAtPeriodEnd, int leadDays)
        => IsOpenEnded(isPermanentFree, endUtc, nowUtc) || !isActive || renewalAlreadyIssued || cancelAtPeriodEnd
            ? null : endUtc.AddDays(-leadDays);
}
