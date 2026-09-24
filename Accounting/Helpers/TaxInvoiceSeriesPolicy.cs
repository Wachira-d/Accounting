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

    /// <summary>คำเดียวกันบนเอกสารภาษาอังกฤษ (เทียบแบบไม่สนตัวพิมพ์)</summary>
    public const string TaxInvoiceKeywordEn = "tax invoice";

    /// <summary>บริษัทนี้ใช้กติกา "หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV" ไหม
    ///
    /// <para><b>null = ยังไม่เคยตั้ง → เปิด</b> เพราะเป็นกติกาที่ถูกต้องสำหรับ
    /// กิจการไทยทั่วไป (เล่มใบกำกับชุดเดียว = รายงานภาษีขาย §87 เรียงไม่ขาดช่วง
    /// ในชุดเดียว) ผู้ใช้ไม่ต้องไปหาสวิตช์เอง. ปิดได้โดยเก็บ <c>false</c> ลง
    /// ฐานข้อมูลซึ่งจะไม่ถูกทับอีก — จงใจไม่ใช้ migration ไล่ UPDATE เพราะ
    /// migration รันทุกครั้งที่สตาร์ท ⇒ จะทับเจตนาผู้ใช้ทุกรอบ</para></summary>
    public static bool IsUnifiedSeriesEnabled(bool? setting) => setting ?? true;

    /// <summary>ใบนี้ทำหน้าที่ "ใบกำกับภาษี" ตามกฎหมายหรือไม่ ตัดสินจากหัวที่จะ
    /// พิมพ์จริง
    ///
    /// <para><b>พื้นบังคับ</b>: <c>TaxInvoice</c> ที่มี VAT ถือเป็นใบกำกับเสมอ
    /// ไม่ว่าหัวจะถูก override เป็นอะไร — กันเคสที่ผู้ใช้ตั้งหัวเอง
    /// (<c>template.CustomTitle</c>) แล้วเผลอลบคำนั้นออก ซึ่งจะทำให้ใบกำกับตัวจริง
    /// หลุดออกจากเล่มหลักโดยไม่ตั้งใจ</para>
    ///
    /// <para><b>พื้นบังคับข้อสอง — ขายอัตรา 0% (§80/1)</b> (รอบ 193 · ฝ่ายค้านรอบสอง R2-C7): ผู้จด VAT ที่ขายส่งออก/บริการ
    /// ใช้ต่างประเทศ<b>ต้องออกใบกำกับภาษีอัตรา 0</b> (กฎเหล็ก #2 D) — ต่างจากยกเว้น §81 (<c>VatRate = -1</c>) ที่ห้ามออก.
    /// เดิมพื้นดูแค่ <c>VatAmount &gt; 0</c> ⇒ ผู้เรียกที่ไม่มีหัวกระดาษ (Integration) ตรึงใบ 0% เป็น "ไม่ใช่ใบกำกับ"
    /// แล้ว e-Tax ถูกข้าม/ถูกปฏิเสธทั้งที่กระดาษพิมพ์ "ใบกำกับภาษี". ใช้ได้เฉพาะเมื่อผู้เรียกยืนยันว่าบริษัทจด VAT
    /// (<paramref name="companyVatRegistered"/> — ผู้ไม่จดออกใบกำกับไม่ได้ §86 แม้ทุกบรรทัดเป็น 0) · ค่าเริ่มต้น <c>false</c>
    /// = พฤติกรรมเดิมของผู้เรียกที่ไม่ส่ง (POS)</para></summary>
    public static bool CarriesTaxInvoiceRole(Document doc, string? resolvedTitle, bool companyVatRegistered = false)
        => (doc.DocumentType == DocumentType.TaxInvoice
               && (doc.VatAmount > 0 || (companyVatRegistered && IsZeroRatedFullTaxInvoice(doc))))
           || (resolvedTitle?.Contains(TaxInvoiceKeyword, StringComparison.Ordinal) ?? false);

    /// <summary>ใบกำกับขาย 0% (§80/1) ที่กระดาษพิมพ์หัว "ใบกำกับภาษี" — ตรงกับ <c>PdfGenerationService.ComputeDocumentTitle</c>:
    /// TaxInvoice ที่ VAT = 0 และผู้ซื้อ<b>ไม่</b>ปฏิเสธใบกำกับ/ไม่ใช่ walk-in คงหัว "ใบกำกับภาษี" (ถ้าปฏิเสธ → หัว
    /// "ใบเสร็จรับเงิน" ⇒ ไม่ใช่ใบกำกับ) · ต้องมีบรรทัดอัตรา 0 จริงอย่างน้อยหนึ่งบรรทัด — ใบที่ทุกบรรทัดยกเว้น §81
    /// (<c>-1</c>) หรือไม่มีบรรทัด <b>ไม่</b>นับ</summary>
    public static bool IsZeroRatedFullTaxInvoice(Document doc)
        => doc.DocumentType == DocumentType.TaxInvoice
           && doc.VatAmount == 0m
           && !doc.BuyerDeclinedTaxInvoice
           && doc.Contact?.IsWalkInCustomer != true
           && doc.Lines.Any(l => !l.IsDeleted && l.VatRate == 0m);

    /// <summary>ใบที่ "หัวประกาศตัวเป็นใบกำกับภาษี" ทั้งที่ชนิดเอกสารเข้าเล่ม TIV
    /// ไม่ได้ — ต้อง**บล็อกตอนอนุมัติ** ไม่ใช่ปล่อยผ่าน
    ///
    /// <para>ที่มา: <c>ApproveDocumentAsync</c> ตรึง <c>IsTaxInvoiceByLaw</c> จากหัว
    /// ให้ทุกชนิด แต่ <see cref="SeriesTypeOverride"/> จงใจข้าม <c>Invoice</c> ⇒
    /// ใบแจ้งหนี้ที่หัวถูกตั้งเอง (CustomTitle / titleOverrides) เป็น
    /// "ใบแจ้งหนี้/ใบกำกับภาษี" จะถูกประทับว่าเป็นใบกำกับตามกฎหมาย **แต่ถือเลข
    /// INV- นอกเล่ม** ไม่ผ่านด่าน §86/4 และออก e-Tax ไม่ได้ — กระดาษประกาศตัว
    /// เป็นใบกำกับ (ลูกค้าเอาไปเคลมภาษีซื้อ) โดยไม่มีคุณสมบัติของใบกำกับสักข้อ</para>
    ///
    /// <para>จำกัดเฉพาะ <c>Invoice</c> — <b>ห้าม</b>ขยายไป CreditNote/DebitNote:
    /// §86/9-10 ให้ถือว่าใบเพิ่มหนี้/ใบลดหนี้เป็นใบกำกับภาษีอยู่แล้ว บางกิจการ
    /// พิมพ์หัว "ใบลดหนี้ (ใบกำกับภาษี)" ซึ่งถูกกฎหมาย บล็อกไม่ได้</para></summary>
    public static bool IsTaxTitleOnPlainInvoice(DocumentType type, string? resolvedTitle)
    {
        if (type != DocumentType.Invoice || string.IsNullOrWhiteSpace(resolvedTitle))
            return false;
        // ⚠️ ต้องจับหัว**ภาษาอังกฤษ**ด้วย — เอกสารที่ตั้ง DocumentLanguage=en
        // จะได้หัวจาก CustomTitleEn ("Tax Invoice") ซึ่งไม่มีคำไทยเลย ⇒
        // ลอดด่านได้ทั้งที่กระดาษประกาศตัวเป็นใบกำกับ (เคสขายส่งออก/ลูกค้า
        // ต่างชาติ ซึ่งพบบ่อยที่สุดของหัวอังกฤษพอดี)
        return resolvedTitle.Contains(TaxInvoiceKeyword, StringComparison.Ordinal)
            || resolvedTitle.Contains(TaxInvoiceKeywordEn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ข้อความบล็อก — ต้องบอกทางไปต่อทั้งสองทาง (ห้ามตันเฉย ๆ)</summary>
    public const string PlainInvoiceTaxTitleBlockedMessage =
        "อนุมัติไม่ได้ — หัวเอกสารของ \"ใบแจ้งหนี้\" ถูกตั้งให้มีคำว่า \"ใบกำกับภาษี\" "
        + "แต่ใบแจ้งหนี้ไม่ได้อยู่ในเล่มเลขใบกำกับ (TIV) จึงจะได้กระดาษที่ประกาศตัวเป็น"
        + "ใบกำกับโดยเลขไม่เรียงในเล่ม ไม่ผ่านด่าน §86/4 และออก e-Tax ไม่ได้. ทางแก้: "
        + "(1) ถ้าต้องการใบกำกับจริง ให้สร้างเอกสารประเภท \"ใบแจ้งหนี้/ใบกำกับภาษี\" แทน "
        + "(ได้เลขชุด TIV ถูกต้อง) หรือ (2) แก้หัวเอกสารที่ ตั้งค่า → หัวเรื่องเอกสาร / "
        + "เทมเพลต ให้ไม่มีคำว่า \"ใบกำกับภาษี\" แล้วอนุมัติใหม่";

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
