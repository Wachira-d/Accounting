namespace Accounting.Helpers;

/// <summary>รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §5 · คำตัดสินข้อ 180) — schema ของการ "เก็บเงินได้จริง" <b>บล็อกเดียว · รันทุกบูตได้</b>
/// (CREATE TABLE/INDEX IF NOT EXISTS + ADD COLUMN IF NOT EXISTS · แก้แถวเดิมเฉพาะ dedupe ก่อน unique index "รายการเปิดหนึ่งรายการต่อใบ" — idempotent) · ชื่อ index ตรงกับ <c>AccountingDbContext</c>
/// (ฐานใหม่/ฐานเทสต์ได้จาก EnsureCreated) · ต้องอยู่หลัง <see cref="PlatformBillingPhase0Migration"/></summary>
public static class PlatformBillingPhase2Migration
{
    /// <summary>ฝ่ายค้าน S3: เงื่อนไขของ unique index "รายการเปิดหนึ่งรายการต่อใบ" — ตัวเดียวทั้ง DbContext (EnsureCreated) และ SQL ด้านล่าง ·
    /// สถานะ 0/1 = <c>SubscriptionPaymentStatus.Pending/UnderReview</c> (เก็บเป็นเลข)</summary>
    public const string OpenPerInvoiceFilter =
        "\"PlatformInvoiceDocumentId\" IS NOT NULL AND \"Status\" IN (0, 1) AND \"IsDeleted\" = false AND \"AttentionReason\" IS NULL";

