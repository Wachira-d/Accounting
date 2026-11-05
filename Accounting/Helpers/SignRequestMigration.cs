namespace Accounting.Helpers;

/// <summary>รอบ 240 — คอลัมน์/ตารางของลิงก์ให้ลูกค้าเซ็น · อีเมลขอเซ็น · webhook ต่อเอกสาร · outbox เหตุการณ์ลูกค้าเซ็น
/// (<c>ADD COLUMN IF NOT EXISTS</c> / <c>CREATE … IF NOT EXISTS</c> — idempotent · ตรงกับโมเดลใน <c>AccountingDbContext</c>) ·
/// ผู้เรียก: <c>DatabaseMigrationHelper</c> (รายการ Statements ตอนบูต)</summary>
public static class SignRequestMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // เอกสาร: webhook ต่อเอกสาร + สถานะติดตามลิงก์ (NULL = ใบเดิม/ยังไม่ส่ง — พฤติกรรมเดิม)
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignCallbackUrl" varchar(500) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignCallbackRef" varchar(200) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignLinkIssuedAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignLinkEmailedAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignLinkEmailedTo" varchar(500) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignLinkOpenedAt" timestamptz NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignReminderSentAt" timestamptz NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_Documents_DeliverySignToken" ON "Documents" ("DeliverySignToken") WHERE "DeliverySignToken" IS NOT NULL;""",
        // ผู้ติดต่อ: ผู้รับล่าสุด (เติมช่องผู้รับให้อัตโนมัติ)
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "SignLinkLastRecipients" varchar(500) NULL;""",
        // ค่าตั้งบริษัท (DEFAULT = ปิดทุกอย่าง — พฤติกรรมเดิม)
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SignLinkAutoEmailOnApprove" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SignLinkAutoEmailSince" timestamptz NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SignLinkReminderDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SignCallbackSecret" text NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SignCallbackAllowedHosts" varchar(1000) NULL;""",
        // บันทึกการส่งอีเมล: วัตถุประสงค์ + ผู้ส่ง (ฐานนับเพดานรายวันของอีเมลระบบ)
        """ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "Purpose" varchar(40) NULL;""",
        """ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "SenderMode" varchar(20) NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_DocumentEmailLogs_Company_Sender_Created" ON "DocumentEmailLogs" ("CompanyId", "SenderMode", "CreatedAt");""",
        // outbox
        """
        CREATE TABLE IF NOT EXISTS "SignEventOutbox" (
            "Id" uuid PRIMARY KEY,
            "CompanyId" uuid NOT NULL REFERENCES "Companies"("Id"),
            "EventId" uuid NOT NULL,
            "EventKey" varchar(200) NOT NULL,
            "EventType" varchar(60) NOT NULL,
            "DocumentId" uuid NOT NULL,
            "PayloadJson" text NOT NULL DEFAULT '{}',
            "CallbackUrl" varchar(500) NULL,
            "CallbackRef" varchar(200) NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "Attempts" integer NOT NULL DEFAULT 0,
            "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
            "LastAttemptAt" timestamptz NULL,
            "LastHttpStatus" integer NULL,
            "LastError" varchar(1000) NULL,
            "DeliveredAt" timestamptz NULL,
            "RegistrationsDispatchedAt" timestamptz NULL,
            "SellerNotifiedAt" timestamptz NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "UpdatedAt" timestamptz NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SignEventOutbox_Company_Key" ON "SignEventOutbox" ("CompanyId", "EventKey");""",
        """CREATE INDEX IF NOT EXISTS "IX_SignEventOutbox_Status_Next" ON "SignEventOutbox" ("Status", "NextAttemptAt");""",
        """CREATE INDEX IF NOT EXISTS "IX_SignEventOutbox_Company_Doc" ON "SignEventOutbox" ("CompanyId", "DocumentId");""",
    };
}
