using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>เอกสาร ERP ของการจองนัดหมายหน้าเว็บ (CMS booking) ตอนสร้างการจอง — ตัวตัดสินเดียว</b> (รอบ 205 ทีม E · BOOKING_REVENUE_AUDIT B-1) · pure
///
/// <para>บั๊กที่มา: การจองแบบ <c>PrePayment</c> จากหน้าเว็บ (ไม่ล็อกอิน · ยังไม่มีใครจ่ายเงิน — ระบบไม่มีเส้นรับเงินของการจองนัดหมายเลย)
/// ⇒ <c>SyncBookingToErpAsync</c> สร้าง <b>และอนุมัติ</b> "ใบเสร็จรับเงิน (มัดจำ)" ทันที ⇒ JE Dr เงินสด/ธนาคาร · Cr มัดจำรับ 217xx + ภาษีขาย 21911
/// ของเงินที่<b>ไม่เคยได้รับ</b> · ภาษีขายเข้า ภ.พ.30 ทั้งที่ไม่มี tax point (§78/1: ยังไม่ได้รับเงิน · ยังไม่ออกใบกำกับต่อผู้ซื้อจริง · ยังไม่ใช้บริการ) ·
/// ใครก็กดจองซ้ำ ๆ สร้างใบเสร็จเลขจริง (gap-free §86/4) ที่ต้องตามยกเลิกทีละใบ = "สถานะปลายทางที่ระบบประทับเอง" (F2 ข้อ 3)</para>
///
/// <para>ตอนนี้: ใบมัดจำของการจองที่ยังไม่ได้รับเงิน = <b>ใบร่าง</b> (ไม่มีเลข ไม่ลง JE ไม่มีภาษีขาย) + หมายเหตุบอกพนักงานว่า
/// "รับเงินแล้วเปิดเอกสาร แก้วันที่เป็นวันรับเงินจริงแล้วกดอนุมัติ" · ยกเลิกการจอง ⇒ ใบร่างถูกยกเลิกตาม <c>CmsBookingCancelPolicy</c> เดิม ·
/// ให้บริการเสร็จทั้งที่ใบยังร่าง ⇒ ข้อความ <c>DepositNotIssued</c> เดิม</para>
/// </summary>
public static class CmsBookingErpPolicy
{
    /// <summary>อนุมัติเอกสาร ERP ของการจองอัตโนมัติไหม</summary>
    /// <param name="type">ชนิดการจองของบริการ</param>
    /// <param name="depositAmount">ยอดมัดจำที่ต้องชำระ</param>
    /// <param name="paidAmount">ยอดที่บันทึกว่าได้รับแล้วบนการจอง (<c>SiteBooking.PaidAmount</c>)</param>
    public static bool AutoApprove(BookingType type, decimal depositAmount, decimal paidAmount) => type switch
    {
        BookingType.Lead => false,
        // ใบเสร็จ = หลักฐานว่า "ได้รับเงินแล้ว" — ออกได้เฉพาะเมื่อเงินเข้าจริง
        BookingType.PrePayment => depositAmount > 0.005m && paidAmount + 0.005m >= depositAmount,
        // Appointment = ใบเสนอราคา (ไม่มีผลทางบัญชี) · Guaranteed = พฤติกรรมเดิม (ใบกำกับตอนจอง — รอเจ้าของตัดสิน: BOOKING_REVENUE_AUDIT B-2)
        _ => true,
    };

    /// <summary>หมายเหตุบนการจอง + ใบร่าง เมื่อใบมัดจำรอรับเงิน (ต่อแบบไม่ซ้ำด้วย <c>DepositPolicyResolver.AppendNoteOnce</c>)</summary>
    public static string AwaitingPaymentNote(decimal depositAmount)
        => $"รอรับเงินมัดจำ {depositAmount:N2} บาท — ใบเสร็จมัดจำเป็นใบร่าง (ยังไม่มีเลข · ยังไม่ลงบัญชี/ภาษีขาย) · "
           + "เมื่อได้รับเงินแล้ว เปิดเอกสาร แก้วันที่เป็นวันรับเงินจริงและบัญชีที่รับ แล้วกด “อนุมัติ”";

    /// <summary>หมายเหตุบนการจองเมื่อสร้างเอกสาร ERP ไม่สำเร็จตอนแขกจอง — การจองยังสำเร็จ (ไม่ทำให้แขกเห็น error แล้วกดจองซ้ำ)</summary>
    public static string SyncFailedNote(string reason)
        => $"⚠️ สร้างเอกสาร ERP ของการจองอัตโนมัติไม่สำเร็จ: {reason} — การจองบันทึกแล้ว · ออกเอกสารเองที่หน้าเอกสาร (อ้างเลขการจอง)";
}
