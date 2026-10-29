namespace Accounting.Helpers;

/// <summary>รอบ 209 · P5 — schema ของสมาชิกไม่จำกัด/พัก/ขยาย/โอน (คำตัดสินข้อ 168 ฎ) + ค่าตอบแทนผู้ให้บริการ (ข้อ 168 ฏ) — <b>รันทุกบูตได้</b>
/// (<c>ADD COLUMN IF NOT EXISTS</c>) · ค่า DEFAULT = พฤติกรรมเดิมทุกตัว: แพ็กเกจ/บัตรเดิม <c>Kind</c> = 0 (นับครั้ง) · ไม่แจ้งเตือนหมดอายุ (0) ·
/// ไม่อนุญาตพักบัตร (0) · ผู้ให้บริการไม่คิดค่าตอบแทน (<c>PayMode</c> 0) · ไม่ UPDATE ข้อมูลเดิม · ชื่อตาราง/index ต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class CourseCreditMembershipMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CreditPackages" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPackages" ADD COLUMN IF NOT EXISTS "MaxBookingsPerDay" integer NOT NULL DEFAULT 1;""",
        """ALTER TABLE "CreditPackages" ADD COLUMN IF NOT EXISTS "MaxActiveBookings" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPackages" ADD COLUMN IF NOT EXISTS "MaxFreezeDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPackages" ADD COLUMN IF NOT EXISTS "ExpiryReminderDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "MaxBookingsPerDay" integer NOT NULL DEFAULT 1;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "MaxActiveBookings" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "MaxFreezeDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "PeriodDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "PeriodStartsOn" timestamp without time zone NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "PeriodBaseRecognized" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "PeriodBaseActiveDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "RecognizedThroughDate" timestamp without time zone NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "FrozenDays" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "TransferredFromPassId" uuid NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "TransferredToPassId" uuid NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "TransferredOutAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "ExpiryNotifiedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "ExpiryNotifyAttemptedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "CreditPasses" ADD COLUMN IF NOT EXISTS "ExpiryNotifyError" text NULL;""",
        // งานรับรู้รายวันค้นบัตรสมาชิกไม่จำกัดที่ยังใช้งาน
        """CREATE INDEX IF NOT EXISTS "IX_CreditPasses_Company_Kind_Status" ON "CreditPasses" ("CompanyId", "Kind", "Status");""",
        """CREATE TABLE IF NOT EXISTS "CreditPassFreezes" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PassId" uuid NOT NULL REFERENCES "CreditPasses"("Id") ON DELETE CASCADE, "FromDate" timestamp without time zone NOT NULL, "ToDate" timestamp without time zone NOT NULL, "Days" integer NOT NULL DEFAULT 0, "Reason" text NOT NULL DEFAULT '', "CancelledAt" timestamp without time zone NULL, "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), "UpdatedAt" timestamp without time zone NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false, CONSTRAINT "FK_CreditPassFreezes_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE);""",
        """CREATE INDEX IF NOT EXISTS "IX_CreditPassFreezes_Company_Pass" ON "CreditPassFreezes" ("CompanyId", "PassId");""",
        // ค่าตอบแทนผู้ให้บริการ (ข้อ 168 ฏ · R-209-C6/D8)
        """ALTER TABLE "SiteBookingResources" ADD COLUMN IF NOT EXISTS "EmployeeId" uuid NULL;""",
        """ALTER TABLE "SiteBookingResources" ADD COLUMN IF NOT EXISTS "PayMode" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingResources" ADD COLUMN IF NOT EXISTS "PayRate" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingResources" ADD COLUMN IF NOT EXISTS "PayMinimumPerClass" numeric(18,2) NOT NULL DEFAULT 0;""",
    };
}
