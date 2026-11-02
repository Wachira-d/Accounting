using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ขั้นที่ลูกค้าอยู่หลังกดจอง (เรื่องเงินล่วงหน้า) — ชื่อ enum ออกไปหน้าเว็บเป็น "ชื่อ" (F2 ข้อ 5)</summary>
public enum BookingPaymentStepKind
{
    /// <summary>ไม่มีเรื่องเงินล่วงหน้าค้าง (ไม่เก็บ/ชำระครบ/คิวสำรอง/ยกเลิก)</summary>
    None = 0,
    /// <summary>ยังไม่ยืนยัน — ต้องชำระก่อน (โหมดบังคับ · การจอง "รอยืนยัน")</summary>
    AwaitingPayment = 1,
    /// <summary>ยืนยันแล้ว · ลูกค้าเลือกจ่ายมัดจำ (โหมดไม่บังคับ) — ชำระตอนนี้หรือภายหลังได้</summary>
    OptionalPayment = 2,
    /// <summary>ยืนยันแล้ว แต่ยังมียอดล่วงหน้าค้าง (ร้านยืนยันเอง / แบบเดิม)</summary>
    OutstandingPayment = 3,
    /// <summary>แนบสลิปแล้ว — รอร้านตรวจ</summary>
    SlipReview = 4,
}

/// <summary>ผลตัดสินขั้นการชำระของลูกค้า — หัวข้อ/ป้ายสถานะ/ไอคอนที่หน้าเว็บแสดงตรง ๆ (หน้าเว็บไม่มีสำเนากติกา)</summary>
/// <param name="CustomerStatusLabel">ป้ายสถานะการจองที่ลูกค้าเห็นแทน <see cref="BookingStatusLabels.Customer"/> (null = ใช้ป้ายตามสถานะเดิม)</param>
/// <param name="PaymentFirst">หน้าผลการจองวาดกล่องชำระเงิน "ก่อน" รายละเอียด (ขั้นชำระเงินมาก่อนคำยืนยัน)</param>
public sealed record BookingPaymentStepResult(
    BookingPaymentStepKind Kind,
    string Headline,
    string? Detail,
    string Icon,
    string? CustomerStatusLabel,
    bool PaymentFirst);

/// <summary>
/// รอบ 223 (BOOKING_PAYMENT_AUDIT · erp-review/2026-11-01) — <b>ตัวตัดสินเดียว</b>ของ "หลังกดจอง ลูกค้าเห็นอะไร" เรื่องเงินล่วงหน้า:
/// หัวข้อหน้าผลการจอง · ป้ายสถานะ · ขั้นชำระมาก่อนคำยืนยันไหม
///
/// <para><b>ที่มา</b>: เจ้าของตั้งมัดจำแล้วลูกค้าจองเห็น "✅ จองสำเร็จ · ยืนยันแล้ว" ทันที ส่วนกล่องชำระอยู่ท้ายสุด (หรือไม่มี) —
/// โหมดบังคับต้องไม่ขึ้นคำว่า "ยืนยันแล้ว/จองสำเร็จ" จนกว่ารับเงิน (สลิปผ่านการตรวจ / ชำระออนไลน์) · แนบสลิปแล้ว = "รอร้านตรวจสลิป" ·
/// โหมดไม่บังคับที่ลูกค้าเลือกจ่าย = ขั้นชำระขึ้นก่อน แต่บอกชัดว่าข้ามได้ (การจองยืนยันตามค่าบริการ — ไม่ยกเลิกอัตโนมัติ)</para>
///
/// <para><b>อินพุต</b>: <paramref name="due"/> = ยอดค้างจากตัวตัดสินเดียว <see cref="CmsBookingOnlinePayment.DepositDue"/> (ไม่คิดซ้ำที่นี่) ·
/// สถานะการจอง "รอยืนยัน" + ยอดค้าง = ต้องชำระเพื่อยืนยัน (PrePayment: รับเงินครบ ⇒ ยืนยันอัตโนมัติ — <c>RecordDepositPaymentAsync</c>)</para>
/// </summary>
public static class BookingPaymentStep
{
    private static string Baht(decimal v) => "฿" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static BookingPaymentStepResult Decide(BookingStatus status, BookingPaymentMode mode, decimal due, bool slipPending, DateTime? holdUntilUtc)
    {
        var open = status is BookingStatus.Pending or BookingStatus.Confirmed;
        if (!open || due <= 0.005m) return Plain(status);
        if (slipPending)
            return status == BookingStatus.Pending
                ? new(BookingPaymentStepKind.SlipReview, "ส่งสลิปแล้ว — รอร้านตรวจสลิป",
                    "ร้านจะตรวจยอดเงินแล้วยืนยันการจอง — ระหว่างนี้ระบบไม่ยกเลิกการจองของคุณ", "⏳", "รอร้านตรวจสลิป", true)
                : new(BookingPaymentStepKind.SlipReview, "จองสำเร็จ — ส่งสลิปแล้ว รอร้านตรวจ", null, "✅", null, true);
        if (status == BookingStatus.Pending)
        {
            var by = holdUntilUtc is DateTime u
                ? $"ชำระ {Baht(due)} ภายใน {u.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} น. — ไม่ชำระในเวลา ระบบยกเลิกการจองอัตโนมัติ"
                : $"ชำระ {Baht(due)} แล้วแนบสลิป หรือชำระออนไลน์ — ร้านยืนยันการจองเมื่อได้รับเงิน";
            return new(BookingPaymentStepKind.AwaitingPayment, "ยังไม่ยืนยัน — ชำระเงินเพื่อยืนยันการจอง", by, "💳", "รอชำระเงิน", true);
        }
        // ยืนยันแล้ว + ยอดค้าง
        return mode == BookingPaymentMode.DepositOptional
            ? new(BookingPaymentStepKind.OptionalPayment, "จองสำเร็จ — ชำระมัดจำตอนนี้ได้ (ไม่บังคับ)",
                $"เลือกจ่ายมัดจำ {Baht(due)} ไว้ — ชำระตอนนี้ หรือภายหลังที่ลิงก์ “ดู/จัดการการจองของฉัน” หรือชำระที่ร้านก็ได้ (ไม่ชำระ การจองไม่ถูกยกเลิก)",
                "✅", null, true)
            : new(BookingPaymentStepKind.OutstandingPayment, "จองสำเร็จ — มียอดชำระล่วงหน้าค้าง",
                $"ยอดค้าง {Baht(due)} — ชำระตอนนี้ หรือที่ร้านก่อนรับบริการ", "✅", null, true);
    }

