using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <param name="Status">สถานะที่เอกสารควรเป็นหลังยอดจ่ายเปลี่ยน</param>
/// <param name="BalanceDue">ยอดค้างชำระที่ควรเก็บลงเอกสาร (ไม่ติดลบ)</param>
/// <param name="Overpaid">จ่าย/รับเกินยอดใบไหม</param>
/// <param name="OverpaidAmount">ส่วนที่เกิน (0 เมื่อไม่เกิน)</param>
public readonly record struct DocumentSettlement(
    DocumentStatus Status, decimal BalanceDue, bool Overpaid, decimal OverpaidAmount);

/// <summary>
/// **"เอกสารใบนี้ค้างเท่าไร สถานะควรเป็นอะไร จ่ายเกินหรือยัง" — ตัวตัดสินตัวเดียว**
/// (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (ผลตรวจ D4-3 รอบ 181) ═══ ตรรกะสามบรรทัดนี้ถูกคัดลอกด้วยมือ
/// <b>7 ที่</b> ใน 3 ไฟล์ และใช้ <b>3 เกณฑ์ปัดเศษ</b>ที่ไม่ตรงกัน
/// (<c>&lt;= 0</c> · <c>&lt;= 0.005</c> · <c>&lt;= 0.01</c>) ⇒ ใบที่เหลือ 0.007 บาท
/// เป็น "ชำระครบ" ทางหนึ่งและ "ชำระบางส่วน" อีกทางในระบบเดียวกัน · และสองทาง
/// (Integration/Import) ปล่อยให้ <c>BalanceDue</c> ติดลบหรือ clamp ทิ้งเงียบ ๆ
/// ⇒ การรับเงินเกินหายไปโดยไม่มีใครเห็น</para>
///
/// <para>═══ เกณฑ์เดียวที่เลือก: <see cref="Tolerance"/> = 0.005 ═══
/// เงินในระบบเก็บทศนิยม 2 ตำแหน่ง ⇒ ยอดค้างที่ "มีจริง" ต้อง ≥ 0.01 เสมอ
/// เศษที่เล็กกว่าครึ่งสตางค์เกิดได้จากการปัดเศษเท่านั้น จึงถือเป็นศูนย์ได้
/// ส่วนเกณฑ์ 0.01 ที่เคยใช้บางจุด <b>กลืนยอดค้างจริง 1 สตางค์</b> แล้วประทับว่า
/// "ชำระครบ" ทั้งที่ <c>BalanceDue</c> ยังโชว์ 0.01 = สองความจริงบนใบเดียว</para>
///
/// <para>═══ ทำไม <see cref="PaymentOvershootAllowance"/> ถึงเป็นคนละตัว ═══
/// มันตอบคนละคำถาม: <see cref="Tolerance"/> ตอบว่า "ยอดค้างที่เหลือถือเป็นศูนย์ไหม"
/// ส่วนตัวนี้ตอบว่า "ยอดที่ผู้จ่ายส่งมาเกินยอดค้างจนต้อง<b>ปฏิเสธ</b>หรือยัง" —
/// ฝั่งผู้จ่าย (ธนาคาร/gateway/ลูกค้า) ปัดเศษเป็นสตางค์ของตัวเอง การรับเกิน
/// ไม่เกิน 1 สตางค์จึงยอมได้แล้ว clamp ยอดค้างเป็น 0 แต่ต้องยัง<b>รายงานว่าเกิน</b>
/// ผ่าน <see cref="DocumentSettlement.Overpaid"/> ห้ามกลืนเงียบ</para>
/// </summary>
public static class DocumentSettlementState
{
    /// <summary>ยอดที่เล็กกว่าครึ่งสตางค์ = ศูนย์ (เศษจากการปัดเศษเท่านั้น)</summary>
    public const decimal Tolerance = 0.005m;

    /// <summary>ยอดรับ/จ่ายที่เกินยอดค้างได้โดยไม่ถูกปฏิเสธ (การปัดเศษฝั่งผู้จ่าย)</summary>
    public const decimal PaymentOvershootAllowance = 0.01m;

    /// <summary>สถานะที่ยอม "ขยับตามยอดเงิน" ได้ — Draft/WaitingApproval ยังไม่มีหนี้
    /// ให้ชำระ · Voided/Rejected เป็นสถานะปลายทางที่การรับเงินไม่ควรไปปลุก</summary>
    private static bool CanFollowMoney(DocumentStatus status)
        => status is DocumentStatus.Approved or DocumentStatus.Sent
            or DocumentStatus.Overdue or DocumentStatus.PartiallyPaid or DocumentStatus.Paid;

    /// <summary>ยอดที่กำลังจะรับ/จ่าย เกินยอดค้างจนต้องปฏิเสธไหม
    /// (ใช้เป็นด่าน<b>ก่อน</b>บันทึก — ทุกทางเข้า: เว็บ · integration · นำเข้าไฟล์)</summary>
    /// <param name="balanceDue">ยอดค้างปัจจุบันของเอกสาร</param>
    /// <param name="incomingAmount">ยอดเงินของงวดนี้ (รวมค่าธรรมเนียมที่หักจากยอดโอน)</param>
    public static bool WouldOverpay(decimal balanceDue, decimal incomingAmount)
        => incomingAmount > balanceDue + PaymentOvershootAllowance;

    /// <summary>คำนวณสถานะ/ยอดค้างจากยอดรวมกับยอดที่จ่ายสะสม</summary>
    /// <param name="totalAmount">ยอดรวมของเอกสาร</param>
    /// <param name="paidAmount">ยอดที่รับ/จ่ายสะสมแล้ว (หลังบวกงวดนี้)</param>
    /// <param name="currentStatus">สถานะปัจจุบัน — สถานะที่ "ยังไม่มีหนี้ให้ชำระ"
    /// (Draft/WaitingApproval) หรือสถานะปลายทาง (Voided/Rejected) จะถูกคืนกลับไป
    /// เหมือนเดิม (ผู้เรียกไม่ต้องจำข้อยกเว้นเอง)</param>
    public static DocumentSettlement Apply(
        decimal totalAmount, decimal paidAmount, DocumentStatus currentStatus)
    {
        var raw = totalAmount - paidAmount;
        var overpaidAmount = raw < -Tolerance
            ? Math.Round(-raw, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var balance = raw <= Tolerance ? 0m : raw;

        if (!CanFollowMoney(currentStatus))
            return new(currentStatus, balance, overpaidAmount > 0m, overpaidAmount);

        // ยังไม่มีเงินเข้าเลย (เช่น กลับรายการชำระจนหมด) → กลับไปตั้งต้นที่ "อนุมัติแล้ว"
        // มิฉะนั้นใบที่ถูกยกเลิกการชำระจะค้างป้าย "ชำระบางส่วน" ทั้งที่ยังไม่ได้รับเงิน
        var status = paidAmount <= Tolerance
            ? DocumentStatus.Approved
            : balance <= Tolerance
                ? DocumentStatus.Paid
                : DocumentStatus.PartiallyPaid;

        return new(status, balance, overpaidAmount > 0m, overpaidAmount);
    }
}
