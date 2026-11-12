namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L2 — schema ของการส่ง LINE ถึงผู้ติดต่อ (ห้าม EF Migrations · <c>IF NOT EXISTS</c> ทุกคำสั่ง · รันทุกบูตได้) ·
/// ชื่อ index ตรงกับ <c>AccountingDbContext</c> · คอลัมน์ครบทั้งของ entity และ <c>TenantEntity</c>/<c>BaseEntity</c>
/// <list type="bullet">
/// <item><c>EmailScheduleRules</c> + ช่องทางต่อกฎ: แถวเดิมได้ <c>ChannelEmail = true</c> · LINE = false (พฤติกรรมเดิม — ไม่มีแถวใดเริ่มส่ง LINE เอง) ·
/// <c>LineFallbackToEmail = true</c> (คำตอบเจ้าของ Q7 · มีผลเฉพาะกฎที่ติ๊ก LINE แต่ไม่ติ๊กอีเมล ⇒ แถวเดิมไม่มีผลใด)</item>
/// <item><c>LineQueues</c> — คิว LINE (unique กุญแจกันซ้ำต่อบริษัท)</item>
/// <item><c>ContactLineInvites</c> — คำเชิญผูก LINE (token/รหัสเก็บเป็น hash)</item>
/// </list>
/// ข้อมูลเดิม (F2 ข้อ 9): ไม่มีค่าที่ต้องซ่อม — ตารางใหม่ล้วน + คอลัมน์ใหม่ที่ค่าตั้งต้นคงพฤติกรรมเดิม
/// </summary>
public static class ContactLineDeliveryMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "ChannelEmail" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "ChannelLineUser" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "ChannelLineGroup" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "LineFallbackToEmail" boolean NOT NULL DEFAULT true;""",
        """
        CREATE TABLE IF NOT EXISTS "LineQueues" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "RuleId" uuid NULL,
            "EntityType" varchar(40) NOT NULL DEFAULT '',
            "EntityId" uuid NOT NULL,
            "ContactId" uuid NOT NULL,
            "EndpointId" uuid NULL,
            "EndpointKind" varchar(16) NOT NULL DEFAULT '',
            "Topic" varchar(16) NOT NULL DEFAULT '',
            "Trigger" varchar(40) NOT NULL DEFAULT '',
            "ScheduledFor" timestamp without time zone NOT NULL,
            "Status" varchar(16) NOT NULL DEFAULT 'Pending',
            "RetryCount" integer NOT NULL DEFAULT 0,
            "RetryKey" uuid NOT NULL,
            "OaScope" varchar(16) NULL,
            "LineRequestId" varchar(100) NULL,
            "SentAt" timestamp without time zone NULL,
            "ErrorMessage" varchar(2000) NULL,
            "RuleCode" varchar(64) NULL,
            "IdempotencyKey" varchar(300) NOT NULL DEFAULT '',
            "BaseKey" varchar(250) NOT NULL DEFAULT '',
            "FallbackToEmail" boolean NOT NULL DEFAULT false,
            "FallbackQueuedAt" timestamp without time zone NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_LineQueues_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LineQueues_Company_Idempotency" ON "LineQueues" ("CompanyId", "IdempotencyKey") WHERE NOT "IsDeleted";""",
        """CREATE INDEX IF NOT EXISTS "IX_LineQueues_Status_Scheduled" ON "LineQueues" ("Status", "ScheduledFor");""",
        """CREATE INDEX IF NOT EXISTS "IX_LineQueues_Company_BaseKey" ON "LineQueues" ("CompanyId", "BaseKey");""",
        """CREATE INDEX IF NOT EXISTS "IX_LineQueues_Company_Entity" ON "LineQueues" ("CompanyId", "EntityId");""",
        """
        CREATE TABLE IF NOT EXISTS "ContactLineInvites" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ContactId" uuid NOT NULL,
            "TokenHash" varchar(64) NOT NULL,
            "ExpiresAt" timestamp without time zone NOT NULL,
            "OaDestination" varchar(64) NULL,
            "TopicsJson" text NOT NULL DEFAULT '[]',
            "CreatedByUserId" uuid NULL,
            "ConsentRecordId" uuid NULL,
            "ConsentedAt" timestamp without time zone NULL,
            "CodeHash" varchar(64) NULL,
            "CodeExpiresAt" timestamp without time zone NULL,
            "UsedAt" timestamp without time zone NULL,
            "EndpointId" uuid NULL,
            "RevokedAt" timestamp without time zone NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_ContactLineInvites_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ContactLineInvites_TokenHash" ON "ContactLineInvites" ("TokenHash");""",
        """CREATE INDEX IF NOT EXISTS "IX_ContactLineInvites_Company_CodeHash" ON "ContactLineInvites" ("CompanyId", "CodeHash");""",
        // รอบ 262 (คำตัดสิน 262-1-2): ขอบเขต OA ของคำเชิญ — แถวเดิมทั้งหมดออกผ่าน OA ของบริษัท ⇒ ค่าตั้งต้น 'company' คงพฤติกรรมเดิม (ไม่มีข้อมูลต้องซ่อม)
        """ALTER TABLE "ContactLineInvites" ADD COLUMN IF NOT EXISTS "OaScope" varchar(16) NOT NULL DEFAULT 'company';""",
        // รอบ 254 WP-11 L3: รหัสผูกกลุ่ม (ระดับแพลตฟอร์ม — ไม่มี CompanyId) + กัน webhook ซ้ำ
        """
        CREATE TABLE IF NOT EXISTS "LineGroupBindCodes" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CodeHash" varchar(64) NOT NULL,
            "GroupIdProtected" text NOT NULL DEFAULT '',
            "GroupIdHash" varchar(64) NOT NULL DEFAULT '',
            "OaScope" varchar(16) NOT NULL DEFAULT '',
            "OaDestination" varchar(64) NULL,
            "CompanyIdHint" uuid NULL,
            "GroupName" varchar(200) NULL,
            "MemberCount" integer NULL,
            "ExpiresAt" timestamp without time zone NOT NULL,
            "UsedAt" timestamp without time zone NULL,
            "UsedByCompanyId" uuid NULL,
            "EndpointId" uuid NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LineGroupBindCodes_CodeHash" ON "LineGroupBindCodes" ("CodeHash");""",
        """CREATE TABLE IF NOT EXISTS "LineWebhookEventsSeen" ("WebhookEventId" varchar(100) NOT NULL PRIMARY KEY, "SeenAt" timestamp without time zone NOT NULL);""",
    };
}
