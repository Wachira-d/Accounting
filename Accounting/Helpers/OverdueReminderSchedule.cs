namespace Accounting.Helpers;

/// <summary>
/// รอบ 250 WP-04 ต่อยอด (ฝ่ายค้าน · settings_reader_check พบ <c>EmailScheduleRule.RepeatEveryDays</c> ไม่มีผู้อ่าน): กฎ "เกินกำหนดชำระ" ส่งซ้ำทุก N วัน —
/// <b>ตัวตัดสินเดียว</b> (pure) ของ <c>EmailScheduleService.ScanDueSoonAndOverdueAsync</c>
/// <para>เดิม: ตัวสแกนเทียบแค่ <c>DueDate == วันนี้ − OffsetDays</c> ⇒ ช่อง "ส่งซ้ำทุก (วัน)" บนหน้าตั้งค่าเก็บ+แสดงแต่ไม่มีผล (silent no-op · กฎ #4 A)</para>
/// <list type="bullet">
/// <item>ครั้งแรก: วันที่เกินกำหนดครบ OffsetDays (พฤติกรรมเดิมทุกตัวอักษร · กุญแจกันซ้ำเดิม) — ไม่ขึ้นกับเพดาน/วันเปิดส่งซ้ำ</item>
/// <item>ครั้งซ้ำที่ k (k ≥ 1): เกินกำหนด = Offset + k×N · k ≤ <see cref="MaxRepeats"/> (สอดคล้องคำตัดสินเจ้าของเรื่องทวง "สูงสุด 6") ·
/// เกินกำหนดไม่เกิน <see cref="MaxOverdueDays"/> วัน · หยุดเองเมื่อชำระครบ (ตัวสแกนคัดเฉพาะใบที่ยังค้าง)</item>
/// <item>watermark (ฝ่ายค้าน H1-ข): ส่งซ้ำเฉพาะใบที่ "วันเตือนครั้งแรก" (DueDate + Offset) ≥ วันที่เปิดส่งซ้ำ (<c>RepeatActivatedAt</c>) —
/// เปิดช่องแล้วใบค้างเก่าทั้งบริษัทไม่ได้อีเมลพร้อมกันตอน deploy/ตอนเปิด · ไม่รู้วันเปิด ⇒ ไม่ส่งซ้ำ (ระวังไว้ก่อน)</item>
/// <item>RepeatEveryDays ≤ 0 ⇒ ไม่มีครั้งซ้ำ</item>
/// </list>
/// </summary>
public static class OverdueReminderSchedule
{
    /// <summary>จำนวนครั้งที่ส่งซ้ำได้สูงสุดต่อเอกสารต่อกฎ (ไม่รวมครั้งแรก)</summary>
    public const int MaxRepeats = 6;

    /// <summary>หยุดส่งซ้ำเมื่อเกินกำหนดเกินจำนวนวันนี้ (ทวงผ่านช่องทางอื่น/ตั้งหนี้สงสัยจะสูญแทน)</summary>
    public const int MaxOverdueDays = 180;

    /// <summary>วันนี้ต้องส่งไหม · <paramref name="overdueDays"/> = วันนี้ − วันครบกำหนด (ไทย) · <paramref name="firstReminderDate"/> = DueDate + Offset ·
    /// <paramref name="repeatActivatedOn"/> = วันที่ (ไทย) ที่เปิดส่งซ้ำของกฎ (null = ไม่รู้ ⇒ ไม่ส่งซ้ำ)</summary>
    public static bool SendToday(int overdueDays, int offsetDays, int repeatEveryDays, DateTime firstReminderDate, DateTime? repeatActivatedOn)
    {
        if (overdueDays < offsetDays) return false;
        var past = overdueDays - offsetDays;
        if (past == 0) return true;                                  // ครั้งแรก — เหมือนเดิม
        if (repeatEveryDays <= 0 || past % repeatEveryDays != 0) return false;
        if (past / repeatEveryDays > MaxRepeats || overdueDays > MaxOverdueDays) return false;
        return repeatActivatedOn is DateTime on && firstReminderDate.Date >= on.Date;
    }

    /// <summary>เป็นครั้งซ้ำไหม (ไม่ใช่ครั้งแรก) — แถว "ข้าม" ของผู้ติดต่อที่ปิดรับเขียนเฉพาะครั้งแรก (ฝ่ายค้าน M4: ไม่สะสมทุกรอบ)</summary>
    public static bool IsRepeat(int overdueDays, int offsetDays) => overdueDays > offsetDays;

    /// <summary>ส่วนท้ายกุญแจกันซ้ำ — ครั้งแรกใช้กุญแจเดิม (<c>overdue-{Offset}</c> · แถวคิวเก่าไม่ซ้ำ) · ครั้งที่ส่งซ้ำต่อท้ายวันที่ ⇒ ส่งได้หนึ่งครั้งต่อรอบ</summary>
    public static string Suffix(int offsetDays, int overdueDays, DateTime todayBangkok)
        => overdueDays == offsetDays ? $"overdue-{offsetDays}" : $"overdue-{offsetDays}-" + todayBangkok.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>ฝ่ายค้าน M2: ส่งซ้ำใช้ได้เฉพาะกฎ "เกินกำหนด" — ทริกเกอร์อื่นที่ตั้ง &gt; 0 ⇒ ข้อความปฏิเสธ (ไม่เก็บค่าที่ไม่มีผลเงียบ ๆ)</summary>
    public static string? RepeatProblem(string? trigger, int repeatEveryDays)
        => repeatEveryDays > 0 && trigger != "DocumentOverdue"
            ? "\"ส่งซ้ำทุก (วัน)\" ใช้ได้เฉพาะกฎ \"เกินกำหนดชำระ\" — ตั้งเป็น 0 สำหรับเหตุการณ์อื่น"
            : null;

    /// <summary>ฝ่ายค้าน H1-ข: ประทับวันเปิดส่งซ้ำเมื่อค่าเปลี่ยนจาก 0 เป็น &gt; 0 · ปิด (0) ⇒ ล้าง · ยังเปิดอยู่ ⇒ คงเดิม</summary>
    public static DateTime? NextActivatedAt(int previousRepeat, DateTime? previousActivatedAt, int newRepeat, DateTime nowUtc)
        => newRepeat <= 0 ? null : previousRepeat > 0 && previousActivatedAt != null ? previousActivatedAt : nowUtc;
}