    public static IReadOnlyList<string> Statements() => new[]
    {
        // ── ขั้น 1: ค่าตั้งการเรียกเก็บ (แถวเดียว) · ผูกรายการชำระกับใบแจ้งหนี้ (หลักฐานยอมรับนโยบาย = ตาราง PolicyAcceptances ของ part B) ──
        """CREATE TABLE IF NOT EXISTS "PlatformBillingSettings" ("Id" uuid PRIMARY KEY, "RenewalInvoiceLeadDays" integer NOT NULL DEFAULT 15, "ReminderDaysBeforeDue" varchar(100) NOT NULL DEFAULT '7,3,1', "ReminderDaysAfterDue" varchar(100) NOT NULL DEFAULT '1,3,7', "SuspendGraceDays" integer NOT NULL DEFAULT 14, "AutoSuspendEnabled" boolean NOT NULL DEFAULT true, "PaymentLinkExpiryDays" integer NOT NULL DEFAULT 14, "ReminderEmailEnabled" boolean NOT NULL DEFAULT true, "ReminderLineEnabled" boolean NOT NULL DEFAULT true, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PlatformInvoiceDocumentId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_SubscriptionPayments_PlatformInvoiceDocumentId" ON "SubscriptionPayments" ("PlatformInvoiceDocumentId") WHERE "PlatformInvoiceDocumentId" IS NOT NULL;""",
        // ── ฝ่ายค้านรอบ 219 เฟส 2 (S2/B2/S3): เรื่องที่แอดมินต้องจัดการบนรายการชำระ + ใบละหนึ่งรายการเปิด ──
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "AttentionReason" varchar(500) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "AttentionRule" varchar(60) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "AttentionAt" timestamp without time zone NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "WhtCertificateFileName" varchar(500) NULL;""",
        // dedupe ก่อนสร้าง unique index (รันซ้ำได้ — หลังมี index แล้วไม่มีแถวเข้าเงื่อนไข): รายการเปิดซ้อนบนใบเดียว
        // (ก) ไม่มีสลิป + มีรายการอื่นที่ "ดีกว่า" (มีสลิป หรือใหม่กว่า) ⇒ ยกเลิก (ยังไม่มีเงิน/หลักฐาน) — ยกเว้นมี intent ที่ยังไม่จบผูกอยู่
        // (ข) มีสลิปทั้งคู่ ⇒ ไม่ยกเลิก (มีหลักฐานเงิน) — พักให้แอดมินตัดสิน (AttentionReason) ตัวที่เก่ากว่า
        """
        UPDATE "SubscriptionPayments" p SET "Status" = 4, "ReviewNotes" = left(coalesce(p."ReviewNotes" || ' · ', '') || 'ยกเลิกอัตโนมัติ: รายการแจ้งชำระซ้อนบนใบเดียวกัน (ยังไม่แนบสลิป)', 1000)
        WHERE p."PlatformInvoiceDocumentId" IS NOT NULL AND p."Status" IN (0, 1) AND p."IsDeleted" = false AND p."AttentionReason" IS NULL
          AND p."SlipFileName" IS NULL
          AND NOT EXISTS (SELECT 1 FROM "PaymentIntents" i WHERE i."SourceId" = p."Id" AND i."Status" IN (0, 1, 2))
          AND EXISTS (SELECT 1 FROM "SubscriptionPayments" q WHERE q."PlatformInvoiceDocumentId" = p."PlatformInvoiceDocumentId"
                AND q."Status" IN (0, 1) AND q."IsDeleted" = false AND q."AttentionReason" IS NULL AND q."Id" <> p."Id"
                AND (q."SlipFileName" IS NOT NULL OR q."CreatedAt" > p."CreatedAt" OR (q."CreatedAt" = p."CreatedAt" AND q."Id" > p."Id")));
        """,
        """
        UPDATE "SubscriptionPayments" p SET "AttentionReason" = 'รายการแจ้งชำระซ้อนบนใบแจ้งหนี้เดียวกัน — ตรวจว่ารับเงินซ้ำหรือไม่ก่อนอนุมัติ', "AttentionRule" = 'PLATFORM-PAYMENT-HELD', "AttentionAt" = (now() at time zone 'utc')
        WHERE p."PlatformInvoiceDocumentId" IS NOT NULL AND p."Status" IN (0, 1) AND p."IsDeleted" = false AND p."AttentionReason" IS NULL
          AND EXISTS (SELECT 1 FROM "SubscriptionPayments" q WHERE q."PlatformInvoiceDocumentId" = p."PlatformInvoiceDocumentId"
                AND q."Status" IN (0, 1) AND q."IsDeleted" = false AND q."AttentionReason" IS NULL AND q."Id" <> p."Id"
                AND (q."CreatedAt" > p."CreatedAt" OR (q."CreatedAt" = p."CreatedAt" AND q."Id" > p."Id")));
        """,
        $"""CREATE UNIQUE INDEX IF NOT EXISTS "UX_SubscriptionPayments_OpenPerInvoice" ON "SubscriptionPayments" ("PlatformInvoiceDocumentId") WHERE {OpenPerInvoiceFilter};""",
        // ── ขั้น 4: ลิงก์ชำระ (เก็บเฉพาะ hash ของโทเคน) ──
        """CREATE TABLE IF NOT EXISTS "PlatformPaymentLinks" ("Id" uuid PRIMARY KEY, "PlatformCompanyId" uuid NOT NULL, "BuyerCompanyId" uuid NOT NULL, "DocumentId" uuid NOT NULL, "TokenHash" varchar(64) NOT NULL DEFAULT '', "AmountSnapshot" numeric(18,2) NOT NULL DEFAULT 0, "ExpiresAt" timestamp without time zone NOT NULL, "RevokedAt" timestamp without time zone NULL, "RevokedBy" varchar(256) NULL, "RevokedReason" varchar(300) NULL, "ViewCount" integer NOT NULL DEFAULT 0, "LastViewedAt" timestamp without time zone NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformPaymentLinks_TokenHash" ON "PlatformPaymentLinks" ("TokenHash");""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformPaymentLinks_Document" ON "PlatformPaymentLinks" ("DocumentId");""",
        // ── ขั้น 3: วิธีชำระส่วนเสริมต่อการซื้อ (ยกเลิกเมื่อสิ้นรอบเป็นของ part B) ──
        """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "BillingChoice" integer NOT NULL DEFAULT 0;""",
        // ── ขั้น 2: ส่งใบ + ทวงหนี้ (คีย์ข้อความ unique ต่อช่อง · สถานะทวงหนี้ต่อบริษัท = watermark ของงาน) ──
        """CREATE TABLE IF NOT EXISTS "PlatformBillingNotices" ("Id" uuid PRIMARY KEY, "BuyerCompanyId" uuid NOT NULL, "DocumentId" uuid NULL, "MessageKey" varchar(200) NOT NULL DEFAULT '', "Channel" varchar(20) NOT NULL DEFAULT '', "Status" varchar(20) NOT NULL DEFAULT 'Claimed', "Detail" varchar(500) NULL, "SentAt" timestamp without time zone NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformBillingNotices_Key_Channel" ON "PlatformBillingNotices" ("MessageKey", "Channel");""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformBillingNotices_Buyer_CreatedAt" ON "PlatformBillingNotices" ("BuyerCompanyId", "CreatedAt");""",
        """CREATE TABLE IF NOT EXISTS "PlatformDunningStates" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "LastEvaluatedAt" timestamp without time zone NULL, "OverdueAmount" numeric(18,2) NOT NULL DEFAULT 0, "OldestOverdueDueDate" timestamp without time zone NULL, "SuspendedByDunningAt" timestamp without time zone NULL, "StatusBeforeSuspend" integer NULL, "WouldSuspendSince" timestamp without time zone NULL, "SuspendExemptUntil" timestamp without time zone NULL, "LastAction" varchar(300) NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformDunningStates_Company" ON "PlatformDunningStates" ("CompanyId");""",
    };
}
