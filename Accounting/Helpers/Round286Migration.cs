namespace Accounting.Helpers;

/// <summary>
/// รอบ 286 — ผู้จ่ายที่ไว้ใจได้ (คำสั่งเจ้าของ · <see cref="TrustedSlipPayerRule"/>) · idempotent (<c>IF NOT EXISTS</c>) · ชื่อ index ตรง <c>AccountingDbContext</c> ·
/// ค่าตั้งต้น <c>SlipTrustedAutoEnabled = false</c> (บริษัทเดิมไม่เปลี่ยนพฤติกรรมเงียบ ๆ) · ไม่มีของเสียที่ persist ไว้ต้องซ่อม
/// (คอลัมน์/ตารางใหม่ทั้งหมด · รายการชำระเดิมไม่มีกุญแจ ⇒ ไม่ถูกเรียนย้อนหลัง — ความไว้ใจเริ่มนับจากสลิปหลัง deploy) · commit 19445ff6
/// </summary>
public static class Round286Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipTrustedAutoEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipTrustedDailyCapPerKey" numeric(18,2) NOT NULL DEFAULT 50000;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipTrustedReviewBusinessDays" integer NOT NULL DEFAULT 3;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustKeyHash" varchar(64) NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustKeyLabel" varchar(120) NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustKeyAccountId" uuid NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustLearnedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustProvisionalAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustReviewDueAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustVerifiedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustFailedAt" timestamp without time zone NULL;""",
        // ฝ่ายค้าน 286: หนึ่งการชำระพิสูจน์หนึ่งรายการ (HIGH-1) · เพดานยอดค้างยืนยันทั้งบริษัท + ล็อกความไว้ใจรายผู้ติดต่อ (HIGH-3)
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "TrustProofPaymentId" uuid NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipTrustedCompanyOutstandingCap" numeric(18,2) NOT NULL DEFAULT 100000;""",
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "SlipTrustBlockedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "SlipTrustBlockedReason" varchar(500) NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "TrustedSlipPayers" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ContactId" uuid NOT NULL,
            "KeyHash" varchar(64) NOT NULL DEFAULT '',
            "Label" varchar(120) NOT NULL DEFAULT '',
            "ReceivingBankAccountId" uuid NOT NULL,
            "MaxConfirmedAmount" numeric(18,2) NOT NULL DEFAULT 0,
            "ConfirmedCount" integer NOT NULL DEFAULT 0,
            "FirstTrustedAt" timestamp without time zone NOT NULL,
            "LastConfirmedAt" timestamp without time zone NOT NULL,
            "LearnedFromIntentId" uuid NULL,
            "RevokedAt" timestamp without time zone NULL,
            "RevokedBy" varchar(64) NULL,
            "RevokeReason" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_TrustedSlipPayers_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TrustedSlipPayers_Company_Key" ON "TrustedSlipPayers" ("CompanyId", "KeyHash");""",
    };
}
