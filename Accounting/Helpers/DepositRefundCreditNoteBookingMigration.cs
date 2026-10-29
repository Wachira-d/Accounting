namespace Accounting.Helpers;

/// <summary>
/// <b>เติมเลขจองให้ใบลดหนี้คืนมัดจำที่ออกก่อนรอบ 208</b> (ฝ่ายค้าน W5 ข้อ 4 · CLAUDE.md F2 หลักการ 9 "Persist แล้วต้องมี migration")
///
/// <para>═══ ที่มา ═══ <c>DocumentService.RefundDepositAsync</c> สร้างใบลดหนี้คืนมัดจำโดยไม่สืบทอด <c>BookingNumber</c> ของใบมัดจำ
/// (กฎ "เอกสารลูกต้องสืบทอด") ⇒ การ์ด "เอกสารทุกใบของการจอง" (R-50 อ่านจาก <c>BookingNumber</c> = เลขจอง) ไม่เห็นใบลดหนี้ที่ออกตอน
/// “ยืนยันคืนเงินแล้ว” · แก้โค้ดให้ใบใหม่สืบทอดแล้ว แต่ใบที่ออกไปก่อนหน้ายังว่างอยู่</para>
///
/// <para>แตะเฉพาะ: ใบลดหนี้ (6) ที่อ้างใบมัดจำ (<c>IsDeposit</c>) ของบริษัทเดียวกัน · ใบลดหนี้ยังไม่มีเลขจอง · ใบมัดจำมีเลขจอง ·
/// ไม่เปลี่ยนค่าที่มีอยู่แล้ว ⇒ รันทุกบูตได้ (idempotent) · เลขจองเป็นข้อมูลอ้างอิง ไม่กระทบยอด/ภาษี/GL</para>
/// </summary>
public static class DepositRefundCreditNoteBookingMigration
{
    /// <summary>ชนิดเอกสารในคำสั่งเป็นเลข enum ตามที่เก็บในฐาน (CreditNote = 6) — เทสต์ล็อกว่าตรงกับ enum จริง</summary>
    public const int CreditNoteTypeValue = 6;

    public static string BackfillSql =>
        "UPDATE \"Documents\" cn SET \"BookingNumber\" = d.\"BookingNumber\" FROM \"Documents\" d "
        + "WHERE cn.\"RelatedDocumentId\" = d.\"Id\" AND cn.\"CompanyId\" = d.\"CompanyId\" "
        + $"AND cn.\"DocumentType\" = {CreditNoteTypeValue} AND cn.\"BookingNumber\" IS NULL "
        + "AND d.\"BookingNumber\" IS NOT NULL AND d.\"IsDeposit\" = true;";

    public static IReadOnlyList<string> Statements() => new[] { BackfillSql };
}
