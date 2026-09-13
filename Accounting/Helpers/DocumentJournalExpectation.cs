using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตอบคำถามเดียว: <b>"เอกสารใบนี้ต้องมีรายการในสมุดรายวันหรือไม่"</b>
///
/// เดิมคำตอบนี้อยู่ในตัวแปร local <c>autoPostTypes</c> ข้างใน
/// <c>ApproveDocumentAsync</c> ที่เดียว ⇒ เส้นอื่นที่ต้องตอบคำถามเดียวกันไม่มีทาง
/// ถามได้ โดยเฉพาะ <c>PdfGenerationService.LoadGlPostingAsync</c> ซึ่งเมื่อหา JE
/// ไม่เจอจะติดป้าย <b>"(ประมาณการ — ก่อนอนุมัติ)"</b> ให้ทุกกรณี — เป็นการ
/// **เดาสาเหตุแทนผู้ใช้** ที่ไฟล์ CLAUDE.md บันทึกไว้ว่าอันตรายกว่าไม่บอกอะไรเลย:
/// ใบที่ออกเลข §86/4 ไปแล้ว (ภ.พ.30 นับแล้ว) แต่ JE ถูกลบ/ถูกกลับรายการ จะอ่านว่า
/// "ยังไม่อนุมัติ" ⇒ ผู้ใช้ไล่ผิดทาง และ **GL ว่างทั้งที่ภาษีขาย/ซื้อนับไปแล้ว**
/// ซึ่งเป็นอาการที่ต้องดังที่สุด กลับกลายเป็นเงียบที่สุด
///
/// กติกา: ทุกเส้นที่ต้องตัดสินว่า "ควรมี JE ไหม" เรียกตัวนี้ ห้ามพิมพ์รายการชนิด
/// เอกสารซ้ำอีก (สำเนามือ = drift แน่นอน)
/// </summary>
public static class DocumentJournalExpectation
{
    /// <summary>ชนิดเอกสารที่ระบบลงบัญชีให้อัตโนมัติตอนอนุมัติ.
    /// ที่ไม่อยู่ในลิสต์ = เอกสารเชิงปฏิบัติการ (ใบเสนอราคา · ใบวางบิล · PR/PO ·
    /// ใบส่งของ) ซึ่ง **ถูกต้องแล้วที่ไม่มี JE** — ห้ามเตือน</summary>
    public static readonly DocumentType[] PostingTypes =
    {
        DocumentType.Invoice, DocumentType.TaxInvoice,
        DocumentType.DebitNote, DocumentType.CreditNote,
        DocumentType.PurchaseInvoice, DocumentType.Expense,
        DocumentType.Receipt, DocumentType.ReceiptVoucher,
        DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
        // 3-way match: GRN ตั้งค้าง "รับของยังไม่วางบิล" (Dr ค่าใช้จ่าย/Cr GR-NI)
        DocumentType.GoodsReceiptNote,
    };

    /// <summary>ชนิดนี้ลงบัญชีอัตโนมัติไหม (ดูแค่ชนิด ไม่ดูสถานะ/ธงของใบ)</summary>
    public static bool PostsToJournal(DocumentType type) => PostingTypes.Contains(type);

    /// <summary>
    /// ใบนี้ <b>ต้องมี JE ที่ยังมีผลจริง</b> หรือไม่ — เงื่อนไขชุดเดียวกับด่านที่
    /// <c>ApproveDocumentAsync</c> ใช้ตัดสินว่าจะเรียก <c>AutoPostToJournalAsync</c>
    /// บวกกับ "ยังไม่ถูกยกเลิก" (ใบ Voided ถูกกลับรายการแล้ว = ไม่มี JE ที่มีผล
    /// **โดยถูกต้อง** — เตือนตรงนั้นคือคำเตือนที่ฟ้องใบถูกกฎหมายทุกใบ)
    /// </summary>
    /// <param name="isSettlementReceipt">ใบเสร็จหลักฐานรับเงิน — การเงินอยู่ที่
    /// Payment แล้ว (JE Dr เงินสด/Cr ลูกหนี้) อนุมัติใบนี้ = ออกเลข+ลายเซ็นเท่านั้น</param>
    /// <param name="replacesAnotherDocument">ใบกำกับเต็มรูปที่ออก "แทน" ใบเดิม —
    /// เศรษฐกิจของรายการไม่เปลี่ยน post ซ้ำ = รายได้/ภาษีขายเบิ้ล</param>
    public static bool ExpectsLiveJournal(
        DocumentType type, DocumentStatus status,
        bool isSettlementReceipt, bool replacesAnotherDocument)
        => PostsToJournal(type)
           && DocumentStatusRules.IsEffective(status)
           && !isSettlementReceipt
           && !replacesAnotherDocument;
}
