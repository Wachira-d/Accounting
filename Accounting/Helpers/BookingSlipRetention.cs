namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบ 218 (PDPA ม.37 + พ.ร.บ.การบัญชี ม.10) — อายุไฟล์สลิปของการจองนัดหมาย (<c>FileAttachment</c> ชนิด <c>SiteBooking</c>) · <b>ตัวตัดสินเดียว</b>ของงานล้าง
/// (<c>CartImageCleanupJob</c> รอบที่สอง):
/// <list type="bullet">
/// <item>สลิปของการจองที่<b>รับเงินแล้ว</b> = หลักฐานประกอบรายการบัญชี ⇒ <b>ไม่ลบ</b> (legal hold 5 ปี ม.10 — ใช้ MAX(retention) ตามกฎ M)</item>
/// <item>สลิปที่ยัง "รอตรวจ" (ตัวปัจจุบันของการจอง) ⇒ ไม่ลบ</item>
/// <item>สลิปที่ถูกปฏิเสธ/ถูกแทน ของการจองที่<b>ไม่มีเงินเข้าเลย</b> และเก่ากว่า <see cref="RejectedRetentionDays"/> วัน ⇒ ลบ (ข้อมูลส่วนบุคคลผู้โอนที่ไม่มีวัตถุประสงค์ต่อ)</item>
/// </list>
/// </summary>
public static class BookingSlipRetention
{
    public const int RejectedRetentionDays = 90;

    public static DateTime Cutoff(DateTime nowUtc) => nowUtc.AddDays(-RejectedRetentionDays);

    public static bool ShouldPurge(DateTime createdAtUtc, bool isCurrentSlip, bool bookingHasMoney, DateTime cutoffUtc)
        => !isCurrentSlip && !bookingHasMoney && createdAtUtc < cutoffUtc;
}
