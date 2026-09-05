using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ชุดชนิดเอกสารที่ **นับเป็นลูกหนี้/เจ้าหนี้** — แหล่งเดียวให้ Dashboard · Aging · Sub-ledger
/// reconciliation · Cash-flow forecast · Customer statement ใช้ร่วม (เดิมเขียนมือ ≥5 ชุดที่ต่างกัน
/// ทีละนิด — ERP_REVIEW_2026-09-05 F-02/F-08/F-09)
///
/// ═══ ทำไมไม่มี BillingNote ═══
/// ใบวางบิลเป็นเอกสาร operational ที่ "รวมใบแจ้งหนี้หลายใบไปทวง" — **ไม่มี JE** (AutoPost ไม่รู้จัก)
/// และครอบใบแจ้งหนี้ที่ตั้งลูกหนี้ไว้แล้ว ⇒ นับเข้าลูกหนี้ = นับซ้ำ และทำให้ AR ฝั่งเอกสาร > GL 11310
/// เสมอเมื่อใช้ใบวางบิล (เจ้าของโปรเจกต์ยืนยัน 2026-09-05: "ปรับให้ถูกต้อง")
/// </summary>
public static class ArApScope
{
    /// <summary>เอกสารที่ **เพิ่ม** ลูกหนี้ (ฝั่งขาย) — CN ลด · Receipt/RV ตัด (ไม่อยู่ในชุดนี้)</summary>
    public static readonly DocumentType[] ReceivableTypes =
    {
        DocumentType.Invoice,
        DocumentType.TaxInvoice,
        DocumentType.DebitNote,
    };

    /// <summary>เอกสารที่ **เพิ่ม** เจ้าหนี้ (ฝั่งซื้อ) ในความหมาย "ตั้งหนี้" — PV/CIL เป็นหลักฐานจ่าย
    /// ไม่ใช่การตั้งหนี้ (aging ตัดออกอยู่แล้วเพราะ voucher BalanceDue>0 จากข้อมูลไม่ครบเคยขึ้น
    /// aging หลอก) · DebitNote อยู่ได้สองฝั่ง ผู้เรียกต้องกรองด้วย CnDnPurchaseSideOverride</summary>
    public static readonly DocumentType[] PayableTypes =
    {
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.DebitNote,
    };

    public static bool IsReceivable(DocumentType type) => ReceivableTypes.Contains(type);
    public static bool IsPayable(DocumentType type) => PayableTypes.Contains(type);
}
