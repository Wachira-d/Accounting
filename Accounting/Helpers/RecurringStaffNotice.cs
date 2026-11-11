namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (B-M1 · B-M5): แจ้งเตือนถึงทีมของ "รายการประจำ" — ผู้รับ + ข้อความของเรื่อง "สร้างเอกสารแล้วแต่ค้างเป็นร่าง" · pure
///
/// <para>═══ B-M5 ═══ แจ้งเตือนรอบล้ม/ข้อสังเกตเคยส่งถึง "ผู้สร้างรายการ" ตรง ๆ โดยไม่ตรวจว่ายังเป็นสมาชิก ⇒ ผู้สร้างออกจากบริษัทแล้ว
/// ไม่มีคนในบริษัทได้รับ (และอดีตพนักงานอาจได้ชื่อรายการ/ลูกค้า) · กติกาเดียวทุกจุด: ผู้สร้างที่<b>ยังเป็นสมาชิกที่ใช้งาน</b> ·
/// ไม่ใช่ ⇒ null = ผู้รับตามตารางแจ้งเตือนของบริษัท (หรือค่าตั้งต้นของเหตุการณ์: เจ้าของบริษัท)</para>
///
/// <para>═══ B-M1 ═══ อนุมัติอัตโนมัติไม่ผ่าน (ใบสร้างแล้วแต่ค้างร่าง) เคยมีแค่หมายเหตุบนใบ + แถวหน้ารายการประจำ + สรุปเช้าวันถัดไป
/// ทั้งที่หน้า Autopilot บอกว่า "ล้ม/ติด = แจ้งทันที" ⇒ ใบกำกับค้างร่างได้ ~32 ชม. โดยไม่มีใครถูกเรียก · ตอนนี้แจ้งทันทีหนึ่งครั้งต่อใบ</para>
/// </summary>
public static class RecurringStaffNotice
{
    /// <summary>ผู้รับแบบระบุตัว — ผู้สร้างรายการที่ยังเป็นสมาชิกที่ใช้งานของบริษัท · อื่น ๆ = null (ตามตารางแจ้งเตือน/ค่าตั้งต้นของเหตุการณ์)</summary>
    public static Guid? Recipient(string? createdBy, bool creatorIsActiveMember)
        => creatorIsActiveMember && Guid.TryParse(createdBy, out var id) && id != Guid.Empty ? id : null;

    /// <summary>รหัสผู้สร้าง (ไว้ถามสมาชิกภาพ) · ไม่ใช่ Guid/ว่าง = null</summary>
    public static Guid? CreatorId(string? createdBy)
        => Guid.TryParse(createdBy, out var id) && id != Guid.Empty ? id : null;

    public static string StuckDraftTitle(string? recurringName, string? documentNumber)
        => $"รายการประจำ \"{recurringName}\" สร้างเอกสารแล้วแต่ค้างเป็นร่าง — อนุมัติอัตโนมัติไม่ผ่าน"
           + (string.IsNullOrWhiteSpace(documentNumber) ? "" : $" ({documentNumber})");

    public static string StuckDraftMessage(string? reason)
        => (string.IsNullOrWhiteSpace(reason) ? "ระบบอนุมัติเอกสารของรอบนี้ไม่ได้" : reason.Trim())
           + " — ใบร่างยังไม่มีเลขที่จริงและยังไม่ถูกส่งให้ลูกค้า: เปิดเอกสารเพื่อตรวจแล้วอนุมัติเอง";
}
