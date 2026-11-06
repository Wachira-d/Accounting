namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 (ฝ่ายค้าน M1) — <b>ตัวตัดสินเดียว</b> (pure) ของ "บันทึกผลแถวคิวล้มหลังส่งแล้ว ⇒ ประทับอะไร" ใช้ร่วมคิวอีเมล
/// (<c>EmailScheduleService.ProcessPendingQueueAsync</c>) และคิว LINE (<c>ContactLineQueueService.ProcessPendingAsync</c>)
/// <para>ที่มา: เดิมบันทึกล้ม ⇒ ล้าง context แล้วประทับ "ล้ม" ทุกกรณี — แถวที่ผู้ให้บริการ<b>รับไปแล้ว</b>กลายเป็น "ล้ม" ⇒ ผู้ใช้กด "ส่งอีกครั้ง" =
/// ลูกค้าได้สองฉบับ · ตอนนี้: สถานะในหน่วยความจำเป็น "ส่งแล้ว" (ผู้ให้บริการตอบรับจริง) ⇒ ประทับ "ส่งแล้ว" + เวลา/หลักฐาน · อื่น ๆ ⇒ "ล้ม" พร้อมเหตุ</para>
/// </summary>
public sealed record SaveFailureOutcome(string Status, string Note, DateTime? SentAt, string? LineRequestId)
{
    public const string SentStatus = "Sent";
    public const string FailedStatus = "Failed";
    public const string SentButNotSavedPrefix = "ส่งถึงผู้รับแล้ว แต่บันทึกรายละเอียดผลไม่สำเร็จ: ";

    public static SaveFailureOutcome For(string? currentStatus, DateTime? sentAt, string? requestId, string? error)
    {
        var why = (error ?? "").Trim();
        return currentStatus == SentStatus
            ? new SaveFailureOutcome(SentStatus, Trunc(SentButNotSavedPrefix + why), sentAt ?? DateTime.UtcNow, requestId)
            : new SaveFailureOutcome(FailedStatus, Trunc("บันทึกผลการส่งไม่สำเร็จ: " + why), null, requestId);
    }

    private static string Trunc(string s) => s.Length > 2000 ? s[..2000] : s;
}
