namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L4 — schema ของโควตาแยกช่อง LINE / อีเมล (ห้าม EF Migrations · ทุกคำสั่งรันซ้ำได้ทุกบูต) · ชื่อคีย์ตรงกับ <c>AccountingDbContext</c>
/// <list type="bullet">
/// <item><c>NotifyUsageCounters.Channel</c> — แถวเดิมได้ <c>all</c> (ก้อนรวม = พฤติกรรมเดิม) · PK เปลี่ยนเป็น (CompanyId, Period, Channel) ครั้งเดียว (ตรวจจำนวนคอลัมน์ของ PK ก่อน)</item>
/// <item>เพดานแยกช่องบน <c>PlanTemplates</c>/<c>Subscriptions</c> — <b>NULL ทั้งหมด</b> (ไม่แยก ⇒ นับรวมก้อนเดิม · migration ไม่จำกัดลูกค้าเดิม — คำตัดสินรอบ 212 · AUDIT_249 ข้อ 34) ·
///   อีเมล: ตั้งตัวเลขหลังเจ้าของรัน SQL NOTIFY §4 เท่านั้น (N9 · ไม่มีเพดานรวมระดับแพลตฟอร์ม)</item>
/// <item>โบนัสแยกช่องบน <c>Subscriptions</c> (0 / NULL)</item>
/// <item>แพ็ก <c>notify.topup.line</c> (1,000 ข้อความ ฿290) · <c>notify.topup.email</c> (5,000 ฉบับ ฿190) อายุ 60 วัน — N7 · ราคา/ขนาดแก้ได้ที่ /admin/addons.html โดยไม่ deploy ·
///   <c>notify.topup</c> ก้อนรวมเดิมยังขายอยู่จนแอดมินปิด (design §5.2 — ปิดเมื่อสองแพ็กใหม่เปิดใช้จริง)</item>
/// </list>
/// ข้อมูลเดิม (F2 ข้อ 9): ตัวนับเดิมทุกแถวเป็นก้อนรวม (ถูกต้อง — ก่อนรอบนี้ไม่มีการแยก) · ไม่มีค่าที่ต้องซ่อม
/// </summary>
public static class NotifyChannelQuotaMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "NotifyUsageCounters" ADD COLUMN IF NOT EXISTS "Channel" varchar(8) NOT NULL DEFAULT 'all';""",
        """
        DO $$
        DECLARE pk_name text; pk_cols int;
        BEGIN
            SELECT c.conname, array_length(c.conkey, 1) INTO pk_name, pk_cols
            FROM pg_constraint c WHERE c.conrelid = '"NotifyUsageCounters"'::regclass AND c.contype = 'p';
            IF pk_name IS NOT NULL AND pk_cols = 2 THEN
                EXECUTE format('ALTER TABLE "NotifyUsageCounters" DROP CONSTRAINT %I', pk_name);
                ALTER TABLE "NotifyUsageCounters" ADD CONSTRAINT "PK_NotifyUsageCounters" PRIMARY KEY ("CompanyId", "Period", "Channel");
            END IF;
        END $$;
        """,

        """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "MaxSharedLineMessagesPerMonth" integer NULL;""",
        """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxSharedLineMessagesPerMonth" integer NULL;""",
        """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "MaxSharedEmailsPerMonth" integer NULL;""",
        """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxSharedEmailsPerMonth" integer NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "MaxSharedLineMessagesPerMonth" integer NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "MaxSharedEmailsPerMonth" integer NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBonusLineQuota" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBonusLineExpiresAt" timestamp with time zone NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBonusEmailQuota" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBonusEmailExpiresAt" timestamp with time zone NULL;""",

        // ตัวนับต้นทุนของ OA กลาง (reply/push ต่องวด) — งาน NextAccOaQuotaWatch (N8)
        """
        CREATE TABLE IF NOT EXISTS "NextAccOaUsageCounters" (
            "Period" varchar(7) NOT NULL,
            "Kind" varchar(16) NOT NULL,
            "Count" bigint NOT NULL DEFAULT 0,
            "Limit" bigint NULL,
            "WarnedAt" timestamp with time zone NULL,
            "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            PRIMARY KEY ("Period", "Kind")
        );
        """,

        // แพ็กแยกช่อง — มิเตอร์ระบบ (Kind 3) · UnitLabel ประกอบจากขนาด (NotifyQuotaPolicy.PackUnitLabelFor) · ใบแจ้งหนี้ไหลเส้นเดิม UsageEvent → UsageInvoicingJob
        """
        INSERT INTO "ApiFeatures" ("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","Kind","ModuleCode","Icon","TrialDays","IsPublished","CreatedBy","PackSize","PackValidDays")
        SELECT gen_random_uuid(),'notify.topup.line','แพ็กข้อความ LINE','LINE message pack','แพ็ก 1,000 ข้อความ LINE','',951,
               'ข้อความ LINE ถึงลูกค้าผ่าน LINE OA ของ NextAcc เพิ่มจากเพดานของแพ็กเกจ (อายุ 60 วัน) — LINE OA ของร้านเองไม่นับโควตา',3,'Billing','💬',0,true,'seed',1000,60
        WHERE NOT EXISTS (SELECT 1 FROM "ApiFeatures" f WHERE f."FeatureCode" = 'notify.topup.line');
        """,
        """
        INSERT INTO "ApiPricingPlans" ("Id","FeatureCode","Method","UnitPrice","FreeQuotaPerMonth","EffectiveFrom","AdminNote","CreatedBy")
        SELECT gen_random_uuid(),'notify.topup.line',1,290.0,0,now(),'แพ็ก 1,000 ข้อความ LINE — ราคาตามคำตอบเจ้าของ N7 (รอบ 254)','seed'
        WHERE NOT EXISTS (SELECT 1 FROM "ApiPricingPlans" p WHERE p."FeatureCode" = 'notify.topup.line' AND p."IsDeleted" = false);
        """,
        """
        INSERT INTO "ApiFeatures" ("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","Kind","ModuleCode","Icon","TrialDays","IsPublished","CreatedBy","PackSize","PackValidDays")
        SELECT gen_random_uuid(),'notify.topup.email','แพ็กอีเมล','Email pack','แพ็ก 5,000 อีเมล','',952,
               'อีเมลถึงลูกค้าผ่าน SMTP ของ NextAcc เพิ่มจากเพดานของแพ็กเกจ (อายุ 60 วัน) — ผู้ส่งอีเมลของบริษัทเองไม่นับโควตา',3,'Billing','✉️',0,true,'seed',5000,60
        WHERE NOT EXISTS (SELECT 1 FROM "ApiFeatures" f WHERE f."FeatureCode" = 'notify.topup.email');
        """,
        """
        INSERT INTO "ApiPricingPlans" ("Id","FeatureCode","Method","UnitPrice","FreeQuotaPerMonth","EffectiveFrom","AdminNote","CreatedBy")
        SELECT gen_random_uuid(),'notify.topup.email',1,190.0,0,now(),'แพ็ก 5,000 อีเมล — ราคาตามคำตอบเจ้าของ N7 (รอบ 254)','seed'
        WHERE NOT EXISTS (SELECT 1 FROM "ApiPricingPlans" p WHERE p."FeatureCode" = 'notify.topup.email' AND p."IsDeleted" = false);
        """,
    };
}
