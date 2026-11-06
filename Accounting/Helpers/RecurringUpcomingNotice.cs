namespace Accounting.Helpers;

/// <summary>
/// รอบ 250 WP-04 ต่อยอด (ฝ่ายค้าน · settings_reader_check พบ <c>RecurringTransaction.NotifyBeforeRun/NotifyDaysBefore</c> ไม่มีผู้อ่าน):
/// แจ้งเตือนเจ้าของรายการประจำ N วันก่อนรอบสร้างเอกสาร — <b>ตัวตัดสินเดียว</b> (pure) ของ
/// <c>RecurringTransactionService.NotifyUpcomingRunsAsync</c> · คีย์แจ้งเตือน <c>recurring.upcoming_run</c> (ค่าตั้งต้น = กระดิ่งของผู้สร้างรายการ)
/// <list type="bullet">
/// <item>แจ้งเมื่อ เปิด · N ≥ 1 · รายการยังใช้งาน · วันรอบถัดไป (ปฏิทินไทย) อยู่ภายใน 1…N วันจากวันนี้ · ยังไม่เคยแจ้งสำหรับรอบนั้น</item>
/// <item>รอบเดียวกันแจ้งครั้งเดียว — ระบบประทับ <c>UpcomingRunNotifiedFor</c> = วันของรอบ (compare-and-set ข้ามเครื่อง) <b>ก่อน</b>ส่ง = อย่างมากครั้งเดียว
/// (เครื่องแจ้งเตือนกลืนข้อผิดพลาดเองไม่คืนผล ⇒ ส่งล้ม = รอบนั้นไม่ได้กระดิ่ง · เป็นการเตือนล่วงหน้า ไม่ใช่งานเงิน)</item>
/// <item>กระดิ่งเท่านั้นเป็นค่าตั้งต้น (ไม่ส่งอีเมลเว้นแต่บริษัทตั้งคีย์นี้เอง) · ผู้รับ = ผู้สร้างที่ยังเป็นสมาชิกบริษัท ไม่งั้นเจ้าของ ·
/// ค่าตั้งต้นของรายการใหม่ = ปิด (แถวเดิมถูกปิดครั้งเดียวตอน deploy — หน้าจอเดิมส่ง true ตายตัว)</item>
/// <item>วันรันเอง (0 วัน) ไม่แจ้งล่วงหน้า (รอบสร้างเอกสารอยู่แล้ว) · ปิด/N = 0 ⇒ ไม่แจ้ง</item>
/// </list>
/// </summary>
public static class RecurringUpcomingNotice
{
    /// <summary>เพดาน N (ช่องบนหน้า) — แจ้งล่วงหน้าเกินหนึ่งเดือนไม่มีความหมายกับรอบรายเดือน</summary>
    public const int MaxDaysBefore = 30;

    public static int DaysUntil(DateTime nextRunDate, DateTime todayThai) => (nextRunDate.Date - todayThai.Date).Days;

    /// <param name="frequency">รายวัน ⇒ ไม่แจ้ง (ทุกวันคือรอบ — แจ้งล่วงหน้าไม่มีความหมาย · ฝ่ายค้าน H2)</param>
    /// <param name="endDate">/<paramref name="totalRuns"/>/<paramref name="maxRuns"/>: รอบถัดไปเลยวันสิ้นสุด/ครบจำนวนครั้ง ⇒ จะไม่รัน ⇒ ไม่แจ้ง (ฝ่ายค้าน M1 · ตัวตัดสิน RecurringSchedule.IsFinished)</param>
    public static bool ShouldNotify(bool notifyBeforeRun, int notifyDaysBefore, bool active, Accounting.Models.Enums.RecurringFrequency frequency,
        DateTime nextRunDate, DateTime? endDate, int totalRuns, int? maxRuns, DateTime todayThai, DateTime? notifiedForRun)
    {
        if (!notifyBeforeRun || notifyDaysBefore <= 0 || !active) return false;
        if (frequency == Accounting.Models.Enums.RecurringFrequency.Daily) return false;
        if (RecurringSchedule.IsFinished(nextRunDate, endDate, totalRuns, maxRuns)) return false;
        var days = DaysUntil(nextRunDate, todayThai);
        if (days < 1 || days > notifyDaysBefore) return false;
        return notifiedForRun?.Date != nextRunDate.Date;
    }

    /// <summary>ค่าที่ผู้ใช้ส่งมา — null = ใช้ได้ · ข้อความ = ปฏิเสธ (ไม่ clamp เงียบ)</summary>
    public static string? RangeError(int? notifyDaysBefore)
        => notifyDaysBefore is int n && (n < 0 || n > MaxDaysBefore)
            ? $"แจ้งเตือนล่วงหน้า (วัน) ต้องอยู่ระหว่าง 0–{MaxDaysBefore} (ได้ {n})"
            : null;
}
