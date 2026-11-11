namespace Accounting.Helpers;

/// <summary>รอบ 258-3 (F-H3) — ตารางการแจ้ง "ผู้ขายคู่ค้ารับเงินแล้ว" ในบริษัทผู้ซื้อ · <c>CREATE TABLE IF NOT EXISTS</c> (idempotent) · ชื่อ index ตรง AccountingDbContext ·
/// ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated)</summary>
public static class PartnerPaymentNoticeMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "PartnerPaymentNotices" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "TradingPartnershipId" uuid NOT NULL,
            "LinkId" uuid NOT NULL,
            "SourceCompanyId" uuid NOT NULL,
            "SourceDocumentId" uuid NOT NULL,
            "SourceDocumentNumber" varchar(60) NOT NULL DEFAULT '',
            "TargetDocumentId" uuid NOT NULL,
            "PaymentIntentId" uuid NOT NULL,
            "CashAmount" numeric(18,2) NOT NULL DEFAULT 0,
            "SellerRecordedWht" numeric(18,2) NOT NULL DEFAULT 0,
            "PaidAt" timestamp without time zone NOT NULL,
            "Status" varchar(16) NOT NULL DEFAULT 'Open',
            "RecordedPaymentId" uuid NULL,
            "ResolvedAt" timestamp without time zone NULL,
            "ResolvedBy" varchar(200) NULL,
            "Note" varchar(1000) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PartnerPaymentNotices_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PartnerPaymentNotices_Company_Intent" ON "PartnerPaymentNotices" ("CompanyId", "PaymentIntentId");""",
        """CREATE INDEX IF NOT EXISTS "IX_PartnerPaymentNotices_Company_Status" ON "PartnerPaymentNotices" ("CompanyId", "Status");""",
        // ฝ่ายค้าน M5 (ชุดแก้): ช่องทางที่ผู้ขายรับเงิน — ตารางที่สร้างไปแล้วได้คอลัมน์เพิ่ม
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SourceChannel" varchar(32) NULL;""",
    };
}
