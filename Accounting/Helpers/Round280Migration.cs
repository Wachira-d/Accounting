namespace Accounting.Helpers;

/// <summary>
/// รอบ 280 — ช่องทางแจ้งคำขอเชื่อมคู่ค้า (<see cref="PartnerLinkRequestChannels"/>) · idempotent (<c>IF NOT EXISTS</c> ทุกบรรทัด · ไม่มี UPDATE) ·
/// ชื่อ index ตรง <c>AccountingDbContext</c> · คำขอเดิม: <c>ContactId</c> = NULL (เลือกผู้ติดต่อตอนกด "ส่งแจ้งอีกครั้ง") ·
/// ปลายทางของผู้ติดต่อเดิม<b>ไม่</b>ถูกเพิ่มเรื่อง <c>PartnerLink</c> (คำตัดสิน 280-2 — ค่าที่เก็บของผู้รับไม่ถูกแก้เงียบ ๆ)
/// </summary>
public static class Round280Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "PartnerLinkRequests" ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL;""",
        // ผู้ขอที่ยังไม่ใช้ NextAcc สมัครทีหลัง ⇒ ค้นคำขอที่ค้างด้วยเลขภาษี (MaterializeIncomingLinkRequestsAsync)
        """CREATE INDEX IF NOT EXISTS "IX_PartnerLinkRequests_PartnerTax_Open" ON "PartnerLinkRequests" ("PartnerTaxId") WHERE "AcceptedAt" IS NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "PartnerLinkRequestNotices" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "LinkRequestId" uuid NOT NULL,
            "Channel" varchar(16) NOT NULL DEFAULT '',
            "Status" varchar(16) NOT NULL DEFAULT '',
            "ContactId" uuid NULL,
            "EndpointId" uuid NULL,
            "RecipientLabel" varchar(200) NULL,
            "Reason" varchar(1000) NULL,
            "RuleCode" varchar(64) NULL,
            "ByUserId" uuid NULL,
            "AttemptedAt" timestamp without time zone NOT NULL,
            "CompletedAt" timestamp without time zone NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PartnerLinkRequestNotices_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_PartnerLinkRequestNotices_Company_Request" ON "PartnerLinkRequestNotices" ("CompanyId", "LinkRequestId", "Channel");""",
    };
}
