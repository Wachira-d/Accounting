namespace Accounting.Helpers;

/// <summary>
/// คำเตือนก่อนอนุมัติ: "ภาษีซื้อใบนี้จะถูก <b>พักที่ 11640</b> (ยังเคลม ภ.พ.30 ไม่ได้)" —
/// ตัดสินด้วยลำดับเดียวกับ <c>DocumentService.ResolveInputVatAccountAsync</c> ที่ใช้ลงบัญชีจริง
///
/// <para><b>ทำไมต้องมี</b> (รอบ 190 ทีม C · ข้อ 3 ของเจ้าของ): หน้าบันทึกใบสำคัญจ่ายมี
/// ตัวตรวจ §86/4 ฝั่ง JS ที่อ่าน "ช่องบนฟอร์ม" ส่วนตัวลงบัญชีอ่าน "ข้อมูลผู้ติดต่อในฐาน"
/// (<c>TaxInvoiceCompletenessChecker.Evaluate</c>) — สองแหล่งตอบไม่ตรงกันได้ทั้งสองทิศ:
/// (ก) จอเตือนแดงทั้งที่ลงบัญชีเคลมได้ (เคสที่เจ้าของเจอ) และ (ข) <b>จอเงียบทั้งที่ลงบัญชี
/// พัก 11640</b> (ผู้ติดต่อไม่มีที่อยู่ · เลข 13 หลักที่ checksum ผิด · ใบจาก API ที่ไม่มี
/// เลขที่/วันที่ใบกำกับ) — ทิศ (ข) คือเงินภาษีซื้อที่เคลมได้ถูกพักเงียบ ๆ จนเลยกรอบ 6 เดือน
/// §82/3 แล้วถูกล้างเป็นค่าใช้จ่าย. ด่านอนุมัติเดิมเตือนเฉพาะ "Expense ที่ไม่ติ๊กใบกำกับ"</para>
///
/// <para>ไม่ใช่การบล็อก — การพัก 11640 เป็นสถานะที่ถูกกฎหมายและแก้ทีหลังได้ (เติมข้อมูลครบ
/// → ระบบย้าย 11640→11610 ให้) แต่ผู้ใช้ต้อง<b>รู้ก่อนกด</b> (หลัก "ล้มดัง")</para>
/// </summary>
public static class InputVatParkingNotice
{
    /// <summary>
    /// คืนข้อความเตือน หรือ <c>null</c> เมื่อภาษีซื้อจะไม่ถูกพักด้วยเหตุ §86/4 ไม่ครบ
    /// </summary>
    /// <param name="isPurchaseInputVatType">PI / Expense / PaymentVoucher (ชนิดที่ลงภาษีซื้อผ่าน
    /// <c>ResolveInputVatAccountAsync</c>)</param>
    /// <param name="isForeignService">§83/6 — พัก 11640 ด้วยเหตุอื่น (มีคำเตือนของตัวเอง)</param>
    /// <param name="inputVatAccountCodeOverride">ผู้ใช้เลือกผังเอง → ตัวลงบัญชีใช้ค่านั้น ไม่ดู §86/4</param>
    /// <param name="claimableVat">ผลรวม VAT ของบรรทัดที่เคลมได้ (0 = ไม่มีอะไรไปพัก)</param>
    /// <param name="claimIntentDeclared">ผู้ใช้ติ๊ก "มีใบกำกับภาษีซื้อ — ขอเครดิต ภ.พ.30"
    /// (<c>HasTaxInvoiceReference</c>) = ประกาศเจตนาเคลมแล้ว ⇒ การพักเงียบคือ "สัญญาที่ผิด".
    /// ใบที่ไม่ได้ประกาศ (เช่น PV จากใบเบิกพนักงาน · Expense ไม่ติ๊ก — ตัวหลังมีคำเตือนของตัวเอง
    /// อยู่แล้ว) <b>ไม่เตือนที่นี่</b>: ทางอนุมัติอัตโนมัติหลายเส้น (ใบเบิก · รายการประจำ) อนุมัติ
    /// โดยไม่ยืนยันคำเตือน ถ้าเตือนใบเหล่านั้นด้วย = เส้นเหล่านั้นล้มทั้งเส้น (ทิศตรงข้ามที่ต้องไม่เกิด)</param>
    /// <param name="missingFields">ผลของ <c>TaxInvoiceCompletenessChecker.Evaluate</c> (ว่าง = ครบ)</param>
    /// <param name="missingContactFields">ส่วนที่ต้องแก้ที่ข้อมูลผู้ติดต่อ</param>
    public static string? Build(
        bool isPurchaseInputVatType,
        bool isForeignService,
        string? inputVatAccountCodeOverride,
        decimal claimableVat,
        bool claimIntentDeclared,
        IReadOnlyList<string> missingFields,
        IReadOnlyList<string> missingContactFields)
    {
        if (!isPurchaseInputVatType) return null;
        if (isForeignService) return null;
        if (!string.IsNullOrWhiteSpace(inputVatAccountCodeOverride)) return null;
        if (claimableVat <= 0) return null;
        if (!claimIntentDeclared) return null;
        if (missingFields == null || missingFields.Count == 0) return null;

        var where = missingContactFields != null && missingContactFields.Count > 0
            ? $" · ส่วนที่ต้องแก้ที่ข้อมูลผู้ติดต่อ: {string.Join(", ", missingContactFields)}"
            : "";
        return $"ภาษีซื้อ {claimableVat:N2} บาท จะถูกพักที่ 11640 \"ภาษีซื้อยังไม่ถึงกำหนด\" (ยังเคลม ภ.พ.30 ไม่ได้) "
            + $"เพราะใบกำกับยังไม่ครบ §86/4 — ขาด: {string.Join(", ", missingFields)}{where}. "
            + "เติมให้ครบก่อนอนุมัติ หรืออนุมัติไปก่อนแล้วเติมภายหลังที่หน้า \"ภาษีซื้อยังไม่ถึงกำหนด\" "
            + "(ต้องเคลมภายใน 6 เดือน §82/3 มิฉะนั้นระบบล้างเป็นค่าใช้จ่าย)";
    }
}
