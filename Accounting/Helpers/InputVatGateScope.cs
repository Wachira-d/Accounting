using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"ใบนี้ *เคลมภาษีซื้อ* หรือไม่" — ตัวตัดสินตัวเดียวของด่าน §82/5**
/// (pure ไม่มี I/O)
///
/// <para>═══ ทำไมต้องแยกจาก <see cref="WhtGateScope"/> ═══ รอบ 183 ยุบด่านสองตัว
/// ใน <c>DocumentService.CollectApprovalWarningsAsync</c> (หัก ณ ที่จ่าย + ภาษีซื้อ
/// ต้องห้าม) ให้ถาม <c>WhtGateScope.Applies</c> ตัวเดียวกัน ซึ่งตอบคำถามว่า
/// <b>"ใบนี้แทนการจ่ายเงินให้คู่ค้าไหม"</b> (ท.ป.4/2528 ข้อ 12). สองคำถามนี้
/// <b>ไม่ใช่คำถามเดียวกัน</b> และวันนี้คำตอบตรงกันเพียงโดยบังเอิญ:</para>
/// <list type="bullet">
/// <item><b>ใบรับรองแทนใบเสร็จ (CIL)</b> = การจ่ายจริง (WHT ต้องดู) แต่
///   <b>เคลมภาษีซื้อไม่ได้</b> (§82/5(1) — ไม่มีใบกำกับเต็มรูป) และ
///   <c>TaxService.GenerateVatReport</c> ตัดออกจากภาษีซื้ออยู่แล้ว
///   ⇒ เตือน "ใบนี้ยังตั้งเป็นเคลมได้" บน CIL คือ<b>เตือนใบที่ถูกอยู่แล้ว</b></item>
/// <item><b>ใบสำคัญจ่ายแบบตัดชำระ</b> (<c>RelatedDocumentId != null</c>) ไม่เคลม —
///   ภาษีซื้ออยู่ที่ใบตั้งหนี้ซึ่งผ่านด่านไปแล้ว ⇒ เตือนซ้ำใบที่สอง</item>
/// <item><b>ใบเพิ่มหนี้ฝั่งซื้อ</b> <i>เพิ่ม</i> ภาษีซื้อ ⇒ ต้องเข้าด่าน —
///   และเข้าแม้ตอน<b>แยกฝั่งไม่ได้</b> เพราะ <c>TaxService</c> มีชั้น fallback
///   (ผัง GL ที่ JE ลงจริง · คู่ค้าที่เป็นผู้ขายอย่างเดียว) ที่ยัง<b>พาใบนั้นเข้า
///   ภาษีซื้อได้</b> ⇒ ถ้าที่นี่เงียบเพราะ "ไม่รู้ฝั่ง" จะได้การเคลมเกินที่ไม่มี
///   ใครเห็น (G3: เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน")</item>
/// <item><b>ใบลดหนี้ (ทุกฝั่ง)</b> <i>ลด</i> ภาษีซื้อ (<c>inputVat -= VatAmount</c>)
///   ⇒ <b>ห้ามเข้าด่าน</b> — คำเตือน "ยังตั้งเป็นเคลมได้" บนใบที่กำลังคืนสิทธิ์
///   ชี้ผู้ใช้ผิดทาง และเป็นคำเตือนที่ฟ้องใบถูก (CLAUDE.md F2 ข้อ 8)</item>
/// <item><b>ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า</b> ไม่เคยเข้ารายงานภาษีซื้อเลย
///   (<c>TaxService.cs</c> เขียนไว้เองว่า "PurchaseOrder excluded: no VAT
///   obligation") ⇒ การที่รอบ 183 ทำให้มันหลุดด่านไป **ไม่ใช่การถดถอย**</item>
/// </list>
///
/// <para>═══ ชุดชนิดเอกสารมาจากไหน ═══ ชุดเดียวกับที่
/// <c>TaxService.GenerateVatReport</c> ใช้ตัดสินว่าใบไหนเข้า "ภาษีซื้อ" ของ
/// ภ.พ.30 — และ <b>TaxService เรียกฟังก์ชันนี้โดยตรง</b> จึงไม่มีสำเนาที่สอง
/// ถ้าวันหน้ามีชนิดใหม่เคลมภาษีซื้อได้ ต้องเพิ่มที่นี่ที่เดียว แล้วทั้งด่านเตือน
/// และรายงานจะรู้พร้อมกัน</para>
/// </summary>
public static class InputVatGateScope
{
    /// <summary>ชนิดที่เคลมภาษีซื้อเสมอ (เมื่อมี VAT และบรรทัดยังตั้งเป็นเคลมได้)</summary>
    private static readonly DocumentType[] AlwaysClaims =
    {
        DocumentType.PurchaseInvoice,   // ใบกำกับภาษีซื้อของผู้ขาย
        DocumentType.Expense,           // ค่าใช้จ่ายที่มีใบกำกับ
    };

