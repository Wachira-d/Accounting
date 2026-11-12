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
        // ── รอบ 264 P3 (คำตัดสิน 264-3-6/7): การแจ้งจากทุกทางเข้ารับชำระของผู้ขาย + ถอนเมื่อผู้ขายยกเลิก + ท่ออัตโนมัติฝั่งผู้ซื้อ ──
        """ALTER TABLE "PartnerPaymentNotices" ALTER COLUMN "PaymentIntentId" DROP NOT NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SellerPaymentId" uuid NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SellerVoidedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "AutoBlockedReason" varchar(500) NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "AutoBlockedCode" varchar(40) NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "AutoEvaluatedAt" timestamp without time zone NULL;""",
        // กุญแจที่ persist แล้ว (F2 ข้อ 9): แถวเดิมเติม SellerPaymentId จากการรับชำระของผู้ขายที่อ้าง intent เดียวกัน
        // (รูปเลขอ้างอิงตาม GatewayPaymentRecord.Reference = PAY-INTENT-{N}) · ตัวที่ยังไม่ถูกยกเลิกก่อน · ครั้งเดียวใต้ marker (ฝ่ายค้าน L4: ไม่สแกนซ้ำทุกบูต)
        BackfillOnceSql(),
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PartnerPaymentNotices_Company_SellerPayment" ON "PartnerPaymentNotices" ("CompanyId", "SellerPaymentId") WHERE "SellerPaymentId" IS NOT NULL;""",
        // C-03 ของที่ค้างอยู่แล้ว (ผู้ขายยกเลิกการรับชำระไปก่อนรอบนี้): <b>ไม่แก้ด้วย SQL</b> (ฝ่ายค้าน L4 — เปลี่ยนสถานะโดยไม่มี audit) ⇒
        // ตัวกวาดในโค้ด PartnerPayablePipeline.SweepSellerVoidedAsync เดินตัวถอนตัวเดียวกับเส้นปกติ (audit สองฝั่ง + แจ้ง · ไม่แตะแถวที่ปิด/ถอนแล้ว)
    };

    public const string BackfillMarkerKey = "R264P3-PartnerNotice-SellerPaymentId";

    /// <summary>ตัว backfill ล้วน (idempotent — เงื่อนไข IS NULL) · เทสต์เรียกตรง</summary>
    public const string BackfillSellerPaymentSql = """
        UPDATE "PartnerPaymentNotices" n SET "SellerPaymentId" = (
            SELECT p."Id" FROM "Payments" p
            WHERE p."CompanyId" = n."SourceCompanyId" AND p."Reference" = 'PAY-INTENT-' || replace(n."PaymentIntentId"::text, '-', '')
            ORDER BY p."IsDeleted", p."CreatedAt" DESC LIMIT 1)
        WHERE n."SellerPaymentId" IS NULL AND n."PaymentIntentId" IS NOT NULL;
        """;

    /// <summary>backfill ครั้งเดียวใต้ marker <c>OneShotDataFixes</c> (ตารางสร้างก่อนบล็อกนี้ใน DatabaseMigrationHelper)</summary>
    internal static string BackfillOnceSql() => $$"""
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM "OneShotDataFixes" WHERE "Key" = '{{BackfillMarkerKey}}') THEN
                {{BackfillSellerPaymentSql}}
                INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ('{{BackfillMarkerKey}}', (now() at time zone 'utc')) ON CONFLICT DO NOTHING;
            END IF;
        END $$;
        """;
}
