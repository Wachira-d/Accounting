using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 250 (WP-02 §2.4 ข้อ 5 · คำตัดสิน §2 #12 · C F8 · A K-02 · ฝ่ายค้าน WP-02 ข้อ 2/3): <b>ทิศเงินของการรับ/จ่ายชำระเอกสาร — ตัวเดียว</b> (pure)
///
/// <para>ผู้ใช้: JE รับ/จ่ายชำระ (<c>DocumentService.CreatePaymentJournalAsync</c>) · ยอดธนาคาร · ข้อความแจ้งเตือน · กลับรายการการชำระ ·
/// ลิงก์ชำระ/งานหลังรับชำระ (<see cref="DocumentPayLinkPolicy.IsSaleSide"/>) — <b>ลำดับเดียวกับ JE ทุกตัวอักษร</b>: ฝั่งของใบลดหนี้/ใบเพิ่มหนี้
/// ตัดสินด้วย <see cref="AdjustmentNoteAccount.ResolvePostedSide"/> (ผู้ใช้สั่งย้ายฝั่ง › ใบต้นทาง › ผู้ติดต่อเป็นผู้ขายอย่างเดียว · ไม่รู้ = ไม่รู้)</para>
/// <para>ที่มา (ฝ่ายค้าน WP-02 ข้อ 2): รุ่นแรกใช้ <c>ResolveSide</c> (ต้นทาง › ผู้ใช้ · ไม่ดูผู้ขายอย่างเดียว · ไม่รู้ = ฝั่งขาย) ขณะที่ JE ใช้ <c>ResolvePostedSide</c> ⇒
/// ใบเพิ่มหนี้ไม่มีต้นทาง + ผู้ติดต่อเป็นผู้ขายอย่างเดียว: JE เงินออก แต่ยอดธนาคาร +ยอด (เพี้ยน 2 เท่า) · ใบที่ถูกสลับฝั่งได้เครื่องหมายกลับ</para></summary>
public static class CashFlowDirection
{
    /// <summary>ผล — <paramref name="SideUnknown"/> = ใบลดหนี้/ใบเพิ่มหนี้ที่ยังไม่รู้ฝั่ง (ผู้เรียกต้องหยุดพร้อมทางไปต่อ ห้ามเดา)</summary>
    public sealed record Result(bool IsInflow, bool SideUnknown);

    /// <param name="type">ชนิดเอกสารที่รับ/จ่ายชำระ</param>
    /// <param name="cnDnPurchaseSideOverride"><c>Document.CnDnPurchaseSideOverride</c> (ผู้ใช้สั่งย้ายฝั่ง — ชนะทุกชั้นเหมือน JE)</param>
    /// <param name="sourceType">ชนิดใบต้นทางของใบลดหนี้/ใบเพิ่มหนี้ (null = ไม่มี/ไม่ได้โหลด)</param>
    /// <param name="contactIsSupplierOnly">ผู้ติดต่อเป็นผู้ขายอย่างเดียว (ไม่ใช่ลูกค้า)</param>
    public static Result Of(DocumentType type, bool? cnDnPurchaseSideOverride, DocumentType? sourceType, bool contactIsSupplierOnly)
    {
        switch (type)
        {
            case DocumentType.Invoice:
            case DocumentType.TaxInvoice:
            case DocumentType.Receipt:
            case DocumentType.ReceiptVoucher:
            case DocumentType.BillingNote:
                return new Result(true, false);
            case DocumentType.DebitNote:
            case DocumentType.CreditNote:
            {
                var side = AdjustmentNoteAccount.ResolvePostedSide(cnDnPurchaseSideOverride, sourceType, null, contactIsSupplierOnly);
                if (side.Unknown) return new Result(false, true);
                // ฝั่งซื้อ: ใบเพิ่มหนี้ = เราจ่ายเพิ่ม (ออก) · ใบลดหนี้ = ผู้ขายคืนเงิน (เข้า)
                // ฝั่งขาย: ใบเพิ่มหนี้ = ลูกค้าจ่ายเพิ่ม (เข้า) · ใบลดหนี้ = เราคืนลูกค้า (ออก)
                return new Result(side.IsPurchase ? type == DocumentType.CreditNote : type == DocumentType.DebitNote, false);
            }
            default:
                return new Result(false, false);
        }
    }

    /// <summary>ต้องโหลดข้อเท็จจริงฝั่ง (ใบต้นทาง · ผู้ติดต่อเป็นผู้ขายอย่างเดียว) ไหม — เฉพาะใบลดหนี้/ใบเพิ่มหนี้</summary>
    public static bool NeedsSideFacts(DocumentType type) => type is DocumentType.CreditNote or DocumentType.DebitNote;

    /// <summary>ข้อความหยุดเมื่อไม่รู้ฝั่ง (ตัวเดียวกับที่ JE เคยพิมพ์เอง)</summary>
    public static string UnknownSideMessage(DocumentType type, string? documentNumber)
        => $"เอกสาร {documentNumber} เป็น{(type == DocumentType.CreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้")}"
           + " ที่ระบบยังไม่รู้ว่าเป็นฝั่งซื้อหรือฝั่งขาย จึงลงบัญชีการรับ/จ่ายเงินให้ไม่ได้ "
           + "(ลงผิดทิศ = เงินสดกับเจ้าหนี้/ลูกหนี้เพี้ยนโดยไม่มีอะไรฟ้อง) — "
           + "กรุณาเปิดเอกสารแล้วเลือกฝั่งให้ถูก หรือผูกใบต้นทาง แล้วลองรับ/จ่ายเงินใหม่";
}
