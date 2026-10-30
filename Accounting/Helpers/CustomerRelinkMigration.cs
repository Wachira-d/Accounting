namespace Accounting.Helpers;

/// <summary>รอบ 212 แพ็ก B — คอลัมน์ความคืบหน้า + คีย์กันซ้ำของ <c>LineOaSwitchEvents</c> (ตารางสร้างล่วงหน้าใน <c>DatabaseMigrationHelper.CmsRound212NotifyStatements</c>
/// ของแพ็ก A ⇒ ต้องรันหลังบล็อกนั้น) · ADD COLUMN/INDEX IF NOT EXISTS ทุกคำสั่ง ⇒ รันทุกบูตได้ · ไม่ UPDATE แถวเดิม · ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c> ·
/// unique บางส่วน (บริษัท, คีย์) เฉพาะแถวที่ยังไม่ประมวลเสร็จ = PUT ซ้อน/webhook หลายครั้งพร้อมกันบันทึกการสลับเดียวกันได้แถวเดียว</summary>
public static class CustomerRelinkMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "LineOaSwitchEvents" ADD COLUMN IF NOT EXISTS "DedupeKey" varchar(200) NULL;""",
        """ALTER TABLE "LineOaSwitchEvents" ADD COLUMN IF NOT EXISTS "Total" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "LineOaSwitchEvents" ADD COLUMN IF NOT EXISTS "Failed" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "LineOaSwitchEvents" ADD COLUMN IF NOT EXISTS "LastError" varchar(500) NULL;""",
        """ALTER TABLE "LineOaSwitchEvents" ADD COLUMN IF NOT EXISTS "LastRunAt" timestamp with time zone NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LineOaSwitchEvents_Pending_Key" ON "LineOaSwitchEvents" ("CompanyId", "DedupeKey") WHERE "ProcessedAt" IS NULL AND "IsDeleted" = false AND "DedupeKey" IS NOT NULL;""",
    };
}
