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
}
