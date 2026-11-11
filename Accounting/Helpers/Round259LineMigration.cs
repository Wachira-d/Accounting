namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 แพ็ก B "ส่งทาง LINE ให้ครบ" — คอลัมน์/index ใหม่ (idempotent · <c>ADD COLUMN IF NOT EXISTS</c>) + ซ่อมค่าที่ persist ไว้ก่อนรอบนี้ ·
/// ชื่อ index ตรง <c>AccountingDbContext</c> · ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)
/// </summary>
public static class Round259LineMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // A-M5: เวลาที่ webhook ของ OA บริษัทได้รับคำขอจาก LINE ล่าสุด (LineWebhookHealth)
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineLastWebhookAt" timestamp NULL;""",
        // 258-3-6: การ์ด LINE ทวง 50 ทวิ — ลิงก์อัปโหลดต่อแถวคิว LINE + จำนวนการ์ดในบันทึกทวง
        """ALTER TABLE "WhtCertUploadLinks" ADD COLUMN IF NOT EXISTS "LineQueueId" uuid NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_WhtCertUploadLinks_LineQueue" ON "WhtCertUploadLinks" ("CompanyId", "LineQueueId") WHERE "LineQueueId" IS NOT NULL;""",
        """ALTER TABLE "WhtCertReminderLogs" ADD COLUMN IF NOT EXISTS "LineQueuedCount" integer NOT NULL DEFAULT 0;""",
        // E-H3: ส่งทาง LINE เองจากหน้าเอกสาร (ปุ่มชำระตามที่ผู้ใช้เลือก · ผู้กด)
        """ALTER TABLE "LineQueues" ADD COLUMN IF NOT EXISTS "IncludePayLink" boolean NULL;""",
        """ALTER TABLE "LineQueues" ADD COLUMN IF NOT EXISTS "RequestedByUserId" uuid NULL;""",
        // A-M4 (F2 ข้อ 9 — ค่าที่ persist ผิดต้องซ่อม): ปลายทาง LINE ที่เก็บ "ลิงก์เซ็น"/"อื่น ๆ" ไว้ (ค่าตั้งต้นเดิมของผู้ใช้ LINE ติ๊กลิงก์เซ็น) — เรื่องที่ไม่มีเส้นส่ง ⇒ ตัดออก ·
        // แทนที่ข้อความ (ไม่ cast jsonb — ค่าเสียหายไม่ทำให้ migration ล้ม) · รูปที่ระบบเขียน = JSON array ของชื่อไม่มีช่องว่าง (NotifyTopics.Serialize) ·
        // ทำซ้ำได้ (แถวที่ตัดแล้วไม่ตรงเงื่อนไข)
        """
        UPDATE "ContactNotifyEndpoints"
        SET "TopicsJson" = replace(regexp_replace("TopicsJson", ',?"(Sign|Other)"', '', 'g'), '[,', '['),
            "UpdatedAt" = (now() at time zone 'utc')
        WHERE "Kind" IN ('LineUser', 'LineGroup') AND "TopicsJson" ~ '"(Sign|Other)"';
        """,
    };

    /// <summary>ตัวเดียวกับคำสั่ง SQL ข้างบน (เทสต์ล็อกทั้งสองทิศ) — ตัดชื่อเรื่องที่ LINE ไม่ส่งออกจาก JSON ที่ระบบเขียน</summary>
    internal static string StripLineUnsupported(string topicsJson)
        => System.Text.RegularExpressions.Regex.Replace(topicsJson, ",?\"(Sign|Other)\"", "").Replace("[,", "[");
}
