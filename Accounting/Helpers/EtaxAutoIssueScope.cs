using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุที่ "ไม่ออก e-Tax อัตโนมัติ" ให้เอกสารใบนี้ <b>โดยเจตนา</b> (ไม่ใช่ความล้มเหลว)</summary>
public enum EtaxAutoSkip
{
    /// <summary>ไม่มีเหตุข้าม — ใบนี้ควรมี e-Tax (ถ้าบริษัทเปิด e-Tax)</summary>
    None = 0,
    /// <summary>ชนิดเอกสารที่ไม่มีทางเป็นใบกำกับภาษี (ใบแจ้งหนี้ · ใบสั่งซื้อ · ใบซื้อ ฯลฯ)</summary>
    NotEtaxType = 1,
    /// <summary>ยังไม่ออกจริง (ร่าง · รออนุมัติ · ถูกปฏิเสธ)</summary>
    NotIssued = 2,
    /// <summary>ยกเลิกแล้ว</summary>
    Voided = 3,
    /// <summary>ใบรับมัดจำที่ VAT ยังพักรอ (ยังไม่ถึงจุดรับผิด §78) — ใบกำกับตัวจริงออกตอนส่งมอบ</summary>
    DeferredDepositReceipt = 4,
    /// <summary>ใบเสร็จที่รับชำระใบกำกับภาษีที่ออกไปแล้ว — ใบกำกับตัวจริงคือใบต้นทาง</summary>
    ReceiptSettlesTaxInvoice = 5,
    /// <summary>ใบลด/เพิ่มหนี้ฝั่งซื้อ — ผู้ขายเป็นผู้ออก เราออก e-Tax แทนไม่ได้</summary>
    PurchaseSideAdjustment = 6,
}

/// <summary>
/// **ขอบเขตของ "e-Tax อัตโนมัติหลังออกเอกสาร" — ตัวตัดสินตัวเดียว** (รอบ 193 · ผลตรวจ S-02)
///
/// <para>═══ ที่มา ═══ เดิม <c>TryAutoGenerateEtaxAsync</c> กรองแค่ "ชนิดเอกสาร" แล้วโยนให้
/// <c>EtaxInvoiceService.GenerateAsync</c> ตัดสินที่เหลือด้วยการ <b>throw</b> ⇒ ใบที่ "โดยเจตนาไม่ใช่ใบกำกับ"
/// (ใบรับมัดจำที่ VAT พักรอ · ใบเสร็จรับชำระใบกำกับ · ใบลดหนี้ฝั่งซื้อ) ถูกประทับ <c>[ETAX-AUTO-FAILED]</c>
/// ทั้งที่ไม่มีอะไรล้ม = คำเตือนที่ฟ้องใบถูก (F2 ข้อ 8 — ผู้ใช้จะเรียนรู้ที่จะไม่อ่านป้ายนี้)
/// และพอทางเข้าที่ประทับ Approved เอง (POS · Integration) มาเรียก hook เดียวกัน ปัญหาจะขยายตาม</para>
///
/// <para>กติกา: <see cref="Judge"/> แยก "ข้ามโดยเจตนา" (เงียบได้ — ไม่ใช่ความล้มเหลว) ออกจาก "ควรมีแต่ออกไม่ได้"
/// (ข้อมูลผู้ซื้อ/บริษัทไม่ครบ · ใบลดหนี้ไม่อ้างใบเดิม — ต้องดัง) · <c>GenerateAsync</c> ใช้ predicate ชุดเดียวกัน
/// (<see cref="IsEtaxType"/> · <see cref="IsDeferredDepositReceipt"/> · <see cref="AdjustmentNoteAccount.SourceIsPurchaseSide"/>)
/// จึงไม่มีสำเนาที่สอง</para>
/// </summary>
public static class EtaxAutoIssueScope
{
    /// <summary>ชนิดเอกสารที่เป็น "ใบกำกับภาษี" ได้ตามกฎหมาย (T01–T04) — ชุดเดียวของทั้งระบบ</summary>
    public static readonly DocumentType[] EtaxTypes =
    {
        DocumentType.TaxInvoice,
        DocumentType.Receipt,
        DocumentType.DebitNote,
        DocumentType.CreditNote,
    };

    /// <summary>ชนิดนี้ออก e-Tax ได้ไหม (ยังไม่ดูบทบาทรายใบ)</summary>
    public static bool IsEtaxType(DocumentType type) => EtaxTypes.Contains(type);

    /// <summary>ใบรับมัดจำที่ VAT ยังพักที่ 21913 (ยังไม่เกิดจุดรับผิด) — ยังไม่ใช่ใบกำกับภาษี</summary>
    public static bool IsDeferredDepositReceipt(bool isDeposit, bool depositOutputVatDeferred, DateTime? depositOutputVatRecognizedAt)
        => isDeposit && depositOutputVatDeferred && depositOutputVatRecognizedAt == null;

    /// <summary>ต้องรู้ชนิดของใบต้นทางก่อนตัดสินไหม (ผู้เรียกจะได้ไม่ต้อง query เปล่า ๆ)</summary>
    public static bool NeedsRelatedType(DocumentType type, Guid? relatedDocumentId)
        => relatedDocumentId.HasValue
           && type is DocumentType.Receipt or DocumentType.CreditNote or DocumentType.DebitNote;

    /// <summary>ใบนี้ "ข้ามโดยเจตนา" ไหม — <see cref="EtaxAutoSkip.None"/> = ควรมี e-Tax (ออกไม่ได้ = ต้องดัง)</summary>
    /// <param name="relatedDocumentType">ชนิดของใบต้นทาง (<c>RelatedDocumentId</c>) · <c>null</c> = ไม่มี/ไม่รู้
    /// ⇒ ไม่ข้าม (ให้ตัวออก e-Tax ตัดสินแล้วดังถ้าขาดใบอ้างอิง — "ไม่รู้" ห้ามตกเป็น "เงียบ")</param>
    public static EtaxAutoSkip Judge(DocumentType type, DocumentStatus status,
        bool isDeposit, bool depositOutputVatDeferred, DateTime? depositOutputVatRecognizedAt,
        DocumentType? relatedDocumentType)
    {
        if (!IsEtaxType(type)) return EtaxAutoSkip.NotEtaxType;
        if (!DocumentStatusRules.IsIssued(status)) return EtaxAutoSkip.NotIssued;
        if (status == DocumentStatus.Voided) return EtaxAutoSkip.Voided;
        if (type == DocumentType.Receipt)
        {
            if (IsDeferredDepositReceipt(isDeposit, depositOutputVatDeferred, depositOutputVatRecognizedAt))
                return EtaxAutoSkip.DeferredDepositReceipt;
            if (relatedDocumentType == DocumentType.TaxInvoice)
                return EtaxAutoSkip.ReceiptSettlesTaxInvoice;
        }
        if (type is DocumentType.CreditNote or DocumentType.DebitNote
            && relatedDocumentType is DocumentType rel
            && AdjustmentNoteAccount.SourceIsPurchaseSide(rel))
            return EtaxAutoSkip.PurchaseSideAdjustment;
        return EtaxAutoSkip.None;
    }
}
