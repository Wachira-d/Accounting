namespace Accounting.Helpers;

/// <summary>รอบ 255 WP-12B — คอลัมน์/ตารางของ Autopilot ส่วน B (รายการประจำทุก N เดือน + สร้างล่วงหน้า · คู่ค้า NextAcc · ใบซื้อร่างจากคู่ค้า ·
/// 50 ทวิ กลับผ่านคู่ค้า · พอร์ทัลลิงก์วิเศษ) · <c>ADD COLUMN IF NOT EXISTS</c>/<c>CREATE TABLE IF NOT EXISTS</c> ทุกบรรทัด (idempotent · รันทุกบูต) ·
/// ค่าตั้งต้นทุกคอลัมน์ = ปิด/NULL/0 = พฤติกรรมเดิม (ไม่มีค่าเก่าต้องซ่อม) · ผู้เรียก: <c>DatabaseMigrationHelper.GetAlterStatements</c> ·
/// ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)</summary>
public static class AutopilotPartnerMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // ข้อ 2: รายการประจำทุก N เดือน + สร้างล่วงหน้า (NULL/0 = เดิม)
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "IntervalMonths" integer NULL;""",
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LeadDays" integer NOT NULL DEFAULT 0;""",
    };
}
