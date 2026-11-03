namespace Accounting.Helpers;

/// <summary>รอบ 230 (PLATFORM_OMISE_AUDIT §5.2/§5.3) — schema ของบัตรที่บันทึกไว้ · ตัดบัตรอัตโนมัติ · รอบโอนจากผู้ให้บริการ
/// <b>บล็อกเดียว · รันทุกบูตได้</b> (CREATE TABLE/INDEX IF NOT EXISTS) · ชื่อ index ตรงกับ <c>AccountingDbContext</c> (ฐานใหม่/ฐานเทสต์ได้จาก EnsureCreated) ·
/// ไม่มีคอลัมน์เลขบัตร/CVV โดยเจตนา (PCI-DSS SAQ-A)</summary>
public static class PlatformCardBillingMigration
{
    private const string Audit = "\"CreatedAt\" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'), \"UpdatedAt\" timestamp without time zone NULL, \"CreatedBy\" text NULL, \"UpdatedBy\" text NULL, \"IsDeleted\" boolean NOT NULL DEFAULT false";

    public static IReadOnlyList<string> Statements() => new[]
    {
        $"""CREATE TABLE IF NOT EXISTS "PlatformSavedCards" ("Id" uuid PRIMARY KEY, "PlatformCompanyId" uuid NOT NULL, "PayerCompanyId" uuid NOT NULL, "ProviderCode" varchar(40) NOT NULL DEFAULT '', "ProviderConfigId" uuid NULL, "IsTestMode" boolean NOT NULL DEFAULT false, "CustomerRef" varchar(100) NOT NULL DEFAULT '', "CardRef" varchar(100) NOT NULL DEFAULT '', "Brand" varchar(40) NULL, "Last4" varchar(4) NULL, "ValidUntil" timestamp without time zone NULL, "IsDefault" boolean NOT NULL DEFAULT false, "ConsentRecordId" uuid NULL, "FirstIntentId" uuid NULL, "VerifiedAt" timestamp without time zone NULL, "RemovedAt" timestamp without time zone NULL, "RemovedBy" varchar(256) NULL, "RemovedReason" varchar(300) NULL, "ProviderRemoveProblem" varchar(500) NULL, {Audit});""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformSavedCards_Payer" ON "PlatformSavedCards" ("PayerCompanyId", "PlatformCompanyId");""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformSavedCards_ProviderCard" ON "PlatformSavedCards" ("ProviderCode", "CardRef") WHERE "RemovedAt" IS NULL AND "IsDeleted" = false;""",

        $"""CREATE TABLE IF NOT EXISTS "PlatformAutoChargeMandates" ("Id" uuid PRIMARY KEY, "PlatformCompanyId" uuid NOT NULL, "PayerCompanyId" uuid NOT NULL, "Enabled" boolean NOT NULL DEFAULT false, "ConsentRecordId" uuid NULL, "ConsentVersion" varchar(150) NULL, "EnabledAt" timestamp without time zone NULL, "EnabledBy" varchar(256) NULL, "DisabledAt" timestamp without time zone NULL, "DisabledBy" varchar(256) NULL, "DisabledReason" varchar(300) NULL, "AdminBlockedAt" timestamp without time zone NULL, "AdminBlockedBy" varchar(256) NULL, "AdminBlockedReason" varchar(300) NULL, {Audit});""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformAutoChargeMandates_Payer" ON "PlatformAutoChargeMandates" ("PlatformCompanyId", "PayerCompanyId");""",

        $"""CREATE TABLE IF NOT EXISTS "PlatformAutoChargeAttempts" ("Id" uuid PRIMARY KEY, "PlatformCompanyId" uuid NOT NULL, "PayerCompanyId" uuid NOT NULL, "DocumentId" uuid NOT NULL, "AttemptNo" integer NOT NULL, "SubscriptionPaymentId" uuid NULL, "SavedCardId" uuid NULL, "IntentId" uuid NULL, "Amount" numeric(18,2) NOT NULL DEFAULT 0, "Outcome" varchar(20) NOT NULL DEFAULT 'Claimed', "Detail" varchar(500) NULL, "AttemptedAt" timestamp without time zone NOT NULL, "FinishedAt" timestamp without time zone NULL, {Audit});""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformAutoChargeAttempts_Doc_No" ON "PlatformAutoChargeAttempts" ("DocumentId", "AttemptNo");""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformAutoChargeAttempts_Payer" ON "PlatformAutoChargeAttempts" ("PayerCompanyId", "AttemptedAt");""",

        $"""CREATE TABLE IF NOT EXISTS "PlatformAutoChargeSettings" ("Id" uuid PRIMARY KEY, "AutoChargeEnabled" boolean NOT NULL DEFAULT false, "ChangedAt" timestamp without time zone NULL, "ChangedBy" varchar(256) NULL, {Audit});""",

        $"""CREATE TABLE IF NOT EXISTS "PlatformProviderTransfers" ("Id" uuid PRIMARY KEY, "PlatformCompanyId" uuid NOT NULL, "ProviderCode" varchar(40) NOT NULL DEFAULT '', "ProviderConfigId" uuid NULL, "IsTestMode" boolean NOT NULL DEFAULT false, "TransferRef" varchar(100) NOT NULL DEFAULT '', "WalletDebit" numeric(18,2) NOT NULL DEFAULT 0, "NetToBank" numeric(18,2) NOT NULL DEFAULT 0, "TransferFee" numeric(18,2) NOT NULL DEFAULT 0, "TransferFeeVat" numeric(18,2) NOT NULL DEFAULT 0, "RawStatus" varchar(40) NULL, "Paid" boolean NOT NULL DEFAULT false, "ProviderCreatedAt" timestamp without time zone NULL, "PaidAt" timestamp without time zone NULL, "ChargeRefsJson" text NULL, "MatchMethod" varchar(20) NOT NULL DEFAULT 'None', "MatchedIntentIdsJson" text NULL, "Problem" varchar(500) NULL, "SettlementBatchId" uuid NULL, "DraftCreatedAt" timestamp without time zone NULL, "PulledAt" timestamp without time zone NOT NULL, {Audit});""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PlatformProviderTransfers_Ref" ON "PlatformProviderTransfers" ("ProviderCode", "TransferRef");""",
        """CREATE INDEX IF NOT EXISTS "IX_PlatformProviderTransfers_Platform" ON "PlatformProviderTransfers" ("PlatformCompanyId", "ProviderCreatedAt");""",
        // ── ฝ่ายค้านรอบ 230 (B2 · S1 · S2 · S4) ──
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "SavedCardRef" varchar(100) NULL;""",
        """ALTER TABLE "PdpaConsentRecords" ADD COLUMN IF NOT EXISTS "UserAgent" varchar(500) NULL;""",
        """ALTER TABLE "PlatformSavedCards" ADD COLUMN IF NOT EXISTS "WhtAcknowledgedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PlatformAutoChargeMandates" ADD COLUMN IF NOT EXISTS "WhtAcknowledgedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PlatformAutoChargeMandates" ADD COLUMN IF NOT EXISTS "LastProcessedAt" timestamp without time zone NULL;""",
    };
}
