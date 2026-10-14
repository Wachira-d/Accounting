using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ปุ่ม "แก้ส่วนลดบรรทัดจากสแกน" ใช้กับเอกสารใบนี้ได้ไหม — ตัวตัดสินเดียวของหน้าตรวจข้อมูล (ซ่อนปุ่ม) และตัวแก้ (ปฏิเสธ)**
/// (ฝ่ายค้านรอบ 204 L2 · ตัวแก้ <c>DocumentService.FixOcrLineDiscountAsync</c>)
/// <para>ตัวแก้เปลี่ยน <c>UnitPrice</c>/<c>DiscountPercent</c>/<c>DiscountAmount</c> ของบรรทัด = <b>รายการที่พิมพ์บนเอกสาร</b> ·
/// ใบฝั่งซื้อ (ใบของผู้ขาย — เราเก็บข้อมูลจากกระดาษ) แก้ metadata ได้ทุกสถานะที่ไม่ยกเลิก ·
/// <b>ใบขายที่เราออกแล้ว</b> (มีเลขที่ · ใบกำกับภาษี §86/4 ห้ามแก้ย้อนหลัง) ⇒ ปฏิเสธพร้อมทางไปต่อ "ยกเลิกแล้วออกใบแทน" · ใบขายที่ยังเป็นร่าง ⇒ แก้ได้</para>
/// <para>ใบลด/เพิ่มหนี้อยู่ได้สองฝั่ง ⇒ ผู้เรียกส่งฝั่งจากตัวตัดสินกลาง (<see cref="AdjustmentNoteAccount.ResolvePostedSide"/>) ·
/// ระบุฝั่งไม่ได้ ⇒ ถือเป็นใบขาย (ใบที่ออกแล้วต้องไม่ถูกแก้รายการเงียบ ๆ — ผู้ใช้ย้ายฝั่งให้ถูกแล้วกดแก้ได้)</para>
/// </summary>
public static class OcrLineDiscountFixScope
{
    /// <summary>ใบนี้เป็น "ใบขายที่เราออก" ไหม — ชนิดฝั่งเดียวตอบจาก <see cref="DocumentSide"/> · ใบลด/เพิ่มหนี้ใช้ผลของ
    /// <see cref="AdjustmentNoteAccount.ResolvePostedSide"/> (ไม่รู้ฝั่ง ⇒ ถือเป็นขาย) · ชนิดกำกวมอื่น (ใบส่งของ) ⇒ ไม่ใช่</summary>
    public static bool IsOurSalesDocument(DocumentType type, (bool IsPurchase, bool Unknown)? cnDnSide)
    {
        if (type is DocumentType.CreditNote or DocumentType.DebitNote)
            return cnDnSide is not { } side || side.Unknown || !side.IsPurchase;
        return !DocumentSide.IsAmbiguous(type) && DocumentSide.IsSales(type);
    }

    /// <summary>เหตุที่แก้ไม่ได้ (ไทย · มีทางไปต่อ) หรือ null = แก้ได้ · ใบยกเลิกตัดสินแยกที่ตัวแก้ (ไม่มีผลทางบัญชี)</summary>
    public static string? Refusal(string documentNumber, DocumentStatus status, bool isOurSalesDocument)
    {
        if (!isOurSalesDocument || !DocumentStatusRules.IsIssued(status)) return null;
        return $"{documentNumber} เป็นเอกสารขายที่ออกแล้ว — รายการที่พิมพ์บนใบกำกับภาษีห้ามแก้ย้อนหลัง (ป.รัษฎากร §86/4) ปุ่มนี้จึงไม่แก้ให้ · "
             + "ยอด/VAT/บัญชีที่บันทึกถูกตามกระดาษอยู่แล้ว: ถ้าไม่ต้องเปลี่ยนอะไร อย่าเปิดแก้แล้วบันทึกซ้ำ · "
             + "ถ้าต้องแก้รายการ: ยกเลิกใบนี้แล้วออกใบแทน (ใบใหม่ตั้งส่วนลดเป็นบาทให้ยอดตรงกระดาษ)";
    }
}
