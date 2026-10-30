using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ราคาแพ็กเกจ NextAcc — ตัวตั้งเดียว** (รอบ 219 เฟส 0 · PLATFORM_BILLING_AUDIT G-0/G-4 · คำตัดสินข้อ 178)
///
/// <para>เดิม switch "รอบบิล → ราคา" ถูกเขียนซ้ำ 5 ที่ (Convert · Change · Review · แอดมินเปลี่ยนแพ็กเกจ · ใบแจ้งหนี้ต่ออายุ)
/// และยอดของ <c>SubscriptionPayment</c> มาจาก body ของผู้เช่า ⇒ ฿1 ได้แพ็กเกจหลายปีบนเส้นยืนยันอัตโนมัติ ·
/// ตอนนี้เซิร์ฟเวอร์คำนวณยอดจากที่นี่ที่เดียว และผู้เช่า "ขอแพ็กเกจ" ได้ แต่ <b>ได้สิทธิ์เมื่อเงินเข้าแล้วเท่านั้น</b></para>
/// </summary>
public static class PlatformPlanPricing
{
    /// <summary>ราคาต่อรอบตามตารางแพ็กเกจ</summary>
    public static decimal CyclePrice(PlanTemplate t, BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => t.MonthlyPrice,
        BillingCycle.Quarterly => t.QuarterlyPrice,
        BillingCycle.SemiAnnual => t.SemiAnnualPrice,
        BillingCycle.Annual => t.AnnualPrice,
        _ => t.MonthlyPrice,
    };

    /// <summary>จำนวนเดือนของรอบบิล (ค่า enum = จำนวนเดือน) · ค่าที่ไม่รู้จัก = 0 (ให้ผู้เรียกปฏิเสธ)</summary>
    public static int MonthsOf(BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => 1,
        BillingCycle.Quarterly => 3,
        BillingCycle.SemiAnnual => 6,
        BillingCycle.Annual => 12,
        _ => 0,
    };

    /// <summary>ยอดที่ต้องจ่ายของคำขอซื้อแพ็กเกจ — ราคารอบหักส่วนลด % (ถ้ามี) · ปัด 2 ตำแหน่ง AwayFromZero</summary>
    public static decimal AmountDue(PlanTemplate t, BillingCycle cycle, decimal? discountPercent = null)
    {
        var price = CyclePrice(t, cycle);
        if (discountPercent is decimal d && d > 0m && d <= 100m)
            price *= (1m - d / 100m);
        return Math.Round(Math.Max(0m, price), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ยอดที่ผู้เช่าแจ้งมาตรงกับยอดที่ต้องจ่ายไหม · ≤ 0 = ไม่ได้แจ้ง (ใช้ยอดของเซิร์ฟเวอร์)</summary>
    public static bool ClaimedAmountAcceptable(decimal claimed, decimal due)
        => claimed <= 0m || Math.Abs(claimed - due) <= 0.01m;

    public enum ChangeKind { Upgrade, Downgrade, Same }

    /// <summary>เปลี่ยนแพ็กเกจเป็นอัปเกรดหรือดาวน์เกรด — เทียบราคาต่อเดือนของแพ็กเกจ (ไม่ใช่ลำดับ enum)
    /// · อัปเกรด = จ่ายก่อนแล้วมีผลทันทีหลังเงินเข้า · ดาวน์เกรด/เปลี่ยนรอบ = มีผลเมื่อครบรอบปัจจุบัน</summary>
    public static ChangeKind Classify(decimal currentMonthlyPrice, decimal newMonthlyPrice)
        => newMonthlyPrice > currentMonthlyPrice ? ChangeKind.Upgrade
         : newMonthlyPrice < currentMonthlyPrice ? ChangeKind.Downgrade
         : ChangeKind.Same;

    /// <summary>อนุมัติการชำระที่เป็นดาวน์เกรดระหว่างรอบที่ยังใช้งาน ⇒ เลื่อนไปมีผลวันครบรอบเดิม (ไม่ตัดสิทธิ์แพ็กเกจที่จ่ายไปแล้ว)</summary>
    public static bool DeferToCycleEnd(ChangeKind kind, SubscriptionStatus status, DateTime currentEndUtc, DateTime nowUtc)
        => kind != ChangeKind.Upgrade && status == SubscriptionStatus.Active && currentEndUtc > nowUtc;

    /// <summary>รายงานแอดมิน: แพ็กเกจที่ผู้เช่าเปิดเอง (Convert/เปลี่ยนแพ็กเกจรุ่นก่อนเฟส 0) หลังการชำระที่อนุมัติครั้งล่าสุด = ยังไม่ได้จ่าย</summary>
    public static bool IsUnpaidSelfActivation(DateTime? lastSelfActivationAt, DateTime? lastApprovedPaymentAt)
        => lastSelfActivationAt is DateTime a && (lastApprovedPaymentAt is not DateTime p || p < a);
}
