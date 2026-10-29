using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "เปลี่ยนสถานะออเดอร์หน้าเว็บจาก A ไป B ได้ไหม" (รอบ 208 · ฝ่ายค้าน W2 ข้อ 1 blocking)
/// <para>ที่มา: ยกเลิก→ยกเลิก (กดซ้ำ) หรือ ยกเลิก→รอ→ยกเลิก รันขั้นริบมัดจำซ้ำ (50% แล้ว 25% ของที่เหลือ) + ใบลดหนี้ร่างใบที่สอง</para>
/// <para>กติกา: สถานะเดิม = ใหม่ ⇒ ไม่ทำอะไร · ยกเลิก/คืนเงินแล้ว เปลี่ยนต่อไม่ได้ (เงิน/เอกสารปิดแล้ว — สั่งใหม่/ใบลดหนี้) ·
/// คืนเงินบางส่วน ⇒ ไปคืนเงินเต็มได้ (ปิดยอดที่เหลือ) · นอกนั้นตามเดิม</para>
/// </summary>
public static class SiteOrderStatusTransition
{
    public const string RuleCode = "CMS-ORDER-TRANSITION";

    public static (BookingTransitionResult Result, string? Message) Check(SiteOrderStatus from, SiteOrderStatus to)
    {
        if (from == to) return (BookingTransitionResult.NoOp, null);
        if (from is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded)
            return (BookingTransitionResult.Rejected,
                $"ออเดอร์นี้{(from == SiteOrderStatus.Cancelled ? "ถูกยกเลิก" : "คืนเงินแล้ว")}แล้ว — เปลี่ยนสถานะต่อไม่ได้ (เงิน/เอกสารปิดแล้ว) · ให้ลูกค้าสั่งใหม่ หรือปรับยอดด้วยใบลดหนี้ที่หน้าเอกสาร");
        return (BookingTransitionResult.Allowed, null);
    }

    public const string NoOpNotice = "ออเดอร์อยู่ในสถานะนี้อยู่แล้ว — ไม่ได้ทำซ้ำ";
}