    private static BookingPaymentStepResult Plain(BookingStatus status) => status switch
    {
        BookingStatus.Waitlisted => new(BookingPaymentStepKind.None, "ลงคิวสำรองแล้ว", null, "🕒", null, false),
        BookingStatus.Pending => new(BookingPaymentStepKind.None, "ได้รับการจองแล้ว — รอร้านยืนยัน", null, "📨", null, false),
        BookingStatus.Cancelled => new(BookingPaymentStepKind.None, "การจองนี้ถูกยกเลิกแล้ว", null, "✖️", null, false),
        BookingStatus.NoShow => new(BookingPaymentStepKind.None, "บันทึกว่าไม่มาตามนัด", null, "✖️", null, false),
        BookingStatus.Completed => new(BookingPaymentStepKind.None, "ใช้บริการแล้ว", null, "✅", null, false),
        _ => new(BookingPaymentStepKind.None, "จองสำเร็จ", null, "✅", null, false),
    };

    /// <summary>ข้อความก่อนกดจอง: หลังกดจองจะเกิดอะไรเรื่องเงิน (null = ไม่มีเงินล่วงหน้า)</summary>
    public static string? PreviewNextStep(bool required, bool optional, bool gatewayReady, bool transferReady)
    {
        if (!required && !optional) return null;
        var how = (transferReady, gatewayReady) switch
        {
            (true, true) => "เลขบัญชี/QR ให้โอนแล้วแนบสลิป หรือปุ่มชำระออนไลน์",
            (true, false) => "เลขบัญชี/QR ให้โอนแล้วแนบสลิป",
            (false, true) => "ปุ่มชำระออนไลน์",
            _ => "วิธีติดต่อร้านเพื่อชำระ",
        };
        return required
            ? $"กดจองแล้วจะไปขั้นชำระเงิน ({how}) — การจองยืนยันเมื่อร้านได้รับเงิน"
            : $"ติ๊กจ่ายมัดจำแล้วกดจอง จะไปขั้นชำระเงิน ({how}) — ไม่ติ๊กก็จองได้ ชำระที่ร้าน";
    }

    /// <summary>ป้ายปุ่มกดจอง (null = ป้ายเดิม "ยืนยันการจอง")</summary>
    public static string? SubmitLabel(bool required, decimal amount)
        => required && amount > 0.005m ? $"จอง แล้วไปขั้นชำระเงิน {Baht(amount)}" : null;
}
