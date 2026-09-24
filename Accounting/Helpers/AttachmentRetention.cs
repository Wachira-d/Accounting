using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัดสินว่า "ลบไฟล์แนบ" แล้วลบไฟล์จริงได้ไหม หรือต้องถอดจากรายการแต่เก็บไฟล์ไว้ (soft-delete)
///
/// <para>หลักฐานประกอบรายการบัญชีต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3) · ที่มา (ฝ่ายค้านรอบ 190):
/// เปิดให้แนบ/ลบไฟล์บนใบที่อนุมัติแล้ว แต่ตัวลบลบไฟล์จริงทุกครั้ง ⇒ ผู้มีสิทธิ์สร้างใบลบหลักฐานของใบที่ยื่น ภ.พ.30
/// ไปแล้วได้ถาวร</para>
///
/// <para>ทิศปลอดภัย = <b>เก็บ</b> (ความเสียหายคือพื้นที่ดิสก์ มองเห็นและแก้ได้ · ลบผิดแก้ไม่ได้) ⇒ ลบจริงได้เฉพาะ
/// ที่รู้แน่ว่าไม่ใช่หลักฐานบัญชี: ใบร่าง (ยังไม่เป็นรายการบัญชี) และข้อมูลหลัก (ผู้ติดต่อ/สินค้า) ·
/// ชนิดที่ไม่รู้จัก = เก็บ</para>
/// </summary>
public static class AttachmentRetention
{
    /// <summary>ชนิดที่ไม่ใช่หลักฐานประกอบรายการบัญชี — ลบไฟล์จริงได้ (ค่าที่หน้าเว็บส่งมาจริง)</summary>
    private static readonly HashSet<string> NonEvidenceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "contacts", "Contact", "products", "Product" };

    /// <param name="entityType">ค่า <c>FileAttachment.EntityType</c></param>
    /// <param name="documentStatus">สถานะเอกสาร เมื่อ <paramref name="entityType"/> = <c>"Document"</c> ·
    /// <c>null</c> = หาเอกสารไม่เจอ/ไม่รู้ ⇒ เก็บ</param>
    public static bool MustKeepPhysicalFile(string? entityType, DocumentStatus? documentStatus)
    {
        if (string.Equals(entityType, "Document", StringComparison.Ordinal))
            return documentStatus != DocumentStatus.Draft;
        return entityType == null || !NonEvidenceTypes.Contains(entityType);
    }

    // ═══ ไฟล์ของสแกน OCR (ฝ่ายค้านรอบสอง R2-C4) ═══
    // เดิมระยะเก็บกลับหัว: ผู้ใช้ลบสแกนที่ยังไม่ผูก → ไฟล์ soft-delete แล้วไม่มีงานไหนเก็บกวาด = ค้าง**ตลอดไป** (ขัด PDPA
    // retention by purpose) ขณะที่งาน purge ลบไฟล์จริงของสแกน `Completed && CreatedDocumentId == null` อายุ > 30 วัน ซึ่งรวม
    // สแกนที่**ลงเป็น JE ตรง** (ไฟล์ยังเป็น OcrScan) ⇒ JE ที่โพสต์แล้วเสียเอกสารประกอบใน 30 วัน (ขัด ม.10 · §87/3)
    // ⇒ ทั้งเส้นลบสแกนและงาน purge ถามสองคำถามข้างล่างนี้ตัวเดียว

    /// <summary>ระยะผ่อนก่อนเก็บกวาดไฟล์สแกนที่ถูกถอดแล้ว (กู้คืนได้ถ้าลบผิด)</summary>
    public const int DeletedScanFileGraceDays = 30;

    /// <summary>
    /// **ไฟล์ของสแกนตัวนี้ลบจริงได้ไหม** — ได้เฉพาะไฟล์ที่ยังเป็นของสแกน (<c>EntityType="OcrScan"</c>) และ**ไม่มีสแกนแถวใดที่ใช้ไฟล์นี้
    /// ผูกเอกสาร/JE ไว้** (ไฟล์นั้นคือหลักฐานของรายการบัญชี) · ไฟล์ของรายการอื่น/ชนิดอื่น = ไม่ใช่ของงานนี้ ห้ามแตะ
    /// </summary>
    /// <param name="linkedToEntry">มีสแกน (ตัวเองหรือพี่น้องที่ชี้ไฟล์เดียวกัน) ที่ <c>CreatedJournalEntryId</c> หรือ
    /// <c>CreatedDocumentId</c> ยังตั้งอยู่</param>
    public static bool ScanFilePurgeable(string? fileEntityType, bool linkedToEntry)
        => string.Equals((fileEntityType ?? "").Trim(), "OcrScan", StringComparison.OrdinalIgnoreCase) && !linkedToEntry;

    /// <summary>
    /// **ไฟล์สแกนที่ถูกถอด (soft-delete) แล้ว เก็บกวาดได้หรือยัง** — ต้องเป็นไฟล์ที่ <see cref="ScanFilePurgeable"/> ยอม ·
    /// ไม่มีสแกนแถวใดชี้อยู่แล้ว · และถอดมาครบ <see cref="DeletedScanFileGraceDays"/> วัน
    /// </summary>
    public static bool DeletedScanFileSweepable(string? fileEntityType, bool referencedByAnyScan,
        DateTime deletedAtUtc, DateTime nowUtc)
        => ScanFilePurgeable(fileEntityType, linkedToEntry: false)
           && !referencedByAnyScan
           && deletedAtUtc <= nowUtc.AddDays(-DeletedScanFileGraceDays);
}
