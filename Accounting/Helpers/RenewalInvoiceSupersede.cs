using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S5) — <b>ตัวเดียว</b>ของ "ใบแจ้งหนี้ต่ออายุที่ออกไปแล้วกลายเป็นใบล้าสมัย" (เปลี่ยนแพ็กเกจทันทีหลังออกใบ — เฟส 2 S9 ·
/// ขอยกเลิกมีผลสิ้นรอบ — ส่วน B S5) · ล้างตัวชี้บน subscription ⇒ ไม่ถือเป็นใบที่รอชำระ/ไม่บล็อกการเปลี่ยนแพ็กเกจ · รอบถัดไป (ถ้ามี) ออกใบใหม่
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

    public static string CancelNote(string invoiceNumber)
        => $"ขอยกเลิกแพ็กเกจ (มีผลสิ้นรอบ) — ใบแจ้งหนี้ต่ออายุ {invoiceNumber} ไม่ต้องชำระแล้ว · ห้ามรับชำระใบเดิม · ผู้ดูแลระบบยกเลิกเอกสารใบเดิมในบริษัทผู้ให้บริการ";
}
