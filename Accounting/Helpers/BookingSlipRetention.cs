namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบ 218 รอบ 2 (F2 · กฎ M "Retention superset" · พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3 · PDPA ม.37) — อายุไฟล์สลิปของการจองนัดหมาย
/// (<c>FileAttachment</c> ชนิด <c>SiteBooking</c>) · <b>ตัวตัดสินเดียว</b>ของงานล้าง (<c>CartImageCleanupJob</c> รอบที่สอง)
///
/// <para><b>คำตัดสิน</b>: สลิปทุกใบเป็นหลักฐานการรับ/คืนเงิน — รับเงินแล้ว (หลักฐานประกอบรายการ) · ถูกปฏิเสธ/ถูกแทน (หลักฐานข้อพิพาท/จ่ายซ้ำ) ·
/// การจองยกเลิก/ไม่มา (หลักฐานคืนเงิน) ⇒ <b>เก็บ <see cref="RetentionYears"/> ปีนับจากวันแนบ</b> (5 ปีหลังสิ้นรอบบัญชี ม.10 — วันแนบอยู่ในรอบที่สิ้นไม่เกิน 12 เดือนถัดไป ⇒ 6 ปีครอบ)
/// แล้วจึงลบ (PDPA: ไม่เก็บเกินวัตถุประสงค์) · สลิปที่ยัง "รอตรวจ" ไม่ลบแม้ครบอายุ ·
/// เดิม (รอบ 218) ลบสลิปที่ถูกปฏิเสธหลัง 90 วัน — ยกเลิก เพราะลบหลักฐานคืนเงินของการจองที่ยกเลิกได้</para>
///
/// <para>แนบไม่สำเร็จ (ยอดเปลี่ยน/ขั้นใต้ล็อกล้ม) ⇒ ไฟล์ถูกลบทันทีที่ทางเข้า (ลูกค้าเห็น "สลิปนี้ยังไม่ถูกบันทึก — แนบอีกครั้ง") — ไม่มีไฟล์กำพร้าที่พนักงานมองไม่เห็น</para>
/// </summary>
public static class BookingSlipRetention
{
    public const int RetentionYears = 6;

    public static DateTime Cutoff(DateTime nowUtc) => nowUtc.AddYears(-RetentionYears);

    /// <summary>ลบได้ไหม — ครบอายุ และไม่ใช่สลิปที่รอตรวจอยู่ (เงื่อนไขเดียวกับที่งานล้างแปลงเป็น SQL)</summary>
    public static bool ShouldPurge(DateTime createdAtUtc, bool isPendingSlip, DateTime cutoffUtc)
        => !isPendingSlip && createdAtUtc < cutoffUtc;
}
