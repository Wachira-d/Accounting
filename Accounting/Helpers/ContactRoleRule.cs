namespace Accounting.Helpers;

/// <summary>
/// รหัสกฎ "ประเภทคู่ค้าไม่ตรงกับฝั่งเอกสาร" — ตัวตั้งตัวเดียวให้ฝั่งเซิร์ฟเวอร์ (throw 409) และหน้าเว็บ (เสนอเปิดสถานะให้เลย) อ่านค่าเดียวกัน
/// คำตัดสินข้อ 138ข (2026-10-05): ไม่ให้ผู้ใช้เดินไปเปิดเองที่หน้าผู้ติดต่อ — ระบบถามแล้วเปิดให้ในคลิกเดียวแล้วบันทึกต่อ
/// </summary>
public static class ContactRoleRule
{
    public const string CustomerRequired = "CONTACT-ROLE-CUSTOMER";
    public const string SupplierRequired = "CONTACT-ROLE-SUPPLIER";
}
