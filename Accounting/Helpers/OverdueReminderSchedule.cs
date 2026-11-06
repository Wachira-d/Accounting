namespace Accounting.Helpers;

/// <summary>
/// รอบ 250 WP-04 ต่อยอด (ฝ่ายค้าน · settings_reader_check พบ <c>EmailScheduleRule.RepeatEveryDays</c> ไม่มีผู้อ่าน): กฎ "เกินกำหนดชำระ" ส่งซ้ำทุก N วัน —
/// <b>ตัวตัดสินเดียว</b> (pure) ของ <c>EmailScheduleService.ScanDueSoonAndOverdueAsync</c>
/// <para>เดิม: ตัวสแกนเทียบแค่ <c>DueDate == วันนี้ − OffsetDays</c> ⇒ ช่อง "ส่งซ้ำทุก (วัน)" บนหน้าตั้งค่าเก็บ+แสดงแต่ไม่มีผล (silent no-op · กฎ #4 A)</para>
/// <list type="bullet">
/// <item>ส่งเมื่อ เกินกำหนดแล้ว ≥ OffsetDays วัน และ (เกินกำหนด − OffsetDays) หารด้วย RepeatEveryDays ลงตัว</item>
/// <item>RepeatEveryDays ≤ 0 ⇒ ส่งครั้งเดียวในวันที่เกินกำหนดครบ OffsetDays (พฤติกรรมเดิมทุกตัวอักษร · กุญแจกันซ้ำเดิม)</item>
/// <item>หยุดเองเมื่อชำระครบ (ตัวสแกนคัดเฉพาะใบที่ยังค้าง) — ไม่มีเพดานจำนวนครั้งในค่าตั้งเดิม</item>
/// </list>
/// </summary>
public static class OverdueReminderSchedule
{
    /// <summary>วันนี้ต้องส่งไหม · <paramref name="overdueDays"/> = วันนี้ − วันครบกำหนด (ไทย)</summary>
    public static bool SendToday(int overdueDays, int offsetDays, int repeatEveryDays)
    {
        if (overdueDays < offsetDays) return false;
        var past = overdueDays - offsetDays;
        if (repeatEveryDays <= 0) return past == 0;
        return past % repeatEveryDays == 0;
    }

    /// <summary>ส่วนท้ายกุญแจกันซ้ำ — ครั้งแรกใช้กุญแจเดิม (<c>overdue-{Offset}</c> · แถวคิวเก่าไม่ซ้ำ) · ครั้งที่ส่งซ้ำต่อท้ายวันที่ ⇒ ส่งได้หนึ่งครั้งต่อรอบ</summary>
    public static string Suffix(int offsetDays, int overdueDays, DateTime todayBangkok)
        => overdueDays == offsetDays ? $"overdue-{offsetDays}" : $"overdue-{offsetDays}-" + todayBangkok.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
}
