namespace Accounting.Helpers;

/// <summary>รอบ 264 P1 — ตารางความยินยอม "งานอัตโนมัติกับคู่ค้า" (<c>PartnerAutomationConsents</c>) · idempotent · ชื่อ index ตรง AccountingDbContext ·
/// unique บางส่วน = แถวที่ยังไม่ถอนได้หนึ่งแถวต่อ (บริษัท · คู่ · ขอบเขต) · trigger append-only: แถวที่ถอนแล้วแก้ไม่ได้ · แถวที่ยังใช้อยู่เปลี่ยนได้แค่ช่องถอน
/// (ฐานเทสต์ได้ตารางจากโมเดล + เทสต์ Db รันชุดนี้ซ้ำเพื่อยืนยันว่า idempotent และ trigger ทำงาน)</summary>
public static class PartnerConsentMigration
{
    public const string AppendOnlyFunction = "PartnerAutomationConsents_append_only";
    public const string NoDeleteFunction = "PartnerAutomationConsents_no_delete";

    /// <summary>SqlState ของ RAISE EXCEPTION ใน trigger ทั้งสอง (plpgsql ตั้งต้น) — ผู้เขียนแปลงเป็น 409 (ฝ่ายค้าน M4)</summary>
    public const string TriggerSqlState = "P0001";

    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "PartnerAutomationConsents" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "TradingPartnershipId" uuid NOT NULL,
            "PartnerCompanyId" uuid NOT NULL,
            "Scope" varchar(32) NOT NULL,
            "MaxAmount" numeric(18,2) NULL,
            "DefaultPayBankAccountId" uuid NULL,
            "TextVersion" integer NOT NULL,
            "EvidenceHash" varchar(64) NOT NULL,
            "GrantedByUserId" uuid NOT NULL,
            "GrantedAt" timestamp without time zone NOT NULL,
            "WithdrawnByUserId" uuid NULL,
            "WithdrawnAt" timestamp without time zone NULL,
            "WithdrawReason" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PartnerAutomationConsents_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        // ฝ่ายค้าน L2: รุ่นของ hash (แถวเดิม = 1)
        """ALTER TABLE "PartnerAutomationConsents" ADD COLUMN IF NOT EXISTS "HashVersion" integer NOT NULL DEFAULT 1;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PartnerAutomationConsents_Active" ON "PartnerAutomationConsents" ("CompanyId", "TradingPartnershipId", "Scope") WHERE "WithdrawnAt" IS NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_PartnerAutomationConsents_Company_Partnership" ON "PartnerAutomationConsents" ("CompanyId", "TradingPartnershipId");""",
        $$"""
        CREATE OR REPLACE FUNCTION "{{AppendOnlyFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          IF OLD."WithdrawnAt" IS NOT NULL THEN
            RAISE EXCEPTION 'PartnerAutomationConsents: ถอนความยินยอมแล้ว แก้แถวไม่ได้ (append-only)';
          END IF;
          IF NEW."CompanyId" IS DISTINCT FROM OLD."CompanyId"
             OR NEW."TradingPartnershipId" IS DISTINCT FROM OLD."TradingPartnershipId"
             OR NEW."PartnerCompanyId" IS DISTINCT FROM OLD."PartnerCompanyId"
             OR NEW."Scope" IS DISTINCT FROM OLD."Scope"
             OR NEW."MaxAmount" IS DISTINCT FROM OLD."MaxAmount"
             OR NEW."DefaultPayBankAccountId" IS DISTINCT FROM OLD."DefaultPayBankAccountId"
             OR NEW."TextVersion" IS DISTINCT FROM OLD."TextVersion"
             OR NEW."EvidenceHash" IS DISTINCT FROM OLD."EvidenceHash"
             OR NEW."GrantedByUserId" IS DISTINCT FROM OLD."GrantedByUserId"
             OR NEW."GrantedAt" IS DISTINCT FROM OLD."GrantedAt"
             OR NEW."HashVersion" IS DISTINCT FROM OLD."HashVersion"
             OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted" THEN
            RAISE EXCEPTION 'PartnerAutomationConsents: แก้ขอบเขต/เพดาน/บัญชี/ผู้ให้ไม่ได้ — ถอนแถวเดิมแล้วให้ความยินยอมใหม่ (append-only)';
          END IF;
          RETURN NEW;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        // ฝ่ายค้าน L1: ห้ามลบแถวหลักฐานความยินยอม (PDPA ม.19 ภาระพิสูจน์อยู่กับผู้ควบคุม) — ยกเว้นบริษัทผู้ให้ไม่มีอยู่แล้ว (cascade ตอนลบบริษัทถาวร)
        $$"""
        CREATE OR REPLACE FUNCTION "{{NoDeleteFunction}}"() RETURNS trigger AS $fn$
        BEGIN
          IF EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = OLD."CompanyId") THEN
            RAISE EXCEPTION 'PartnerAutomationConsents: ลบหลักฐานความยินยอมไม่ได้ (append-only) — ถอนแทน';
          END IF;
          RETURN OLD;
        END
        $fn$ LANGUAGE plpgsql;
        """,
        """DROP TRIGGER IF EXISTS "TR_PartnerAutomationConsents_NoDelete" ON "PartnerAutomationConsents";""",
        $$"""CREATE TRIGGER "TR_PartnerAutomationConsents_NoDelete" BEFORE DELETE ON "PartnerAutomationConsents" FOR EACH ROW EXECUTE FUNCTION "{{NoDeleteFunction}}"();""",
        """DROP TRIGGER IF EXISTS "TR_PartnerAutomationConsents_AppendOnly" ON "PartnerAutomationConsents";""",
        $$"""CREATE TRIGGER "TR_PartnerAutomationConsents_AppendOnly" BEFORE UPDATE ON "PartnerAutomationConsents" FOR EACH ROW EXECUTE FUNCTION "{{AppendOnlyFunction}}"();""",
    };
}
