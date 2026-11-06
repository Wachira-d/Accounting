using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 250 (WP-02 §2.4 ข้อ 5 · คำตัดสิน §2 #12 · C F8 · A K-02): <b>ทิศเงินของการรับ/จ่ายชำระเอกสาร — ตัวเดียว</b> (pure)
///
/// <para>แทน <c>DocumentService.IsCashInflowDocAsync</c> (ลบแล้ว) ที่ตอบ "เงินออก" ให้ใบลดหนี้<b>ทุกใบ</b> — ใบลดหนี้ฝั่งซื้อ (ผู้ขายคืนเงินเรา) คือเงิน<b>เข้า</b> ·
/// วันนี้เส้นรับชำระยังไปไม่ถึงใบลดหนี้ (ไม่อยู่ใน PayableDocumentTypes) แต่ตัวปรับยอดธนาคาร/ข้อความแจ้งเตือนใช้ตัวเดียวกัน ⇒ ปิดกับดักก่อนเปิดทาง ·
/// ฝั่งของใบลดหนี้/ใบเพิ่มหนี้ตัดสินด้วย <see cref="AdjustmentNoteAccount.ResolveSide"/> ตัวเดิม (ใบต้นทางชนะ › ที่ผู้ใช้เลือก) ·
/// ไม่รู้ฝั่ง ⇒ ถือเป็นฝั่งขาย (พฤติกรรมเดิมของใบเพิ่มหนี้ = เงินเข้า · ใบลดหนี้ = เงินออก)</para></summary>
public static class CashFlowDirection
{
    /// <summary>เงินเข้า (ลูกค้า/ผู้ขายจ่ายเรา) ไหม</summary>
    /// <param name="type">ชนิดเอกสารที่รับ/จ่ายชำระ</param>
    /// <param name="relatedType">ชนิดใบต้นทาง (ใช้กับใบลดหนี้/ใบเพิ่มหนี้ · ผู้เรียกโหลดเฉพาะเมื่อเป็นสองชนิดนี้)</param>
    /// <param name="cnDnPurchaseSideOverride"><c>Document.CnDnPurchaseSideOverride</c> — ฝั่งที่ผู้ใช้เลือกเมื่อไม่มีใบต้นทาง</param>
    public static bool IsInflow(DocumentType type, DocumentType? relatedType, bool? cnDnPurchaseSideOverride)
    {
        switch (type)
        {
            case DocumentType.Invoice:
            case DocumentType.TaxInvoice:
            case DocumentType.Receipt:
            case DocumentType.ReceiptVoucher:
            case DocumentType.BillingNote:
                return true;
            case DocumentType.DebitNote:
                // ใบเพิ่มหนี้ฝั่งขาย = ลูกค้าจ่ายเพิ่ม (เข้า) · ฝั่งซื้อ = เราจ่ายผู้ขายเพิ่ม (ออก)
                return AdjustmentNoteAccount.ResolveSide(relatedType, cnDnPurchaseSideOverride) != true;
            case DocumentType.CreditNote:
                // ใบลดหนี้ฝั่งขาย = คืนเงินลูกค้า (ออก) · ฝั่งซื้อ = ผู้ขายคืนเงินเรา (เข้า)
                return AdjustmentNoteAccount.ResolveSide(relatedType, cnDnPurchaseSideOverride) == true;
            default:
                return false;
        }
    }

    /// <summary>ต้องโหลดชนิดใบต้นทางไหม (เฉพาะใบลดหนี้/ใบเพิ่มหนี้ที่อ้างใบเดิม) — ผู้เรียกไม่ต้อง query เมื่อไม่จำเป็น</summary>
    public static bool NeedsRelatedType(DocumentType type, Guid? relatedDocumentId)
        => relatedDocumentId.HasValue && type is DocumentType.CreditNote or DocumentType.DebitNote;
}
