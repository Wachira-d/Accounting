using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ป้ายสถานะการจองที่<b>ลูกค้า</b>เห็น (หน้าเว็บ storefront · ใบยืนยันการจอง) — ตัวตั้งเดียว (รอบ 207 · เดิมมีตารางในหน้าเว็บ)
/// · ป้ายฝั่งพนักงานอยู่ที่ <c>Layout.ENUM_LABELS.bookingStatus</c></summary>
public static class BookingStatusLabels
{
    public static string Customer(BookingStatus s) => s switch
    {
        BookingStatus.Pending => "รอร้านยืนยัน",
        BookingStatus.Confirmed => "ยืนยันแล้ว",
        BookingStatus.InProgress => "กำลังให้บริการ",
        BookingStatus.Completed => "ใช้บริการแล้ว",
        BookingStatus.Cancelled => "ยกเลิกแล้ว",
        BookingStatus.NoShow => "ไม่มาตามนัด",
        BookingStatus.Waitlisted => "คิวสำรอง",
        _ => s.ToString(),
    };
}
