namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (B3) — โหมดที่ผู้ให้บริการรายงาน (<c>livemode</c> ของ account/charge/event) ต้องตรงกับโหมดที่คาด · pure
/// · ไม่รู้ฝั่งใดฝั่งหนึ่ง = ไม่ตัดสิน (ไม่เดา)
/// </summary>
public static class PaymentLiveModeGuard
{
    /// <summary>null = ตรง/ไม่รู้ · ข้อความ = ไม่ตรง (ห้ามเปลี่ยนสถานะ/ห้ามนับว่าทดสอบผ่าน)</summary>
    public static string? Mismatch(bool? providerLiveMode, bool expectTest)
        => providerLiveMode is not bool live ? null
            : live && expectTest ? "ผู้ให้บริการรายงานว่าเป็นรายการเงินจริง แต่รายการ/คีย์นี้เป็นโหมดทดสอบ — ไม่ประมวลผล ให้ผู้ดูแลระบบตรวจคีย์"
            : !live && !expectTest ? "ผู้ให้บริการรายงานว่าเป็นรายการทดสอบ แต่รายการ/คีย์นี้เป็นโหมดใช้งานจริง — ไม่ประมวลผล ให้ผู้ดูแลระบบตรวจคีย์"
            : null;
}
