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
        // ฝ่ายค้านรอบ 214: ร่างได้ฉบับเดียวต่อชนิด (ระดับฐาน — สองแท็บสร้างร่างแรกพร้อมกัน) · ชื่อตรงกับ AccountingDbContext
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LegalDocumentVersions_Kind_Draft" ON "LegalDocumentVersions" ("Kind") WHERE "Status" = 'Draft' AND "IsDeleted" = false;""",
        // รอบ 214 หลัง CI (คำตัดสินข้อ 173 ฌ): การยอมรับนโยบายแพลตฟอร์มระดับผู้ใช้ — ไม่มี CompanyId/FK (เดิมเขียน PdpaConsentRecords ด้วย CompanyId ศูนย์
        // ⇒ ฐานที่มี FK ปฏิเสธ) · คัดลอกแถวเดิม (ฐานที่สร้างจาก migration ไม่มี FK จึงมีแถวอยู่) ด้วย Id เดิม · ON CONFLICT ⇒ รันซ้ำได้ · ไม่ลบแถวเดิม (append-only)
        """CREATE TABLE IF NOT EXISTS "PlatformPolicyAcceptances" ("Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "Email" varchar(256) NOT NULL DEFAULT '', "Purpose" varchar(100) NOT NULL DEFAULT '', "PolicyVersion" varchar(50) NOT NULL DEFAULT '', "AcceptedAt" timestamp without time zone NOT NULL, "Channel" varchar(50) NULL, "IpAddress" varchar(45) NULL, "EvidenceHash" varchar(100) NULL, "WithdrawnAt" timestamp without time zone NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        // รอบ 214 ฝ่ายค้าน (ฏ): ที่มาของ hash ความยินยอม (N2) + เครื่องหมายลบตามคำขอบนแถวเดิม (S1)
        """ALTER TABLE "PdpaConsentRecords" ADD COLUMN IF NOT EXISTS "EvidenceSource" varchar(20) NULL;""",
        """ALTER TABLE "PdpaConsentRecords" ADD COLUMN IF NOT EXISTS "PseudonymisedAt" timestamp without time zone NULL;""",
        // รอบ 214 ต่อ (ฎ): ช่องใน canonical ของหลักฐาน + เครื่องหมายลบตามคำขอ (legal hold)
        """ALTER TABLE "PlatformPolicyAcceptances" ADD COLUMN IF NOT EXISTS "VersionShown" varchar(50) NULL;""",
        """ALTER TABLE "PlatformPolicyAcceptances" ADD COLUMN IF NOT EXISTS "UserAgent" varchar(500) NULL;""",
        """ALTER TABLE "PlatformPolicyAcceptances" ADD COLUMN IF NOT EXISTS "PseudonymisedAt" timestamp without time zone NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformPolicyAcceptances_User_AcceptedAt" ON "PlatformPolicyAcceptances" ("UserId", "AcceptedAt");""",
        $"""INSERT INTO "PlatformPolicyAcceptances" ("Id","UserId","Email","Purpose","PolicyVersion","AcceptedAt","Channel","IpAddress","EvidenceHash","WithdrawnAt","CreatedAt","IsDeleted") SELECT r."Id", r."SubjectUserId", COALESCE(r."SubjectContact", ''), r."Purpose", r."PolicyVersion", r."GrantedAt", r."Channel", r."IpAddress", r."EvidenceHash", r."WithdrawnAt", r."CreatedAt", r."IsDeleted" FROM "PdpaConsentRecords" r WHERE r."CompanyId" = '00000000-0000-0000-0000-000000000000' AND r."SubjectUserId" IS NOT NULL AND r."Purpose" IN ('{PdpaPolicy.SignupPurpose}', '{PdpaPolicy.PolicyUpdatePurpose}') ON CONFLICT ("Id") DO NOTHING;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyMarkdown" text NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyUpdatedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceMarkdown" text NULL;""",
        """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceUpdatedAt" timestamp without time zone NULL;""",
        """UPDATE "Sites" SET "PrivacyPolicyUrl" = NULL WHERE "PrivacyPolicyUrl" IS NOT NULL AND btrim("PrivacyPolicyUrl") = '';""",
        """UPDATE "Sites" SET "TermsOfServiceUrl" = NULL WHERE "TermsOfServiceUrl" IS NOT NULL AND btrim("TermsOfServiceUrl") = '';""",
    };
}
