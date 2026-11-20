namespace Accounting.Helpers;

/// <summary>
/// รอบ 272 — สลิปชำระภาษีจากกลุ่ม LINE/อีเมล ⇒ บันทึกนำส่งในบัญชีลูกค้า · idempotent (<c>IF NOT EXISTS</c>) · ชื่อ index ตรง <c>AccountingDbContext</c> ·
/// ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated) แล้วเทสต์ Db รันชุดนี้ซ้ำเพื่อยืนยันว่า idempotent + trigger append-only ทำงาน
/// <list type="bullet">
/// <item>P2 <c>ProviderAutomationConsents</c> (append-only · ห้ามลบ — ชุด trigger เดียวกับ <c>PartnerConsentMigration</c>) + <c>ProviderConsentRequests</c></item>
/// <item>P3 <c>TaxPaymentRequests</c> (ใบขอชำระ · Control Code เป็นแฮช)</item>
/// <item>P4 <c>TaxSlipEvidences</c> (กันซ้ำ 4 ชั้น §7) · ช่องที่มาบน <c>StatutoryRemittances</c> · สวิตช์ <c>CompanySettings.TaxSlipIntakeEnabled</c></item>
/// <item>P5 ผลตรวจทานหลังนำส่งบน <c>StatutoryRemittances</c></item>
/// </list>
/// </summary>
public static class Round272Migration
{
    public const string AppendOnlyFunction = "ProviderAutomationConsents_append_only";
    public const string NoDeleteFunction = "ProviderAutomationConsents_no_delete";
    public const string TriggerSqlState = "P0001";

    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "ProviderAutomationConsents" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ProviderCompanyId" uuid NOT NULL,
            "Scope" varchar(32) NOT NULL,
            "TaxTypes" varchar(300) NOT NULL,
            "MaxAmount" numeric(18,2) NULL,
            "DefaultTaxPayBankAccountId" uuid NULL,
            "IncludesHeldSlips" boolean NOT NULL DEFAULT false,
            "TextVersion" integer NOT NULL,
            "EvidenceHash" varchar(64) NOT NULL,
            "HashVersion" integer NOT NULL DEFAULT 1,
            "EvidenceSource" varchar(16) NOT NULL DEFAULT 'web',
            "EvidenceAttachmentId" uuid NULL,
            "GrantedByUserId" uuid NOT NULL,
            "GrantedAt" timestamp without time zone NOT NULL,
            "IpAddress" varchar(64) NULL,
            "UserAgent" varchar(300) NULL,
            "WithdrawnByUserId" uuid NULL,
            "WithdrawnAt" timestamp without time zone NULL,
            "WithdrawReason" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_ProviderAutomationConsents_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProviderAutomationConsents_Active" ON "ProviderAutomationConsents" ("CompanyId", "ProviderCompanyId", "Scope", "EvidenceSource") WHERE "WithdrawnAt" IS NULL;""",
        $$"""
        CREATE OR REPLACE FUNCTION "{{AppendOnlyFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          IF OLD."WithdrawnAt" IS NOT NULL THEN
            RAISE EXCEPTION 'ProviderAutomationConsents: ถอนความยินยอมแล้ว แก้แถวไม่ได้ (append-only)';
          END IF;
          IF NEW."CompanyId" IS DISTINCT FROM OLD."CompanyId"
             OR NEW."ProviderCompanyId" IS DISTINCT FROM OLD."ProviderCompanyId"
             OR NEW."Scope" IS DISTINCT FROM OLD."Scope"
             OR NEW."TaxTypes" IS DISTINCT FROM OLD."TaxTypes"
             OR NEW."MaxAmount" IS DISTINCT FROM OLD."MaxAmount"
             OR NEW."DefaultTaxPayBankAccountId" IS DISTINCT FROM OLD."DefaultTaxPayBankAccountId"
             OR NEW."IncludesHeldSlips" IS DISTINCT FROM OLD."IncludesHeldSlips"
             OR NEW."TextVersion" IS DISTINCT FROM OLD."TextVersion"
             OR NEW."EvidenceHash" IS DISTINCT FROM OLD."EvidenceHash"
             OR NEW."HashVersion" IS DISTINCT FROM OLD."HashVersion"
             OR NEW."EvidenceSource" IS DISTINCT FROM OLD."EvidenceSource"
             OR NEW."GrantedByUserId" IS DISTINCT FROM OLD."GrantedByUserId"
             OR NEW."GrantedAt" IS DISTINCT FROM OLD."GrantedAt"
             OR NEW."EvidenceAttachmentId" IS DISTINCT FROM OLD."EvidenceAttachmentId"
             OR NEW."IpAddress" IS DISTINCT FROM OLD."IpAddress"
             OR NEW."UserAgent" IS DISTINCT FROM OLD."UserAgent"
             OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
             OR (OLD."ApprovedAt" IS NOT NULL AND (NEW."ApprovedAt" IS DISTINCT FROM OLD."ApprovedAt" OR NEW."ApprovedByUserId" IS DISTINCT FROM OLD."ApprovedByUserId"))
             OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted" THEN
            RAISE EXCEPTION 'ProviderAutomationConsents: แก้เนื้อความยินยอมไม่ได้ (append-only) — ถอนแล้วให้ใหม่';
          END IF;
          RETURN NEW;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        $$"""
        CREATE OR REPLACE FUNCTION "{{NoDeleteFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          IF EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = OLD."CompanyId") THEN
            RAISE EXCEPTION 'ProviderAutomationConsents: ลบหลักฐานความยินยอมไม่ได้ (append-only) — ถอนแทน';
          END IF;
          RETURN OLD;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        """DROP TRIGGER IF EXISTS "TR_ProviderAutomationConsents_NoDelete" ON "ProviderAutomationConsents";""",
        $$"""CREATE TRIGGER "TR_ProviderAutomationConsents_NoDelete" BEFORE DELETE ON "ProviderAutomationConsents" FOR EACH ROW EXECUTE FUNCTION "{{NoDeleteFunction}}"();""",
        """DROP TRIGGER IF EXISTS "TR_ProviderAutomationConsents_AppendOnly" ON "ProviderAutomationConsents";""",
        $$"""CREATE TRIGGER "TR_ProviderAutomationConsents_AppendOnly" BEFORE UPDATE ON "ProviderAutomationConsents" FOR EACH ROW EXECUTE FUNCTION "{{AppendOnlyFunction}}"();""",

