using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลการตรวจการชนกันของข้อมูลขาเข้าจากพาร์ตเนอร์ — ระดับความรุนแรง</summary>
public enum PartnerConflictLevel
{
    /// <summary>ไม่ชน — เขียนได้ตามที่ส่งมา</summary>
    None = 0,
    /// <summary>เขียนได้ แต่มีบางช่อง<b>ไม่ถูกนำไปใช้</b> — ต้องบอกพาร์ตเนอร์เสมอ</summary>
    Warning = 1,
    /// <summary>ไม่เขียนทั้งแถว — พาร์ตเนอร์ต้องแก้ที่ต้นทางแล้วส่งใหม่</summary>
    Blocked = 2,
}

/// <summary>คำตัดสินหนึ่งข้อ — <c>Code</c> ให้เครื่องอ่าน · <c>Message</c> ให้คนอ่าน
/// (ต้องบอกว่า "ชนกับอะไร" และ "ทำอะไรได้แทน" เสมอ — G4/F2 ข้อ 8)</summary>
public readonly record struct PartnerConflict(
    PartnerConflictLevel Level, string Code, string Message)
{
    public static readonly PartnerConflict Ok = new(PartnerConflictLevel.None, "", "");
    public bool IsBlocked => Level == PartnerConflictLevel.Blocked;
    public bool HasIssue => Level != PartnerConflictLevel.None;
}

/// <summary>
/// **ด่านกันข้อมูลขาเข้าจากพาร์ตเนอร์ "ทับของเดิมเงียบ ๆ / สร้างซ้ำเงียบ ๆ"**
/// (DECISION_AUDIT_2026-09-18 §9.3 D-2 — "ด่านต้องมาก่อน migration เสมอ")
///
/// ═══ ทำไมต้องมี ═══
/// <para><c>/api/v1</c> เป็นทางเข้าที่<b>เครื่องยิงเข้ามาเป็นรอบ ๆ อัตโนมัติ</b> ⇒
/// ช่องโหว่หนึ่งจุดถูกยิงซ้ำทุกชั่วโมงตลอดไป และ "การ sync รอบถัดไปเขียนทับสิ่งที่
/// เพิ่งซ่อม" คือเหตุผลที่ §9.3 D-2 สั่งให้ทำด่านนี้<b>ก่อน</b> migration ใด ๆ</para>
///
/// ═══ กติกา (D-2) ═══
/// <para>"ให้ประกาศของพาร์ตเนอร์ชนะ <b>เว้นแต่ขัดกับทะเบียนที่ checksum ผ่าน</b>"
/// — เลขผู้เสียภาษี 13 หลักที่ผ่าน mod-11 คือหลักฐานระดับทะเบียน ไม่ใช่ข้อความ
/// ที่ระบบต้นทางพิมพ์มา ⇒ อะไรที่ขัดกับมัน<b>ต้องหยุดและถาม</b> ไม่ใช่ทับ</para>
///
/// <para>G5 ("ทิศที่ความเสียหายมองเห็นและแก้ทัน"): การ<b>ปฏิเสธ</b>ทำให้พาร์ตเนอร์
/// ได้ข้อความกลับทันทีและแก้ที่ต้นทางได้ · การ<b>ทับเงียบ ๆ</b> ทำให้คู่ค้าสองราย
/// กลายเป็นรายเดียว / เลขผู้เสียภาษีบนใบกำกับที่ออกไปแล้วเปลี่ยนไปโดยไม่มีใครรู้
/// ซึ่งไปโผล่ตอนสรรพากรตรวจ — สายเกินแก้</para>
///
/// <para>G6: pure · ไม่มี I/O · ผู้เรียกไปคิวรีข้อเท็จจริงมาให้ · ไม่ throw</para>
/// </summary>
public static class PartnerSyncConflict
{
    /// <summary>ธงที่พาร์ตเนอร์ส่งมาเพื่อ "ยืนยันว่าตั้งใจเปลี่ยนเลขผู้เสียภาษีจริง" —
    /// ทางไปต่อของผู้ที่ถูกด่านนี้กัน (F2 ข้อ 8)</summary>
    public const string OverrideFlagName = "allowTaxIdChange";

