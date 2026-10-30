using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S5) — <b>ตัวเดียว</b>ของการล้างตัวชี้ "ใบแจ้งหนี้ต่ออายุที่ออกไปแล้วกลายเป็นใบล้าสมัย" (เปลี่ยนแพ็กเกจทันทีหลังออกใบ — เฟส 2 S9) ·
/// ล้างตัวชี้บน subscription ⇒ ไม่ถือเป็นใบที่รอชำระ/ไม่บล็อกการเปลี่ยนแพ็กเกจ · รอบถัดไป (ถ้ามี) ออกใบใหม่
/// <para><b>ไม่ใช้ตอนขอยกเลิกสิ้นรอบ</b> — เฟส 2 ใช้ตัวชี้นั้นต่อ (ทวงหนี้ยกเว้นใบนี้ · ห้ามจ่าย · "ใช้ต่อ" แล้วจ่ายใบเดิมได้)</para>
/// <para>เอกสารภาษีใบเดิมในบริษัทผู้ให้บริการ<b>ไม่ถูกยกเลิกเงียบ ๆ</b> — ผู้เรียกบันทึกประวัติ "ห้ามรับชำระใบเดิม · แอดมินยกเลิกเอกสาร" (ใบกำกับ/ใบแจ้งหนี้ยกเลิกผ่านเส้นเอกสารเท่านั้น)</para>
/// </summary>
public static class RenewalInvoiceSupersede
{
    /// <summary>ล้างตัวชี้ใบต่ออายุ — คืนเลขใบเดิม (null = ไม่มีใบให้ล้าง)</summary>
    public static string? Apply(Subscription sub)
    {
        var old = sub.RenewalInvoiceNumber;
        if (old == null && sub.RenewalInvoiceDocumentId == null) return null;
        sub.RenewalInvoiceNumber = null;
        sub.RenewalInvoiceIssuedAt = null;
        sub.RenewalInvoiceForEndDate = null;
        sub.RenewalInvoiceDocumentId = null;
        return old ?? "(ไม่มีเลข)";
    }
}
