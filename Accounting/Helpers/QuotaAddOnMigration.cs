namespace Accounting.Helpers;

/// <summary>รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.3/§B.9) — ตารางโควตาเสริม · <b>ตารางใหม่ล้วน</b> (CREATE TABLE/INDEX IF NOT EXISTS ⇒ รันทุกบูตได้) ·
/// <b>ไม่ backfill</b> โดยตั้งใจ: ทรัพยากรที่มีช่องเดิมบนแพ็กเกจ (ผู้ใช้/พื้นที่/OCR) ยังอ่านช่องเดิมเป็นตัวตั้ง (ไม่ทำสำเนาที่สอง) ·
/// ทรัพยากรใหม่ (เว็บ CMS/หน้า/สินค้า/ที่พัก) ไม่มีแถว = ไม่จำกัด/ค่าเดิม ⇒ deploy แล้วไม่มีผู้เช่าเดิมคนไหนถูกบล็อก (§B.9 ข้อ 3) ·
/// ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class QuotaAddOnMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "PlanQuotaAllowances" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "PlanTemplateId" uuid NOT NULL,
            "ResourceKey" varchar(64) NOT NULL,
            "Included" integer NOT NULL DEFAULT -1,
            "TrialIncluded" integer NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlanQuotaAllowances_Plan_Resource" ON "PlanQuotaAllowances" ("PlanTemplateId", "ResourceKey") WHERE "IsDeleted" = false;""",
        """
        CREATE TABLE IF NOT EXISTS "CompanyQuotaOverrides" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ResourceKey" varchar(64) NOT NULL,
            "Limit" integer NOT NULL,
            "Reason" varchar(500) NOT NULL DEFAULT '',
            "SetBy" varchar(200) NULL,
            "SetAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyQuotaOverrides_Company_Resource" ON "CompanyQuotaOverrides" ("CompanyId", "ResourceKey") WHERE "IsDeleted" = false;""",
        """
        CREATE TABLE IF NOT EXISTS "QuotaAddOnOffers" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "ResourceKey" varchar(64) NOT NULL,
            "Name" varchar(200) NULL,
            "UnitSize" integer NOT NULL DEFAULT 1,
            "MonthlyUnitPrice" numeric(18,2) NOT NULL DEFAULT 0,
            "AnnualUnitPrice" numeric(18,2) NULL,
            "AllowedModes" integer NOT NULL DEFAULT 7,
            "MaxUnitsPerCompany" integer NULL,
            "MinChargeOneMonth" boolean NOT NULL DEFAULT true,
            "ValidDays" integer NULL,
            "IsActive" boolean NOT NULL DEFAULT true,
            "SortOrder" integer NOT NULL DEFAULT 0,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_QuotaAddOnOffers_Resource" ON "QuotaAddOnOffers" ("ResourceKey") WHERE "IsDeleted" = false;""",
        """
        CREATE TABLE IF NOT EXISTS "CompanyQuotaAddOns" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "OfferId" uuid NOT NULL,
            "ResourceKey" varchar(64) NOT NULL,
            "Units" integer NOT NULL,
            "UnitSize" integer NOT NULL DEFAULT 1,
            "Mode" integer NOT NULL,
            "PeriodStartUtc" timestamp with time zone NOT NULL,
            "PeriodEndUtc" timestamp with time zone NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "CancelAtPeriodEnd" boolean NOT NULL DEFAULT false,
            "RenewWithPlan" boolean NOT NULL DEFAULT true,
            "RenewUnits" integer NULL,
            "RenewUnitPrice" numeric(18,2) NULL,
            "RenewalInvoiceDocumentId" uuid NULL,
            "RenewedFromId" uuid NULL,
            "UnitPriceSnapshot" numeric(18,2) NOT NULL DEFAULT 0,
            "MonthlyUnitPriceSnapshot" numeric(18,2) NOT NULL DEFAULT 0,
            "AmountDue" numeric(18,2) NOT NULL DEFAULT 0,
            "VatIncluded" boolean NOT NULL DEFAULT true,
            "ProrationNote" varchar(300) NULL,
            "PaymentIntentId" uuid NULL,
            "SlipPath" varchar(500) NULL,
            "SlipUploadedAt" timestamp with time zone NULL,
            "SlipReference" varchar(120) NULL,
            "RejectedReason" varchar(500) NULL,
            "ReviewedAt" timestamp with time zone NULL,
            "ReviewedBy" varchar(200) NULL,
            "ReceiptDocumentId" uuid NULL,
            "ReceiptNumber" varchar(64) NULL,
            "LastBilledPeriod" varchar(7) NULL,
            "OcrCreditPurchaseId" uuid NULL,
            "PolicyVersionAccepted" varchar(64) NULL,
            "RequestKey" varchar(80) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_CompanyQuotaAddOns_Company_Resource_Status" ON "CompanyQuotaAddOns" ("CompanyId", "ResourceKey", "Status");""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyQuotaAddOns_Company_RequestKey" ON "CompanyQuotaAddOns" ("CompanyId", "RequestKey") WHERE "RequestKey" IS NOT NULL AND "IsDeleted" = false;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyQuotaAddOns_RenewedFrom" ON "CompanyQuotaAddOns" ("RenewedFromId") WHERE "RenewedFromId" IS NOT NULL AND "IsDeleted" = false;""",
        // ฝ่ายค้านรอบ 229 (S2/S7/B2): หลักฐานเงินจริง + เรื่องค้างของแอดมิน — แถวเดิม NULL
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "SlipTransferDate" timestamp with time zone NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "ReceivedAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "OfflineReference" varchar(200) NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "AttentionRule" varchar(64) NULL;""",
        """ALTER TABLE "CompanyQuotaAddOns" ADD COLUMN IF NOT EXISTS "AttentionReason" varchar(500) NULL;""",
    };
}