    /// <summary>
    /// ผู้ติดต่อขาเข้าชนกับทะเบียนที่มีอยู่ไหม
    /// </summary>
    /// <param name="externalId">รหัสในระบบพาร์ตเนอร์ (กุญแจของการ upsert)</param>
    /// <param name="incomingTaxId">เลขที่ส่งมารอบนี้ (normalize แล้ว หรือ <c>null</c>/ว่าง)</param>
    /// <param name="existingTaxId">เลขที่แถวเดิมเก็บอยู่ (<c>null</c> = แถวใหม่/ยังไม่เคยมี)</param>
    /// <param name="taxIdOwnerExternalId">ถ้าเลขที่ส่งมาถูก<b>ผู้ติดต่อรายอื่น</b>ถืออยู่แล้ว
    /// ให้ส่งรหัส/ชื่อของรายนั้นมา — <c>null</c> = ไม่มีใครถือ</param>
    /// <param name="taxIdOwnerName">ชื่อของผู้ติดต่อรายที่ถือเลขนั้นอยู่</param>
    /// <param name="allowTaxIdChange">พาร์ตเนอร์ยืนยันว่าตั้งใจเปลี่ยนเลข</param>
    public static PartnerConflict CheckContact(
        string externalId,
        string? incomingTaxId,
        string? existingTaxId,
        string? taxIdOwnerExternalId,
        string? taxIdOwnerName,
        bool allowTaxIdChange = false)
    {
        var inc = ThaiTaxId.Normalize(incomingTaxId);
        var cur = ThaiTaxId.Normalize(existingTaxId);

        // ── เลขนี้เป็นของคู่ค้ารายอื่นอยู่แล้ว ──
        // เขียนต่อ = คู่ค้าสองรายถือเลขเดียวกัน ⇒ รายงานภาษีซื้อ/ขาย §87 แยกไม่ออก
        // ว่าซื้อจากใคร และ 50 ทวิ ออกซ้ำให้เลขเดียวกันสองใบ
        if (inc.Length == 13 && taxIdOwnerExternalId != null
            && !string.Equals(taxIdOwnerExternalId, externalId, StringComparison.OrdinalIgnoreCase))
            return new PartnerConflict(PartnerConflictLevel.Blocked, "CONTACT-TAXID-OWNED",
                $"เลขผู้เสียภาษี {inc} เป็นของผู้ติดต่อรหัส \"{taxIdOwnerExternalId}\""
                + (string.IsNullOrWhiteSpace(taxIdOwnerName) ? "" : $" ({taxIdOwnerName})")
                + $" อยู่แล้ว — ไม่บันทึกให้รหัส \"{externalId}\" เพื่อไม่ให้คู่ค้าสองรายถือเลขเดียวกัน. "
                + "ทางแก้: ถ้าเป็นคู่ค้ารายเดียวกัน ให้ผูกรหัสด้วย POST /api/v1/contacts/map "
                + "(ผู้ติดต่อที่ระบบเราสร้างจากเอกสารจะอยู่ใน GET /api/v1/contacts/unmapped) "
                + "· ถ้าเป็นคนละราย ให้แก้เลขของรายใดรายหนึ่งที่ระบบต้นทางแล้ว sync ใหม่");

        // ── เปลี่ยนเลขของแถวเดิมที่ checksum ผ่านอยู่แล้ว ──
        // ใบกำกับที่ออกไปแล้วอ้างเลขเดิม ⇒ เปลี่ยนเงียบ ๆ = §86/4 บนกระดาษกับในระบบไม่ตรงกัน
        if (inc.Length == 13 && cur.Length == 13 && inc != cur
            && ThaiTaxId.IsValid(cur) && !allowTaxIdChange)
            return new PartnerConflict(PartnerConflictLevel.Blocked, "CONTACT-TAXID-CHANGED",
                $"รหัส \"{externalId}\" เคยบันทึกเลขผู้เสียภาษี {cur} (ตรวจ checksum ผ่าน) "
                + $"แต่รอบนี้ส่ง {inc} มา — ไม่เขียนทับเพราะใบกำกับที่ออกไปแล้วอ้างเลขเดิม. "
                + $"ทางแก้: ส่ง \"{OverrideFlagName}\": true มาพร้อมรายการนี้ถ้าตั้งใจเปลี่ยนจริง "
                + "หรือสร้างเป็นผู้ติดต่อรายใหม่ถ้าเป็นคนละนิติบุคคล");

        // ── ส่งเลขที่ใช้ไม่ได้มาทับเลขที่ใช้ได้ ──
        if (inc.Length > 0 && !ThaiTaxId.IsValid(inc) && cur.Length == 13 && ThaiTaxId.IsValid(cur))
            return new PartnerConflict(PartnerConflictLevel.Warning, "CONTACT-TAXID-INVALID-KEEP",
                $"เลข \"{incomingTaxId}\" ที่ส่งมาไม่ผ่านการตรวจสอบ — คงเลขเดิม {cur} ไว้ "
                + "(ช่องอื่นบันทึกตามปกติ)");

        return PartnerConflict.Ok;
    }

