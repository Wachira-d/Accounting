namespace Accounting.Helpers;

/// <summary>
/// รอบ 260 — สลิปจากแชต LINE OA ของบริษัท: ตาราง <c>LineChatSlips</c> + สวิตช์ <c>CompanySettings.LineChatSlipIntakeEnabled</c> (ค่าตั้งต้นเปิด — คำตัดสิน 260-1-4) ·
/// idempotent (<c>IF NOT EXISTS</c>) · ชื่อ index ตรง <c>AccountingDbContext</c> · ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated)
/// </summary>
public static class Round260LineSlipMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineChatSlipIntakeEnabled" boolean NOT NULL DEFAULT true;""",
        """
        CREATE TABLE IF NOT EXISTS "LineChatSlips" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "SourceKind" varchar(16) NOT NULL DEFAULT '',
            "SourceKey" varchar(64) NOT NULL DEFAULT '',
            "ContactId" uuid NULL,
            "LineMessageId" varchar(64) NULL,
            "FileSha256" varchar(64) NOT NULL DEFAULT '',
            "StoragePath" varchar(1024) NOT NULL DEFAULT '',
            "ContentType" varchar(64) NOT NULL DEFAULT '',
            "OcrAmount" numeric(18,2) NULL,
            "OcrAmounts" varchar(500) NULL,
            "OcrDate" timestamp NULL,
            "OcrRef" varchar(64) NULL,
            "Status" varchar(24) NOT NULL DEFAULT '',
            "Reason" varchar(32) NULL,
            "ReasonDetail" varchar(500) NULL,
            "CandidatesJson" text NULL,
            "ChoiceExpiresAt" timestamp NULL,
            "ShowAmounts" boolean NOT NULL DEFAULT false,
            "DocumentId" uuid NULL,
            "PaymentIntentId" uuid NULL,
            "ResolvedAt" timestamp NULL,
            "ResolvedBy" varchar(256) NULL,
            "CreatedAt" timestamp NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_LineChatSlips_Company_Status" ON "LineChatSlips" ("CompanyId", "Status");""",
        """CREATE INDEX IF NOT EXISTS "IX_LineChatSlips_Company_Sha" ON "LineChatSlips" ("CompanyId", "FileSha256");""",
        // ฝ่ายค้าน 260-1 M4: วันที่ลบสำเนารูป (PDPA retention)
        """ALTER TABLE "LineChatSlips" ADD COLUMN IF NOT EXISTS "FileDeletedAt" timestamp NULL;""",
        // ฝ่ายค้าน 260-1 L1: ข้อความ LINE เดียว = แถวเดียว (LINE ส่ง webhook ซ้ำระหว่างรอบแรกยังอ่านรูป) — ล้างค่าซ้ำก่อนสร้าง unique (แถวเก่าสุดคงไว้)
        """
        UPDATE "LineChatSlips" s SET "LineMessageId" = NULL
        WHERE "LineMessageId" IS NOT NULL AND EXISTS (SELECT 1 FROM "LineChatSlips" o WHERE o."CompanyId" = s."CompanyId"
            AND o."LineMessageId" = s."LineMessageId" AND (o."CreatedAt" < s."CreatedAt" OR (o."CreatedAt" = s."CreatedAt" AND o."Id" < s."Id")));
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LineChatSlips_Company_Message" ON "LineChatSlips" ("CompanyId", "LineMessageId") WHERE "LineMessageId" IS NOT NULL;""",
    };
}
