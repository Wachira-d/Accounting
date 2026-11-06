using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ป้ายไทยของรอบบิล/สถานะทดลองใช้ — <b>ตารางเดียว</b> (รอบ 252 WP-13 J · audit 249 D §10)
/// <para>ที่มา: <c>subscription.html</c> มีตาราง enum→ไทยของตัวเอง (cycleMap สองชุด · statusMap ของ Trial) — F2 ข้อ 5 "server computes · page displays"
/// ⇒ DTO ส่งป้ายจากที่นี่ (<c>SubscriptionResponse.BillingCycleLabel</c> · <c>SubscriptionPaymentResponse.RequestedBillingCycleLabel</c> ·
/// <c>TrialStatusResponse.TrialStatusLabel</c>) · สถานะแพ็กเกจใช้ <see cref="PlanIdentityResolver.StatusLabel"/> · วิธีชำระใช้ <see cref="PaymentMethodNames.Thai"/></para></summary>
public static class SubscriptionLabels
{
    public static string Cycle(BillingCycle c) => c switch
    {
        BillingCycle.Monthly => "รายเดือน",
        BillingCycle.Quarterly => "ราย 3 เดือน",
        BillingCycle.SemiAnnual => "ราย 6 เดือน",
        BillingCycle.Annual => "รายปี",
        _ => c.ToString(),
    };

    public static string Trial(TrialStatus s) => s switch
    {
        TrialStatus.NotStarted => "ยังไม่เริ่ม",
        TrialStatus.Active => "กำลังทดลอง",
        TrialStatus.Extended => "ขยายเวลาแล้ว",
        TrialStatus.Expired => "หมดอายุ",
        TrialStatus.Converted => "อัปเกรดแล้ว",
        _ => s.ToString(),
    };
}
