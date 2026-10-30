namespace Accounting.Helpers;

/// <summary>รอบ 212 C — ตารางสรุปประจำวันทาง LINE ถึงพนักงาน (<c>StaffDailySummaryConfigs</c> · <c>StaffDailySummaryDeliveries</c>) ·
/// ตารางใหม่ล้วน ⇒ แถวเดิมไม่ถูกแตะ · รันทุกบูตได้ (IF NOT EXISTS ทุกคำสั่ง) · ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c> ·
/// unique (ชุด, วัน, ผู้รับ) = ด่านกันส่งซ้ำข้ามเครื่อง (claim ด้วย <c>INSERT … ON CONFLICT</c> ใน <c>StaffDailySummaryService</c>)</summary>
public static class StaffDailySummaryMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """
        CREATE TABLE IF NOT EXISTS "StaffDailySummaryConfigs" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "SiteId" uuid NULL,
            "Enabled" boolean NOT NULL DEFAULT true,
            "SendAtLocal" time without time zone NOT NULL DEFAULT '07:30:00',
            "Sections" varchar(200) NOT NULL DEFAULT '',
            "RecipientUserIds" text NULL,
            "RecipientRoles" varchar(200) NULL,
            "LastSentLocalDate" date NULL,
            "LastRunNote" varchar(1000) NULL,
            "LastRunAt" timestamp with time zone NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_StaffDailySummaryConfigs_Company" ON "StaffDailySummaryConfigs" ("CompanyId");""",
        """
        CREATE TABLE IF NOT EXISTS "StaffDailySummaryDeliveries" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ConfigId" uuid NOT NULL,
            "LocalDate" date NOT NULL,
            "UserId" uuid NOT NULL,
            "Status" varchar(32) NOT NULL,
            "Attempts" integer NOT NULL DEFAULT 0,
            "ClaimedAt" timestamp with time zone NULL,
            "SentAt" timestamp with time zone NULL,
            "Messages" integer NOT NULL DEFAULT 0,
            "Units" integer NOT NULL DEFAULT 0,
            "Error" varchar(1000) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_StaffDailySummaryDeliveries_Config_Date_User" ON "StaffDailySummaryDeliveries" ("ConfigId", "LocalDate", "UserId");""",
        """CREATE INDEX IF NOT EXISTS "IX_StaffDailySummaryDeliveries_Company_Date" ON "StaffDailySummaryDeliveries" ("CompanyId", "LocalDate");""",
    };
}
