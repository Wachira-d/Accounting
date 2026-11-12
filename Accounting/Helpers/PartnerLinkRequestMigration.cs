namespace Accounting.Helpers;

/// <summary>รอบ 264 — คำขอเชื่อมคู่ค้าถึงปลายทางเสมอ + แจ้งผ่านเครื่องแจ้งเตือน (outbox บนแถวคู่) + "คำขอที่เราส่ง" ของผู้ขอ ·
/// idempotent (<c>IF NOT EXISTS</c> ทุกบรรทัด · ไม่มี UPDATE) · ชื่อ index ตรง <c>AccountingDbContext</c> · ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated) ·
/// แถวคู่เดิม: คอลัมน์ใหม่ = NULL ⇒ ไม่มีการแจ้งย้อนหลัง · วันหมดอายุของแถวเดิมนับจาก CreatedAt (<c>PartnerLinkRequestRules.RequestedAt</c>)</summary>
public static class PartnerLinkRequestMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "InvitedAt" timestamp NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "InviteNoticeDueAt" timestamp NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "InviteNotifiedAt" timestamp NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "InviteNoticeResult" varchar(32) NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "LinkRequestId" uuid NULL;""",
        // ฝ่ายค้าน L8 (คำตัดสิน 264-1-13): ปฏิเสธและไม่รับคำขอจากบริษัทนี้อีก
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "RequestsBlockedByCompanyId" uuid NULL;""",
        """ALTER TABLE "TradingPartnerships" ADD COLUMN IF NOT EXISTS "RequestsBlockedAt" timestamp NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_TradingPartnerships_InviteNoticeDue" ON "TradingPartnerships" ("InviteNoticeDueAt") WHERE "InviteNoticeDueAt" IS NOT NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "PartnerLinkRequests" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "PartnerTaxId" varchar(13) NOT NULL DEFAULT '',
            "PartnerBranchCode" varchar(5) NULL,
            "Message" varchar(500) NULL,
            "RequestedByUserId" uuid NOT NULL,
            "ExpiresAt" timestamp without time zone NOT NULL,
            "AcceptedAt" timestamp without time zone NULL,
            "AcceptedPartnershipId" uuid NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_PartnerLinkRequests_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_PartnerLinkRequests_Company_Created" ON "PartnerLinkRequests" ("CompanyId", "CreatedAt");""",
    };
}
