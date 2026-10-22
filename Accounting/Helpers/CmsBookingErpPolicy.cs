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
        // Appointment = ใบเสนอราคา (ไม่มีผลทางบัญชี) · Guaranteed = ใบกำกับตอนจอง หรือใบยืนยันการจอง (ใบเสนอราคา) ตามค่าตั้งไซต์ —
        // ชนิดตัดสินที่ BookingInvoiceTimingPolicy (คำตัดสินข้อ 155 · B-2) · ทั้งสองแบบอนุมัติอัตโนมัติเหมือนเดิม
        _ => true,
    };

    /// <summary>หมายเหตุบนการจอง + ใบร่าง เมื่อใบมัดจำรอรับเงิน (ต่อแบบไม่ซ้ำด้วย <c>DepositPolicyResolver.AppendNoteOnce</c>)</summary>
    public static string AwaitingPaymentNote(decimal depositAmount)
        => $"รอรับเงินมัดจำ {depositAmount:N2} บาท — ใบเสร็จมัดจำเป็นใบร่าง (ยังไม่มีเลข · ยังไม่ลงบัญชี/ภาษีขาย) · "
           + "เมื่อได้รับเงินแล้ว เปิดเอกสาร แก้วันที่เป็นวันรับเงินจริงและบัญชีที่รับ แล้วกด “อนุมัติ”";

    /// <summary>ต้องสร้างเอกสาร ERP ไหม — PrePayment ที่ไม่มียอดมัดจำ ⇒ ไม่ (ไม่มีเงินให้ออกใบเสร็จ · ฝ่ายค้านรอบ 205 ข้อ 6) · ชนิดอื่นตามเดิม</summary>
    public static bool NeedsErpDocument(BookingType type, decimal depositAmount)
        => type != BookingType.PrePayment || depositAmount > 0.005m;

    /// <summary>หมายเหตุเมื่อบริการแบบชำระล่วงหน้าไม่ได้ตั้งยอดมัดจำ</summary>
    public const string NoDepositNote = "บริการนี้ตั้งเป็น “ชำระล่วงหน้า” แต่ยอดมัดจำ 0 — ไม่ออกใบเสร็จมัดจำ · ออกเอกสารเมื่อรับเงินจริงที่หน้าเอกสาร (อ้างเลขการจอง)";

    /// <summary>ชนิดเอกสาร ERP บนการจอง จากชนิดเอกสารที่สร้างจริง (ใช้ตอนผูกใบที่สร้างแล้วกลับหลังขั้นถัดไปล้ม)</summary>
    public static ErpBookingDocumentType ErpTypeOf(DocumentType docType) => docType switch
    {
        DocumentType.TaxInvoice => ErpBookingDocumentType.TaxInvoice,
        DocumentType.Receipt => ErpBookingDocumentType.AdvanceReceipt,
        _ => ErpBookingDocumentType.DraftServiceOrder,
    };

    /// <summary>หมายเหตุบนการจองเมื่อสร้างเอกสาร ERP ไม่สำเร็จตอนแขกจอง — การจองยังสำเร็จ (ไม่ทำให้แขกเห็น error แล้วกดจองซ้ำ) ·
    /// มีใบที่สร้างไปแล้ว ⇒ บอกเลข (ใบถูกผูกกลับแล้ว — ห้ามออกใหม่ซ้ำ)</summary>
    public static string SyncFailedNote(string reason, string? createdDocumentNumber = null)
        => createdDocumentNumber is { Length: > 0 } no
            ? $"⚠️ ขั้นเอกสาร ERP ของการจองล้มหลังสร้างใบ {no} แล้ว: {reason} — ใบถูกผูกกับการจองแล้ว ตรวจ/อนุมัติใบนั้นที่หน้าเอกสาร (ห้ามออกใบใหม่ซ้ำ)"
            : $"⚠️ สร้างเอกสาร ERP ของการจองอัตโนมัติไม่สำเร็จ: {reason} — การจองบันทึกแล้ว · ออกเอกสารเองที่หน้าเอกสาร (อ้างเลขการจอง)";
}
