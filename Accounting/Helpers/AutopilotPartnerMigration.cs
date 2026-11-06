namespace Accounting.Helpers;

/// <summary>รอบ 255 WP-12B — คอลัมน์/ตารางของ Autopilot ส่วน B (รายการประจำทุก N เดือน + สร้างล่วงหน้า · คู่ค้า NextAcc · ใบซื้อร่างจากคู่ค้า ·
/// 50 ทวิ กลับผ่านคู่ค้า · พอร์ทัลลิงก์วิเศษ) · <c>ADD COLUMN IF NOT EXISTS</c>/<c>CREATE TABLE IF NOT EXISTS</c> ทุกบรรทัด (idempotent · รันทุกบูต) ·
/// ค่าตั้งต้นทุกคอลัมน์ = ปิด/NULL/0 = พฤติกรรมเดิม (ไม่มีค่าเก่าต้องซ่อม) · ผู้เรียก: <c>DatabaseMigrationHelper.GetAlterStatements</c> ·
/// ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)</summary>
public static class AutopilotPartnerMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // ข้อ 2: รายการประจำทุก N เดือน + สร้างล่วงหน้า (NULL/0 = เดิม)
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "IntervalMonths" integer NULL;""",
        """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LeadDays" integer NOT NULL DEFAULT 0;""",
        // ข้อ 6: เพดานคู่ค้าต่อแพ็กเกจ (NULL = ไม่จำกัด — Y5)
        """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "MaxTradingPartners" integer NULL;""",
        // ข้อ 7: ใบซื้อร่างจากคู่ค้า (ค่าตั้งต้นปิด · เพดานต่อคู่ของผู้รับ · หลักฐานภาษีซื้อ)
        """ALTER TABLE "WorkflowAutomationConfigs" ADD COLUMN IF NOT EXISTS "CreatePurchaseDraftsFromPartners" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "PurchaseDraftLimitA" numeric(18,2) NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "PurchaseDraftLimitB" numeric(18,2) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PartnerInputVatEvidence" varchar(32) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PartnerSourceLinkId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_Documents_PartnerSourceLinkId" ON "Documents" ("CompanyId", "PartnerSourceLinkId") WHERE "PartnerSourceLinkId" IS NOT NULL;""",
        // ข้อ 8: 50 ทวิ ผ่านคู่ค้า (ค่าตั้งต้นปิด · watermark · ผลการส่ง)
        """ALTER TABLE "WorkflowAutomationConfigs" ADD COLUMN IF NOT EXISTS "SendWhtCertsToPartners" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "WorkflowAutomationConfigs" ADD COLUMN IF NOT EXISTS "SendWhtCertsToPartnersSince" timestamp NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PartnerDeliveredAt" timestamp NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PartnerIntakeId" uuid NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PartnerDeliveryError" text NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PartnerDeliveryAttempts" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "PartnerVoidNotifiedAt" timestamp NULL;""",
        // ข้อ 8: ลิงก์วิเศษพอร์ทัล (token เก็บเป็น hash)
        """
        CREATE TABLE IF NOT EXISTS "PortalMagicLinks" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "PortalAccessId" uuid NOT NULL,
            "TokenHash" varchar(64) NOT NULL,
            "ExpiresAt" timestamp without time zone NOT NULL,
            "UsedAt" timestamp without time zone NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PortalMagicLinks_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PortalMagicLinks_TokenHash" ON "PortalMagicLinks" ("TokenHash");""",
        """CREATE INDEX IF NOT EXISTS "IX_PortalMagicLinks_Company_Access" ON "PortalMagicLinks" ("CompanyId", "PortalAccessId");""",
    };
}
