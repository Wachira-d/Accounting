namespace Accounting.Helpers;

/// <summary>
/// รอบ 267 แพ็ก P2 — schema ของ "ผู้ลงนาม + บัญชีหลักฐาน + ฉบับที่ลูกค้าเซ็น" (คำตัดสิน 267-1-4 · 267-1-6 · DOCUMENT_FLOW §2.12n-P2) ·
/// รันตอนบูตผ่าน <c>DatabaseMigrationHelper</c> (รันซ้ำได้) · ตารางหลักฐานเป็น append-only ด้วย trigger ในฐาน
/// (โค้ดที่เผลอ UPDATE/DELETE ล้มดังที่ฐาน — ไม่ใช่แค่ข้อตกลงในโค้ด)
/// </summary>
public static class CustomerSignMigration
{
    /// <summary>SQLSTATE ของ RAISE EXCEPTION ใน trigger (plpgsql ค่าเริ่มต้น)</summary>
    public const string TriggerSqlState = "P0001";
    public const string GuardFunction = "customer_document_actions_guard";
    public const string NoDeleteFunction = "customer_document_actions_no_delete";
    public const string NoTruncateFunction = "customer_document_actions_no_truncate";

    public static IEnumerable<string> Statements() => new[]
    {
        """
        CREATE TABLE IF NOT EXISTS "CustomerDocumentActions" (
            "Id" uuid PRIMARY KEY,
            "CompanyId" uuid NOT NULL REFERENCES "Companies"("Id"),
            "DocumentId" uuid NOT NULL,
            "RevisionNumber" integer NOT NULL DEFAULT 0,
            "Action" varchar(30) NOT NULL,
            "Channel" varchar(20) NOT NULL,
            "IdentityLevel" varchar(20) NOT NULL,
            "SignerFirstName" varchar(200) NULL,
            "SignerLastName" varchar(100) NULL,
            "SignerTitle" varchar(100) NULL,
            "SignerOrgName" varchar(300) NULL,
            "SignerEmail" varchar(254) NULL,
            "EmailVerified" boolean NOT NULL DEFAULT false,
            "AttestationVersion" varchar(40) NULL,
            "AttestationTextHash" varchar(64) NULL,
            "SignatureFilePath" varchar(500) NULL,
            "SignatureSha256" varchar(64) NULL,
            "ReasonCode" varchar(40) NULL,
            "Message" varchar(1000) NULL,
            "ContentHash" varchar(80) NOT NULL,
            "TokenHash" varchar(64) NOT NULL,
            "Ip" varchar(64) NULL,
            "UserAgentHash" varchar(64) NULL,
            "SignedPdfPath" varchar(500) NULL,
            "SignedPdfSha256" varchar(64) NULL,
            "SignedPdfAt" timestamptz NULL,
            "ErasedAt" timestamptz NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "UpdatedAt" timestamptz NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_CustomerDocumentActions_Company_Doc" ON "CustomerDocumentActions" ("CompanyId", "DocumentId", "CreatedAt");""",
        // รอบ 267 P5: วันส่งที่ผู้ขายยืนยัน (รับทราบใบสั่งซื้อ · S31) · โปรไฟล์ผู้ลงนามที่เติมฟอร์ม (อ้างที่มาเท่านั้น — 267-1-13)
        """ALTER TABLE "CustomerDocumentActions" ADD COLUMN IF NOT EXISTS "PromisedDate" timestamptz NULL;""",
        """ALTER TABLE "CustomerDocumentActions" ADD COLUMN IF NOT EXISTS "SignerProfileId" uuid NULL;""",
        // รอบ 267 P5 (267-1-13): ข้อมูลผู้ลงนามที่ยินยอมให้จำกับ LINE — ชื่อ/ตำแหน่ง/อีเมลเข้ารหัส (EncryptedColumnConverter ⇒ ความยาวเผื่อ ciphertext) · หนึ่งแถว/บริษัท/คน
        """
        CREATE TABLE IF NOT EXISTS "CustomerSignerProfiles" (
            "Id" uuid PRIMARY KEY,
            "CompanyId" uuid NOT NULL REFERENCES "Companies"("Id"),
            "SubjectKey" varchar(64) NOT NULL,
            "ContactId" uuid NULL,
            "FirstName" varchar(600) NOT NULL,
            "LastName" varchar(600) NOT NULL,
            "Title" varchar(600) NULL,
            "Email" varchar(800) NULL,
            "ConsentRecordId" uuid NOT NULL,
            "LastUsedAt" timestamptz NOT NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "UpdatedAt" timestamptz NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CustomerSignerProfiles_Company_Subject" ON "CustomerSignerProfiles" ("CompanyId", "SubjectKey");""",
        """CREATE INDEX IF NOT EXISTS "IX_CustomerSignerProfiles_LastUsed" ON "CustomerSignerProfiles" ("LastUsedAt");""",
        """
        CREATE TABLE IF NOT EXISTS "CustomerSignOtps" (
            "Id" uuid PRIMARY KEY,
            "CompanyId" uuid NOT NULL REFERENCES "Companies"("Id"),
            "DocumentId" uuid NOT NULL,
            "EmailHash" varchar(64) NOT NULL,
            "CodeHash" varchar(64) NOT NULL,
            "ExpiresAt" timestamptz NOT NULL,
            "Attempts" integer NOT NULL DEFAULT 0,
            "ConsumedAt" timestamptz NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "UpdatedAt" timestamptz NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_CustomerSignOtps_Company_Doc" ON "CustomerSignOtps" ("CompanyId", "DocumentId", "CreatedAt");""",
        // ภาพฉายใหม่บนใบ: คำตอบล่าสุดของลูกค้าต่อ Rev (ชื่อ enum) — ผู้อ่าน: ปุ่มบนหน้าเอกสาร · ป้ายบนรายการเอกสาร
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomerResponse" varchar(30) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomerResponseAt" timestamptz NULL;""",
        // 267-1-6: ยอดขั้นต่ำที่ต้องยืนยันอีเมลด้วยรหัสก่อนยอมรับใบเสนอราคา (null = ไม่ใช้รหัส — ค่าตั้งต้น)
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "CustomerSignOtpMinAmount" numeric(18,2) NULL;""",
        // outbox: ช่อง CustomerActionId/SignedCopyDoneAt อยู่ใน SignRequestMigration (เจ้าของตาราง SignEventOutbox — รันก่อนไฟล์นี้)
        // ── append-only: แก้ได้เฉพาะ (1) เติม SignedPdf* ครั้งเดียว (2) ลบข้อมูลส่วนบุคคลพร้อมประทับ ErasedAt · ลบแถวไม่ได้ ──
        $$"""
        CREATE OR REPLACE FUNCTION "{{GuardFunction}}"() RETURNS trigger AS $fn$
        DECLARE erasing boolean := OLD."ErasedAt" IS NULL AND NEW."ErasedAt" IS NOT NULL;
        BEGIN
          IF NEW."CompanyId" IS DISTINCT FROM OLD."CompanyId"
             OR NEW."DocumentId" IS DISTINCT FROM OLD."DocumentId"
             OR NEW."RevisionNumber" IS DISTINCT FROM OLD."RevisionNumber"
             OR NEW."Action" IS DISTINCT FROM OLD."Action"
             OR NEW."Channel" IS DISTINCT FROM OLD."Channel"
             OR NEW."IdentityLevel" IS DISTINCT FROM OLD."IdentityLevel"
             OR NEW."EmailVerified" IS DISTINCT FROM OLD."EmailVerified"
             OR NEW."AttestationVersion" IS DISTINCT FROM OLD."AttestationVersion"
             OR NEW."AttestationTextHash" IS DISTINCT FROM OLD."AttestationTextHash"
             OR NEW."SignatureSha256" IS DISTINCT FROM OLD."SignatureSha256"
             OR NEW."ReasonCode" IS DISTINCT FROM OLD."ReasonCode"
             OR NEW."ContentHash" IS DISTINCT FROM OLD."ContentHash"
             OR NEW."TokenHash" IS DISTINCT FROM OLD."TokenHash"
             OR NEW."PromisedDate" IS DISTINCT FROM OLD."PromisedDate"
             OR NEW."SignerProfileId" IS DISTINCT FROM OLD."SignerProfileId"
             OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
             OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy"
             OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted" THEN
            RAISE EXCEPTION 'CustomerDocumentActions: แก้แถวหลักฐานการตอบรับของลูกค้าไม่ได้ (append-only)';
          END IF;
          IF OLD."ErasedAt" IS NOT NULL AND NEW."ErasedAt" IS DISTINCT FROM OLD."ErasedAt" THEN
            RAISE EXCEPTION 'CustomerDocumentActions: แถวที่ลบข้อมูลส่วนบุคคลแล้วแก้ไม่ได้ (append-only)';
          END IF;
          IF NOT erasing AND (
               NEW."SignerFirstName" IS DISTINCT FROM OLD."SignerFirstName"
            OR NEW."SignerLastName" IS DISTINCT FROM OLD."SignerLastName"
            OR NEW."SignerTitle" IS DISTINCT FROM OLD."SignerTitle"
            OR NEW."SignerOrgName" IS DISTINCT FROM OLD."SignerOrgName"
            OR NEW."SignerEmail" IS DISTINCT FROM OLD."SignerEmail"
            OR NEW."SignatureFilePath" IS DISTINCT FROM OLD."SignatureFilePath"
            OR NEW."Message" IS DISTINCT FROM OLD."Message"
            OR NEW."Ip" IS DISTINCT FROM OLD."Ip"
            OR NEW."UserAgentHash" IS DISTINCT FROM OLD."UserAgentHash") THEN
            RAISE EXCEPTION 'CustomerDocumentActions: แก้ข้อมูลผู้ลงนามไม่ได้ (append-only) — ลบได้เฉพาะตามคำขอของเจ้าของข้อมูลหลังพ้นอายุเก็บ';
          END IF;
          -- ฝ่ายค้าน 267-P2 L2: "ลบตามคำขอ" ล้างได้อย่างเดียว (ชื่อ = ค่าคงที่ของการปิดบัง · ช่องอื่น = NULL) — ใช้เป็นทางเขียนชื่อปลอมพร้อมประทับ ErasedAt ไม่ได้ ·
          -- และห้ามแตะ hash/เวลาของฉบับที่ลงนามในคำสั่งเดียวกัน
          IF erasing AND (
               NEW."SignerFirstName" IS DISTINCT FROM '{{QuotationAcceptance.Anonymized}}'
            OR NEW."SignerLastName" IS NOT NULL OR NEW."SignerTitle" IS NOT NULL OR NEW."SignerOrgName" IS NOT NULL
            OR NEW."SignerEmail" IS NOT NULL OR NEW."SignatureFilePath" IS NOT NULL OR NEW."Message" IS NOT NULL
            OR NEW."Ip" IS NOT NULL OR NEW."UserAgentHash" IS NOT NULL OR NEW."SignedPdfPath" IS NOT NULL
            OR NEW."SignedPdfSha256" IS DISTINCT FROM OLD."SignedPdfSha256"
            OR NEW."SignedPdfAt" IS DISTINCT FROM OLD."SignedPdfAt") THEN
            RAISE EXCEPTION 'CustomerDocumentActions: การลบข้อมูลส่วนบุคคลล้างค่าได้อย่างเดียว (append-only)';
          END IF;
          IF NEW."SignedPdfSha256" IS DISTINCT FROM OLD."SignedPdfSha256" AND OLD."SignedPdfSha256" IS NOT NULL THEN
            RAISE EXCEPTION 'CustomerDocumentActions: ฉบับที่ลูกค้าเซ็นถูกบันทึกแล้ว เขียนทับไม่ได้ (append-only)';
          END IF;
          IF NOT erasing AND OLD."SignedPdfPath" IS NOT NULL
             AND (NEW."SignedPdfPath" IS DISTINCT FROM OLD."SignedPdfPath" OR NEW."SignedPdfAt" IS DISTINCT FROM OLD."SignedPdfAt") THEN
            RAISE EXCEPTION 'CustomerDocumentActions: ฉบับที่ลูกค้าเซ็นถูกบันทึกแล้ว เขียนทับไม่ได้ (append-only)';
          END IF;
          RETURN NEW;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        $$"""
        CREATE OR REPLACE FUNCTION "{{NoDeleteFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          IF EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = OLD."CompanyId") THEN
            RAISE EXCEPTION 'CustomerDocumentActions: ลบแถวหลักฐานการตอบรับของลูกค้าไม่ได้ (append-only)';
          END IF;
          RETURN OLD;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        """DROP TRIGGER IF EXISTS "TR_CustomerDocumentActions_NoDelete" ON "CustomerDocumentActions";""",
        $$"""CREATE TRIGGER "TR_CustomerDocumentActions_NoDelete" BEFORE DELETE ON "CustomerDocumentActions" FOR EACH ROW EXECUTE FUNCTION "{{NoDeleteFunction}}"();""",
        // ฝ่ายค้าน 267-P2 L2: TRUNCATE ข้าม trigger ระดับแถว ⇒ กันระดับคำสั่ง
        $$"""
        CREATE OR REPLACE FUNCTION "{{NoTruncateFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          RAISE EXCEPTION 'CustomerDocumentActions: ล้างตารางหลักฐานการตอบรับของลูกค้าไม่ได้ (append-only)';
        END
        $fn$ LANGUAGE plpgsql;
        """,
        """DROP TRIGGER IF EXISTS "TR_CustomerDocumentActions_NoTruncate" ON "CustomerDocumentActions";""",
        $$"""CREATE TRIGGER "TR_CustomerDocumentActions_NoTruncate" BEFORE TRUNCATE ON "CustomerDocumentActions" FOR EACH STATEMENT EXECUTE FUNCTION "{{NoTruncateFunction}}"();""",
        """DROP TRIGGER IF EXISTS "TR_CustomerDocumentActions_Guard" ON "CustomerDocumentActions";""",
        $$"""CREATE TRIGGER "TR_CustomerDocumentActions_Guard" BEFORE UPDATE ON "CustomerDocumentActions" FOR EACH ROW EXECUTE FUNCTION "{{GuardFunction}}"();""",
    };
}
