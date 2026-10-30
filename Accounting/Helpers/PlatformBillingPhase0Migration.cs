namespace Accounting.Helpers;

/// <summary>รอบ 219 เฟส 0 (PLATFORM_BILLING_AUDIT · คำตัดสินข้อ 178) — schema ของการอุดรั่วเงินค่าบริการ NextAcc <b>บล็อกเดียว · รันทุกบูตได้</b>
/// (ADD COLUMN IF NOT EXISTS ล้วน · ไม่แก้แถวเดิม — การย้อนสิทธิ์/เก็บเงินย้อนหลังเป็นคำตัดสินของเจ้าของ ดูรายงานแอดมิน
/// <c>api/admin/platform-billing/*</c>) · ค่าตั้งต้น PaymentModel ของ BillingAccounts ถูกเปลี่ยนในบล็อกเดิมของตารางนั้นด้วย
/// (ต้องมาก่อน backfill — ดู DatabaseMigrationHelper)</summary>
public static class PlatformBillingPhase0Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "PayerCompanyId" uuid NULL;""",
        """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentAwaitingSince" timestamp with time zone NULL;""",
        """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "UpfrontPeriod" varchar(7) NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ScheduledPlan" integer NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ScheduledBillingCycle" integer NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ScheduledPlanEffectiveAt" timestamp with time zone NULL;""",
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformBankTransferInstructions" text NULL;""",
        // G-6: ตาข่าย — คำสั่งเดียวกับในบล็อกตาราง BillingAccounts (ซ้ำได้)
        """ALTER TABLE "BillingAccounts" ALTER COLUMN "PaymentModel" SET DEFAULT 2;""",
    };
}
