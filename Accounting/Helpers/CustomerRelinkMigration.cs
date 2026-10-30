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
        // ฝ่ายค้าน (B+D) ข้อ 2: bot จาก webhook ที่ไม่ตรงกับที่จำไว้ (secret ของ OA อื่น)
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineWebhookDestinationMismatch" varchar(64) NULL;""",
        // ฝ่ายค้าน (B+D) ข้อ 4: bot ของ OA กลางที่ webhook เห็น — งานเบื้องหลังสแกนข้ามบริษัทแทนการสแกนในคำขอ webhook (ระดับแพลตฟอร์ม ไม่มี CompanyId)
        """
        CREATE TABLE IF NOT EXISTS "LineOaDestinationSightings" (
            "Destination" varchar(64) NOT NULL PRIMARY KEY,
            "SeenAt" timestamp with time zone NOT NULL DEFAULT now(),
            "ScannedAt" timestamp with time zone NULL
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LineOaSwitchEvents_Pending_Key" ON "LineOaSwitchEvents" ("CompanyId", "DedupeKey") WHERE "ProcessedAt" IS NULL AND "IsDeleted" = false AND "DedupeKey" IS NOT NULL;""",
    };
}
