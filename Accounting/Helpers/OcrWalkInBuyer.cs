using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ใบขายจากสแกนที่อ่านผู้ซื้อไม่ได้: ใช้ "ลูกค้าเงินสด (walk-in)" ได้ไหม** (รอบ 201 ทีม OC · C-24 · คำตัดสินข้อ 97) — pure
///
/// <para>คำตัดสิน: <b>คงบล็อก</b> (ข้อความบอกทางไปต่อของ C-03) · เลือก walk-in ได้<b>เฉพาะเมื่อเอกสารเป้าหมายไม่ใช่ใบกำกับภาษีเต็มรูป</b> —
/// §86/4 บังคับชื่อ/ที่อยู่ผู้ซื้อบนใบกำกับ · ผู้ติดต่อ walk-in ถูกยกเว้นด่านผู้ซื้อตอนอนุมัติ (<c>TaxInvoiceCompletenessChecker.BuyerBlockingFields</c>)
/// และหัวกระดาษลดเป็นใบเสร็จเอง ⇒ ถ้าให้ใบกำกับจากสแกนผูก walk-in ได้ = กระดาษที่เราออกจริง (ใบกำกับมีชื่อผู้ซื้อ) กลายเป็นใบเสร็จในระบบเงียบ ๆ</para>
///
/// <para>"ใบกำกับเต็มรูป" ตัดสินจาก<b>กฎหมาย</b> ไม่ใช่ค่าตั้งบริษัท: ใบกำกับภาษี · ใบเพิ่มหนี้/ใบลดหนี้ (§86/9-10 ถือเป็นใบกำกับ) · ใบเสร็จ/ใบสำคัญรับ
/// ที่มี VAT (ใบกำกับโดยสภาพ — กติกาเดียวกับ <c>MustEnforceBuyerFields</c> ส่วนที่ไม่ขึ้นกับค่าตั้ง) · อื่น ๆ (ใบแจ้งหนี้ · ใบเสร็จไม่มี VAT · ใบเสนอราคา …) = ได้</para>
/// </summary>
public static class OcrWalkInBuyer
{
    public const string RuleCode = "OCR-WALKIN-FULL-TAX-INVOICE";
    public const string NotSalesRuleCode = "OCR-WALKIN-NOT-SALES";

    /// <summary>เอกสารเป้าหมายเป็นใบกำกับภาษีเต็มรูปตามกฎหมายไหม</summary>
    private static bool IsFullTaxInvoiceTarget(DocumentType target, decimal vatAmount)
        => target is DocumentType.TaxInvoice or DocumentType.DebitNote or DocumentType.CreditNote
           || ((target is DocumentType.Receipt or DocumentType.ReceiptVoucher) && vatAmount > 0m);

    /// <summary>ข้อความไทยเมื่อ<b>ห้าม</b>ใช้ walk-in (บอกเหตุ + ทางไปต่อ) · null = ใช้ได้</summary>
    public static string? BlockReason(DocumentType target, decimal vatAmount)
        => IsFullTaxInvoiceTarget(target, vatAmount)
            ? $"ใช้ “ลูกค้าเงินสด (walk-in)” กับ{DocumentTypeNames.Title(target, null)}{(vatAmount > 0m ? "ที่มีภาษีมูลค่าเพิ่ม" : "")}ไม่ได้ — ใบกำกับภาษีเต็มรูปต้องมีชื่อ/ที่อยู่ผู้ซื้อ (ป.รัษฎากร §86/4) · "
              + "ทางไปต่อ: กรอก “ชื่อผู้ซื้อ” (และเลขผู้เสียภาษีถ้ามี) ในหน้าตรวจสแกน หรือเลือกลูกค้าที่ “จับคู่ผู้ติดต่อ” · "
              + "ถ้ากระดาษจริงไม่ใช่ใบกำกับ ให้เปลี่ยน “เอกสารที่จะสร้าง” เป็นใบเสร็จ/ใบแจ้งหนี้ก่อน"
            : null;

    /// <summary>ข้อความเมื่อสแกนเป็นฝั่งซื้อ (walk-in เป็นลูกค้า ไม่ใช่ผู้ขาย)</summary>
    public const string NotSalesMessage =
        "“ลูกค้าเงินสด (walk-in)” ใช้ได้กับเอกสารฝั่งขายเท่านั้น — สแกนนี้เป็นเอกสารฝั่งซื้อ (ผู้ขายคือคู่ค้า) · เลือกผู้ขายที่ “จับคู่ผู้ติดต่อ” แทน";
}
