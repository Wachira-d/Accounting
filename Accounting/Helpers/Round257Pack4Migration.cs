namespace Accounting.Helpers;

/// <summary>รอบ 257 ชุด 4 (รายการประจำ + LINE/โควตา) — คอลัมน์ + ซ่อมค่าที่ persist ไว้ก่อนแก้ (F3 ข้อ 10) · idempotent ทุกบรรทัด (รันทุกบูต) ·
/// ผู้เรียก: <c>DatabaseMigrationHelper.GetAlterStatements</c> · ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)</summary>
public static class Round257Pack4Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // A-M4: ลายนิ้วมือของค่าตั้งต้นรายการประจำ (NULL = รายการก่อนรอบ 257 — AutoApprovePolicy.MatchesDefaultBinding ถือว่าตรง · ประทับเมื่อผู้มีสิทธิ์บันทึกขั้นอนุมัติ)
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "DefaultPolicyBindingHash" varchar(80) NULL;""",
        // ฝ่ายค้าน LOW-1: ลายนิ้วมือรุ่น 2 = "v2:" + 64 hex (67 ตัว) — ฐานที่รัน 8918f558 แล้วได้ varchar(64) ⇒ ขยาย (idempotent · ขยายอย่างเดียว ไม่ตัดข้อมูล)
        """ALTER TABLE "RecurringTransactions" ALTER COLUMN "DefaultPolicyBindingHash" TYPE varchar(80);""",
        // ฝ่ายค้าน MED-2: ผู้ประทับค่าตั้งต้นล่าสุด (NULL = ผู้สร้าง — แถวเดิม)
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "DefaultPolicyStampedBy" varchar(100) NULL;""",
        // A-H3: รายการสมุดรายวันที่ตั้งสร้างล่วงหน้าไว้ก่อนรอบนี้ ⇒ 0 (ใบสำคัญลงวันที่วันที่สร้าง — ล่วงหน้า = ลงงวดผิด · ตอนนี้ปฏิเสธตอนบันทึก)
        """UPDATE "RecurringTransactions" SET "LeadDays" = 0 WHERE lower("TemplateType") = 'journal' AND "LeadDays" <> 0;""",
        // C LOW: ปลายทางที่ผู้ดูแลบริษัทยกเลิกไปก่อนรอบนี้ ⇒ ประทับถอนความยินยอม (เดิมค้าง "ยังยินยอม") · เฉพาะความยินยอมที่ไม่มีปลายทางอื่นที่ยังใช้งานอ้างอยู่ · บริษัทเดียวกันเท่านั้น
        """
        UPDATE "PdpaConsentRecords" c SET "WithdrawnAt" = e."UnlinkedAt", "WithdrawnReason" = 'company-unlink'
        FROM "ContactNotifyEndpoints" e
        WHERE e."ConsentRecordId" = c."Id" AND e."CompanyId" = c."CompanyId" AND e."UnlinkReason" = 'company' AND e."UnlinkedAt" IS NOT NULL
          AND c."WithdrawnAt" IS NULL
          AND NOT EXISTS (SELECT 1 FROM "ContactNotifyEndpoints" o
                          WHERE o."ConsentRecordId" = c."Id" AND o."CompanyId" = c."CompanyId" AND o."UnlinkedAt" IS NULL);
        """,
    };
}
