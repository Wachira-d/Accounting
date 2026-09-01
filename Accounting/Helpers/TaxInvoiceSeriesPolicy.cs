using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกา "ใบไหนใช้เลขชุดใบกำกับภาษี (TIV)" — ตัวเดียวของระบบ
///
/// ═══ ปัญหาที่แก้ ═══
/// เดิมตัวย่อของเลขมาจาก <c>DocumentType</c> (ชนิดข้อมูล) ล้วน ๆ ส่วนคำว่า
/// "ใบกำกับภาษี" บนหัวกระดาษมาจาก **บทบาททางกฎหมาย** ที่คำนวณจากธงคนละชุด ⇒
/// ใบที่หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน" เหมือนกันเป๊ะ ได้เลขคนละชุด
/// (TIV- หรือ REC-) แล้วแต่ทางที่ผู้ใช้กดเข้า และมีเคสกลับด้าน (TaxInvoice ที่
/// VAT=0 หัวพิมพ์ "ใบเสร็จรับเงิน" แต่ได้เลข TIV-)
///
/// ผลคือ **รายงานภาษีขาย §87 มีเลขปนกันหลายชุด** ทั้งที่มันคือรายการใบกำกับ
/// ล้วน ๆ — สรรพากรขอ "เล่มใบกำกับ" ต้องหยิบหลายเล่ม
///
/// ═══ กติกา ═══
/// <b>หัวใบมีคำว่า "ใบกำกับภาษี" (จะมี "/ใบเสร็จรับเงิน" ต่อท้ายหรือไม่ก็ได้)
/// → เลขชุด TIV เสมอ · ไม่มีคำนั้น → ชุดของตัวเอง (REC/RV)</b>
///
/// ═══ ทำไมปลอดภัย ═══
///   • **ไม่แตะ <c>DocumentType</c>** ซึ่งเป็นตัวตัดสิน JE / การนับ ภ.พ.30 /
///     สายแปลงเอกสาร — เปลี่ยนแค่ *ตัวย่อของเลข*. ตัวนับเลข
///     (<c>DocumentNumberGenerator</c>) นับจาก prefix ไม่ใช่ชนิดเอกสารอยู่แล้ว
///     ⇒ สองชนิดใช้ TIV ร่วมกันได้และยังเรียงไม่ขาดช่วงต่อ prefix ตาม §86/4
///   • ตัดสินจาก **หัวที่ resolver ตัวเดียวกับกระดาษคำนวณให้** ไม่ใช่กติกาสำเนา
///     ที่สอง (<c>PdfGenerationService.ResolveDocumentTitleAsync</c>)
///   • ตัดสิน ณ ตอน "อนุมัติ" ซึ่งเป็นจังหวะเดียวกับที่ออกเลข และข้อมูลที่ใช้
///     (VAT · ธงขายสด · ผู้ซื้อครบ §86/4 · ใบต้นทาง) นิ่งแล้วทั้งหมด
///
/// ⚠️ ใบที่ออกเลขไปแล้วห้ามเปลี่ยนเลขย้อนหลัง (§86/4) — กติกานี้มีผลกับใบใหม่
/// เท่านั้น
/// </summary>
public static class TaxInvoiceSeriesPolicy
{
    /// <summary>คำที่กฎหมายบังคับให้ปรากฏบนใบกำกับภาษี (§86/4 (1))</summary>
    public const string TaxInvoiceKeyword = "ใบกำกับภาษี";

    /// <summary>ใบนี้ทำหน้าที่ "ใบกำกับภาษี" ตามกฎหมายหรือไม่ ตัดสินจากหัวที่จะ
    /// พิมพ์จริง
    ///
    /// <para><b>พื้นบังคับ</b>: <c>TaxInvoice</c> ที่มี VAT ถือเป็นใบกำกับเสมอ
    /// ไม่ว่าหัวจะถูก override เป็นอะไร — กันเคสที่ผู้ใช้ตั้งหัวเอง
    /// (<c>template.CustomTitle</c>) แล้วเผลอลบคำนั้นออก ซึ่งจะทำให้ใบกำกับตัวจริง
    /// หลุดออกจากเล่มหลักโดยไม่ตั้งใจ</para></summary>
    public static bool CarriesTaxInvoiceRole(Document doc, string? resolvedTitle)
        => (doc.DocumentType == DocumentType.TaxInvoice && doc.VatAmount > 0)
           || (resolvedTitle?.Contains(TaxInvoiceKeyword, StringComparison.Ordinal) ?? false);

    /// <summary>ชนิดที่ใช้ "เลือกตัวย่อของเลข" (ไม่ใช่ชนิดจริงของเอกสาร) —
    /// <c>null</c> = ใช้ชนิดของตัวเองตามเดิม
    ///
    /// <list type="bullet">
    /// <item>มีบทบาทใบกำกับ → TIV (ครอบทั้ง TaxInvoice / Receipt / ReceiptVoucher
    ///   ที่เก็บ VAT จริง เช่นใบเสร็จที่เป็นใบกำกับ ณ วันรับเงิน §78/1)</item>
    /// <item>เป็น <c>TaxInvoice</c> แต่ไม่มีบทบาทใบกำกับ (VAT=0 → หัวพิมพ์
    ///   "ใบเสร็จรับเงิน") → REC — เคสกลับด้านที่เดิมได้เลข TIV ทั้งที่กระดาษ
    ///   ไม่ใช่ใบกำกับ</item>
    /// <item>Receipt / ReceiptVoucher ที่ไม่ใช่ใบกำกับ → <b>คงชุดของตัวเอง</b>
    ///   (ใบสำคัญรับเป็นกระดาษคนละอย่างกับใบเสร็จ ห้ามยุบรวมกัน)</item>
    /// <item>ชนิดอื่น (ใบแจ้งหนี้ / ใบวางบิล / ฝั่งซื้อ) ไม่เกี่ยวกับกติกานี้เลย</item>
    /// </list></summary>
    public static DocumentType? SeriesTypeOverride(Document doc, bool carriesTaxInvoiceRole)
    {
        if (doc.DocumentType is not (DocumentType.TaxInvoice
            or DocumentType.Receipt or DocumentType.ReceiptVoucher))
            return null;
        if (carriesTaxInvoiceRole)
            return doc.DocumentType == DocumentType.TaxInvoice ? null : DocumentType.TaxInvoice;
        return doc.DocumentType == DocumentType.TaxInvoice ? DocumentType.Receipt : null;
    }
}
