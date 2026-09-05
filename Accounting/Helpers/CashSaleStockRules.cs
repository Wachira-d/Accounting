using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาเดียวของ "ใบเสร็จ/ใบสำคัญรับ standalone ที่มีสินค้าคงคลัง ทำอะไรกับสต๊อก" —
/// ทั้ง ApplyStockMovementsAsync (ทิศสต๊อก) · AutoPostToJournalAsync (COGS) · ApproveDocumentAsync
/// (บล็อก) ต้องถามที่นี่ ห้ามเช็ค enum เองในแต่ละไฟล์ (ERP_REVIEW_2026-09-05 A-06)
/// </summary>
public static class CashSaleStockRules
{
    /// <summary>เอกสารนี้อยู่ใต้กติกานี้ไหม — เฉพาะ Receipt/ReceiptVoucher ที่ **ไม่อ้างเอกสาร
    /// ต้นทาง** (ใบเสร็จ settlement ของใบแจ้งหนี้ให้ใบแจ้งหนี้ตัดสต๊อกไปแล้ว) และ **ไม่ใช่มัดจำ**
    /// (ยังไม่ส่งมอบของ). TaxInvoice+IssuedAsCashReceipt เดินสายใบกำกับอยู่แล้ว ไม่เกี่ยว</summary>
    public static bool AppliesTo(Document doc)
        => doc.DocumentType is DocumentType.Receipt or DocumentType.ReceiptVoucher
           && doc.RelatedDocumentId == null
           && !doc.IsDeposit;

    /// <summary>ทิศสต๊อกที่ต้องใช้กับใบเสร็จ standalone ตามนโยบาย (−1 = ขายออก · 0 = ไม่แตะ)</summary>
    public static int StockDirection(CashSaleStockPolicy policy)
        => policy == CashSaleStockPolicy.MoveStockAndCogs ? -1 : 0;

    public static bool PostsCogs(CashSaleStockPolicy policy)
        => policy == CashSaleStockPolicy.MoveStockAndCogs;

    public static bool BlocksApproval(CashSaleStockPolicy policy)
        => policy == CashSaleStockPolicy.Block;

    /// <summary>คำอธิบายที่หน้าตั้งค่าแสดง — เซิร์ฟเวอร์เป็นเจ้าของข้อความ (ห้ามหน้าเว็บแต่งเอง)</summary>
    public static string Describe(CashSaleStockPolicy policy) => policy switch
    {
        CashSaleStockPolicy.Ignore =>
            "ใบเสร็จ/ใบสำคัญรับที่ขายสินค้าคงคลังโดยตรง (ไม่อ้างใบแจ้งหนี้) จะลงรายได้อย่างเดียว "
            + "— สต๊อกไม่ลด และไม่ลงต้นทุนขาย ⇒ กำไรขั้นต้นของบิลนั้นสูงเกินจริงจนกว่าจะปรับสต๊อก/ต้นทุนเอง "
            + "(เหมาะเมื่อใช้ใบเสร็จเฉพาะงานบริการ)",
        CashSaleStockPolicy.MoveStockAndCogs =>
            "ใบเสร็จ/ใบสำคัญรับที่ขายสินค้าคงคลังโดยตรงจะตัดสต๊อกและลงต้นทุนขาย (Dr ต้นทุนขาย / Cr สินค้าคงเหลือ) "
            + "เหมือนใบกำกับภาษี ⇒ กำไรขั้นต้นถูกทุกช่องทาง · ใบเสร็จที่รับชำระใบแจ้งหนี้และใบมัดจำไม่กระทบ (ค่าแนะนำ)",
        CashSaleStockPolicy.Block =>
            "อนุมัติใบเสร็จ/ใบสำคัญรับที่มีบรรทัดสินค้าคงคลังโดยตรงไม่ได้ — ระบบจะบอกให้ออกใบกำกับภาษี/ใบแจ้งหนี้แทน "
            + "(ทุกการขายสินค้าจึงมีใบกำกับเสมอ) · ใบเสร็จงานบริการและใบเสร็จรับชำระใบแจ้งหนี้ยังออกได้ตามปกติ",
        _ => $"นโยบาย \"{policy}\" ยังไม่มีคำอธิบาย",
    };
}
