using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลตัดสิน "ลูกค้าเลือกชำระมัดจำภายหลังได้ไหม" — <see cref="Show"/> false = ไม่มีปุ่ม (ไม่เกี่ยว) · <see cref="Allowed"/> false + <see cref="Problem"/> = มีเหตุบอกลูกค้า</summary>
public sealed record BookingDepositLaterDecision(bool Show, bool Allowed, decimal Amount, string? Problem);

/// <summary>
/// รอบ 228 (BOOKING_PAYMENT_AUDIT §4 ข้อ 1 · §9) — <b>ตัวตัดสินเดียว</b>ของ "มัดจำไม่บังคับ: ลูกค้าไม่ได้ติ๊กจ่ายตอนจอง แล้วมาเลือกจ่ายภายหลังจากหน้าจัดการการจอง"
///
/// <para><b>คำตัดสิน 228-1</b>: เลือกแล้ว ⇒ ตั้ง <c>SiteBooking.DepositAmount</c> = ยอดเดียวกับที่ลูกค้าจะได้ถ้าติ๊กตอนจอง
/// (<see cref="BookingPaymentPolicy.AmountDue"/> · ค่าตั้งปัจจุบันของเว็บ/บริการ · ฐานค่าบริการ — คำตัดสิน 226-4) แล้วใช้<b>ขั้นชำระเดิมทุกช่องทาง</b>
/// (ออนไลน์ · โอน/QR + แนบสลิป) · <b>ไม่ออกใบร่างตอนเลือก</b> — ใบรับมัดจำออกตอนเงินเข้า ลงวันที่รับเงิน (§78/1) ผ่านเส้นเดิม
/// <c>IssueDepositReceiptAfterPaymentAsync</c> (การจองชนิดชำระล่วงหน้าที่ไม่มียอดตอนจองไม่มีเอกสาร ERP — <c>CmsBookingErpPolicy.NeedsErpDocument</c>) ·
/// ไม่ถือรอบ/ไม่ตั้งกำหนดชำระ (โหมดไม่บังคับ — ไม่จ่ายก็ยังคงการจองไว้) · สถานะการจองไม่เปลี่ยนตอนเลือก (รับเงินครบ ⇒ รอยืนยัน → ยืนยันแล้ว ตามเส้นเดิม)</para>
/// <para>ใช้ได้เมื่อ: ชนิดที่ตรึงบนการจอง = ชำระมัดจำล่วงหน้า · โหมดที่มีผล = มัดจำไม่บังคับ · ยังไม่มียอดมัดจำ · รอยืนยัน/ยืนยันแล้ว · ยังไม่รับเงิน ·
/// ไม่ได้จ่ายด้วยบัตรคอร์ส · ยังไม่ถึงเวลานัด · ร้านตั้งยอดไว้ (&gt; 0)</para>
/// </summary>
public static class BookingDepositLater
{
    public const string RuleCode = "CMS-BOOKING-DEPOSIT-LATER";
    public const string LegalReference = "RD-78/1";

    private static string Baht(decimal v) => "฿" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static BookingDepositLaterDecision Hide() => new(false, false, 0m, null);
    private static BookingDepositLaterDecision Blocked(string why) => new(true, false, 0m, why);

    public static BookingDepositLaterDecision Decide(BookingPaymentEffective eff, BookingType bookingType, BookingStatus status,
        decimal depositAmount, decimal paidAmount, decimal serviceTotal, decimal deliveryFee, DateTime startUtc, DateTime nowUtc, bool paidWithCredit)
    {
        if (bookingType != BookingType.PrePayment || eff.Mode != BookingPaymentMode.DepositOptional) return Hide();
        if (depositAmount > 0.005m) return Hide();   // เลือกไว้แล้ว (ตอนจอง/ภายหลัง) ⇒ กล่องชำระปกติแสดงยอดค้าง
        if (status is not (BookingStatus.Pending or BookingStatus.Confirmed)) return Hide();
        if (paidWithCredit) return Hide();
        if (paidAmount > 0.005m) return Blocked("ร้านบันทึกรับเงินของการจองนี้แล้ว — ติดต่อร้านหากต้องการชำระเพิ่ม");
        if (startUtc <= nowUtc) return Blocked("เลยเวลานัดแล้ว — ชำระที่ร้าน");
        var amount = BookingPaymentPolicy.AmountDue(eff, serviceTotal, customerOptsIn: true, deliveryFee);
        if (amount <= 0.005m) return Hide();   // ร้านยังไม่ได้ตั้งยอด — ไม่มีอะไรให้จ่าย (ไม่ใช่การบล็อก)
        return new BookingDepositLaterDecision(true, true, amount, null);
    }

    /// <summary>ข้อความเหนือปุ่มบนหน้าจัดการการจอง</summary>
    public static string Offer(decimal amount)
        => $"มัดจำไม่บังคับ — ถ้าต้องการล็อกคิวไว้ เลือกชำระมัดจำ {Baht(amount)} ตอนนี้ได้ (โอน/สแกน QR แล้วแนบสลิป หรือชำระออนไลน์ตามที่ร้านเปิด) · ไม่เลือก = ชำระที่ร้านตามเดิม";

    /// <summary>หมายเหตุภายในบนการจองเมื่อลูกค้าเลือก (ร้านเห็นว่ามัดจำมาจากไหน)</summary>
    public static string ChosenNote(decimal amount, DateTime atUtc)
        => $"[{RuleCode}] ลูกค้าเลือกชำระมัดจำ {amount:N2} บาทภายหลังจากหน้าจัดการการจอง เมื่อ {atUtc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} — "
           + "ใบรับมัดจำออกอัตโนมัติเมื่อรับเงิน (ลงวันที่รับเงิน §78/1)";

    /// <summary>บรรทัดในข้อความยืนยันการจอง (อีเมล/LINE) — มัดจำไม่บังคับที่ยังไม่เลือก ⇒ บอกว่าเลือกชำระภายหลังได้ที่ลิงก์จัดการการจอง</summary>
    public static string NotifyLine(decimal amount)
        => $"💳 มัดจำไม่บังคับ {Baht(amount)} — เลือกชำระภายหลังได้ที่ลิงก์จัดการการจองด้านล่าง (ไม่เลือก = ชำระที่ร้าน)";
}
