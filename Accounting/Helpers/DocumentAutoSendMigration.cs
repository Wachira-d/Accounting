namespace Accounting.Helpers;

/// <summary>รอบ 241 — คอลัมน์/ตารางของ "ส่งเอกสารอัตโนมัติ": ลิงก์ชำระเงินในอีเมลตามตาราง · ช่องทางชำระต่อเอกสาร · งานหลังรับชำระ
/// (<c>ADD COLUMN IF NOT EXISTS</c> / <c>CREATE … IF NOT EXISTS</c> — idempotent · ตรงกับโมเดลใน <c>AccountingDbContext</c>) ·
/// ผู้เรียก: <c>DatabaseMigrationHelper</c> (รายการ Statements ตอนบูต) · ค่าเริ่มต้นทุกคอลัมน์ = ปิด/NULL = พฤติกรรมเดิม (ไม่มีค่าเก่าต้องซ่อม)</summary>
public static class DocumentAutoSendMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // ค่าเริ่มต้นบริษัท
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "PayLinkDefaultEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "PayLinkDefaultChannelsJson" varchar(500) NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "PostPayEmailReceipt" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "PostPayIncludePartial" boolean NOT NULL DEFAULT false;""",
        // ต่อตารางส่งเอกสาร (NULL = ตามค่าบริษัท)
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "AttachPayLink" boolean NULL;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "PayLinkChannelsJson" varchar(500) NULL;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "PostPayEmailReceipt" boolean NULL;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "PostPayIncludePartial" boolean NULL;""",
        // ต่อเอกสาร: ลิงก์ชำระเงิน (หนึ่งลิงก์ต่อใบ) + ตารางที่ส่งใบนี้
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkTokenHash" varchar(64) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkTokenProtected" text NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkIssuedAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkExpiresAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkRevokedAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PayLinkChannelsJson" varchar(500) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "AutoSendRuleId" uuid NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Documents_PayLinkTokenHash" ON "Documents" ("PayLinkTokenHash") WHERE "PayLinkTokenHash" IS NOT NULL;""",
        // งานหลังรับชำระ
        """
        CREATE TABLE IF NOT EXISTS "PostPaymentAutomationRuns" (
            "Id" uuid PRIMARY KEY,
            "CompanyId" uuid NOT NULL REFERENCES "Companies"("Id"),
            "PaymentId" uuid NOT NULL,
            "DocumentId" uuid NOT NULL,
            "Source" varchar(30) NOT NULL DEFAULT '',
            "Status" varchar(20) NOT NULL DEFAULT 'Pending',
            "FullyPaid" boolean NOT NULL DEFAULT false,
            "Attempts" integer NOT NULL DEFAULT 0,
            "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
            "ReceiptDocumentId" uuid NULL,
            "StepsDone" varchar(200) NULL,
            "Message" varchar(2000) NULL,
            "CompletedAt" timestamptz NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "UpdatedAt" timestamptz NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """ALTER TABLE "PostPaymentAutomationRuns" ADD COLUMN IF NOT EXISTS "FullyPaid" boolean NOT NULL DEFAULT false;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PostPaymentAutomationRuns_Company_Payment" ON "PostPaymentAutomationRuns" ("CompanyId", "PaymentId");""",
        """CREATE INDEX IF NOT EXISTS "IX_PostPaymentAutomationRuns_Status_Next" ON "PostPaymentAutomationRuns" ("Status", "NextAttemptAt");""",
        """CREATE INDEX IF NOT EXISTS "IX_PostPaymentAutomationRuns_Company_Doc" ON "PostPaymentAutomationRuns" ("CompanyId", "DocumentId");""",
    };
}
