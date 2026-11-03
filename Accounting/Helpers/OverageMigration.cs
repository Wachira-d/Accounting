namespace Accounting.Helpers;

/// <summary>รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.2/§D.1 · คำตัดสินข้อ 231-1..7) — ส่วนเกินโควตา + ประวัติการใช้งาน ·
/// <b>ตารางใหม่ล้วน + คอลัมน์ใหม่บน <c>PlatformBillingSettings</c></b> (CREATE/ALTER … IF NOT EXISTS ⇒ รันทุกบูตได้) ·
/// <b>ไม่ backfill นโยบาย</b> โดยตั้งใจ: ไม่มีแถว <c>CompanyOveragePolicies</c> = HardLock = พฤติกรรมเดิมทุกประการ ⇒ deploy แล้วไม่มีใครโดนบิลที่ไม่ได้ตกลง ·
/// ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c> (ฐานใหม่ได้จาก EnsureCreated — เทสต์ Db)</summary>
public static class OverageMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "OverageRates" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "ResourceKey" varchar(64) NOT NULL,
            "PlanTemplateId" uuid NULL,
            "UnitSize" integer NOT NULL DEFAULT 1,
            "UnitPrice" numeric(18,2) NOT NULL DEFAULT 0,
            "EffectiveFromUtc" timestamp with time zone NOT NULL DEFAULT now(),
            "EffectiveToUtc" timestamp with time zone NULL,
            "IsActive" boolean NOT NULL DEFAULT true,
            "SetBy" varchar(200) NULL,
            "Reason" varchar(500) NOT NULL DEFAULT '',
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_OverageRates_Resource_Plan" ON "OverageRates" ("ResourceKey", "PlanTemplateId", "EffectiveFromUtc");""",
        """
        CREATE TABLE IF NOT EXISTS "CompanyOveragePolicies" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "Mode" integer NOT NULL DEFAULT 1,
            "MonthlyCapAmount" numeric(18,2) NULL,
            "ResourcesCsv" varchar(300) NULL,
            "AcceptedPolicyVersion" varchar(64) NULL,
            "AcceptedBy" varchar(200) NULL,
            "AcceptedAtUtc" timestamp with time zone NULL,
            "LockedByUnpaidAt" timestamp with time zone NULL,
            "LockedReason" varchar(500) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyOveragePolicies_Company" ON "CompanyOveragePolicies" ("CompanyId") WHERE "IsDeleted" = false;""",
        """ALTER TABLE "CompanyOveragePolicies" ADD COLUMN IF NOT EXISTS "CapNotifiedPeriod" varchar(7) NULL;""",
        """ALTER TABLE "CompanyOveragePolicies" ADD COLUMN IF NOT EXISTS "StartedNotifiedPeriod" varchar(7) NULL;""",
        """ALTER TABLE "CompanyOveragePolicies" ADD COLUMN IF NOT EXISTS "FreeOverageNotifiedPeriod" varchar(7) NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "UsageMonthlySnapshots" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "Period" varchar(7) NOT NULL,
            "ResourceKey" varchar(64) NOT NULL,
            "Used" bigint NOT NULL DEFAULT 0,
            "Limit" bigint NULL,
            "Purchased" integer NOT NULL DEFAULT 0,
            "OverageUnits" integer NOT NULL DEFAULT 0,
            "OverageAmount" numeric(18,2) NOT NULL DEFAULT 0,
            "Source" integer NOT NULL DEFAULT 2,
            "SnapshotAtUtc" timestamp with time zone NOT NULL DEFAULT now(),
            "WarnedPercent" integer NOT NULL DEFAULT 0,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageMonthlySnapshots_Company_Period_Resource" ON "UsageMonthlySnapshots" ("CompanyId", "Period", "ResourceKey");""",
        """
        CREATE TABLE IF NOT EXISTS "UsageDailySnapshots" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "Date" timestamp with time zone NOT NULL,
            "ResourceKey" varchar(64) NOT NULL,
            "Used" bigint NOT NULL DEFAULT 0,
            "Limit" bigint NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageDailySnapshots_Company_Date_Resource" ON "UsageDailySnapshots" ("CompanyId", "Date", "ResourceKey");""",
        """ALTER TABLE "UsageDailySnapshots" ADD COLUMN IF NOT EXISTS "UnitPrice" numeric(18,2) NULL;""",
        // ค่าตั้งส่วนเกินบนแถวเดียวของ PlatformBillingSettings — ค่าตั้งต้นตาม PlatformBillingSettingsRules (แถวเดิมได้ค่าตั้งต้นเดียวกัน)
        """ALTER TABLE "PlatformBillingSettings" ADD COLUMN IF NOT EXISTS "OverageGraceDays" integer NOT NULL DEFAULT 7;""",
        """ALTER TABLE "PlatformBillingSettings" ADD COLUMN IF NOT EXISTS "OverageUnpaidAction" integer NOT NULL DEFAULT 2;""",
        """ALTER TABLE "PlatformBillingSettings" ADD COLUMN IF NOT EXISTS "OverageMinInvoiceAmount" numeric(18,2) NOT NULL DEFAULT 50;""",
        """ALTER TABLE "PlatformBillingSettings" ADD COLUMN IF NOT EXISTS "QuotaWarnPercents" varchar(60) NOT NULL DEFAULT '80,90,100';""",
        // ฝ่ายค้านรอบ 231 S3: ตั้งต้น "ไม่ตัดบัตรอัตโนมัติ" — การตัดบัตรต้องเป็นสิ่งที่แอดมินเปิดและผู้เช่าเห็นในเงื่อนไข (TermsNote) ก่อน
        """ALTER TABLE "PlatformBillingSettings" ADD COLUMN IF NOT EXISTS "OverageAutoChargeSavedCard" boolean NOT NULL DEFAULT false;""",
    };
}
