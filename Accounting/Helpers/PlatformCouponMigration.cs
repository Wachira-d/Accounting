namespace Accounting.Helpers;

/// <summary>รอบ 230 (คำตัดสินข้อ 230-Q1/230-Q2) — คูปองแพลตฟอร์ม + การใช้คูปอง (ตารางใหม่) · ช่องส่วนต่างอัปเกรด/ยอดที่เซิร์ฟเวอร์เสนอ/คูปอง/แอดมินแก้ยอด
/// บน <c>SubscriptionPayments</c> · ช่องคูปองบน <c>CompanyQuotaAddOns</c> · CREATE/ADD … IF NOT EXISTS ⇒ รันทุกบูตได้ · ไม่ backfill
/// (แถวเดิม: ไม่มีคูปอง · <c>QuotedAmount</c> null = กติกาเดิม "ต้องเท่าราคาเต็ม") · ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class PlatformCouponMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "PlatformCoupons" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "Code" varchar(40) NOT NULL,
            "Description" varchar(300) NULL,
            "Kind" integer NOT NULL DEFAULT 1,
            "Value" numeric(18,2) NOT NULL DEFAULT 0,
            "ValidFromUtc" timestamp with time zone NULL,
            "ValidToUtc" timestamp with time zone NULL,
            "MaxUsesTotal" integer NULL,
            "MaxUsesPerCompany" integer NULL,
            "UsedCount" integer NOT NULL DEFAULT 0,
            "AppliesTo" integer NOT NULL DEFAULT 3,
            "PlansCsv" varchar(200) NULL,
            "CyclesCsv" varchar(200) NULL,
            "AddOnResourcesCsv" varchar(400) NULL,
            "FirstPaymentOnly" boolean NOT NULL DEFAULT false,
            "IsActive" boolean NOT NULL DEFAULT true,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformCoupons_Code" ON "PlatformCoupons" ("Code") WHERE "IsDeleted" = false;""",
        """
        CREATE TABLE IF NOT EXISTS "PlatformCouponRedemptions" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CouponId" uuid NOT NULL,
            "CompanyId" uuid NOT NULL,
            "SourceKind" varchar(40) NOT NULL,
            "SourceId" uuid NOT NULL,
            "ListAmount" numeric(18,2) NOT NULL DEFAULT 0,
            "DiscountAmount" numeric(18,2) NOT NULL DEFAULT 0,
            "AmountAfter" numeric(18,2) NOT NULL DEFAULT 0,
            "Status" integer NOT NULL DEFAULT 1,
            "ReservedUntilUtc" timestamp with time zone NOT NULL DEFAULT now(),
            "RedeemedAt" timestamp with time zone NULL,
            "ReleasedAt" timestamp with time zone NULL,
            "ReleaseReason" varchar(300) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformCouponRedemptions_Source" ON "PlatformCouponRedemptions" ("CouponId", "SourceKind", "SourceId") WHERE "IsDeleted" = false;""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformCouponRedemptions_Coupon_Company" ON "PlatformCouponRedemptions" ("CouponId", "CompanyId", "Status");""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "IsProration" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ProrationFromPlan" varchar(32) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ProrationDays" integer NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ProrationCycleDays" integer NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ProrationCycleEndUtc" timestamp with time zone NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "QuotedAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ListAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "CouponId" uuid NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "CouponCode" varchar(40) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "CouponDiscountAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "AmountOverrideReason" varchar(500) NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "CouponId" uuid NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "CouponCode" varchar(40) NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "CouponDiscountAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "ListAmount" numeric(18,2) NULL;""",
    };
}
