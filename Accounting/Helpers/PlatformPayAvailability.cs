using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 219 ส่วน B: ช่องทางค่าบริการ NextAcc ออนไลน์ใช้ได้ไหม · pure — null = ใช้ได้</summary>
public static class PlatformPayAvailability
{
    /// <param name="platformLiveConfirmed">ฝ่ายค้าน S1: แอดมินเปิดรับเงินจริงผ่านด่านแพลตฟอร์มแล้ว (<c>PlatformLiveConfirmedAt</c>) —
    /// โหมด Live อย่างเดียวไม่พอ (สลับจากหน้าตั้งค่าร้านของบริษัทผู้ให้บริการได้โดยไม่ผ่านด่านนโยบายคืนเงิน)</param>
    public static string? OfflineReason(bool isActive, PaymentProviderMode mode, int methodCount, bool platformLiveConfirmed)
        => !isActive ? "ช่องทางรับชำระออนไลน์ของ NextAcc ปิดอยู่ชั่วคราว — กรุณาโอนเงินแล้วแนบสลิป"
            : mode != PaymentProviderMode.Live || !platformLiveConfirmed ? "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่เปิดรับเงินจริง — กรุณาโอนเงินแล้วแนบสลิป"
            : methodCount == 0 ? "ช่องทางรับชำระออนไลน์ของ NextAcc ยังไม่ได้เปิดวิธีชำระใด — กรุณาโอนเงินแล้วแนบสลิป"
            : null;
}
