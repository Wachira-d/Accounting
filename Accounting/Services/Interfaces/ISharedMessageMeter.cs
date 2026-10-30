namespace Accounting.Services.Interfaces;

/// <summary>มิเตอร์ "ข้อความผ่านช่องกลาง" (push ผ่าน OA ของ NextAcc · SMTP ของระบบ) — รอบ 212 C กำหนดเป็นช่องเสียบเล็ก ๆ
/// เพราะโควตาจริง (<c>INotifyQuotaService</c> · <c>NotifyUsageCounters</c>) เป็นของแพ็ก A ที่ทำขนานกัน · ผู้ประสานงานต่อสายตัวนี้เข้ากับมิเตอร์ของแพ็ก A ตอน merge ·
/// คำตัดสิน §10: สรุปประจำวัน<b>นับ</b> 1 หน่วย/ผู้รับ/วัน · แจ้งเตือนรายเหตุการณ์ของ <c>NotificationEngine</c> <b>ไม่นับ</b> (ไม่เรียกตัวนี้)</summary>
public interface ISharedMessageMeter
{
    /// <summary>จองหนึ่งหน่วยด้วยคีย์กันซ้ำ — false = โควตาเต็ม (ห้ามส่ง · ผู้เรียกประทับเหตุ) · คีย์เดิมจองซ้ำ = ไม่นับซ้ำ</summary>
    Task<bool> TryReserveAsync(Guid companyId, string messageKey, CancellationToken ct = default);

    /// <summary>คืนหน่วยที่จองไว้เมื่อส่งไม่สำเร็จ (ไม่คิดเงินข้อความที่ไม่ถึง)</summary>
    Task ReleaseAsync(Guid companyId, string messageKey, CancellationToken ct = default);
}
