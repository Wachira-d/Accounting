using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 (งาน PLATFORM BILLING ข้อ 6) — ตัวเลขรายงานรายได้ประจำของ NextAcc (MRR · ลูกหนี้ค้างตามอายุ) · pure · ตัวเดียวของหน้าแอดมิน
/// <para>MRR = ราคาต่อรอบ ÷ จำนวนเดือนของรอบ (ปัดครึ่งขึ้น 2 ตำแหน่ง) ของแพ็กเกจที่ <b>ใช้งานจ่ายเงิน</b> (Active/PastDue · ไม่ฟรีถาวร · ราคา &gt; 0) —
/// ทดลองใช้/ยกเลิก/หมดอายุ/ระงับไม่นับ · ส่วนเสริมนับราคา/เดือนของที่เปิดใช้และต้องจ่าย</para>
/// </summary>
public static class PlatformRevenueMetrics
{
    /// <summary>รายได้ต่อเดือนเทียบเท่าของราคาต่อรอบ</summary>
    public static decimal MonthlyEquivalent(decimal pricePerCycle, BillingCycle cycle)
    {
        var months = (int)cycle <= 0 ? 1 : (int)cycle;
        return Math.Round(pricePerCycle / months, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>แพ็กเกจนี้นับเป็นรายได้ประจำไหม</summary>
    public static bool CountsTowardMrr(SubscriptionStatus status, bool isPermanentFree, decimal pricePerCycle)
        => (status is SubscriptionStatus.Active or SubscriptionStatus.PastDue) && !isPermanentFree && pricePerCycle > 0m;

    /// <summary>ฝ่ายค้านรอบ 228 S6: ส่วนเสริมนับเป็นรายได้ประจำไหม — จ่ายแล้ว · พ้นช่วงทดลอง · บริษัทยังมีแพ็กเกจที่ใช้งาน (รอชำระ/ทดลอง/บริษัทหมดอายุ ไม่นับ)</summary>
    public static bool AddOnCountsTowardMrr(AddOnPaymentStatus status, DateTime? trialUntil, bool companyActive, DateTime nowUtc)
        => status == AddOnPaymentStatus.Paid && !(trialUntil > nowUtc) && companyActive;

    /// <summary>ช่วงอายุลูกหนี้ (ใบที่ยังค้าง) — ตามวันเกินกำหนด (null/≤0 = ยังไม่ถึงกำหนด) · ลำดับคงที่ของ <see cref="AgingBuckets"/></summary>
    public static string AgingBucket(int? daysPastDue) => daysPastDue switch
    {
        null or <= 0 => AgingBuckets[0],
        <= 30 => AgingBuckets[1],
        <= 60 => AgingBuckets[2],
        <= 90 => AgingBuckets[3],
        _ => AgingBuckets[4],
    };

    public static readonly string[] AgingBuckets = { "ยังไม่ถึงกำหนด", "เกิน 1–30 วัน", "เกิน 31–60 วัน", "เกิน 61–90 วัน", "เกิน 90 วัน" };
}
