using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 219 ส่วน B: ช่องทางค่าบริการ NextAcc ออนไลน์ใช้ได้ไหม · pure — null = ใช้ได้</summary>
public static class PlatformPayAvailability
{
    public static string? OfflineReason(bool isActive, PaymentProviderMode mode, int methodCount)
        => !isActive ? "ช่องทางรับชำระออนไลน์ของ NextAcc ปิดอยู่ชั่วคราว — กรุณาโอนเงินแล้วแนบสลิป"
            : mode != PaymentProviderMode.Live ? "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่เปิดรับเงินจริง — กรุณาโอนเงินแล้วแนบสลิป"
            : methodCount == 0 ? "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่ได้เปิดวิธีชำระใด — กรุณาโอนเงินแล้วแนบสลิป"
            : null;
}
