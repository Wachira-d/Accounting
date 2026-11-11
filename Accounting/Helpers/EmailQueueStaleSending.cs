namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (B-H2): แถวคิวอีเมลที่ค้าง "กำลังส่ง" (<c>Sending</c>) — ตัวตั้งตัวเดียวของ "ค้างเมื่อไร · ทำอะไรกับมันได้" · pure
///
/// <para>═══ ที่มา ═══ ตัวส่งจองแถวเป็น <c>Sending</c> ทีละ 50 แล้ววนส่ง — ปิดระบบ/ deploy กลางแบตช์ = แถวที่เหลือค้าง <c>Sending</c> ตลอดไป:
/// ไม่มีใครคืนแถว · "ลองใหม่" รับเฉพาะ <c>Failed</c> · "ยกเลิก" รับเฉพาะ <c>Pending</c> · หน้า Autopilot นับเฉพาะ <c>Failed</c>
/// ⇒ ใบกำกับงวดนั้นไม่ถึงลูกค้าและไม่มีที่ใดบอก (คิว LINE มี <c>StaleSending</c> 30 นาทีอยู่แล้ว — คู่สมมาตรที่ขาดฝั่งอีเมล)</para>
///
/// <para>═══ กติกา ═══ ค้างเกิน <see cref="StaleAfter"/> นับจากครั้งสุดท้ายที่ตัวส่งแตะแถว (ตัวส่งประทับ <c>UpdatedAt</c> ก่อนส่งทีละแถว)
/// ⇒ <b><c>Failed</c> พร้อมเหตุ — ไม่คืนเป็น <c>Pending</c> เอง</b>: ผู้ให้บริการอีเมลอาจรับไปแล้ว การส่งซ้ำอัตโนมัติ = ลูกค้าได้ใบกำกับสองฉบับ ·
/// คนตรวจแล้วกด "ลองใหม่" (รับ <c>Failed</c> และ <c>Sending</c> ที่ค้าง) หรือ "ยกเลิก" (รับ <c>Pending</c> และ <c>Sending</c> ที่ค้าง)</para>
/// </summary>
public static class EmailQueueStaleSending
{
    public const string StatusPending = "Pending";
    public const string StatusSending = "Sending";
    public const string StatusFailed = "Failed";

    /// <summary>ค่าเดียวกับคิว LINE (<c>ContactLineQueueService.StaleSending</c>)</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>เหตุบนแถว (ผู้ใช้เห็นในคิว + เรื่องค้างของ Autopilot) — บอกทั้งสาเหตุและทางไปต่อ</summary>
    public const string StaleReason =
        "ค้างระหว่างส่ง (ระบบหยุดกลางคัน) — ไม่ทราบว่าอีเมลออกไปแล้วหรือยัง: ตรวจกับผู้รับก่อน แล้วกด “ลองใหม่” (ส่งอีกครั้ง) หรือ “ยกเลิก”";

    // รอบ 258 ฝ่ายค้าน R-M3: ตัวกู้ (EmailScheduleService.RecoverStaleSendingAsync) แยกแถวค้างด้วย SendStartedAt ในคำสั่ง SQL แบบมีเงื่อนไข —
    // ยังไม่เคยเริ่มส่ง ⇒ คืน Pending · เริ่มส่งแล้ว ⇒ Failed พร้อมเหตุ · ตัวส่งเริ่มส่งเฉพาะแถวที่ยังถือ ClaimToken ของรอบจองตัวเอง
    // (กติกาอยู่ในคำสั่ง UPDATE เพราะต้องเป็นอะตอมข้ามเครื่อง — ล็อกด้วย required_call_site_check R258p/R258r3 + Db test)

    /// <summary>เส้นแบ่ง: แถว <c>Sending</c> ที่ <c>UpdatedAt</c> ก่อนเวลานี้ = ค้าง</summary>
    public static DateTime Cutoff(DateTime nowUtc) => nowUtc - StaleAfter;

    /// <summary>แถวนี้ค้างไหม — <c>UpdatedAt</c> ว่าง (ข้อมูลเก่า) ใช้เวลาสร้างแทน</summary>
    public static bool IsStale(string? status, DateTime? updatedAt, DateTime createdAt, DateTime nowUtc)
        => string.Equals(status, StatusSending, StringComparison.Ordinal) && (updatedAt ?? createdAt) < Cutoff(nowUtc);

    /// <summary>"ลองใหม่" รับไหม — <c>Failed</c> หรือ <c>Sending</c> ที่ค้าง (แถวที่ส่งแล้ว/กำลังส่งจริง/รอส่ง ไม่รับ = ไม่ส่งซ้ำ)</summary>
    public static bool CanRetry(string? status, DateTime? updatedAt, DateTime createdAt, DateTime nowUtc)
        => string.Equals(status, StatusFailed, StringComparison.Ordinal) || IsStale(status, updatedAt, createdAt, nowUtc);

    /// <summary>"ยกเลิก" รับไหม — <c>Pending</c> หรือ <c>Sending</c> ที่ค้าง (แถวที่ตัวส่งเพิ่งจองไป ไม่รับ)</summary>
    public static bool CanCancel(string? status, DateTime? updatedAt, DateTime createdAt, DateTime nowUtc)
        => string.Equals(status, StatusPending, StringComparison.Ordinal) || IsStale(status, updatedAt, createdAt, nowUtc);
}