    /// <summary>
    /// ชนิดผู้ติดต่อที่พาร์ตเนอร์ประกาศมา ขัดกับรูปเลขที่ checksum ผ่านไหม
    ///
    /// <para><b>ไม่บล็อก</b> — ประกาศยังชนะ (ราชการกับบริษัทใช้เลขขึ้นต้น 0 เหมือนกัน
    /// จึงมีเคสที่ "ขัด" แต่ถูก) แต่ต้อง<b>ส่งคำเตือนกลับให้พาร์ตเนอร์เห็น</b>
    /// ไม่ใช่เงียบ — มิฉะนั้นเลขที่พิมพ์ผิดจะฝังอยู่จนกว่าจะยื่นแบบผิด</para>
    /// </summary>
    public static PartnerConflict CheckDeclaredType(
        string externalId, ContactType declared, string? taxId)
    {
        var shape = ContactTypeResolver.FromTaxId(taxId);
        if (declared == ContactType.Unknown || shape == null || shape == declared)
            return PartnerConflict.Ok;
        // ราชการถือเลขขึ้นต้น 0 เหมือนบริษัท — ไม่ถือว่าขัด
        if (declared == ContactType.GovernmentAgency && shape == ContactType.JuristicPerson)
            return PartnerConflict.Ok;

        return new PartnerConflict(PartnerConflictLevel.Warning, "CONTACT-TYPE-VS-TAXID",
            $"รหัส \"{externalId}\": ส่งชนิดผู้ติดต่อมาเป็น \"{ContactTypeResolver.ThaiLabel(declared)}\" "
            + $"แต่เลขผู้เสียภาษี {ThaiTaxId.Normalize(taxId)} บ่งชี้ \"{ContactTypeResolver.ThaiLabel(shape.Value)}\" "
            + "— ใช้ค่าที่ส่งมา แต่ชนิดนี้เป็นตัวตัดสิน ภ.ง.ด.3/53 และ scheme ของ e-Tax "
            + "โปรดตรวจว่าเลขหรือชนิดผิด");
    }

    /// <summary>
    /// เอกสารขาเข้าซ้ำกับใบที่มีอยู่แล้วไหม — กันการตั้งหนี้/เคลมภาษีซื้อสองครั้ง
    ///
    /// <para>กุญแจธุรกิจคือ <b>เลขใบกำกับของผู้ขาย + คู่ค้า</b> ไม่ใช่ <c>Reference</c>
    /// (ซึ่งพาร์ตเนอร์ใช้เก็บอะไรก็ได้). ใบเดียวกันถูกยิงซ้ำเพราะ retry / cron ซ้อน
    /// เป็นเรื่องปกติของ API ⇒ ถ้าไม่กัน ภาษีซื้อจะถูกเคลมสองรอบ (§82/5 ตรวจเจอ
    /// = เบี้ยปรับ) และค่าใช้จ่ายถูกบันทึกซ้ำ</para>
    /// </summary>
    /// <param name="supplierInvoiceNumber">เลขใบกำกับของผู้ขายที่ส่งมา</param>
    /// <param name="existingDocumentNumber">เลขเอกสารในระบบเราที่ถือเลขใบกำกับเดียวกันอยู่
    /// (<c>null</c> = ยังไม่มี)</param>
    /// <param name="existingDocumentId">รหัสเอกสารนั้น — ส่งกลับให้พาร์ตเนอร์ใช้ต่อได้ทันที</param>
    public static PartnerConflict CheckDocument(
        string? supplierInvoiceNumber, string? existingDocumentNumber, Guid? existingDocumentId)
    {
        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber) || existingDocumentId == null)
            return PartnerConflict.Ok;

        return new PartnerConflict(PartnerConflictLevel.Blocked, "DOC-SUPPLIER-INVOICE-DUPLICATE",
            $"เลขใบกำกับผู้ขาย \"{supplierInvoiceNumber.Trim()}\" ของคู่ค้ารายนี้ถูกบันทึกไว้แล้วที่เอกสาร "
            + $"{existingDocumentNumber ?? existingDocumentId.Value.ToString()} (id {existingDocumentId}) "
            + "— ไม่สร้างใบใหม่เพื่อกันการตั้งหนี้และเคลมภาษีซื้อซ้ำ. "
            + "ทางแก้: ใช้ documentId ใบเดิมต่อ หรือแก้เลขใบกำกับให้ถูกแล้วส่งใหม่");
    }
}
