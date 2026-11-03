namespace Accounting.Helpers;

/// <summary>รอบ 231 (คำตัดสินข้อ 231-8..231-11 · `erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md` §C/§B.8/E7/P0) — ตารางราคาพิเศษรายบริษัท ·
/// ช่องราคาพิเศษ/เปลี่ยนรอบบิลบน <c>SubscriptionPayments</c> · <c>Subscriptions.Kind</c> + backfill · ย้ายเพดานที่แอดมินเคยตั้งบน <c>Subscription.Max*</c> ไป
/// <c>CompanyQuotaOverrides</c> (กลไกเดียว) · CREATE/ADD … IF NOT EXISTS + UPDATE/INSERT แบบมีเงื่อนไข ⇒ รันทุกบูตได้ (idempotent) · ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c>
///
/// <para><b>backfill Kind</b> — สูตรเดียวกับ <see cref="SubscriptionKindRule.Derive"/> (ฝ่ายค้าน B1 ข): ฟรีถาวร ⇒ Free · สถานะ Trial ⇒ Trial ·
/// ไม่เคยจ่าย (LastPaymentDate null + ไม่มีรายการ Approved) + (Plan FreeTrial หรือ TrialConfig หมดอายุ) ⇒ Trial · อื่น ๆ (รวมแพ็กเกจจ่ายเงินที่หมดอายุ) ⇒ Paid ·
/// ทำเฉพาะแถว <c>Kind = 0</c> (แถวที่โค้ดเขียนแล้วไม่ถูกทับ)</para>
/// <para><b>ย้าย override เพดาน</b> (คำตัดสินข้อ 231-11 · F2 ข้อ 9 "persist แล้วต้องมี migration"): เฉพาะ users/storage.gb ที่ <c>QuotaResources</c> รู้จัก ·
/// เฉพาะ sub <b>Active</b> ที่ค่าต่างจากแม่แบบ <b>และ</b> มีประวัติ <c>AdminLimitsChanged</c> (หลักฐานว่าแอดมินตั้ง — ค่าที่ต่างเพราะทดลองใช้/แม่แบบเปลี่ยน ไม่ย้าย ·
/// ไม่งั้น override จะชนะแม่แบบหลังแปลงเป็นจ่ายเงิน) · มีแถว override อยู่แล้ว = ไม่แตะ (แอดมินตั้งผ่านกลไกใหม่แล้ว) · ≥ 999 ผู้ใช้ / พื้นที่ ≥ UnlimitedBytes ⇒ -1 ·
/// เอกสาร/รายการบัญชี/บริษัท ไม่มี key ใน <c>QuotaResources</c> ⇒ ยังอยู่บน <c>Subscription.Max*</c> (ทางเข้าเดียว <c>AdminChangeLimits</c> พร้อม audit)</para></summary>
public static class PlatformPriceOverrideMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "CompanyPlanPriceOverrides" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NULL,
            "AccountSubscriptionId" uuid NULL,
            "PlanTemplateId" uuid NOT NULL,
            "Mode" integer NOT NULL DEFAULT 3,
            "ReferencePlanTemplateId" uuid NULL,
            "FixedMonthly" numeric(18,2) NULL,
            "FixedQuarterly" numeric(18,2) NULL,
            "FixedSemiAnnual" numeric(18,2) NULL,
            "FixedAnnual" numeric(18,2) NULL,
            "PercentOff" numeric(9,4) NULL,
            "EffectiveFromUtc" timestamp with time zone NOT NULL DEFAULT now(),
            "EffectiveToUtc" timestamp with time zone NULL,
            "AppliesToUpgrades" boolean NOT NULL DEFAULT true,
            "AppliesToRenewals" boolean NOT NULL DEFAULT true,
            "Reason" varchar(500) NOT NULL DEFAULT '',
            "ApprovedBy" varchar(200) NULL,
            "IsActive" boolean NOT NULL DEFAULT true,
            "EndedAtUtc" timestamp with time zone NULL,
            "EndedBy" varchar(200) NULL,
            "EndReason" varchar(500) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_CompanyPlanPriceOverrides_Company_Plan" ON "CompanyPlanPriceOverrides" ("CompanyId", "PlanTemplateId") WHERE "IsDeleted" = false;""",
        """CREATE INDEX IF NOT EXISTS "IX_CompanyPlanPriceOverrides_Account_Plan" ON "CompanyPlanPriceOverrides" ("AccountSubscriptionId", "PlanTemplateId") WHERE "IsDeleted" = false;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PriceOverrideId" uuid NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "StandardAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "IsCycleChange" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "CycleChangeCredit" numeric(18,2) NULL;""",
        """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0;""",
        // backfill Kind — สูตร = SubscriptionKindRule.Derive (Trial=1 · Free=2 · Paid=3 · ฝ่ายค้าน B1 ข): "ไม่เคยจ่าย" = LastPaymentDate IS NULL AND ไม่มี SubscriptionPayments Approved (Status=2) ·
        // ทดลอง = ไม่เคยจ่าย + (Plan = FreeTrial (0) หรือ TrialConfig.TrialStatus = Expired (3)) · แพ็กเกจจ่ายเงินที่หมดอายุโดยไม่มีประวัติทดลอง = Paid
        """
        UPDATE "Subscriptions" s SET "Kind" = CASE
            WHEN s."IsPermanentFree" THEN 2
            WHEN s."Status" = 0 THEN 1
            WHEN s."LastPaymentDate" IS NULL
                 AND NOT EXISTS (SELECT 1 FROM "SubscriptionPayments" p WHERE p."SubscriptionId" = s."Id" AND p."Status" = 2)
                 AND (s."Plan" = 0 OR EXISTS (SELECT 1 FROM "TrialConfigs" tc WHERE tc."SubscriptionId" = s."Id" AND tc."TrialStatus" = 3)) THEN 1
            ELSE 3 END
        WHERE s."Kind" = 0;
        """,
        // ฝ่ายค้าน B2: แจ้งเจ้าของล่วงหน้าก่อนปิดข้อมูลทดลองใช้ (ค่าตั้งต้น 14 วัน) + เวลาที่แจ้งแล้ว
        """ALTER TABLE "TrialConfigs" ADD COLUMN IF NOT EXISTS "PurgeNoticeDays" integer NOT NULL DEFAULT 14;""",
        """ALTER TABLE "TrialConfigs" ADD COLUMN IF NOT EXISTS "PurgeNoticeSentAt" timestamp with time zone NULL;""",
        // ย้าย override เพดานผู้ใช้ (Subscription.MaxUsers → CompanyQuotaOverrides 'users') — ฝ่ายค้าน S8: DISTINCT ON (CompanyId) · แม่แบบ = แถว active ล่าสุดของ Plan ที่ sub ผูก ·
        // หลักฐานรายฟิลด์ = ประวัติ AdminLimitsChanged ที่ Notes เอ่ย "users"/"MaxUsers" (รูปแบบรอบ 231/เดิม) — ไม่มีหลักฐานรายฟิลด์แต่มี AdminLimitsChanged ⇒ ย้ายพร้อม Reason บอกว่า "ไม่มีหลักฐานรายฟิลด์"
        """
        INSERT INTO "CompanyQuotaOverrides" ("Id", "CompanyId", "ResourceKey", "Limit", "Reason", "SetBy", "SetAt", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT DISTINCT ON (s."CompanyId") gen_random_uuid(), s."CompanyId", 'users',
               CASE WHEN s."MaxUsers" >= 999 THEN -1 ELSE s."MaxUsers" END,
               CASE WHEN EXISTS (SELECT 1 FROM "SubscriptionHistories" h WHERE h."SubscriptionId" = s."Id" AND h."Action" = 'AdminLimitsChanged'
                                   AND (h."Notes" ILIKE '%users%' OR h."Notes" ILIKE '%MaxUsers%'))
                    THEN 'migration รอบ 231: ย้ายเพดานผู้ใช้ที่แอดมินเคยตั้งบน Subscription.MaxUsers (กลไกเดียว CompanyQuotaOverride · มีประวัติแก้ฟิลด์นี้)'
                    ELSE 'migration รอบ 231: ย้ายจาก Subscription.MaxUsers เดิม (ต่างจากแม่แบบ + มีประวัติ AdminLimitsChanged แต่ไม่มีหลักฐานรายฟิลด์ — แอดมินตรวจทาน)' END,
               'system:migration-231', now(), now(), 'system:migration-231', false
        FROM "Subscriptions" s
        JOIN LATERAL (SELECT t."MaxUsers" FROM "PlanTemplates" t WHERE t."Plan" = s."Plan" AND t."IsActive" = true AND t."IsDeleted" = false ORDER BY t."CreatedAt" DESC LIMIT 1) t ON true
        WHERE s."IsDeleted" = false AND s."Status" = 1 AND s."MaxUsers" <> t."MaxUsers"
          AND EXISTS (SELECT 1 FROM "SubscriptionHistories" h WHERE h."SubscriptionId" = s."Id" AND h."Action" = 'AdminLimitsChanged')
          AND NOT EXISTS (SELECT 1 FROM "CompanyQuotaOverrides" o WHERE o."CompanyId" = s."CompanyId" AND o."ResourceKey" = 'users' AND o."IsDeleted" = false)
        ORDER BY s."CompanyId", s."CreatedAt" DESC;
        """,
        // ย้าย override พื้นที่ (Subscription.MaxStorageBytes → CompanyQuotaOverrides 'storage.gb' · GB ปัดขึ้น · ≥ UnlimitedBytes = -1) — กติกาเดียวกับผู้ใช้ (S8)
        """
        INSERT INTO "CompanyQuotaOverrides" ("Id", "CompanyId", "ResourceKey", "Limit", "Reason", "SetBy", "SetAt", "CreatedAt", "CreatedBy", "IsDeleted")
        SELECT DISTINCT ON (s."CompanyId") gen_random_uuid(), s."CompanyId", 'storage.gb',
               CASE WHEN s."MaxStorageBytes" >= 2305843009213693951 THEN -1 ELSE CEIL(s."MaxStorageBytes" / 1073741824.0)::integer END,
               CASE WHEN EXISTS (SELECT 1 FROM "SubscriptionHistories" h WHERE h."SubscriptionId" = s."Id" AND h."Action" = 'AdminLimitsChanged'
                                   AND (h."Notes" ILIKE '%storage%' OR h."Notes" ILIKE '%MaxStorageBytes%'))
                    THEN 'migration รอบ 231: ย้ายเพดานพื้นที่ที่แอดมินเคยตั้งบน Subscription.MaxStorageBytes (กลไกเดียว CompanyQuotaOverride · มีประวัติแก้ฟิลด์นี้)'
                    ELSE 'migration รอบ 231: ย้ายจาก Subscription.MaxStorageBytes เดิม (ต่างจากแม่แบบ + มีประวัติ AdminLimitsChanged แต่ไม่มีหลักฐานรายฟิลด์ — แอดมินตรวจทาน)' END,
               'system:migration-231', now(), now(), 'system:migration-231', false
        FROM "Subscriptions" s
        JOIN LATERAL (SELECT t."MaxStorageBytes" FROM "PlanTemplates" t WHERE t."Plan" = s."Plan" AND t."IsActive" = true AND t."IsDeleted" = false ORDER BY t."CreatedAt" DESC LIMIT 1) t ON true
        WHERE s."IsDeleted" = false AND s."Status" = 1 AND s."MaxStorageBytes" <> t."MaxStorageBytes"
          AND EXISTS (SELECT 1 FROM "SubscriptionHistories" h WHERE h."SubscriptionId" = s."Id" AND h."Action" = 'AdminLimitsChanged')
          AND NOT EXISTS (SELECT 1 FROM "CompanyQuotaOverrides" o WHERE o."CompanyId" = s."CompanyId" AND o."ResourceKey" = 'storage.gb' AND o."IsDeleted" = false)
        ORDER BY s."CompanyId", s."CreatedAt" DESC;
        """,
    };
}