        """
        CREATE TABLE IF NOT EXISTS "ProviderConsentRequests" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ProviderCompanyId" uuid NOT NULL,
            "Status" varchar(16) NOT NULL,
            "RequestedVia" varchar(32) NOT NULL,
            "RequestedByUserId" uuid NULL,
            "ExpiresAt" timestamp without time zone NOT NULL,
            "LastCardAt" timestamp without time zone NULL,
            "ResolvedAt" timestamp without time zone NULL,
            "ConsentId" uuid NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_ProviderConsentRequests_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProviderConsentRequests_Open" ON "ProviderConsentRequests" ("CompanyId", "ProviderCompanyId") WHERE "Status" = 'Open';""",

        """
        CREATE TABLE IF NOT EXISTS "TaxPaymentRequests" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ProviderCompanyId" uuid NOT NULL,
            "ProviderContactId" uuid NULL,
            "RemittanceType" varchar(32) NOT NULL,
            "PeriodYear" integer NOT NULL,
            "PeriodMonth" integer NOT NULL,
            "Amount" numeric(18,2) NOT NULL,
            "Surcharge" numeric(18,2) NOT NULL DEFAULT 0,
            "Penalty" numeric(18,2) NOT NULL DEFAULT 0,
            "ControlCodeHash" varchar(64) NOT NULL,
            "ControlCodeLast4" varchar(8) NOT NULL,
            "ControlCodeExpiresAt" timestamp without time zone NULL,
            "DueDate" timestamp without time zone NULL,
            "IsAmendment" boolean NOT NULL DEFAULT false,
            "BooksMismatch" boolean NOT NULL DEFAULT false,
            "BooksAmountAtSend" numeric(18,2) NULL,
            "SentVia" varchar(16) NOT NULL,
            "SendNote" varchar(500) NULL,
            "SentAt" timestamp without time zone NOT NULL,
            "SentByUserId" uuid NULL,
            "Status" varchar(16) NOT NULL,
            "StatutoryRemittanceId" uuid NULL,
            "TaxSlipEvidenceId" uuid NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_TaxPaymentRequests_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxPaymentRequests_ControlCode" ON "TaxPaymentRequests" ("CompanyId", "ControlCodeHash") WHERE "Status" <> 'Cancelled';""",
        """CREATE INDEX IF NOT EXISTS "IX_TaxPaymentRequests_Provider" ON "TaxPaymentRequests" ("ProviderCompanyId", "Status");""",

