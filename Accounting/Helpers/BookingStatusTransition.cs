using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลตรวจการเปลี่ยนสถานะการจอง</summary>
public enum BookingTransitionResult { Allowed, NoOp, Rejected }

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "เปลี่ยนสถานะการจองจาก A ไป B ได้ไหม" (รอบ 208 · ฝ่ายค้าน W2 ข้อ 1/3 blocking)
/// <para>ที่มา: เส้นสถานะเดิมรับทุกสถานะไปทุกสถานะ ⇒ ยกเลิก→ยกเลิก (กดซ้ำ/สองแท็บ) หรือ ไม่มา→ยืนยัน→ไม่มา ทำให้ขั้นเงิน (ริบมัดจำ ·
/// ใบส่วนที่เหลือ · ปรับสมุดเครดิต) รันซ้ำ ⇒ ริบ 50% + 25% · ใบส่วนที่เหลือสองใบ · ไม่มา(ริบแล้ว)→เสร็จ = เก็บค่าปรับ + ราคาเต็ม</para>
/// <para>กติกา: สถานะเดิม = สถานะใหม่ ⇒ ไม่ทำอะไร (คืนผลเดิม — กดซ้ำปลอดภัย) · สถานะปลายทาง (เสร็จสิ้น · ยกเลิก · ไม่มา) เปลี่ยนต่อไม่ได้
/// (เงิน/เอกสารปิดแล้ว — แก้ด้วยการจองใหม่/ใบลดหนี้) · นอกนั้นตามเดิม (คิวสำรองตรวจแยกที่เส้นสถานะ)</para>
/// </summary>
public static class BookingStatusTransition
{
    public const string RuleCode = "CMS-BOOKING-TRANSITION";

    public static bool IsFinal(BookingStatus s) => s is BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow;

    public static (BookingTransitionResult Result, string? Message) Check(BookingStatus from, BookingStatus to)
    {
        if (from == to) return (BookingTransitionResult.NoOp, null);
        if (IsFinal(from))
            return (BookingTransitionResult.Rejected,
                $"การจองนี้{Label(from)}แล้ว — เปลี่ยนสถานะต่อไม่ได้ (เงิน/เอกสารของการจองปิดแล้ว) · ถ้าลูกค้ามาใช้บริการใหม่ให้สร้างการจองใหม่ · ถ้าต้องคืนเงินให้ออกใบลดหนี้ที่หน้าเอกสาร");
        return (BookingTransitionResult.Allowed, null);
    }

    public const string NoOpNotice = "การจองอยู่ในสถานะนี้อยู่แล้ว — ไม่ได้ทำซ้ำ (กดซ้ำ/เปิดสองหน้าจอ)";

    private static string Label(BookingStatus s) => s switch
    {
        BookingStatus.Completed => "ให้บริการเสร็จ",
        BookingStatus.Cancelled => "ถูกยกเลิก",
        BookingStatus.NoShow => "บันทึกว่าลูกค้าไม่มา",
        _ => s.ToString(),
    };
}
