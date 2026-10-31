using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S2) — <b>ตัวตัดสินเดียว</b>ของ "ค่าบริการ NextAcc ถูกจ่ายซ้อนสองทาง" (ออนไลน์ + โอน/แนบสลิป) · pure
/// <list type="bullet">
/// <item>เริ่มชำระออนไลน์ไม่ได้ ถ้ารายการ<b>แนบสลิปรอตรวจอยู่แล้ว</b> (<see cref="OnlineStartRefusal"/>)</item>
/// <item>เงินออนไลน์เข้า ทั้งที่รายการ<b>ปิดแล้วด้วยทางอื่น</b> ⇒ ไม่ให้สิทธิ์ซ้ำ + ล้มดัง (audit + ประวัติ ⚠️ + แจ้ง) — ต้องคืนเงินรายการนี้</item>
/// <item>เงินออนไลน์เข้า ขณะสลิปรอตรวจ (แนบหลังเริ่มจ่าย) ⇒ เปิดสิทธิ์ตามเงินที่ยืนยันแล้ว แต่บันทึก audit ให้แอดมินปฏิเสธสลิป/คืนเงินโอน</item>
/// </list>
/// </summary>
public static class PlatformDoublePayment
{
    public const string RuleCode = "PLATFORM-DOUBLE-PAYMENT";
    public const string AuditAction = "platform-double-payment";
    public const string AlreadySettledMessage =
        "ชำระซ้ำ: รายการนี้ยืนยันการชำระไปแล้วจากช่องทางอื่น — ไม่ให้สิทธิ์ซ้ำ · ผู้ดูแลระบบต้องคืนเงินรายการออนไลน์นี้";
    public const string SlipAfterOnlineMessage =
        "รายการนี้ชำระออนไลน์สำเร็จแล้ว — อนุมัติสลิปซ้ำไม่ได้ ให้ปฏิเสธสลิปและคืนเงินที่โอนมา";
    public const string SlipPendingMessage =
        "แนบสลิปการโอนไว้แล้ว รอผู้ดูแลระบบตรวจ — ชำระออนไลน์ซ้ำไม่ได้ (ถ้าต้องการเปลี่ยนวิธีชำระ ติดต่อผู้ดูแลระบบ)";

    public enum Outcome { Apply, SlipPendingButPaidOnline, AlreadySettled }

    /// <summary>เริ่มชำระออนไลน์ได้ไหม — null = ได้</summary>
    public static string? OnlineStartRefusal(bool slipAttachedAwaitingReview)
        => slipAttachedAwaitingReview ? SlipPendingMessage : null;

    /// <summary>เงินออนไลน์เข้า — ทำอย่างไร</summary>
    public static Outcome OnGatewaySuccess(bool alreadyPaid, Guid? paidByIntentId, Guid thisIntentId, bool slipPendingReview)
        => alreadyPaid && paidByIntentId != thisIntentId ? Outcome.AlreadySettled
            : slipPendingReview ? Outcome.SlipPendingButPaidOnline
            : Outcome.Apply;

    /// <summary>ค่าแพ็กเกจ: สถานะรายการชำระที่ "ปิดแล้ว" (อนุมัติ/ปฏิเสธ/ยกเลิก) — เงินออนไลน์ที่เข้าทีหลังต้องคืน</summary>
    public static bool SubscriptionPaymentClosed(SubscriptionPaymentStatus status)
        => status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview);
}
