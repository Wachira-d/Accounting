namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-4 (LINE ที่เหลือจากแพ็ก B) — คอลัมน์ใหม่ (idempotent · <c>ADD COLUMN IF NOT EXISTS</c>) · ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated) ·
/// DEFAULT = <see cref="LineQuietHours"/> (ปิด · ช่วงที่เสนอ 21:00–08:00 — บริษัทเดิมเปิดเองที่ ตั้งค่า → LINE · คำตัดสิน 260-4-1)
/// </summary>
public static class Round260LineMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        "ALTER TABLE \"CompanySettings\" ADD COLUMN IF NOT EXISTS \"LineQuietHoursEnabled\" boolean NOT NULL DEFAULT "
            + (LineQuietHours.DefaultEnabled ? "true" : "false") + ";",
        "ALTER TABLE \"CompanySettings\" ADD COLUMN IF NOT EXISTS \"LineQuietStartHour\" integer NOT NULL DEFAULT "
            + LineQuietHours.DefaultStartHour.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";",
        "ALTER TABLE \"CompanySettings\" ADD COLUMN IF NOT EXISTS \"LineQuietEndHour\" integer NOT NULL DEFAULT "
            + LineQuietHours.DefaultEndHour.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";",
    };
}
