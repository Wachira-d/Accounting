namespace Accounting.Helpers;

/// <summary>รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §5 · คำตัดสินข้อ 180) — schema ของการ "เก็บเงินได้จริง" <b>บล็อกเดียว · รันทุกบูตได้</b>
/// (CREATE TABLE/INDEX IF NOT EXISTS + ADD COLUMN IF NOT EXISTS ล้วน · ไม่แก้แถวเดิม) · ชื่อ index ตรงกับ <c>AccountingDbContext</c>
/// (ฐานใหม่/ฐานเทสต์ได้จาก EnsureCreated) · ต้องอยู่หลัง <see cref="PlatformBillingPhase0Migration"/></summary>
public static class PlatformBillingPhase2Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        // ── ขั้น 1: ค่าตั้งการเรียกเก็บ (แถวเดียว) · หลักฐานยอมรับเงื่อนไขการซื้อ · ผูกรายการชำระกับใบแจ้งหนี้ ──
        """CREATE TABLE IF NOT EXISTS "PlatformBillingSettings" ("Id" uuid PRIMARY KEY, "RenewalInvoiceLeadDays" integer NOT NULL DEFAULT 15, "ReminderDaysBeforeDue" varchar(100) NOT NULL DEFAULT '7,3,1', "ReminderDaysAfterDue" varchar(100) NOT NULL DEFAULT '1,3,7', "SuspendGraceDays" integer NOT NULL DEFAULT 14, "AutoSuspendEnabled" boolean NOT NULL DEFAULT true, "PaymentLinkExpiryDays" integer NOT NULL DEFAULT 14, "ReminderEmailEnabled" boolean NOT NULL DEFAULT true, "ReminderLineEnabled" boolean NOT NULL DEFAULT true, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE TABLE IF NOT EXISTS "PlatformPurchaseAcceptances" ("Id" uuid PRIMARY KEY, "BuyerCompanyId" uuid NOT NULL, "SubjectKind" varchar(40) NOT NULL DEFAULT '', "SubjectId" uuid NULL, "Subject" varchar(256) NOT NULL DEFAULT '', "PolicyVersion" varchar(50) NOT NULL DEFAULT '', "AcceptedAt" timestamp without time zone NOT NULL, "Channel" varchar(50) NULL, "IpAddress" varchar(45) NULL, "UserAgent" varchar(500) NULL, "EvidenceHash" varchar(100) NOT NULL DEFAULT '', "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformPurchaseAcceptances_Buyer_AcceptedAt" ON "PlatformPurchaseAcceptances" ("BuyerCompanyId", "AcceptedAt");""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformPurchaseAcceptances_Subject" ON "PlatformPurchaseAcceptances" ("SubjectKind", "SubjectId");""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PlatformInvoiceDocumentId" uuid NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PolicyAcceptanceId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_SubscriptionPayments_PlatformInvoiceDocumentId" ON "SubscriptionPayments" ("PlatformInvoiceDocumentId") WHERE "PlatformInvoiceDocumentId" IS NOT NULL;""",
    };
}
