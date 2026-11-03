namespace Accounting.Helpers;

/// <summary>ฝ่ายค้าน 232 B1 (คำตัดสิน 232-Notify) — <b>backfill ครั้งเดียว</b>ของช่อง "กลุ่ม LINE ของบริษัท" บนแถว <c>document.approved</c> ที่บริษัทตั้งไว้ก่อนรอบ 232
/// <list type="bullet">
/// <item>ก่อนรอบ 232 "✅ เอกสารอนุมัติแล้ว" ถูกประกาศเข้ากลุ่ม LINE ของบริษัทที่ผูก OA+กลุ่มไว้ <b>โดยไม่มีค่าตั้ง</b> (<c>NotifyDocumentApprovedAsync</c>) ·
/// รอบ 232 ย้ายมาเป็นช่อง <c>EnableLineGroup</c> ของเครื่องแจ้งเตือน — <c>DefaultMatrix</c> เติมให้เฉพาะบริษัทที่ "ยังไม่มีแถว" ⇒ บริษัทที่เคยตั้งแถว document.approved
/// (เช่น กระดิ่ง/อีเมลถึงฝ่ายบัญชี) จะเสียประกาศกลุ่มไปเงียบ ๆ = ถดถอย</item>
/// <item>เงื่อนไขแถว: บริษัทผูก OA ของตัวเอง (<c>LineEnabled</c> + token + <c>LineDefaultGroupId</c> — เกณฑ์เดียวกับ <see cref="NotificationAudiencePolicy.CompanyLineGroupConfigured"/>) ·
/// แถวยังไม่เปิดช่องกลุ่ม · ไม่ถูกลบ</item>
/// <item><b>ครั้งเดียวจริง</b>: <see cref="MigrationSql"/> รัน UPDATE ใน DO-block เดียวกับการเพิ่มคอลัมน์ และเฉพาะเมื่อคอลัมน์ <c>EnableLineGroup</c> <b>ยังไม่มี</b>
/// (แพตเทิร์น information_schema guard ที่เรพใช้อยู่ เช่น rename <c>QuotationRevision</c>) — ตอนนั้นยังไม่มีผู้ใช้คนใดเคยเห็นช่องกลุ่ม ⇒ false ทุกแถวไม่ใช่เจตนาผู้ใช้ ·
/// บูตครั้งต่อไปคอลัมน์มีแล้ว ⇒ ไม่แตะแถวที่ผู้ใช้ปิดช่องกลุ่มเองภายหลัง · ไม่ขึ้นกับวันปล่อยจริง</item>
/// <item>ทางเลือกที่ไม่เอา: ตัด <c>UpdatedAt</c> &lt; วันปล่อย (วันปล่อยจริงไม่แน่นอน — ปล่อยช้ากว่าวันที่ฝังไว้ ⇒ แถวที่ผู้ใช้ตั้งด้วยหน้าเก่าหลุด backfill ·
/// ปล่อยเร็วกว่า ⇒ แถวที่ผู้ใช้ปิดกลุ่มด้วยหน้าใหม่ถูกทับทุกบูต) · คอลัมน์ธง "IsDefault" (ค่าที่ persist เพิ่มเพื่อ migration ครั้งเดียว)</item>
/// </list></summary>
public static class NotificationLineGroupBackfill
{
    public const string EventKey = "document.approved";

    /// <summary>UPDATE ล้วน (ผู้เรียก: <see cref="MigrationSql"/> + Db test) — อ่าน <c>CompanySettings</c> ของบริษัทเดียวกัน (กฎ M)</summary>
    public const string UpdateSql = $"""
        UPDATE "NotificationSettings" n
           SET "EnableLineGroup" = true
          FROM "CompanySettings" c
         WHERE c."CompanyId" = n."CompanyId"
           AND n."EventKey" = '{EventKey}'
           AND n."IsDeleted" = false
           AND n."EnableLineGroup" = false
           AND c."LineEnabled" = true
           AND COALESCE(c."LineChannelAccessToken", '') <> ''
           AND COALESCE(c."LineDefaultGroupId", '') <> ''
        """;

    /// <summary>เพิ่มคอลัมน์ + backfill ในก้อนเดียว เฉพาะครั้งแรกที่คอลัมน์ยังไม่มี (idempotent: รันซ้ำทุกบูต = no-op) ·
    /// ต้องอยู่<b>ก่อน</b> <c>ADD COLUMN IF NOT EXISTS "EnableLineGroup"</c> แบบธรรมดาใน <c>DatabaseMigrationHelper</c></summary>
    public const string MigrationSql = $"""
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = current_schema() AND table_name = 'NotificationSettings' AND column_name = 'EnableLineGroup')
            THEN
                ALTER TABLE "NotificationSettings" ADD COLUMN "EnableLineGroup" boolean NOT NULL DEFAULT false;
                {UpdateSql};
            END IF;
        END $$;
        """;
}
