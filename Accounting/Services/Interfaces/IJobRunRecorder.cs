namespace Accounting.Services.Interfaces;

/// <summary>WP-F2: บันทึกผลการรัน background job ลง JobRunLog.
/// best-effort — การบันทึกผลห้ามทำให้ job จริงพัง.</summary>
public interface IJobRunRecorder
{
    /// <summary>ห่อการรัน job: จับเวลา + บันทึกผล (สำเร็จ/ล้มเหลว + ข้อความ).
    /// work คืนจำนวนรายการที่ทำ (0 ถ้าไม่นับ). exception จะถูก re-throw
    /// หลังบันทึกผลล้มเหลว เพื่อให้ caller เดิมยังเห็น error.</summary>
    Task<int> TrackAsync(string jobName, Func<Task<int>> work);

    /// <summary>บันทึกผลตรง ๆ (กรณีคุมเองว่าจะบันทึกเมื่อไร).</summary>
    Task RecordAsync(string jobName, bool success, string? message, int itemsProcessed, DateTime startedAt);
}