        """
        CREATE TABLE IF NOT EXISTS "TaxSlipEvidences" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ClientCompanyId" uuid NULL,
            "Source" varchar(8) NOT NULL,
            "ExternalMessageId" varchar(200) NOT NULL,
            "SourceKey" varchar(64) NOT NULL,
            "ContactId" uuid NULL,
            "FileSha256" varchar(64) NOT NULL,
            "StoragePath" varchar(500) NOT NULL,
            "ContentType" varchar(64) NOT NULL,
            "Agency" varchar(32) NULL,
            "BankCode" varchar(3) NULL,
            "BankTxnHash" varchar(64) NULL,
            "BankTxnLast4" varchar(8) NULL,
            "ControlCodeHash" varchar(64) NULL,
            "ControlCodeLast4" varchar(8) NULL,
            "TaxIdMasked" varchar(32) NULL,
            "Amount" numeric(18,2) NULL,
            "Fee" numeric(18,2) NULL,
            "SlipDate" timestamp without time zone NULL,
            "Status" varchar(20) NOT NULL,
            "RuleCode" varchar(40) NULL,
            "Reason" varchar(500) NULL,
            "CandidatesJson" text NULL,
            "ChoiceExpiresAt" timestamp without time zone NULL,
            "PreselectAccounts" varchar(400) NULL,
            "TaxPaymentRequestId" uuid NULL,
            "StatutoryRemittanceId" uuid NULL,
            "ClientAttachmentId" uuid NULL,
            "ShowAmounts" boolean NOT NULL DEFAULT false,
            "ResolvedByUserId" uuid NULL,
            "ResolvedAt" timestamp without time zone NULL,
            "LastCardText" varchar(1000) NULL,
            "LastCardOutcome" varchar(300) NULL,
            "LastCardAt" timestamp without time zone NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_TaxSlipEvidences_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        // §7 กันซ้ำ: ชั้น 1 ข้อความเดียว · ชั้น 2 ไฟล์เดียว · ชั้น 3 รายการธนาคารเดียว · ชั้น 4 Control Code ที่บันทึกแล้ว
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxSlipEvidences_Message" ON "TaxSlipEvidences" ("CompanyId", "Source", "ExternalMessageId");""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxSlipEvidences_File" ON "TaxSlipEvidences" ("CompanyId", "FileSha256");""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxSlipEvidences_BankTxn" ON "TaxSlipEvidences" ("CompanyId", "BankCode", "BankTxnHash") WHERE "BankTxnHash" IS NOT NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaxSlipEvidences_ControlRemitted" ON "TaxSlipEvidences" ("ClientCompanyId", "ControlCodeHash") WHERE "Status" = 'Remitted' AND "ControlCodeHash" IS NOT NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_TaxSlipEvidences_Status" ON "TaxSlipEvidences" ("CompanyId", "Status");""",

        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "Source" varchar(16) NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "SlipEvidenceId" uuid NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ActedByProviderCompanyId" uuid NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ProviderConsentId" uuid NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ControlCodeLast4" varchar(8) NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ReviewStatus" varchar(20) NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ReviewNote" varchar(500) NULL;""",
        """ALTER TABLE "StatutoryRemittances" ADD COLUMN IF NOT EXISTS "ReviewedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "TaxSlipIntakeEnabled" boolean NOT NULL DEFAULT true;""",
        // ฝ่ายค้าน 272 M4/H3/M6
        """ALTER TABLE "ProviderAutomationConsents" ADD COLUMN IF NOT EXISTS "ApprovedByUserId" uuid NULL;""",
        """ALTER TABLE "ProviderAutomationConsents" ADD COLUMN IF NOT EXISTS "ApprovedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "TaxSlipEvidences" ADD COLUMN IF NOT EXISTS "SenderUnverified" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "TaxSlipEvidences" ADD COLUMN IF NOT EXISTS "FilePurgedAt" timestamp without time zone NULL;""",
        // รอบ 275 แพ็ก D (M4): เตือนแถวที่รอพนักงานนาน (TaxSlipOpenAge)
        """ALTER TABLE "TaxSlipEvidences" ADD COLUMN IF NOT EXISTS "StaffRemindedAt" timestamp without time zone NULL;""",
        // รอบ 275 แพ็ก D ฝ่ายค้าน MED-3/LOW-3
        """ALTER TABLE "TaxSlipEvidences" ADD COLUMN IF NOT EXISTS "ReleasedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "TaxPaymentRequests" ADD COLUMN IF NOT EXISTS "ReopenedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "TaxPaymentRequests" ADD COLUMN IF NOT EXISTS "ReopenNoticeAt" timestamp without time zone NULL;""",
        // รอบ 275 แพ็ก D (ผู้ประสานงาน): ค่าเทียบเลขผู้เสียภาษีเต็ม — แถวเก่า null ⇒ พนักงานยืนยันจากรูป (ไม่ backfill: เลขเต็มไม่เคยถูกเก็บ)
        """ALTER TABLE "TaxSlipEvidences" ADD COLUMN IF NOT EXISTS "TaxIdHash" varchar(64) NULL;""",
        // รอบ 275 แพ็ก D (M4 · ข้อมูลที่ persist ผิดอยู่แล้ว): แถว "ไม่บันทึก" ก่อนรอบนี้ถูกลบไฟล์ทันทีตอนปฏิเสธ — ไม่มีอะไรให้ซ่อม (ไฟล์หายแล้ว · แถวประทับ FilePurgedAt ตรงความจริง)
    };
}