    /// <summary>ใบนี้ทำให้ "ภาษีซื้อที่ขอเคลม" <b>เพิ่มขึ้น</b> หรือไม่ —
    /// ถ้าใช่ ด่านภาษีซื้อต้องห้าม (§82/5) ต้องทำงาน
    ///
    /// <para>ใบที่<b>ลด</b>การเคลม (ใบลดหนี้) และใบที่<b>ไม่เคลมเลย</b>
    /// (PO/PR/GRN · ใบรับรองแทนใบเสร็จ · ใบสำคัญจ่ายแบบตัดชำระ) คืน
    /// <c>false</c> — การเคลมเกินเกิดขึ้นไม่ได้จากใบเหล่านี้</para></summary>
    /// <param name="type">ชนิดเอกสาร</param>
    /// <param name="cnDnPurchaseSide">ใบเพิ่มหนี้/ลดหนี้อยู่ฝั่งซื้อไหม
    /// (<c>null</c> = แยกฝั่งไม่ได้). <b>ใบเพิ่มหนี้: <c>null</c> นับเข้าด่าน</b>
    /// เพราะรายงานมีชั้น fallback ที่ยังพาเข้าภาษีซื้อได้ — ตรงข้ามกับ
    /// <see cref="WhtGateScope"/> ที่ "ไม่รู้ = ไม่ใช่การจ่าย" เพราะทิศของความ
    /// เสียหายคนละทาง (ที่นั่น = เตือนให้หักภาษีจากคนที่เราไม่ได้จ่าย)</param>
    /// <param name="hasTaxInvoiceReference">ใบสำคัญจ่ายใบนี้ติ๊ก "ใช้ใบกำกับภาษี"
    /// (<c>Document.HasTaxInvoiceReference</c>) หรือไม่ — ชนิดอื่นไม่ใช้ค่านี้</param>
    /// <param name="hasRelatedDocument">ใบนี้อ้างใบต้นทาง (<c>RelatedDocumentId</c>)
    /// หรือไม่ — ใบสำคัญจ่ายที่อ้างใบตั้งหนี้ = ตัดชำระ ไม่ใช่การเคลมรอบใหม่</param>
    public static bool ClaimsInputVat(
        DocumentType type,
        bool? cnDnPurchaseSide = null,
        bool hasTaxInvoiceReference = false,
        bool hasRelatedDocument = false)
    {
        if (AlwaysClaims.Contains(type)) return true;

        // ใบสำคัญจ่ายแบบยืนเดี่ยว (ตั้งหนี้+จ่ายในใบเดียว) ที่อ้างใบกำกับภาษีซื้อ
        // — เงื่อนไขเดียวกับสาขา input VAT ใน TaxService.GenerateVatReport
        if (type == DocumentType.PaymentVoucher)
            return hasTaxInvoiceReference && !hasRelatedDocument;

        // ใบเพิ่มหนี้ฝั่งซื้อ = เพิ่มภาษีซื้อ · "ไม่รู้ฝั่ง" ก็ยังเข้าด่าน (ดูหมายเหตุพารามิเตอร์)
        if (type == DocumentType.DebitNote) return cnDnPurchaseSide != false;

        // ใบลดหนี้ = ลดภาษีซื้อ ⇒ ไม่เข้าด่านทุกฝั่ง (รวมถึง null)
        return false;
    }
}
