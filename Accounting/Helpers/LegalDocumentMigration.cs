namespace Accounting.Helpers;

/// <summary>รอบ 214 (คำตัดสินข้อ 173) — ตารางฉบับเอกสารทางกฎหมายของแพลตฟอร์ม + ข้อความนโยบาย/ข้อกำหนดที่ร้านเขียนเองบนเว็บ CMS ·
/// CREATE/ADD ... IF NOT EXISTS ⇒ รันทุกบูตได้ · ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c> ·
/// <b>ไม่ seed แถว</b>: ฉบับ 1.0 อยู่ในโค้ด (<see cref="LegalDocumentDefaults"/>) — ตารางว่าง = พฤติกรรมเดิมทุกอย่าง ·
/// <b>ซ่อมค่าเดิม (F2 #9)</b>: ก่อนรอบนี้ <c>Sites.PrivacyPolicyUrl</c>/<c>TermsOfServiceUrl</c> เก็บสตริงว่างได้ (ไม่ผ่าน Blank) —
/// ล้างเป็น NULL ให้ตัวตัดสินอ่านความหมายเดียว ("ไม่ได้ตั้ง") · ค่าที่ไม่ปลอดภัยเดิมไม่ลบ (ตัวตัดสินถือว่าไม่ได้ตั้งอยู่แล้ว)</summary>
public static class LegalDocumentMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """CREATE TABLE IF NOT EXISTS "LegalDocumentVersions" ("Id" uuid PRIMARY KEY, "Kind" varchar(20) NOT NULL DEFAULT '', "Version" varchar(20) NOT NULL DEFAULT '', "EffectiveDate" timestamp without time zone NOT NULL, "BodyMarkdown" text NOT NULL DEFAULT '', "ChangeSummary" varchar(500) NULL, "Status" varchar(20) NOT NULL DEFAULT 'Draft', "PublishedAt" timestamp without time zone NULL, "PublishedBy" varchar(100) NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LegalDocumentVersions_Kind_Version" ON "LegalDocumentVersions" ("Kind", "Version") WHERE "IsDeleted" = false;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyMarkdown" text NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyUpdatedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceMarkdown" text NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceUpdatedAt" timestamp without time zone NULL;""",
        """UPDATE "Sites" SET "PrivacyPolicyUrl" = NULL WHERE "PrivacyPolicyUrl" IS NOT NULL AND btrim("PrivacyPolicyUrl") = '';""",
        """UPDATE "Sites" SET "TermsOfServiceUrl" = NULL WHERE "TermsOfServiceUrl" IS NOT NULL AND btrim("TermsOfServiceUrl") = '';""",
    };
}
