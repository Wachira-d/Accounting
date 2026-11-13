namespace Accounting.Helpers;

/// <summary>รอบ 264 P4 (คำตัดสิน 264-5-11/12/13) — ตาราง "ผู้ซื้อเป็นผู้เริ่มจ่าย" (<c>PartnerPaymentPlans</c>) + คอลัมน์ผลจับคู่สลิปบนการแจ้ง (L3) ·
/// <c>CREATE TABLE IF NOT EXISTS</c> (idempotent) · ชื่อ index ตรง AccountingDbContext · ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated)</summary>
public static class PartnerPaymentPlanMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "PartnerPaymentPlans" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "TradingPartnershipId" uuid NOT NULL,
            "LinkId" uuid NOT NULL,
            "PurchaseDocumentId" uuid NOT NULL,
            "SourceCompanyId" uuid NOT NULL,
            "SourceDocumentId" uuid NOT NULL,
            "Kind" varchar(16) NOT NULL DEFAULT '',
            "BankAccountId" uuid NOT NULL,
            "Method" varchar(32) NOT NULL DEFAULT '',
            "Amount" numeric(18,2) NOT NULL DEFAULT 0,
            "ChosenByUserId" uuid NOT NULL,
            "SellerIntentId" uuid NULL,
            "BuyerPaymentId" uuid NULL,
            "TransferredAt" timestamp without time zone NULL,
            "SlipCopyNote" varchar(500) NULL,
            "AccountOrigin" varchar(32) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PartnerPaymentPlans_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PartnerPaymentPlans_Company_SellerIntent" ON "PartnerPaymentPlans" ("CompanyId", "SellerIntentId") WHERE "SellerIntentId" IS NOT NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_PartnerPaymentPlans_Company_Purchase" ON "PartnerPaymentPlans" ("CompanyId", "PurchaseDocumentId");""",
        // ฝ่ายค้าน 264-P2 L3 (คำตัดสิน 264-5-13): ผลจับคู่สลิปของผู้ซื้อหลังล้างเลขปิดบาง (ชื่อ PartnerPaySourceAccount.SlipKind)
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipPayerMatch" varchar(16) NULL;""",
    };
}
