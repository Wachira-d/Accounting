using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Adds missing columns to existing tables that were created before
/// new entity properties were added. EnsureCreated() does not alter
/// existing tables, so we run ALTER TABLE … ADD COLUMN IF NOT EXISTS guards.
/// </summary>
public static class DatabaseMigrationHelper
{
    public static void ApplyMissingColumns(AccountingDbContext db, ILogger? logger = null)
    {
        var statements = GetAlterStatements();
        foreach (var sql in statements)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
            }
            catch (Exception ex)
            {
                // Existing-column / existing-index errors are expected and benign
                // (idempotent ALTER ADD COLUMN IF NOT EXISTS still triggers a
                // benign "already exists" on some PG versions). Anything else —
                // syntax error in a new statement, FK constraint refusing the
                // CREATE TABLE — used to silently disappear and we'd only learn
                // about it when runtime queries failed. Log everything so
                // genuine breakage is visible in the startup log.
                var msg = ex.Message ?? "";
                var benign = msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                          || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
                if (!benign)
                    logger?.LogWarning(ex, "[DbMigration] Statement failed (continuing): {Sql}",
                        sql.Length > 200 ? sql[..200] + "…" : sql);
            }
        }
    }

    private static List<string> GetAlterStatements()
    {
        return
        [
            // ===== Users: security fields =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "FailedLoginAttempts" integer NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LockoutEnd" timestamp NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetToken" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetTokenExpiry" timestamp NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "EmailVerified" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordWeakDetectedAt" timestamp NULL;
            """,

            // ===== Users: Signature fields (auto-stamped on documents) =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureImageBase64" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureTitle" varchar(200) NULL;
            """,

            // ===== Contacts: structured address fields (ETDA-compliant) =====
            // Pre-existing schema had only `Address` text. New columns enable proper
            // ETDA Schematron-conformant XML (BuildingNumber/CityName/CitySubDivisionName/
            // CountrySubDivisionID required when CountryID=TH).
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BranchName" varchar(200) NULL;
            """,
            // Contact LINE binding — ส่งเอกสารผ่าน LINE flex message ให้ลูกค้า
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LineUserId" varchar(100) NULL;""",
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BuildingNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BuildingName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "StreetName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "SubDistrict" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "District" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "Province" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PostalCode" varchar(10) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "CountryCode" varchar(3) NOT NULL DEFAULT 'TH';
            """,
            // ลูกค้ารายนี้ออกใบกำกับภาษีเสมอ (pre-select TaxInvoice ตอนสร้างเอกสาร)
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultIssueTaxInvoice" boolean NOT NULL DEFAULT false;""",
            // เครดิตเทอมต่อลูกค้า — เติมวันครบกำหนดอัตโนมัติตอนสร้างเอกสารขาย
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PaymentDueDays" integer NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PaymentTerms" varchar(100) NULL;""",

            // ===== Companies: structured address — add missing BuildingNumber/Name/StreetName =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BuildingNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BuildingName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "StreetName" varchar(200) NULL;
            """,

            // ===== Documents: currency field =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Currency" varchar(3) NOT NULL DEFAULT 'THB';
            """,

            // ===== Documents: PaymentType (Cash vs Credit settlement basis) =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentType" int NULL;
            """,

            // ===== Documents: PricesIncludeVat (VAT-inclusive line pricing) =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PricesIncludeVat" boolean NOT NULL DEFAULT false;
            """,

            // ===== DocumentLines: ProductCode =====
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "ProductCode" varchar(50) NULL;
            """,

            // ===== Documents: per-document appendix/notes overrides =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomAppendix" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomFooterNotes" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomTermsAndConditions" text NULL;
            """,

            // ===== Documents: Revenue Contract auto-link =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RevenueContractId" uuid NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PerformanceObligationId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RevenueContractId" ON "Documents" ("RevenueContractId");
            """,

            // ===== Documents: Project linking (header-level) =====
            // Tags an entire document to a project — propagates to JE.ProjectId
            // on auto-post so per-project P&L picks up revenue/cost automatically.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_ProjectId" ON "Documents" ("ProjectId");
            """,

            // ===== DocumentLines: per-line project override =====
            // Allows a single document to bill multiple projects (e.g. shared invoice).
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_ProjectId" ON "DocumentLines" ("ProjectId");
            """,

            // ===== CompanySettings: e-Tax fields =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxEnabled" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxCertificatePath" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxCertificatePassword" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxRdApiKey" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxRdApiSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxTestMode" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxAutoSign" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxAutoSubmit" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AutoAttachWhtCertPdf" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxServiceProvider" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxXmlOutputPath" varchar(500) NULL;
            """,

            // ===== Recurring late-fee accrual policy =====
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeRatePerDay" numeric(8,4) NOT NULL DEFAULT 0.05;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeGraceDays" int NOT NULL DEFAULT 7;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeMaxPercent" numeric(5,2) NULL DEFAULT 20.0;""",

            // ===== POS deposit support — IsDeposit + DepositRealizedAt =====
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "IsDeposit" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "DepositRealizedAt" timestamp with time zone NULL;""",

            // ===== PDPA ม.26 — widen Employee PII columns เพื่อรองรับ ciphertext =====
            // (Base64 of nonce 12 + ciphertext 13 + tag 16 = ~56 chars + prefix)
            """ALTER TABLE "Employees" ALTER COLUMN "CitizenId" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "TaxId" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "PassportNumber" TYPE varchar(200);""",

            // ===== CompanySettings: §82/5(6) vehicle dealer override =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "IsVehicleDealer" boolean NOT NULL DEFAULT false;
            """,

            // ===== Companies: IndustryType =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IndustryType" integer NOT NULL DEFAULT 0;
            """,

            // ===== Users: SSO columns =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AuthProvider" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AuthProviderId" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "IsSystemAdmin" boolean NOT NULL DEFAULT false;
            """,

            // ===== Companies: Business Registration & Setup =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BranchName" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "JuristicId" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsVatRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "VatRate" decimal(18,2) NOT NULL DEFAULT 7;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsWhtRegistered" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsSocialSecurityRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "SocialSecurityAccountNo" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Fax" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Website" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsSetupComplete" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BaseCurrency" varchar(10) NOT NULL DEFAULT 'THB';
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BusinessType" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "FiscalYearStartMonth" int NOT NULL DEFAULT 1;
            """,

            // ===== DocumentLines: WHT income type =====
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "IncomeTypeCode" text NULL;
            """,

            // ===== Subscriptions: Notification settings =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBeforeExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysBeforeExpiry" text NOT NULL DEFAULT '30,15,7,3,1';
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyOnExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyAfterExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysAfterExpiry" text NOT NULL DEFAULT '1,3,7';
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "DeactivationDaysAfterExpiry" integer NOT NULL DEFAULT 14;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBeforeDeactivation" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysBeforeDeactivation" text NOT NULL DEFAULT '7,3,1';
            """,

            // ===== Subscriptions / PlanTemplates: Permanent free flag =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "IsPermanentFree" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "IsPermanentFree" boolean NOT NULL DEFAULT false;
            """,

            // ===== JournalEntries: Project / branch / dimension / metadata / source doc =====
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "DimensionId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Note" varchar(2000) NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Tags" varchar(500) NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "DimensionId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "Tags" varchar(500) NULL;
            """,

            // ===== CompanySettings: Cross-tenant knowledge sharing =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShareTrainingDataAnonymously" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnTrainingBonusMultiplier" decimal(4,2) NOT NULL DEFAULT 2.0;
            """,

            // ===== CompanySettings: Landing page services =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactPhone" varchar(50) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactLine" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactEmail" varchar(200) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingServicesJson" text NULL;
            """,

            // ===== POS Tables =====
            """
            CREATE TABLE IF NOT EXISTS "PosTerminals" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(200) NOT NULL,
                "BusinessMode" integer NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Location" varchar(500) NULL,
                "SettingsJson" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosTerminals" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosTerminals_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosSessions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TerminalId" uuid NOT NULL,
                "OpenedByUserId" uuid NOT NULL,
                "ClosedByUserId" uuid NULL,
                "OpenedAt" timestamp NOT NULL DEFAULT now(),
                "ClosedAt" timestamp NULL,
                "OpeningBalance" decimal(18,2) NOT NULL,
                "ClosingBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "ExpectedBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 1,
                "Notes" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosSessions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosSessions_PosTerminals" FOREIGN KEY ("TerminalId") REFERENCES "PosTerminals"("Id"),
                CONSTRAINT "FK_PosSessions_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrders" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SessionId" uuid NOT NULL,
                "OrderNumber" varchar(50) NOT NULL,
                "OrderType" integer NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "CustomerId" uuid NULL,
                "CustomerName" varchar(500) NULL,
                "TableNumber" varchar(50) NULL,
                "GuestCount" integer NULL,
                "QueueNumber" varchar(50) NULL,
                "AppointmentTime" timestamp NULL,
                "PrimaryStaffId" uuid NULL,
                "SubTotal" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountPercent" decimal(5,2) NOT NULL DEFAULT 0,
                "ServiceChargePercent" decimal(5,2) NOT NULL DEFAULT 0,
                "ServiceChargeAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "VatAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "TotalAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "RoundingAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "NetAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "Reference" varchar(200) NULL,
                "JournalEntryId" uuid NULL,
                "DocumentId" uuid NULL,
                "CompletedAt" timestamp NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrders" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrders_PosSessions" FOREIGN KEY ("SessionId") REFERENCES "PosSessions"("Id"),
                CONSTRAINT "FK_PosOrders_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ServicePackages" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(500) NOT NULL,
                "NameEn" varchar(500) NULL,
                "Description" text NULL,
                "Sku" varchar(50) NULL,
                "Category" varchar(200) NULL,
                "Price" decimal(18,2) NOT NULL,
                "CostPrice" decimal(18,2) NULL,
                "DurationMinutes" integer NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "IsVatIncluded" boolean NOT NULL DEFAULT true,
                "RevenueAccountId" uuid NULL,
                "ImageUrl" text NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ServicePackages" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ServicePackages_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ServiceComponents" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PackageId" uuid NOT NULL,
                "StepOrder" integer NOT NULL,
                "Name" varchar(500) NOT NULL,
                "NameEn" varchar(500) NULL,
                "Description" text NULL,
                "DurationMinutes" integer NOT NULL,
                "CommissionType" integer NOT NULL DEFAULT 1,
                "CommissionValue" decimal(18,2) NOT NULL DEFAULT 0,
                "RequiresStaff" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ServiceComponents" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ServiceComponents_ServicePackages" FOREIGN KEY ("PackageId") REFERENCES "ServicePackages"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrderItems" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderId" uuid NOT NULL,
                "ProductId" uuid NULL,
                "ServicePackageId" uuid NULL,
                "ItemName" varchar(500) NOT NULL,
                "ItemCode" varchar(50) NULL,
                "Quantity" decimal(18,4) NOT NULL DEFAULT 1,
                "Unit" varchar(50) NULL,
                "UnitPrice" decimal(18,2) NOT NULL,
                "DiscountAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountPercent" decimal(5,2) NOT NULL DEFAULT 0,
                "SubTotal" decimal(18,2) NOT NULL DEFAULT 0,
                "VatAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "TotalAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "LineOrder" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrderItems" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrderItems_PosOrders" FOREIGN KEY ("OrderId") REFERENCES "PosOrders"("Id"),
                CONSTRAINT "FK_PosOrderItems_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id"),
                CONSTRAINT "FK_PosOrderItems_ServicePackages" FOREIGN KEY ("ServicePackageId") REFERENCES "ServicePackages"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrderItemModifiers" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderItemId" uuid NOT NULL,
                "ModifierOptionId" uuid NULL,
                "ModifierGroupName" varchar(200) NOT NULL,
                "ModifierName" varchar(200) NOT NULL,
                "PriceAdjustment" decimal(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrderItemModifiers" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrderItemModifiers_PosOrderItems" FOREIGN KEY ("OrderItemId") REFERENCES "PosOrderItems"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosPayments" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderId" uuid NOT NULL,
                "PaymentMethod" integer NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "ReceivedAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "ChangeAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "ReferenceNo" varchar(200) NULL,
                "CardLastFour" varchar(4) NULL,
                "PaidAt" timestamp NOT NULL DEFAULT now(),
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosPayments" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosPayments_PosOrders" FOREIGN KEY ("OrderId") REFERENCES "PosOrders"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosServiceActivities" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderItemId" uuid NOT NULL,
                "ComponentId" uuid NOT NULL,
                "StaffId" uuid NULL,
                "StaffName" varchar(200) NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "StartedAt" timestamp NULL,
                "CompletedAt" timestamp NULL,
                "CommissionAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosServiceActivities" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosServiceActivities_PosOrderItems" FOREIGN KEY ("OrderItemId") REFERENCES "PosOrderItems"("Id"),
                CONSTRAINT "FK_PosServiceActivities_ServiceComponents" FOREIGN KEY ("ComponentId") REFERENCES "ServiceComponents"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierGroups" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "IsRequired" boolean NOT NULL DEFAULT false,
                "AllowMultiple" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierGroups" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierGroups_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierGroupLinks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProductId" uuid NOT NULL,
                "ModifierGroupId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierGroupLinks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierGroupLinks_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id"),
                CONSTRAINT "FK_ProductModifierGroupLinks_Groups" FOREIGN KEY ("ModifierGroupId") REFERENCES "ProductModifierGroups"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierOptions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "GroupId" uuid NOT NULL,
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "PriceAdjustment" decimal(18,2) NOT NULL DEFAULT 0,
                "IsDefault" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierOptions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierOptions_Groups" FOREIGN KEY ("GroupId") REFERENCES "ProductModifierGroups"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "StaffCommissionSummaries" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "StaffId" uuid NOT NULL,
                "StaffName" varchar(200) NOT NULL,
                "PeriodStart" timestamp NOT NULL,
                "PeriodEnd" timestamp NOT NULL,
                "TotalActivities" integer NOT NULL DEFAULT 0,
                "TotalCommission" decimal(18,2) NOT NULL DEFAULT 0,
                "PaidAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "RemainingAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "IsPaid" boolean NOT NULL DEFAULT false,
                "JournalEntryId" uuid NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_StaffCommissionSummaries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_StaffCommissionSummaries_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,

            // Contact Inquiries (public contact form)
            """
            -- ===== JournalEntries: JournalType column =====
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "JournalType" integer NOT NULL DEFAULT 0;
            """,
            """
            -- ===== WithholdingTaxCerts: DocumentId column =====
            ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "DocumentId" uuid NULL;
            """,
            """
            -- ===== Contacts: ContactType column =====
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "ContactType" integer NOT NULL DEFAULT 1;
            """,
            """
            -- ===== Auto-classify existing contacts: BranchCode != NULL/00000 → JuristicPerson =====
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE lower(table_name)='contacts' AND lower(column_name)='contacttype') THEN
                UPDATE "Contacts" SET "ContactType" = 2
                WHERE "ContactType" = 1
                  AND "BranchCode" IS NOT NULL AND "BranchCode" <> '' AND "BranchCode" <> '00000';
            END IF;
            END $$;
            """,
            """
            CREATE TABLE IF NOT EXISTS "ContactInquiries" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(500) NOT NULL,
                "Email" varchar(500) NOT NULL,
                "Phone" varchar(50) NULL,
                "Company" varchar(500) NULL,
                "Subject" varchar(1000) NOT NULL,
                "Message" text NOT NULL,
                "IsRead" boolean NOT NULL DEFAULT false,
                "IsReplied" boolean NOT NULL DEFAULT false,
                "IpAddress" varchar(100) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ContactInquiries" PRIMARY KEY ("Id")
            );
            """,

            // ===== JournalEntries: Reversal tracking =====
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "ReversedByEntryId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "OriginalEntryId" uuid NULL;
            """,

            // ===== SiteSettings: global site configuration (singleton) =====
            // ===== BankTransactions: AI Reconciliation MatchGroupId =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchGroupId" text NULL;
            """,
            // ===== BankTransactions: many-to-one matched entry IDs (JSON array) =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchedEntryIdsJson" text NULL;
            """,
            // ===== BankTransactions: link to true M:N reconciliation group =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "ReconciliationGroupId" uuid NULL;
            """,
            // ===== ReconciliationGroups: header table for M:N + net-off matching =====
            """
            CREATE TABLE IF NOT EXISTS "ReconciliationGroups" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "GroupNumber" varchar(40) NOT NULL,
                "ReconciledDate" timestamp with time zone NOT NULL,
                "TotalBankAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "TotalMatchedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "IsBalanced" boolean NOT NULL DEFAULT false,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconciliationGroups_CompanyId_GroupNumber"
                ON "ReconciliationGroups" ("CompanyId", "GroupNumber");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroups_BankAccountId"
                ON "ReconciliationGroups" ("BankAccountId");
            """,
            // ===== ReconciliationGroupItems: line items (polymorphic) =====
            """
            CREATE TABLE IF NOT EXISTS "ReconciliationGroupItems" (
                "Id" uuid PRIMARY KEY,
                "GroupId" uuid NOT NULL REFERENCES "ReconciliationGroups"("Id") ON DELETE CASCADE,
                "ItemType" int NOT NULL,
                "ItemId" uuid NOT NULL,
                "AllocatedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroupItems_GroupId"
                ON "ReconciliationGroupItems" ("GroupId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroupItems_ItemType_ItemId"
                ON "ReconciliationGroupItems" ("ItemType", "ItemId");
            """,
            // ===== BankReconciliationPatterns: AI learning store =====
            """
            CREATE TABLE IF NOT EXISTS "BankReconciliationPatterns" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "DescriptionSignature" varchar(500) NOT NULL,
                "AmountBucket" varchar(20) NOT NULL,
                "TargetType" int NOT NULL,
                "ContactId" uuid NULL,
                "TargetAccountCode" varchar(20) NULL,
                "TimesConfirmed" int NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "AvgAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "MinAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "MaxAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankReconciliationPatterns_Lookup"
                ON "BankReconciliationPatterns" ("CompanyId", "BankAccountId", "DescriptionSignature", "AmountBucket");
            """,

            // ===== ChequeBook + Cheque (Thai SME cheque management) =====
            """
            CREATE TABLE IF NOT EXISTS "ChequeBooks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "BookNumber" varchar(50) NOT NULL,
                "StartChequeNumber" bigint NOT NULL,
                "EndChequeNumber" bigint NOT NULL,
                "NextNumber" bigint NOT NULL,
                "ReceivedFromBankAt" timestamp NOT NULL DEFAULT now(),
                "ExhaustedAt" timestamp NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ChequeBooks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ChequeBooks_BankAccount" FOREIGN KEY ("BankAccountId") REFERENCES "BankAccounts"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ChequeBooks_Company_Bank" ON "ChequeBooks" ("CompanyId", "BankAccountId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "Cheques" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ChequeBookId" uuid NULL,
                "ChequeNumber" bigint NOT NULL,
                "ChequeDate" timestamp NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "Currency" varchar(3) NOT NULL DEFAULT 'THB',
                "ContactId" uuid NULL,
                "IssuingBankName" varchar(100) NULL,
                "PaymentId" uuid NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "ClearedAt" timestamp NULL,
                "BounceReason" text NULL,
                "ReplacesChequeId" uuid NULL,
                "IsInbound" boolean NOT NULL DEFAULT false,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Cheques" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Cheques_Book" FOREIGN KEY ("ChequeBookId") REFERENCES "ChequeBooks"("Id"),
                CONSTRAINT "FK_Cheques_Replaces" FOREIGN KEY ("ReplacesChequeId") REFERENCES "Cheques"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Cheques_Company_Status" ON "Cheques" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_Cheques_Payment" ON "Cheques" ("PaymentId") WHERE "PaymentId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== StampDutyRecords (อากรแสตมป์) =====
            """
            CREATE TABLE IF NOT EXISTS "StampDutyRecords" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Reference" varchar(100) NOT NULL,
                "ContactId" uuid NULL,
                "InstrumentEntityType" varchar(50) NULL,
                "InstrumentEntityId" uuid NULL,
                "RdScheduleNumber" integer NOT NULL,
                "InstrumentType" varchar(50) NOT NULL,
                "InstrumentValue" decimal(18,2) NOT NULL,
                "DutyAmount" decimal(18,2) NOT NULL,
                "InstrumentDate" timestamp NOT NULL,
                "PaymentMethod" varchar(20) NOT NULL DEFAULT 'ESD',
                "PaidAt" timestamp NULL,
                "RdReceiptNumber" varchar(50) NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_StampDutyRecords" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_StampDutyRecords_Company" ON "StampDutyRecords" ("CompanyId", "InstrumentDate") WHERE "IsDeleted" = false;""",

            // ===== Product costing extension =====
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "CostingMethod" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "AverageUnitCost" decimal(18,4) NOT NULL DEFAULT 0;""",

            // ===== Petty cash + stock count =====
            """
            CREATE TABLE IF NOT EXISTS "PettyCashFunds" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Name" varchar(100) NOT NULL,
                "CustodianUserId" uuid NULL,
                "ImprestAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "CurrentBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "LinkedAccountId" uuid NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PettyCashFunds" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PettyCashTransactions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FundId" uuid NOT NULL,
                "TransactionDate" timestamp NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "Type" varchar(20) NOT NULL,
                "Description" text NOT NULL,
                "ReceiptReference" varchar(100) NULL,
                "ExpenseAccountId" uuid NULL,
                "JournalEntryId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PettyCashTransactions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PettyCashTransactions_Fund" FOREIGN KEY ("FundId") REFERENCES "PettyCashFunds"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PettyCashTransactions_Fund_Date" ON "PettyCashTransactions" ("FundId", "TransactionDate") WHERE "IsDeleted" = false;""",
            // StockCount table already exists from prior schema; just
            // add the UnitCost column on the line we need for variance JE.
            """ALTER TABLE "StockCountLines" ADD COLUMN IF NOT EXISTS "UnitCost" decimal(18,4) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "StockCounts" ADD COLUMN IF NOT EXISTS "CountType" varchar(20) NOT NULL DEFAULT 'Full';""",

            // ===== PDPA + BOM + Consignment (Tier 3) =====
            """
            CREATE TABLE IF NOT EXISTS "PdpaDataSubjectRequests" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RequestNumber" varchar(50) NOT NULL,
                "RequestedAt" timestamp NOT NULL DEFAULT now(),
                "RequesterContact" varchar(200) NOT NULL,
                "RequesterName" varchar(200) NULL,
                "LinkedUserId" uuid NULL,
                "LinkedContactId" uuid NULL,
                "RequestType" varchar(30) NOT NULL,
                "Description" text NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "DueBy" timestamp NOT NULL,
                "CompletedAt" timestamp NULL,
                "CompletionNote" text NULL,
                "AssignedDpoUserId" uuid NULL,
                "RejectionReason" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PdpaDataSubjectRequests" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Pdpa_Status_Due" ON "PdpaDataSubjectRequests" ("CompanyId", "Status", "DueBy") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "BillsOfMaterials" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ParentProductId" uuid NOT NULL,
                "Version" varchar(20) NOT NULL DEFAULT 'v1',
                "EffectiveFrom" timestamp NOT NULL DEFAULT now(),
                "EffectiveTo" timestamp NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_BillsOfMaterials" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "BomLines" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "BomId" uuid NOT NULL,
                "ComponentProductId" uuid NOT NULL,
                "QuantityPerParent" decimal(18,4) NOT NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_BomLines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BomLines_Bom" FOREIGN KEY ("BomId") REFERENCES "BillsOfMaterials"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ConsignmentRecords" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ProductId" uuid NOT NULL,
                "ContactId" uuid NOT NULL,
                "Direction" varchar(20) NOT NULL,
                "QuantityOnHand" decimal(18,4) NOT NULL DEFAULT 0,
                "AgreedUnitPrice" decimal(18,4) NULL,
                "ReceivedAt" timestamp NOT NULL,
                "ReturnedOrSettledAt" timestamp NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ConsignmentRecords" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Consignment_Product_Contact" ON "ConsignmentRecords" ("CompanyId", "ProductId", "ContactId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "VendorPortalTokens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "TokenHash" varchar(64) NOT NULL,
                "ContactId" uuid NOT NULL,
                "Role" varchar(20) NOT NULL DEFAULT 'Vendor',
                "IssuedAt" timestamp NOT NULL DEFAULT now(),
                "ExpiresAt" timestamp NOT NULL,
                "LastUsedAt" timestamp NULL,
                "RevokedAt" timestamp NULL,
                "RevokedReason" text NULL,
                "IssuedByUserId" uuid NULL,
                "RecipientEmail" varchar(200) NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VendorPortalTokens" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_VendorPortalTokens_Hash" ON "VendorPortalTokens" ("TokenHash") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_VendorPortalTokens_Contact" ON "VendorPortalTokens" ("CompanyId", "ContactId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "ProductionOrders" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "OrderNumber" varchar(50) NOT NULL,
                "ParentProductId" uuid NOT NULL,
                "BomId" uuid NOT NULL,
                "PlannedQty" decimal(18,4) NOT NULL,
                "CompletedQty" decimal(18,4) NOT NULL DEFAULT 0,
                "PlannedStartAt" timestamp NOT NULL,
                "CompletedAt" timestamp NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Planned',
                "CumulativeComponentCost" decimal(18,4) NOT NULL DEFAULT 0,
                "JournalEntryId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductionOrders" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductionOrders_Company_Status" ON "ProductionOrders" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",

            // ===== TaxReport e-Filing ACK lifecycle =====
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAckNumber" varchar(50) NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAcknowledgedAt" timestamp NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdSubmissionStatus" varchar(30) NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdRejectionReason" text NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAcknowledgementDocumentUrl" text NULL;""",

            // ============================================================
            // ERP Upgrade — Task 1 (period close + migration), Task 4
            // (VAT deferral + filing lock + e-Filing), Task 5 (OCR audit)
            // ============================================================

            // FiscalPeriods: soft/hard close + year-end closing JE link
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "ClosedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "ClosedBy" text NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "LockedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "LockedBy" text NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "YearEndJournalEntryId" uuid NULL;
            """,

            // TaxReports: filing-lock + e-filing audit + rejection workflow
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "FilingLockedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "FilingLockedBy" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "EFilingExportedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "EFilingReferenceNumber" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectionReason" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectedBy" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "ReversalJournalEntryId" uuid NULL;
            """,

            // Documents: OCR compliance + aging cache
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrConfidenceScore" numeric(5,4) NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RdComplianceStatus" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RdComplianceIssuesJson" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrTenantMismatchFlag" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "AgingDays" int NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "AgingLastEvaluatedAt" timestamp with time zone NULL;
            """,
            // OverdueDunningJob — track last dunning send + level (1/2/3)
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LastDunningSentAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LastDunningLevel" int NULL;
            """,
            // Undue Input VAT (§82/3) — PV/PurchaseInvoice ที่ใบกำกับยังไม่ครบ §86/4
            // → VAT post เข้า 11640 ก่อน, รอ user มาแก้ครบแล้ว gen adjusting JE
            // Dr 11610 / Cr 11640. BecameClaimableAt = tax-point จริงสำหรับ ภ.พ.30.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatPostedAsUndue" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatBecameClaimableAt" timestamp with time zone NULL;
            """,
            // §82/3: ภาษีซื้อ 11640 พ้น 6 เดือน → reclassify เป็นค่าใช้จ่าย
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatExpiredAt" timestamp with time zone NULL;""",
            // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — type=TaxInvoice แต่พิมพ์หัวรวม
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CombinedInvoiceTaxInvoice" boolean NOT NULL DEFAULT false;""",
            // User override ผัง VAT ปลายทาง (เช่น "51000" = ลงต้นทุนขายแทน)
            // — ใช้ AccountCode (string) เพื่อ portable, validator แปลงเป็น Id ตอน post
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatAccountCodeOverride" varchar(20) NULL;
            """,
            // เงินมัดจำ/รับล่วงหน้า — ใบเสร็จที่พักรายได้ไว้ "ขายรอรับรู้" (217xx)
            // แต่รับรู้ภาษีขายทันที (tax point §78). RealizeDeposit ตัด → รายได้.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsDeposit" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRealizedAmount" numeric(18,2) NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRealizedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositDeferredAccountCode" varchar(20) NULL;
            """,
            // มัดจำ: เคสภาษีขาย — Deferred=true → 21913 (รอเรียกเก็บ, ยังไม่เข้า
            // ภ.พ.30); RecognizedAt = วันย้าย 21913→21911 (tax point เกิดจริง)
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositOutputVatDeferred" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositOutputVatRecognizedAt" timestamp with time zone NULL;
            """,

            // OpeningBalances — per-period per-account opening figures
            """
            CREATE TABLE IF NOT EXISTS "OpeningBalances" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalPeriodId" uuid NOT NULL,
                "AccountId" uuid NOT NULL,
                "OpeningDebit" numeric(18,2) NOT NULL DEFAULT 0,
                "OpeningCredit" numeric(18,2) NOT NULL DEFAULT 0,
                "DimensionId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_OpeningBalances_Lookup"
                ON "OpeningBalances" ("CompanyId", "FiscalPeriodId", "AccountId");
            """,

            // YearEndClosings — audit per closed fiscal year
            """
            CREATE TABLE IF NOT EXISTS "YearEndClosings" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalYear" int NOT NULL,
                "RetainedEarningsAccountId" uuid NOT NULL,
                "TransferredAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ClosingJournalEntryId" uuid NOT NULL,
                "LockedFiscalPeriods" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_YearEndClosings_CompanyYear"
                ON "YearEndClosings" ("CompanyId", "FiscalYear");
            """,

            // MigrationSessions + AccountMappings (Task 1 wizard)
            """
            CREATE TABLE IF NOT EXISTS "MigrationSessions" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "SessionName" varchar(200) NOT NULL,
                "MigrationType" int NOT NULL,
                "Status" int NOT NULL DEFAULT 0,
                "TargetFiscalPeriodId" uuid NULL,
                "MappingsJson" text NULL,
                "ImportSummaryJson" text NULL,
                "CompletedAt" timestamp with time zone NULL,
                "CompletedBy" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_MigrationSessions_CompanyStatus"
                ON "MigrationSessions" ("CompanyId", "Status");
            """,
            """
            CREATE TABLE IF NOT EXISTS "AccountMappings" (
                "Id" uuid PRIMARY KEY,
                "MigrationSessionId" uuid NOT NULL REFERENCES "MigrationSessions"("Id") ON DELETE CASCADE,
                "LegacyCode" varchar(50) NOT NULL,
                "LegacyName" varchar(200) NULL,
                "MappedAccountId" uuid NULL,
                "LegacyDebit" numeric(18,2) NOT NULL DEFAULT 0,
                "LegacyCredit" numeric(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_AccountMappings_Session"
                ON "AccountMappings" ("MigrationSessionId", "LegacyCode");
            """,

            // VatDeferrals (Task 4)
            """
            CREATE TABLE IF NOT EXISTS "VatDeferrals" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "OriginalTaxReportId" uuid NOT NULL,
                "DeferredAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "DeferralReason" varchar(500) NULL,
                "DeferredFromPeriod" int NOT NULL,
                "DeferredToPeriod" int NOT NULL,
                "ClaimedAt" timestamp with time zone NULL,
                "ClaimedTaxReportId" uuid NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_VatDeferrals_Target"
                ON "VatDeferrals" ("CompanyId", "DeferredToPeriod");
            """,

            // EFilingExports — RD pipe-delimited file artifacts
            """
            CREATE TABLE IF NOT EXISTS "EFilingExports" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FormType" varchar(20) NOT NULL,
                "PeriodYear" int NOT NULL,
                "PeriodMonth" int NOT NULL,
                "FileContent" text NOT NULL,
                "LineCount" int NOT NULL DEFAULT 0,
                "TotalAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "TotalTax" numeric(18,2) NOT NULL DEFAULT 0,
                "RdReferenceNumber" text NULL,
                "GeneratedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "GeneratedBy" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_EFilingExports_FormPeriod"
                ON "EFilingExports" ("CompanyId", "FormType", "PeriodYear", "PeriodMonth");
            """,

            // OcrValidationLogs (Task 5)
            """
            CREATE TABLE IF NOT EXISTS "OcrValidationLogs" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "RuleCode" varchar(50) NOT NULL,
                "IsValid" boolean NOT NULL DEFAULT false,
                "Severity" varchar(20) NOT NULL DEFAULT 'Warning',
                "Message" text NULL,
                "FieldValue" text NULL,
                "OverriddenBy" uuid NULL,
                "OverriddenAt" timestamp with time zone NULL,
                "OverrideReason" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrValidationLogs_Document"
                ON "OcrValidationLogs" ("DocumentId");
            """,

            // ===== CompanySettings: e-Tax mode + by-email registration columns =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxMode" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRdRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRegistrationDate" timestamp NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRegistrationNumber" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailSenderEmail" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRdTimestampAddress" varchar(256) NOT NULL DEFAULT 'csemail@etax.teda.th';
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailEmbedXml" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailAutoSendOnApprove" boolean NOT NULL DEFAULT false;
            """,

            // ===== CompanySettings: Email sending configuration columns =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailProvider" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailFromAddress" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailConfigured" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailLastTestedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailLastTestStatus" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpHost" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpPort" int NOT NULL DEFAULT 587;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpUsername" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpPassword" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpUseSsl" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsTenantId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsClientId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsClientSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsSenderUpn" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailClientId" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailClientSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailRefreshToken" varchar(2000) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailServiceAccountJson" text NULL;
            """,
            // OCR document-target default — 13 = DocumentType.PaymentVoucher.
            // Match the entity default so existing rows behave like the new
            // "cash-basis" default (which most Thai SMEs want).
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OcrBuyerInvoiceDefaultTarget" integer NOT NULL DEFAULT 13;
            """,

            // ===== RecurringTransactions: PreferredDay to prevent date drift on monthly schedules =====
            """
            ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "PreferredDay" integer NULL;
            """,

            // ===== RevenueContracts: ProjectId (optional link to Projects) =====
            """
            ALTER TABLE "RevenueContracts" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_RevenueContracts_ProjectId" ON "RevenueContracts" ("ProjectId");
            """,

            // ===== BankFeedImports: allow null BankConnectionId for manual file imports =====
            """
            ALTER TABLE "BankFeedImports" ALTER COLUMN "BankConnectionId" DROP NOT NULL;
            """,

            // ===== PlanTemplates: convert legacy "FreeTrial" template to permanent-free Free Edition =====
            // The Free plan was originally a 14-day trial; we now offer it as ฟรีตลอดชีพ.
            """
            UPDATE "PlanTemplates"
            SET "IsPermanentFree" = true,
                "Name" = 'Free Edition',
                "TrialDurationDays" = 36500,
                "TrialMaxExtensions" = 0
            WHERE "Plan" = 0  -- SubscriptionPlan.FreeTrial
              AND ("IsPermanentFree" IS NULL OR "IsPermanentFree" = false);
            """,
            """
            UPDATE "Subscriptions"
            SET "IsPermanentFree" = true,
                "Status" = 1,                            -- Active
                "EndDate" = "StartDate" + INTERVAL '100 years'
            WHERE "Plan" = 0                              -- SubscriptionPlan.FreeTrial
              AND ("IsPermanentFree" IS NULL OR "IsPermanentFree" = false);
            """,

            // ===== EtaxInvoices: file path columns for PDF/A-3 + XML persistence (Thai e-Tax by Email compliance) =====
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "PdfFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "XmlFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "VoidedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "VoidReason" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "PdfFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "XmlFilePath" varchar(1000) NULL;
            """,

            """
            CREATE TABLE IF NOT EXISTS "SiteSettings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ContactPhone" varchar(50) NULL,
                "ContactLine" varchar(100) NULL,
                "ContactEmail" varchar(256) NULL,
                "ServicesJson" jsonb NULL,
                "PricingSectionTitle" varchar(500) NULL,
                "PricingSectionSubtitle" varchar(1000) NULL,
                "SiteName" varchar(200) NULL,
                "SiteDescription" varchar(1000) NULL,
                "SiteLogoUrl" varchar(500) NULL,
                "FacebookUrl" varchar(500) NULL,
                "LineOfficialUrl" varchar(500) NULL,
                "WebsiteUrl" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SiteSettings" PRIMARY KEY ("Id")
            );
            """,

            // ===== SiteSettings: Extended branding & system behavior =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FaviconUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LoginBackgroundUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PrimaryColor" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "HeroTitle" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "HeroSubtitle" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FooterCopyright" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "YouTubeUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "InstagramUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "RegistrationEnabled" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "MaintenanceMode" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "MaintenanceMessage" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "DefaultLanguage" varchar(5) NOT NULL DEFAULT 'th';
            """,

            // ===== SiteSettings: System Email config (admin-managed SMTP/API) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailProvider" int NOT NULL DEFAULT 0;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailFromAddress" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailFromName" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailReplyTo" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailConfigured" boolean NOT NULL DEFAULT false;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailLastTestedAt" timestamp NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailLastTestStatus" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpHost" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpPort" int NOT NULL DEFAULT 587;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpUsername" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpPassword" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpUseSsl" boolean NOT NULL DEFAULT true;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsTenantId" varchar(100) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsClientId" varchar(100) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsClientSecret" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsSenderUpn" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailClientId" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailClientSecret" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailRefreshToken" varchar(2000) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AppBaseUrl" varchar(500) NULL;
            """,

            // ===== Documents: bank account + expense category link =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ExpenseCategoryId" uuid NULL;
            """,

            // ===== Payments: proper FK to BankAccount =====
            """
            ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;
            """,

            // ===== Documents: PaymentAccountId for non-bank money accounts =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentAccountId" uuid NULL;
            """,

            // Document template layout style — the chosen structural look.
            """
            ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "LayoutStyle" varchar(40) NOT NULL DEFAULT 'Classic';
            """,
            // ===== OcrScanResults: duplicate detection fields =====
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "FileHash" varchar(64) NULL;
            """,
            // JE-only path: tracks the Journal Entry recorded straight from a
            // scan (no business document), so we can block double-posting.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "CreatedJournalEntryId" uuid NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "RetryCount" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ContentFingerprint" varchar(64) NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrScanResults_CompanyId_ContentFingerprint"
            ON "OcrScanResults" ("CompanyId", "ContentFingerprint")
            WHERE "ContentFingerprint" IS NOT NULL;
            """,

            // ===== Performance indexes — added after audit identified hot-path query slowdowns =====
            // 60-second pre-upload duplicate check (OcrController.UploadAndScan):
            // covers (CompanyId + FileHash + CreatedAt) for the most-recent-duplicate lookup.
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrScanResults_CompanyId_FileHash_CreatedAt"
            ON "OcrScanResults" ("CompanyId", "FileHash", "CreatedAt");
            """,
            // Self-correction maintenance scans by IsNegativeExample + LastConfirmedAt:
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrLearnedPatterns_IsNegativeExample_LastConfirmedAt"
            ON "OcrLearnedPatterns" ("IsNegativeExample", "LastConfirmedAt");
            """,
            // OcrCreditPurchases SUM aggregation in GetQuotaStatusAsync filters (Status, ExpiresAt, PagesRemaining):
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCreditPurchases_Status_ExpiresAt"
            ON "OcrCreditPurchases" ("Status", "ExpiresAt") WHERE "PagesRemaining" > 0;
            """,
            // Documents.RelatedDocumentId — used for cycle detection in ConvertDocumentAsync
            // and for the "show child documents" UI in document chain rendering:
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RelatedDocumentId"
            ON "Documents" ("RelatedDocumentId") WHERE "RelatedDocumentId" IS NOT NULL;
            """,
            // Documents.RevenueContractId — used for "list invoices for contract" lookups:
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RevenueContractId"
            ON "Documents" ("RevenueContractId") WHERE "RevenueContractId" IS NOT NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "IsDuplicate" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "DuplicateOfScanId" uuid NULL;
            """,
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerName" varchar(500) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerTaxId" varchar(20) NULL;""",
            // AI GL suggestion transparency — เก็บ AI primary แม้ถูก confidence guard ปฏิเสธ
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiSuggestedCode" varchar(20) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiConfidence" numeric(5,4) NULL;""",

            // OcrScanResults: which OCR engine produced this result
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OcrEngine" varchar(40) NULL;
            """,

            // OcrScanResults: potential fixed asset detection flags (Phase 4)
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasPotentialFixedAsset" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PotentialAssetLinesJson" text NULL;
            """,
            // OcrScanResults: handwriting detection
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasHandwriting" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HandwritingConfidence" decimal(4,2) NULL;
            """,

            // ===== OcrScanResults: document role inference fields =====
            // Thai-accounting workflow: a scanned receipt from a supplier should
            // create a PaymentVoucher in our books — not a "Receipt" document.
            // These three columns let OcrDocumentRoleInferrer record the
            // separation between the paper (ScannedDocumentType), our role
            // (OurRole: Buyer/Seller), and the target doc to create
            // (TargetDocumentType).
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ScannedDocumentType" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OurRole" varchar(20) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "TargetDocumentType" varchar(50) NULL;
            """,

            // ===== OcrScanResults: expense/account suggestion fields =====
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExpenseCategory" text NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedAccountsJson" text NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasWht" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "WhtRate" numeric(5,2) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PaymentTermsDays" integer NULL;
            """,

            // External-system metadata payload (project/order info uploaded
            // alongside the file) — drives auto project allocation per line.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExternalMetadataJson" text NULL;
            """,
            // Business-flow hints computed at scan time: suggested entry mode
            // (Stock/Expense) + open POs of the matched vendor.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedEntryMode" varchar(20) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OpenPoNumbersJson" text NULL;
            """,
            // PO linkage (the "ฟังก์ชันชื่อแทน / รับตาม PO" function): which PO
            // the operator linked this scan to + per-line OCR↔PO mappings.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPurchaseOrderId" uuid NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPurchaseOrderNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PoLineMappingsJson" text NULL;
            """,
            // Header discount read off the paper (raw-text enrichment).
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExtractedDiscountAmount" numeric(18,2) NULL;
            """,

            // ===== SsoYearConfigs: per-company-per-year SSO ceiling/rate
            // override (default schedule lives in Helpers.SsoRateSchedule:
            // 15,000 → 17,500 (2026) → 20,000 (2029) → 23,000 (2032)) =====
            """
            CREATE TABLE IF NOT EXISTS "SsoYearConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Year" integer NOT NULL,
                "WageCeiling" numeric(18,2) NOT NULL,
                "RatePercent" numeric(5,2) NOT NULL DEFAULT 5,
                "EmployerRatePercent" numeric(5,2) NOT NULL DEFAULT 5,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SsoYearConfigs" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SsoYearConfigs_Company_Year"
            ON "SsoYearConfigs" ("CompanyId", "Year") WHERE "IsDeleted" = false;
            """,

            // ===== OcrLearnedPatterns: zone analyzer learning =====
            """
            CREATE TABLE IF NOT EXISTS "OcrLearnedPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorTaxId" varchar(13) NULL,
                "FieldName" varchar(50) NOT NULL,
                "ContextKeyword" varchar(200) NOT NULL,
                "ExtractionRegex" text NULL,
                "SearchRadius" integer NOT NULL DEFAULT 300,
                "TimesConfirmed" integer NOT NULL DEFAULT 1,
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrLearnedPatterns" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_OcrLearnedPatterns_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrLearnedPatterns_CompanyId_VendorTaxId_FieldName"
            ON "OcrLearnedPatterns" ("CompanyId", "VendorTaxId", "FieldName");
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "IsNegativeExample" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "NegativeValue" text NULL;
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "FailureCount" integer NOT NULL DEFAULT 0;
            """,

            // ===== VendorKnownGoodValues: Azure-DI-sourced canonical field values =====
            // Populated whenever Azure DI extracts a high-confidence field
            // for a recognized vendor. The Tier-2/3 local OCR cascade
            // fuzzy-matches its own noisy output against these values
            // and substitutes the canonical version when similarity ≥ 0.80.
            """
            CREATE TABLE IF NOT EXISTS "VendorKnownGoodValues" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorTaxId" varchar(13) NULL,
                "FieldName" varchar(50) NOT NULL,
                "Value" text NOT NULL,
                "Confidence" numeric(5,4) NOT NULL DEFAULT 0,
                "ConfirmedCount" integer NOT NULL DEFAULT 1,
                "Source" varchar(20) NOT NULL DEFAULT 'AzureDI',
                "LastSeenAt" timestamp NOT NULL DEFAULT now(),
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VendorKnownGoodValues" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_VendorKnownGoodValues_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_VendorKnownGoodValues_CompanyId_VendorTaxId_FieldName"
            ON "VendorKnownGoodValues" ("CompanyId", "VendorTaxId", "FieldName");
            """,

            // ===== Sites: CMS columns added after initial table creation =====
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CaptchaProvider" varchar(50) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CaptchaSiteKey" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CookieConsentEnabled" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxStorageBytes" bigint NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxBandwidthBytesPerMonth" bigint NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxProducts" integer NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxPages" integer NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CurrentStorageUsed" bigint NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CurrentBandwidthUsed" bigint NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "BandwidthResetDate" timestamp NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "OgImageUrl" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CanonicalUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RobotsDirective" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CustomHeadScripts" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CustomBodyScripts" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaTitle" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaDescription" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaKeywords" text NULL;
            """,

            // ===== SiteSettings: Azure Document Intelligence + OCR config (system-wide) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiEndpoint" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiApiKey" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiModelId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiApiVersion" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiEnabled" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiLastTestedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiLastTestStatus" text NULL;
            """,
            // Tier-aware throttle. Default 1 / 1500ms is safe for F0
            // (1 TPS analyze, 1 TPS poll). Paid S0 admins bump in UI.
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiMaxConcurrentSubmits" integer NOT NULL DEFAULT 1;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiPollIntervalMs" integer NOT NULL DEFAULT 1500;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrProvider" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrLocalServiceUrl" text NULL;
            """,
            // OcrGoogleApiKey + OcrTesseractApiKey columns are intentionally not created
            // for new installs — those providers were removed in favor of Azure DI v4 +
            // Local (PaddleOCR+EasyOCR). Existing installs keep the columns harmlessly;
            // a future cleanup migration can DROP them when no rows reference them.
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrAutoCreateThreshold" decimal(5,2) NOT NULL DEFAULT 0.85;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesTrial" integer NOT NULL DEFAULT 10;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesBasic" integer NOT NULL DEFAULT 50;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesPro" integer NOT NULL DEFAULT 500;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesEnterprise" integer NOT NULL DEFAULT 5000;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrCreditPricePerPage" decimal(10,2) NOT NULL DEFAULT 2.00;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrCreditMinPurchase" integer NOT NULL DEFAULT 100;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LastOcrMaintenanceAt" timestamp NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMaxPenalty" decimal(5,2) NOT NULL DEFAULT 0.60;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMathTolerance" decimal(10,2) NOT NULL DEFAULT 2.0;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayTaxIdPenalty" decimal(5,2) NOT NULL DEFAULT 0.15;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMathPenalty" decimal(5,2) NOT NULL DEFAULT 0.20;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayDatePenalty" decimal(5,2) NOT NULL DEFAULT 0.15;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayVatRatePenalty" decimal(5,2) NOT NULL DEFAULT 0.10;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayLowConfidencePenalty" decimal(5,2) NOT NULL DEFAULT 0.05;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrMaxRetriesPerScan" integer NOT NULL DEFAULT 1;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrMaxPagesPerScan" integer NULL DEFAULT 10;
            """,

            // ===== Subscriptions: OCR quota fields =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "OcrBonusPages" integer NOT NULL DEFAULT 0;
            """,
            // Per-engine OCR quotas — split Azure DI vs local OCR
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthAzureOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthLocalOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "OcrBonusExpiresAt" timestamp NULL;
            """,

            // ===== PlanTemplates: OCR quota fields =====
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,
            // PlanTemplate entity defines these per-engine columns but they
            // were only migrated on Subscriptions / SubscriptionPlans —
            // /api/admin/plans 500'd because EF SELECT *'d the missing
            // columns from PlanTemplates.
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "Currency" text NOT NULL DEFAULT 'THB';
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxUsers" integer NOT NULL DEFAULT 2;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxDocumentsPerMonth" integer NOT NULL DEFAULT 20;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxJournalEntriesPerMonth" integer NOT NULL DEFAULT 50;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialBlockOnExpiry" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialGracePeriodDays" integer NOT NULL DEFAULT 7;
            """,

            // ===== TrialConfigs: OCR quota fields =====
            """
            ALTER TABLE "TrialConfigs" ADD COLUMN IF NOT EXISTS "TrialMaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,

            // ===== OcrCategoryMappings: learned vendor → expense-account mappings =====
            """
            CREATE TABLE IF NOT EXISTS "OcrCategoryMappings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "DescriptionKeyword" varchar(200) NOT NULL DEFAULT '',
                "AccountCode" varchar(20) NOT NULL DEFAULT '',
                "AccountName" text NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "TrainedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrCategoryMappings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCategoryMappings_CompanyId_VendorKey_Description"
            ON "OcrCategoryMappings" ("CompanyId", "VendorKey", "DescriptionKeyword");
            """,

            // ===== OcrVendorIntelligence: per-vendor aggregated stats for self-learning =====
            // One row per vendor per company. Updated each time a Document is approved.
            // Read on every OCR scan to suggest DocumentType / debit account / WHT rate.
            """
            CREATE TABLE IF NOT EXISTS "OcrVendorIntelligence" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "VendorName" varchar(300) NULL,
                "VendorTaxId" varchar(20) NULL,
                "MostCommonDocumentType" varchar(50) NULL,
                "MostCommonDocumentTypeCount" integer NOT NULL DEFAULT 0,
                "TotalDocuments" integer NOT NULL DEFAULT 0,
                "DocumentTypeBreakdownJson" text NULL,
                "MostCommonDebitAccountCode" varchar(20) NULL,
                "MostCommonDebitAccountName" text NULL,
                "MostCommonDebitAccountCount" integer NOT NULL DEFAULT 0,
                "DebitAccountBreakdownJson" text NULL,
                "TypicallyHasWht" boolean NOT NULL DEFAULT false,
                "TypicalWhtRate" decimal(5,2) NULL,
                "WhtUsageCount" integer NOT NULL DEFAULT 0,
                "AvgTotalAmount" decimal(18,2) NULL,
                "MinTotalAmount" decimal(18,2) NULL,
                "MaxTotalAmount" decimal(18,2) NULL,
                "MedianTotalAmount" decimal(18,2) NULL,
                "TypicalPaymentTermsDays" integer NULL,
                "LastTrainedAt" timestamp NOT NULL DEFAULT now(),
                "LastDocumentDate" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrVendorIntelligence" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_OcrVendorIntelligence_CompanyId_VendorKey"
            ON "OcrVendorIntelligence" ("CompanyId", "VendorKey");
            """,

            // OcrVendorIntelligence: learned-pattern columns for OCR boosting
            // (added after the table existed for some tenants — must be
            // idempotent ALTER, not part of the original CREATE TABLE).
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TypicalDocNumberPrefix" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TopLineKeywordsJson" text NULL;
            """,
            // z-score anomaly detection needs running log-amount mean/variance
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "LogAmountMean" decimal(10,4) NULL;
            """,
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "LogAmountVariance" decimal(10,4) NULL;
            """,

            // ===== SystemOcrCategoryMappings: system-wide vendor → account knowledge =====
            // Mirrors OcrCategoryMappings but without CompanyId. Trained by SystemAdmin
            // from /admin/ocr-config; consulted as fallback when tenant has no row.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrCategoryMappings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "DescriptionKeyword" varchar(200) NOT NULL DEFAULT '',
                "AccountCode" varchar(20) NOT NULL DEFAULT '',
                "AccountName" text NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "TrainedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrCategoryMappings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemOcrCategoryMappings_VendorKey_Description"
            ON "SystemOcrCategoryMappings" ("VendorKey", "DescriptionKeyword");
            """,

            // ===== SystemOcrVendorIntelligence: system-wide per-vendor stats =====
            // One row per VendorKey for the whole system; tenant rows always win at
            // prediction time, this is consulted as fallback for unseen vendors.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrVendorIntelligence" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "VendorName" varchar(300) NULL,
                "VendorTaxId" varchar(20) NULL,
                "MostCommonDocumentType" varchar(50) NULL,
                "MostCommonDocumentTypeCount" integer NOT NULL DEFAULT 0,
                "TotalDocuments" integer NOT NULL DEFAULT 0,
                "DocumentTypeBreakdownJson" text NULL,
                "MostCommonDebitAccountCode" varchar(20) NULL,
                "MostCommonDebitAccountName" text NULL,
                "MostCommonDebitAccountCount" integer NOT NULL DEFAULT 0,
                "DebitAccountBreakdownJson" text NULL,
                "TypicallyHasWht" boolean NOT NULL DEFAULT false,
                "TypicalWhtRate" decimal(5,2) NULL,
                "WhtUsageCount" integer NOT NULL DEFAULT 0,
                "AvgTotalAmount" decimal(18,2) NULL,
                "MinTotalAmount" decimal(18,2) NULL,
                "MaxTotalAmount" decimal(18,2) NULL,
                "MedianTotalAmount" decimal(18,2) NULL,
                "TypicalPaymentTermsDays" integer NULL,
                "LastTrainedAt" timestamp NOT NULL DEFAULT now(),
                "LastDocumentDate" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrVendorIntelligence" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SystemOcrVendorIntelligence_VendorKey"
            ON "SystemOcrVendorIntelligence" ("VendorKey");
            """,

            // Industry-similarity weighting: track which industries
            // contributed to each cross-tenant aggregate so predict-time
            // consumers can boost rows whose contributing tenants share
            // their IndustryType.
            """
            ALTER TABLE "SystemOcrCategoryMappings" ADD COLUMN IF NOT EXISTS "IndustryBreakdownJson" text NULL;
            """,
            """
            ALTER TABLE "SystemOcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "IndustryBreakdownJson" text NULL;
            """,

            // ===== SystemOcrAssociationRules: basket-analysis output =====
            // Discovered association rules from system-wide Apriori mining over
            // approved-document transactions. Refreshed by an admin-triggered
            // background job, not per-scan.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrAssociationRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "AntecedentJson" text NOT NULL DEFAULT '[]',
                "Consequent" varchar(100) NOT NULL DEFAULT '',
                "Support" decimal(8,6) NOT NULL DEFAULT 0,
                "Confidence" decimal(8,6) NOT NULL DEFAULT 0,
                "Lift" decimal(10,4) NOT NULL DEFAULT 0,
                "TransactionCount" integer NOT NULL DEFAULT 0,
                "MinedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrAssociationRules" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemOcrAssociationRules_Consequent"
            ON "SystemOcrAssociationRules" ("Consequent");
            """,

            // ===== TradingPartnerships: cross-tenant B2B handshake =====
            // Single row per ordered (CompanyA, CompanyB) pair; both sides
            // must accept before any cross-tenant document flow is allowed.
            """
            CREATE TABLE IF NOT EXISTS "TradingPartnerships" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyAId" uuid NOT NULL,
                "CompanyBId" uuid NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "InvitedByCompanyId" uuid NOT NULL,
                "InvitedByUserId" uuid NOT NULL,
                "InvitationMessage" text NULL,
                "AcceptedAt" timestamp NULL,
                "AcceptedByUserId" uuid NULL,
                "RejectedAt" timestamp NULL,
                "RejectionReason" text NULL,
                "AutoApproveAmountLimit" decimal(18,2) NULL,
                "NotifyEmail" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_TradingPartnerships" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_TradingPartnerships_AB"
            ON "TradingPartnerships" ("CompanyAId", "CompanyBId");
            """,

            // ===== CrossTenantDocumentLinks: per-step routing log =====
            // One row per document flow event (Quotation→approval, PO→supplier,
            // Invoice→buyer, ...). SnapshotJson preserves the source doc state
            // at the moment of approval for immutable audit.
            """
            CREATE TABLE IF NOT EXISTS "CrossTenantDocumentLinks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TradingPartnershipId" uuid NOT NULL,
                "SourceCompanyId" uuid NOT NULL,
                "SourceDocumentId" uuid NOT NULL,
                "TargetCompanyId" uuid NOT NULL,
                "TargetDocumentId" uuid NULL,
                "LinkType" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "ApproverUserId" uuid NULL,
                "ApprovedAt" timestamp NULL,
                "DocumentApprovalId" uuid NULL,
                "RejectionReason" text NULL,
                "SnapshotJson" text NULL,
                "Comment" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CrossTenantDocumentLinks" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_Target_Status"
            ON "CrossTenantDocumentLinks" ("TargetCompanyId", "Status");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_Source_Status"
            ON "CrossTenantDocumentLinks" ("SourceCompanyId", "Status");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_SourceDoc"
            ON "CrossTenantDocumentLinks" ("SourceDocumentId");
            """,

            // ===== WorkflowAutomationConfigs: per-company toggles =====
            """
            CREATE TABLE IF NOT EXISTS "WorkflowAutomationConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "AutoApproveIncomingQuotations" boolean NOT NULL DEFAULT false,
                "AutoApproveMinAmount" decimal(18,2) NULL,
                "AutoApproveMaxAmount" decimal(18,2) NULL,
                "AutoCreatePoOnQuotationApproval" boolean NOT NULL DEFAULT false,
                "AutoCreateInvoiceFromIncomingPo" boolean NOT NULL DEFAULT false,
                "AutoCreateReceiptFromIncomingPayment" boolean NOT NULL DEFAULT false,
                "AutoStampSignature" boolean NOT NULL DEFAULT false,
                "DefaultApproverUserId" uuid NULL,
                "DefaultSignatureId" uuid NULL,
                "NotifyOnIncomingDocument" boolean NOT NULL DEFAULT true,
                "NotifyEmail" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_WorkflowAutomationConfigs" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkflowAutomationConfigs_CompanyId"
            ON "WorkflowAutomationConfigs" ("CompanyId");
            """,

            // ===== OcrCreditPurchases table =====
            """
            CREATE TABLE IF NOT EXISTS "OcrCreditPurchases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "SubscriptionId" uuid NOT NULL,
                "PagesPurchased" integer NOT NULL DEFAULT 0,
                "PagesRemaining" integer NOT NULL DEFAULT 0,
                "AmountPaid" decimal(18,2) NOT NULL DEFAULT 0,
                "Currency" varchar(3) NOT NULL DEFAULT 'THB',
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "PaymentReference" text NULL,
                "SlipFileName" text NULL,
                "SlipStoragePath" text NULL,
                "ReviewedByUserId" uuid NULL,
                "ReviewedAt" timestamp NULL,
                "ReviewNotes" text NULL,
                "ExpiresAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrCreditPurchases" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCreditPurchases_CompanyId_SubscriptionId"
            ON "OcrCreditPurchases" ("CompanyId", "SubscriptionId");
            """,

            // ===== Custom Roles & Per-Menu Permissions (per-company RBAC) =====
            // Each company can define its own roles and assign per-menu access.
            // CompanyUsers.CompanyRoleId is nullable so existing members default
            // to "no custom role" (full access — backward compatible).
            """
            CREATE TABLE IF NOT EXISTS "CompanyRoles" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Name" varchar(100) NOT NULL,
                "Description" text NULL,
                "Color" varchar(20) NOT NULL DEFAULT '#6B7280',
                "Icon" varchar(10) NOT NULL DEFAULT '👤',
                "IsSystemRole" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CompanyRoles" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyRoles_CompanyId_Name"
            ON "CompanyRoles" ("CompanyId", "Name");
            """,
            """
            CREATE TABLE IF NOT EXISTS "CompanyRolePermissions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyRoleId" uuid NOT NULL,
                "MenuItemId" varchar(100) NOT NULL,
                "CanAccess" boolean NOT NULL DEFAULT true,
                CONSTRAINT "PK_CompanyRolePermissions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CompanyRolePermissions_CompanyRoles" FOREIGN KEY ("CompanyRoleId")
                    REFERENCES "CompanyRoles"("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyRolePermissions_CompanyRoleId_MenuItemId"
            ON "CompanyRolePermissions" ("CompanyRoleId", "MenuItemId");
            """,
            // Add CompanyRoleId to existing CompanyUsers — nullable so old rows stay valid.
            """
            ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "CompanyRoleId" uuid NULL;
            """,

            // ===== Documents: CertificateInLieu (ใบรับรองแทนใบเสร็จ) fields =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertificateReason" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertifierName" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertifierPosition" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WitnessName" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WitnessPosition" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentDate" timestamp NULL;
            """,
            // Product gallery images (JSON array of /uploads/products/... URLs).
            """
            ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "ImageUrlsJson" text NULL;
            """,

            // Asset type discriminator — broadens the module from PPE-only to
            // cover Intangible / RightOfUse / InvestmentProperty too.
            // Default 1 = Tangible so existing rows behave unchanged.
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "SourceDocumentId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "SourceDocumentLineId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "NeedsReview" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "AssetType" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LeaseTermMonths" integer NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LessorName" varchar(200) NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "MonthlyLeasePayment" numeric(18,2) NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LeaseLiabilityAccountId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_FixedAssets_AssetType" ON "FixedAssets" ("CompanyId", "AssetType") WHERE "IsDeleted" = false;""",

            // POS: tip + coupon columns
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "TipAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "CouponCode" varchar(50) NULL;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "CouponDiscountAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // Loyalty points on Contact (per-tenant; reset never).
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LoyaltyPoints" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LastVisitAt" timestamp NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TotalVisitCount" integer NOT NULL DEFAULT 0;""",

            // Multi-printer routing — Product.PrintStation routes kitchen
            // tickets per item ("Kitchen-Hot" / "Kitchen-Cold" / "Bar" / "Drinks").
            // Null = goes to default cashier printer only.
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "PrintStation" varchar(50) NULL;""",

            // Sensitivity classification: 0=None / 1=Payroll / 2=ExecutivePay / 3=HrPersonal / 9=Confidential.
            // Documents (payroll vouchers) and JEs (auto-generated payroll JEs) stamp
            // this so SensitivityService can redact for users lacking the matching role.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Sensitivity" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Sensitivity" integer NOT NULL DEFAULT 0;
            """,
            // Per-company allow-list — one row per (Company, Kind, Role).
            """
            CREATE TABLE IF NOT EXISTS "SensitivityAccessRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Kind" integer NOT NULL,
                "Role" integer NOT NULL,
                "CanView" boolean NOT NULL DEFAULT true,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SensitivityAccessRules" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_SensitivityAccessRules_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SensitivityAccessRules_Company_Kind_Role" ON "SensitivityAccessRules" ("CompanyId", "Kind", "Role") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Sensitivity" ON "Documents" ("CompanyId", "Sensitivity") WHERE "Sensitivity" > 0 AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_JournalEntries_Sensitivity" ON "JournalEntries" ("CompanyId", "Sensitivity") WHERE "Sensitivity" > 0 AND "IsDeleted" = false;""",

            // VAT filing history (ภ.พ.30 ย้อนหลังที่ยื่นในระบบเดิม).
            // One row per (Company, Year, Month) keeps it simple — upsert semantics.
            """
            CREATE TABLE IF NOT EXISTS "VatFilingHistories" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Year" integer NOT NULL,
                "Month" integer NOT NULL,
                "SalesTotal" numeric(18,2) NOT NULL DEFAULT 0,
                "OutputVat" numeric(18,2) NOT NULL DEFAULT 0,
                "PurchaseTotal" numeric(18,2) NOT NULL DEFAULT 0,
                "InputVat" numeric(18,2) NOT NULL DEFAULT 0,
                "NetPayable" numeric(18,2) NOT NULL DEFAULT 0,
                "IsFiled" boolean NOT NULL DEFAULT true,
                "FiledAt" timestamp NULL,
                "FilingReference" varchar(100) NULL,
                "Notes" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VatFilingHistories" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_VatFilingHistories_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_VatFilingHistories_Company_Year_Month" ON "VatFilingHistories" ("CompanyId", "Year", "Month") WHERE "IsDeleted" = false;""",

            // Visual floor plan + tables for POS.
            """
            CREATE TABLE IF NOT EXISTS "PosFloorPlans" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(100) NOT NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CanvasWidth" integer NOT NULL DEFAULT 1200,
                "CanvasHeight" integer NOT NULL DEFAULT 800,
                "BackgroundImageUrl" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosFloorPlans" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosFloorPlans_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosTables" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FloorPlanId" uuid NOT NULL,
                "TableNumber" varchar(50) NOT NULL,
                "Seats" integer NOT NULL DEFAULT 4,
                "Shape" varchar(20) NOT NULL DEFAULT 'rectangle',
                "X" integer NOT NULL DEFAULT 0,
                "Y" integer NOT NULL DEFAULT 0,
                "Width" integer NOT NULL DEFAULT 100,
                "Height" integer NOT NULL DEFAULT 80,
                "Rotation" integer NOT NULL DEFAULT 0,
                "Color" varchar(20) NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosTables" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosTables_FloorPlan" FOREIGN KEY ("FloorPlanId") REFERENCES "PosFloorPlans"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PosTables_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PosTables_FloorPlan" ON "PosTables" ("FloorPlanId") WHERE "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosTables_Floor_Number" ON "PosTables" ("FloorPlanId", "TableNumber") WHERE "IsDeleted" = false;""",

            // Reservations.
            """
            CREATE TABLE IF NOT EXISTS "PosReservations" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TableId" uuid NULL,
                "TableNumber" varchar(50) NULL,
                "ContactId" uuid NULL,
                "CustomerName" varchar(200) NOT NULL,
                "Phone" varchar(30) NULL,
                "Email" varchar(200) NULL,
                "PartySize" integer NOT NULL DEFAULT 2,
                "ReservedAt" timestamp NOT NULL,
                "DurationMinutes" integer NOT NULL DEFAULT 90,
                "Status" integer NOT NULL DEFAULT 0,
                "PosOrderId" uuid NULL,
                "Notes" text NULL,
                "Source" varchar(50) NULL,
                "ReminderCount" integer NOT NULL DEFAULT 0,
                "LastReminderAt" timestamp NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosReservations" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosReservations_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PosReservations_Table" FOREIGN KEY ("TableId") REFERENCES "PosTables"("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_PosReservations_Contact" FOREIGN KEY ("ContactId") REFERENCES "Contacts"("Id") ON DELETE SET NULL
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PosReservations_Company_Date" ON "PosReservations" ("CompanyId", "ReservedAt") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PosReservations_Table_Date" ON "PosReservations" ("TableId", "ReservedAt") WHERE "IsDeleted" = false AND "TableId" IS NOT NULL;""",
            // Cancellation + deposit columns.
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "FreeCancelHoursBefore" integer NOT NULL DEFAULT 24;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositPaid" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "LateCancelRefundPercent" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositPaidAt" timestamp NULL;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositReference" varchar(200) NULL;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "PublicToken" varchar(64) NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosReservations_PublicToken" ON "PosReservations" ("PublicToken") WHERE "PublicToken" IS NOT NULL AND "IsDeleted" = false;""",

            // Account-level subscription — one paying User covers N Companies.
            // Resolves the "freelancer accountant pays once for 10 client books"
            // and "holding owner runs 5 sub-companies on one plan" cases.
            """
            CREATE TABLE IF NOT EXISTS "AccountSubscriptions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OwnerUserId" uuid NOT NULL,
                "PlanTemplateId" uuid NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "StartDate" timestamp NOT NULL DEFAULT now(),
                "EndDate" timestamp NOT NULL,
                "MaxCompanies" integer NOT NULL DEFAULT 1,
                "MaxUsersPerCompany" integer NOT NULL DEFAULT 1,
                "MaxDocumentsPerMonth" integer NOT NULL DEFAULT 30,
                "MaxJournalEntriesPerMonth" integer NOT NULL DEFAULT 50,
                "MaxStorageBytes" bigint NOT NULL DEFAULT 104857600,
                "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 0,
                "AzureOcrPagesPerMonth" integer NULL,
                "LocalOcrPagesPerMonth" integer NULL,
                "EnabledFeatures" bigint NOT NULL DEFAULT 0,
                "MonthlyPrice" numeric(18,2) NOT NULL DEFAULT 0,
                "AnnualPrice" numeric(18,2) NOT NULL DEFAULT 0,
                "BillingCycle" integer NOT NULL DEFAULT 0,
                "LastPaidAt" timestamp NULL,
                "GracePeriodDays" integer NOT NULL DEFAULT 7,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AccountSubscriptions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_AccountSubscriptions_Users" FOREIGN KEY ("OwnerUserId") REFERENCES "Users"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AccountSubscriptions_Plan" FOREIGN KEY ("PlanTemplateId") REFERENCES "PlanTemplates"("Id")
            );
            """,
            // Only one ACTIVE account-plan per user. Trial(0) / Active(1) /
            // PastDue(2) / Suspended(5) are non-terminal — Expired(4) and
            // Cancelled(3) free the slot so the user can subscribe again.
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccountSubscriptions_Owner_Active" ON "AccountSubscriptions" ("OwnerUserId") WHERE "Status" IN (0,1,2,5) AND "IsDeleted" = false;""",
            // Subscription gets the AccountSubscriptionId opt-in column.
            """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "AccountSubscriptionId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Subscriptions_AccountSub" ON "Subscriptions" ("AccountSubscriptionId") WHERE "AccountSubscriptionId" IS NOT NULL AND "IsDeleted" = false;""",
            // History rows from cascaded actions point at both layers.
            """ALTER TABLE "SubscriptionHistories" ADD COLUMN IF NOT EXISTS "AccountSubscriptionId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_SubscriptionHistories_AccountSub" ON "SubscriptionHistories" ("AccountSubscriptionId") WHERE "AccountSubscriptionId" IS NOT NULL;""",
            // Reminder bookkeeping on AccountSubscription so the daily job
            // doesn't double-send.
            """ALTER TABLE "AccountSubscriptions" ADD COLUMN IF NOT EXISTS "LastExpiryReminderAt" timestamp NULL;""",
            """ALTER TABLE "AccountSubscriptions" ADD COLUMN IF NOT EXISTS "ExpiryRemindersSentMask" integer NOT NULL DEFAULT 0;""",
            // หมู่ที่ on Contact addresses — needed for provincial / rural
            // tenants where building numbers alone don't identify a property.
            // ETDA e-Tax schema doesn't have a dedicated element so it gets
            // folded into the composed free-text Address by ComposeAddress.
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "Moo" text NULL;""",

            // CompanyInvitation — lets Owners invite people whose email
            // isn't on the platform yet. The invitee gets an email link
            // that doubles as a signup shortcut: signing up with the token
            // auto-joins the company. Existing users open the same link,
            // sign in, and accept.
            """
            CREATE TABLE IF NOT EXISTS "CompanyInvitations" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Email" text NOT NULL,
                "Role" integer NOT NULL DEFAULT 5,
                "InvitedByUserId" uuid NOT NULL,
                "Token" text NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "ExpiresAt" timestamp NOT NULL,
                "AcceptedAt" timestamp NULL,
                "AcceptedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CompanyInvitations" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CompanyInvitations_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CompanyInvitations_InvitedBy" FOREIGN KEY ("InvitedByUserId") REFERENCES "Users"("Id") ON DELETE RESTRICT
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyInvitations_Token" ON "CompanyInvitations" ("Token") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CompanyInvitations_Company_Email" ON "CompanyInvitations" ("CompanyId", "Email") WHERE "IsDeleted" = false;""",
            // Mirror on Company so seller addresses on e-Tax XML don't drop
            // Moo when the tenant's own office is provincial / rural.
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Moo" text NULL;""",

            // CreditNoteReason — required for new CreditNotes; existing rows
            // get NULL (silent grandfather; UI badges them "ไม่ระบุเหตุผล").
            // 1=Return (restocks), 2=Discount, 3=Adjustment, 4=Writeoff.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CreditNoteReason" integer NULL;""",

            // Payment.OverrideBankAccountId — when populated, settlement GL
            // hits this bank instead of doc.BankAccountId. Lets operators
            // record "Invoice was for Bangkok Bank but cheque cleared via
            // Kasikorn" without editing the original document.
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "OverrideBankAccountId" uuid NULL;""",

            // Payment.WithholdingTaxAmount — per-installment WHT, required
            // under cash-basis WHT (§50/§52) when the customer withholds
            // proportionally on each partial payment. Existing rows default
            // to 0 (pre-cash-basis world; the source invoice already booked
            // WHT-Asset upfront so per-payment WHT is irrelevant for them).
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "WithholdingTaxAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // CompanySettings.WhtRecognitionBasis — 1=Cash (legal default
            // per §50/§52), 2=Accrual (existing SMB practice). Existing
            // tenants need to stay on Accrual to keep their historical GL
            // consistent, so they get 2; new tenants will be created with 1.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WhtRecognitionBasis" integer NOT NULL DEFAULT 2;""",

            // EF Core convention auto-created a shadow "OwnerId" FK column +
            // FK constraint because AccountSubscription.Owner nav wasn't bound
            // to OwnerUserId in OnModelCreating. The shadow column defaults to
            // Guid.Empty on insert and trips the FK — every "create License"
            // call blew up with FK_AccountSubscriptions_Users_OwnerId. We've
            // since added explicit fluent config; this cleans up the orphan
            // on existing databases.
            """ALTER TABLE "AccountSubscriptions" DROP CONSTRAINT IF EXISTS "FK_AccountSubscriptions_Users_OwnerId";""",
            """ALTER TABLE "AccountSubscriptions" DROP COLUMN IF EXISTS "OwnerId";""",
            // OCR self-learning idempotency watermark — set when VendorIntelligenceService
            // counts this document into the per-vendor stats; prevents double-counting on
            // re-approval (Draft → Approved → Rejected → Draft → Approved).
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrIntelTrainedAt" timestamp NULL;
            """,
            // WHT cert dismissal — operator skips a doc from the "waiting to
            // issue WHT cert" list without deleting the source document.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtCertSkipped" boolean NOT NULL DEFAULT false;
            """,
            // External preparer signature override — name + signature image
            // (base64/data-URI) supplied by an integrating system for the
            // "ผู้จัดทำ" slot when the preparer is not a NextAcc User.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PreparerName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PreparerSignatureBase64" text NULL;
            """,
            // Per-payment GL funding override — pay from เงินทดรองกรรมการ /
            // เงินสดย่อย / clearing instead of the default cash/bank GL.
            """
            ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "OverridePaymentAccountId" uuid NULL;
            """,
            // Integration → NextAcc user mapping: attribute integration actions
            // (and creator signatures) to the real operator the partner sends.
            """
            CREATE TABLE IF NOT EXISTS "IntegrationUserMappings" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "IntegrationId" uuid NOT NULL,
                "ExternalUserKey" varchar(256) NOT NULL,
                "UserId" uuid NOT NULL,
                "ExternalUserName" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_IntegrationUserMappings_Integration_Key"
                ON "IntegrationUserMappings" ("IntegrationId", "ExternalUserKey");
            """,
            // ===== Bank reconciliation: rejected-match memory + audit log =====
            // New tables; CREATE IF NOT EXISTS so existing DBs gain them on
            // first startup after deploy (the GenerateCreateScript path also
            // creates them on a brand-new DB, but this guarantees it for an
            // existing one without an EF migration).
            """
            CREATE TABLE IF NOT EXISTS "BankMatchExclusions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankTransactionId" uuid NOT NULL,
                "CandidateId" uuid NOT NULL,
                "CandidateType" varchar(32) NOT NULL DEFAULT '',
                "RejectedAt" timestamp NOT NULL DEFAULT now(),
                "RejectionReason" text NULL,
                "RejectedByUserId" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankMatchExclusions_Lookup"
                ON "BankMatchExclusions" ("CompanyId", "BankTransactionId", "CandidateId");
            """,
            """
            CREATE TABLE IF NOT EXISTS "BankMatchAuditLogs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankTransactionId" uuid NOT NULL,
                "AppliedByUserId" uuid NOT NULL,
                "AppliedAt" timestamp NOT NULL DEFAULT now(),
                "ConfidenceAtApply" numeric(5,4) NOT NULL DEFAULT 0,
                "OutcomeJson" text NULL,
                "AlternativesJson" text NULL,
                "WasAiValidated" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankMatchAuditLog_Lookup"
                ON "BankMatchAuditLogs" ("CompanyId", "BankTransactionId");
            """,
        ];
    }

    /// <summary>
    /// Creates PostgreSQL extensions and GIN indexes for full-text search and trigram matching.
    /// This dramatically improves LIKE '%term%' and text search performance on hot search paths.
    /// </summary>
    public static void ApplyFullTextSearchIndexes(AccountingDbContext db)
    {
        var statements = new[]
        {
            // Enable pg_trgm for trigram-based LIKE/ILIKE optimization
            """CREATE EXTENSION IF NOT EXISTS pg_trgm;""",
            // Enable unaccent for accent-insensitive search (useful for Thai + multilingual)
            """CREATE EXTENSION IF NOT EXISTS unaccent;""",

            // === Documents: searched by DocumentNumber + Contact name ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_DocumentNumber_trgm"
            ON "Documents" USING gin ("DocumentNumber" gin_trgm_ops);
            """,

            // === Contacts: searched by Name, TaxId ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_Name_trgm"
            ON "Contacts" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_TaxId_trgm"
            ON "Contacts" USING gin ("TaxId" gin_trgm_ops);
            """,

            // === Products: searched by Name, Code ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Products_Name_trgm"
            ON "Products" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Products_Code_trgm"
            ON "Products" USING gin ("Code" gin_trgm_ops);
            """,

            // === ChartOfAccounts: searched by AccountCode, AccountName ===
            """
            CREATE INDEX IF NOT EXISTS "IX_ChartOfAccounts_AccountName_trgm"
            ON "ChartOfAccounts" USING gin ("AccountName" gin_trgm_ops);
            """,

            // === FixedAssets: searched by Name, AssetCode ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'FixedAssets') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_FixedAssets_Name_trgm" ON "FixedAssets" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Loans: searched by LoanNumber, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Loans') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Loans_Name_trgm" ON "Loans" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Projects: searched by Code, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Projects') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Projects_Name_trgm" ON "Projects" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Employees: searched by EmployeeCode, FirstNameTh, LastNameTh, FirstNameEn ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Employees') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Employees_Names_trgm" ON "Employees" USING gin (("EmployeeCode" || '' '' || COALESCE("FirstNameTh",'''') || '' '' || COALESCE("LastNameTh",'''') || '' '' || COALESCE("FirstNameEn",'''')) gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // ===== CompanySettings: show GL posting summary (Dr/Cr) on printed documents =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShowGlEntryOnDocument" boolean NOT NULL DEFAULT false;
            """,

            // ===== Employees: accounting payee link (mirror employee as a Contact) =====
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_ContactId" ON "Employees" ("ContactId");
            """,

            // ===== EmployeeLeaves: rejection reason for the reject workflow =====
            """
            ALTER TABLE "EmployeeLeaves" ADD COLUMN IF NOT EXISTS "RejectionReason" text NULL;
            """,

            // ===== ExpenseClaims: link to the auto-generated PaymentVoucher document =====
            """
            ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "PaymentVoucherDocumentId" uuid NULL;
            """,

            // ===== CompanySettings: per-company annual leave quotas (JSON by LeaveType) =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LeaveQuotasJson" text NULL;
            """,

            // ===== SalaryAdvances: employee salary-advance workflow → posts to central GL =====
            """
            CREATE TABLE IF NOT EXISTS "SalaryAdvances" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "AdvanceNumber" varchar(50) NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "RequestDate" timestamp NOT NULL,
                "Amount" numeric(18,2) NOT NULL DEFAULT 0,
                "Reason" text NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Draft',
                "MonthlyDeduction" numeric(18,2) NOT NULL DEFAULT 0,
                "ClearedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "OutstandingAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ApprovedByUserId" uuid NULL,
                "ApprovedAt" timestamp NULL,
                "ApprovalNotes" text NULL,
                "RejectionReason" text NULL,
                "DisbursedAt" timestamp NULL,
                "DisbursementDocumentId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SalaryAdvances" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SalaryAdvances_CompanyId_AdvanceNumber"
                ON "SalaryAdvances" ("CompanyId", "AdvanceNumber");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SalaryAdvances_CompanyId_EmployeeId_Status"
                ON "SalaryAdvances" ("CompanyId", "EmployeeId", "Status");
            """,

            // ===== Departments: first-class org-structure table (replaces
            //                    Employee.Department string + maps to AccountingDimension) =====
            """
            CREATE TABLE IF NOT EXISTS "Departments" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "Description" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "ParentDepartmentId" uuid NULL,
                "BranchId" uuid NULL,
                "DimensionId" uuid NULL,
                "DefaultExpenseAccountId" uuid NULL,
                "ManagerEmployeeId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Departments_CompanyId_Code"
                ON "Departments" ("CompanyId", "Code");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Departments_DimensionId" ON "Departments" ("DimensionId");
            """,

            // ===== Positions / Job titles =====
            """
            CREATE TABLE IF NOT EXISTS "Positions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "Title" varchar(200) NOT NULL,
                "TitleEn" varchar(200) NULL,
                "Description" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Band" varchar(50) NULL,
                "MinSalary" numeric(18,2) NULL,
                "MaxSalary" numeric(18,2) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Positions" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Positions_CompanyId_Code"
                ON "Positions" ("CompanyId", "Code");
            """,

            // ===== Employees: org-structure FKs (additive; legacy string
            //                  Department / Position fields preserved for
            //                  back-compat + historical payslip display) =====
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DepartmentId" uuid NULL;
            """,
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "PositionId" uuid NULL;
            """,
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DirectManagerId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_DepartmentId" ON "Employees" ("DepartmentId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_DirectManagerId" ON "Employees" ("DirectManagerId");
            """,

            // ===== CompanySettings: HR approval enforcement flag =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EnforceManagerApproval" boolean NOT NULL DEFAULT false;
            """,

            // ===== Users: LINE Messaging API binding (per-user push) =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LineUserId" varchar(64) NULL;
            """,

            // ===== NotificationSettings: per-company event × recipient × channel matrix =====
            """
            CREATE TABLE IF NOT EXISTS "NotificationSettings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EventKey" varchar(80) NOT NULL,
                "RecipientRole" varchar(50) NOT NULL,
                "EnableSystem" boolean NOT NULL DEFAULT false,
                "EnableEmail" boolean NOT NULL DEFAULT false,
                "EnableLine" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_NotificationSettings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NotificationSettings_CompanyId_EventKey_RecipientRole"
                ON "NotificationSettings" ("CompanyId", "EventKey", "RecipientRole");
            """,

            // ===== NotificationPreferences: per-user suppression overrides =====
            """
            CREATE TABLE IF NOT EXISTS "NotificationPreferences" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "EventKey" varchar(80) NOT NULL,
                "SuppressSystem" boolean NOT NULL DEFAULT false,
                "SuppressEmail" boolean NOT NULL DEFAULT false,
                "SuppressLine" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_NotificationPreferences" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NotificationPreferences_CompanyId_UserId_EventKey"
                ON "NotificationPreferences" ("CompanyId", "UserId", "EventKey");
            """,

            // ===== SystemAccountTemplates: admin-editable master Chart of Accounts =====
            """
            CREATE TABLE IF NOT EXISTS "SystemAccountTemplates" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "AccountCode" varchar(20) NOT NULL,
                "AccountNameTh" varchar(256) NOT NULL,
                "AccountNameEn" text NULL,
                "AccountType" integer NOT NULL DEFAULT 0,
                "Level" integer NOT NULL DEFAULT 1,
                "IsActive" boolean NOT NULL DEFAULT true,
                "BusinessType" integer NULL,
                "IndustryType" integer NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemAccountTemplates" PRIMARY KEY ("Id")
            );
            """,
            // Scope columns — ADD IF NOT EXISTS for tables created before they existed.
            """ALTER TABLE "SystemAccountTemplates" ADD COLUMN IF NOT EXISTS "BusinessType" integer NULL;""",
            """ALTER TABLE "SystemAccountTemplates" ADD COLUMN IF NOT EXISTS "IndustryType" integer NULL;""",
            // Old single-column unique index no longer valid (a code may repeat
            // across scopes) — replace with a plain lookup index.
            """DROP INDEX IF EXISTS "IX_SystemAccountTemplates_AccountCode";""",
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemAccountTemplates_Scope"
                ON "SystemAccountTemplates" ("BusinessType", "IndustryType", "AccountCode");
            """,

            // ===== DocumentLines.SourceLineId: partial / flexible document composition =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "SourceLineId" uuid NULL;""",
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_SourceLineId"
                ON "DocumentLines" ("SourceLineId") WHERE "SourceLineId" IS NOT NULL;
            """,

            // ===== ChartOfAccounts.InputVatClaimable: prohibited input VAT (ภาษีซื้อต้องห้าม) =====
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "InputVatClaimable" boolean NOT NULL DEFAULT true;""",
            // Back-fill: mark existing entertainment (ค่ารับรอง) accounts non-claimable
            // so their input VAT stops being credited on the ภ.พ.30. Scoped to
            // IsSystemAccount — those are seeded/managed by the system and cannot
            // be edited by admins, so this stays safe to re-run on every startup
            // without overriding a deliberate admin change on a custom account.
            """
            UPDATE "ChartOfAccounts" SET "InputVatClaimable" = false
            WHERE ("AccountCode" LIKE '54460%' OR "AccountName" LIKE '%รับรอง%')
              AND "IsSystemAccount" = true
              AND "InputVatClaimable" = true;
            """,

            // ===== TaxReportLines.IsExcluded: accountant include/exclude toggle =====
            """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "IsExcluded" boolean NOT NULL DEFAULT false;""",

            // ===== Documents.IsOpeningBalance: migrated opening AR/AP subledger docs =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsOpeningBalance" boolean NOT NULL DEFAULT false;""",

            // ===== PosOrderItems.RefundedQuantity: POS partial refunds =====
            """ALTER TABLE "PosOrderItems" ADD COLUMN IF NOT EXISTS "RefundedQuantity" numeric NOT NULL DEFAULT 0;""",

            // ===== PosOrders.ClientOrderId: offline-sale idempotency key =====
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "ClientOrderId" uuid NULL;""",
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosOrders_CompanyId_ClientOrderId"
                ON "PosOrders" ("CompanyId", "ClientOrderId") WHERE "ClientOrderId" IS NOT NULL;
            """,

            // ===== Composite indexes for hot read paths flagged by the perf audit =====
            // Every TenantEntity query filters by CompanyId first; the secondary
            // filter is usually a foreign key + date. Single-column FKs alone
            // make Postgres seq-scan within the FK group.
            """
            CREATE INDEX IF NOT EXISTS "IX_BankTransactions_CompanyId_BankAccount_Date"
                ON "BankTransactions" ("CompanyId", "BankAccountId", "TransactionDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_StockMovements_CompanyId_Product_Date"
                ON "StockMovements" ("CompanyId", "ProductId", "CreatedAt" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_EmployeeLeaves_Employee_Status_StartDate"
                ON "EmployeeLeaves" ("EmployeeId", "Status", "StartDate");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_JournalEntries_CompanyId_EntryDate"
                ON "JournalEntries" ("CompanyId", "EntryDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_Type_Date"
                ON "Documents" ("CompanyId", "DocumentType", "DocumentDate" DESC);
            """,
            // Document.ExchangeRate — multi-currency FX rate persisted per doc
            // so JE auto-post can convert non-THB amounts to THB consistently.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ExchangeRate" numeric(18,6) NOT NULL DEFAULT 1;""",

            // ===== Performance indexes (S5 sprint) — composite covering ที่
            // ใช้บ่อยในรายงาน/หน้าเอกสาร/AR/AP/audit. WHERE !IsDeleted กรอง
            // soft-delete รวด (Postgres bitmap-scan ใช้ได้ทันที) =====
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_Status_Date"
                ON "Documents" ("CompanyId", "Status", "DocumentDate" DESC)
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_ContactId_Date"
                ON "Documents" ("CompanyId", "ContactId", "DocumentDate" DESC)
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_RelatedDoc"
                ON "Documents" ("CompanyId", "RelatedDocumentId")
                WHERE "RelatedDocumentId" IS NOT NULL AND "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_OutstandingAR"
                ON "Documents" ("CompanyId", "DueDate")
                WHERE "Status" IN (1,3,8) AND "BalanceDue" > 0 AND "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_AccountId_Doc"
                ON "DocumentLines" ("AccountId", "DocumentId")
                WHERE "AccountId" IS NOT NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Payments_CompanyId_Document"
                ON "Payments" ("CompanyId", "DocumentId")
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_JournalEntryLines_AccountId_Date"
                ON "JournalEntryLines" ("AccountId", "JournalEntryId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_CompanyId_EntityType_Time"
                ON "AuditLogs" ("CompanyId", "EntityType", "Timestamp" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_TaxReportLines_DocumentId_Excluded"
                ON "TaxReportLines" ("DocumentId", "IsExcluded")
                WHERE "DocumentId" IS NOT NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_StockMovements_Company_Product_Date"
                ON "StockMovements" ("CompanyId", "ProductId", "MovementDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_Company_Active"
                ON "Contacts" ("CompanyId")
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Notifications_User_Unread"
                ON "Notifications" ("UserId", "CreatedAt" DESC)
                WHERE "IsRead" = false AND "IsDeleted" = false;
            """,

            // ===== LineBindCodes — LINE bot user-to-account linking =====
            """
            CREATE TABLE IF NOT EXISTS "LineBindCodes" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
                "Code" varchar(10) NOT NULL,
                "ExpiresAt" timestamptz NOT NULL,
                "UsedAt" timestamptz NULL,
                "UsedByLineUserId" varchar(64) NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NULL,
                "CreatedBy" varchar(64) NULL,
                "UpdatedBy" varchar(64) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_LineBindCodes_Code" ON "LineBindCodes" ("Code") WHERE "UsedAt" IS NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Users_LineUserId" ON "Users" ("LineUserId") WHERE "LineUserId" IS NOT NULL;""",

            // ===== LineUserStates — multi-company active selection per LINE user =====
            """
            CREATE TABLE IF NOT EXISTS "LineUserStates" (
                "Id" uuid PRIMARY KEY,
                "LineUserId" varchar(64) NOT NULL,
                "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
                "ActiveCompanyId" uuid NULL REFERENCES "Companies"("Id") ON DELETE SET NULL,
                "LastInteractionAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NULL,
                "CreatedBy" varchar(64) NULL,
                "UpdatedBy" varchar(64) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LineUserStates_LineUserId" ON "LineUserStates" ("LineUserId");""",

            // ===== ProductAliases — OCR-driven product name aliases for fuzzy matching =====
            """
            CREATE TABLE IF NOT EXISTS "ProductAliases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProductId" uuid NOT NULL,
                "ContactId" uuid NULL,
                "AliasName" varchar(500) NOT NULL,
                "NormalizedName" varchar(500) NOT NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "Source" varchar(20) NOT NULL DEFAULT 'user',
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductAliases" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductAliases_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ProductAliases_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_CompanyId_NormalizedName" ON "ProductAliases" ("CompanyId", "NormalizedName") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_CompanyId_ContactId_NormalizedName" ON "ProductAliases" ("CompanyId", "ContactId", "NormalizedName") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_ProductId" ON "ProductAliases" ("ProductId") WHERE "IsDeleted" = false;""",
            // Trigram index on the normalized form — powers SIMILARITY()
            // queries from the OCR product matcher in sub-50ms.
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_NormalizedName_Trgm" ON "ProductAliases" USING gin ("NormalizedName" gin_trgm_ops);""",
            // Trigram index on Product.Name too — first-pass match when no
            // alias exists yet for a freshly OCR'd description.
            """CREATE INDEX IF NOT EXISTS "IX_Products_Name_Trgm" ON "Products" USING gin ("Name" gin_trgm_ops);""",

            // ===== GlobalProductPatterns — cross-tenant federated learning of product wordings =====
            // Anonymized, system-wide. No tenant identifiers in the row itself —
            // distinct-tenant counting is offloaded to the tiny join table below.
            """
            CREATE TABLE IF NOT EXISTS "GlobalProductPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedKey" varchar(500) NOT NULL,
                "CanonicalLabel" varchar(500) NULL,
                "Brand" varchar(100) NULL,
                "Unit" varchar(50) NULL,
                "CategoryHint" varchar(100) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalProductPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_NormalizedKey" ON "GlobalProductPatterns" ("NormalizedKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_Status" ON "GlobalProductPatterns" ("Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_NormalizedKey_Trgm" ON "GlobalProductPatterns" USING gin ("NormalizedKey" gin_trgm_ops);""",

            // Tracks DISTINCT tenants who confirmed each pattern — uniqueness
            // counter ONLY. One row per (PatternId, CompanyId). Never joined
            // back to tenant data in matching queries; the per-tenant data
            // is queried inside that tenant's own ProductAlias table.
            """
            CREATE TABLE IF NOT EXISTS "GlobalProductPatternTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalProductPatternTenantSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GPPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalProductPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GPPSeens_PatternId_CompanyId" ON "GlobalProductPatternTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== SuppliesUsageLogs — multi-line notes + issued-to fields =====
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "Notes" text NULL;""",
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "IssuedToUserId" text NULL;""",
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "IssuedToName" varchar(200) NULL;""",

            // ===== ProductNegativeAliases — "this OCR wording is NOT this product" =====
            // Stops the matcher from re-suggesting a candidate the user has
            // explicitly rejected. Scoped to (CompanyId, NormalizedKey,
            // RejectedProductId) — rejection is per-wording, not blanket.
            """
            CREATE TABLE IF NOT EXISTS "ProductNegativeAliases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedName" varchar(500) NOT NULL,
                "RejectedProductId" uuid NOT NULL,
                "ContactId" uuid NULL,
                "Reason" varchar(200) NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductNegativeAliases" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductNegativeAliases_Products" FOREIGN KEY ("RejectedProductId") REFERENCES "Products"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ProductNegativeAliases_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_NegAlias_Company_Norm" ON "ProductNegativeAliases" ("CompanyId", "NormalizedName") WHERE "IsDeleted" = false;""",

            // ===== GlobalExpenseCategoryPatterns — federated category prediction =====
            """
            CREATE TABLE IF NOT EXISTS "GlobalExpenseCategoryPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL,
                "DescriptionKeyword" varchar(200) NULL,
                "AccountCode" varchar(50) NOT NULL,
                "AccountName" varchar(200) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalExpenseCategoryPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GECP_VendorKeyword" ON "GlobalExpenseCategoryPatterns" ("VendorKey", "DescriptionKeyword") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GECP_Status" ON "GlobalExpenseCategoryPatterns" ("Status") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalExpenseCategoryTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GECPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GECPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalExpenseCategoryPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GECPSeens_Pattern_Company" ON "GlobalExpenseCategoryTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== SystemOcrVendorIntelligence — track distinct contributing tenants =====
            """ALTER TABLE "SystemOcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TenantContributionCount" integer NOT NULL DEFAULT 0;""",

            """
            CREATE TABLE IF NOT EXISTS "SystemOcrVendorIntelTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SystemIntelId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SVITenantSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_SVITenantSeens_Intel" FOREIGN KEY ("SystemIntelId") REFERENCES "SystemOcrVendorIntelligence"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SVITenantSeens_Intel_Company" ON "SystemOcrVendorIntelTenantSeens" ("SystemIntelId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== GlobalDocWorkflowPatterns — federated doc-type prediction =====
            // (VendorKey, ScannedDocType) → TargetDocType consensus across tenants.
            """
            CREATE TABLE IF NOT EXISTS "GlobalDocWorkflowPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL,
                "ScannedDocType" varchar(50) NOT NULL,
                "TargetDocType" varchar(50) NOT NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalDocWorkflowPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GDWP_Vendor_Scanned" ON "GlobalDocWorkflowPatterns" ("VendorKey", "ScannedDocType") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalDocWorkflowTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GDWPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GDWPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalDocWorkflowPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GDWPSeens_Pattern_Company" ON "GlobalDocWorkflowTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== GlobalAssetCategoryPatterns — federated FixedAsset category + useful-life =====
            """
            CREATE TABLE IF NOT EXISTS "GlobalAssetCategoryPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedKey" varchar(500) NOT NULL,
                "Category" varchar(200) NOT NULL,
                "UsefulLifeMonths" integer NOT NULL,
                "DepreciationMethod" varchar(50) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalAssetCategoryPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GACP_NormalizedKey" ON "GlobalAssetCategoryPatterns" ("NormalizedKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GACP_Status" ON "GlobalAssetCategoryPatterns" ("Status") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalAssetCategoryTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GACPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GACPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalAssetCategoryPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GACPSeens_Pattern_Company" ON "GlobalAssetCategoryTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== CmsLeads — unified lead capture for RFQ/viewing/demo/enrollment/etc =====
            """
            CREATE TABLE IF NOT EXISTS "CmsLeads" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SiteId" uuid NOT NULL,
                "LeadNumber" varchar(50) NOT NULL,
                "LeadType" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "SourceSlug" varchar(100) NULL,
                "CustomerName" varchar(200) NULL,
                "CustomerEmail" varchar(200) NULL,
                "CustomerPhone" varchar(50) NULL,
                "CustomerCompany" varchar(200) NULL,
                "CustomerTaxId" varchar(50) NULL,
                "Message" text NULL,
                "DataJson" text NULL,
                "AssignedToUserId" uuid NULL,
                "AssignedToName" varchar(200) NULL,
                "QualifiedAt" timestamp NULL,
                "QuotedAt" timestamp NULL,
                "WonAt" timestamp NULL,
                "LostAt" timestamp NULL,
                "LostReason" varchar(500) NULL,
                "InternalNotes" text NULL,
                "ErpDocumentId" uuid NULL,
                "ContactId" uuid NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CmsLeads" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CmsLeads_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"),
                CONSTRAINT "FK_CmsLeads_Sites" FOREIGN KEY ("SiteId") REFERENCES "Sites"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CmsLeads_Contacts" FOREIGN KEY ("ContactId") REFERENCES "Contacts"("Id") ON DELETE SET NULL
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_CmsLeads_Company_Site_Created" ON "CmsLeads" ("CompanyId", "SiteId", "CreatedAt" DESC) WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CmsLeads_Status" ON "CmsLeads" ("CompanyId", "SiteId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CmsLeads_Company_LeadNumber" ON "CmsLeads" ("CompanyId", "LeadNumber") WHERE "IsDeleted" = false;""",

            // ===== AI Integration tables (DeepSeek / OpenAI / Anthropic / etc) =====
            // Provider registry — one row per configured provider, at most
            // one IsActive at a time (enforced by partial unique index).
            """
            CREATE TABLE IF NOT EXISTS "AiProviderConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProviderType" integer NOT NULL,
                "DisplayName" varchar(200) NOT NULL DEFAULT '',
                "IsActive" boolean NOT NULL DEFAULT false,
                "IsEnabled" boolean NOT NULL DEFAULT true,
                "Endpoint" varchar(500) NULL,
                "ApiKey" text NULL,
                "Model" varchar(200) NOT NULL DEFAULT 'deepseek-chat',
                "Temperature" decimal(4,2) NOT NULL DEFAULT 0.10,
                "MaxOutputTokens" integer NOT NULL DEFAULT 1024,
                "RequestTimeoutSeconds" integer NOT NULL DEFAULT 8,
                "DailyCallCap" integer NULL,
                "MonthlyBudgetUsd" decimal(18,4) NULL,
                "PricePerInputTokenUsd1M" decimal(18,6) NULL,
                "PricePerOutputTokenUsd1M" decimal(18,6) NULL,
                "LastTestedAt" timestamp NULL,
                "LastTestStatus" varchar(500) NULL,
                "ExtraSettingsJson" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiProviderConfigs" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiProviderConfigs_OneActive" ON "AiProviderConfigs" ("IsActive") WHERE "IsActive" = true;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiProviderConfigs_ProviderType" ON "AiProviderConfigs" ("ProviderType");""",

            // Per-call feedback row — the training set. Every orchestrator
            // invocation writes here (Success / Cached / Failed / Skipped).
            """
            CREATE TABLE IF NOT EXISTS "AiSuggestionFeedbacks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "PromptHash" varchar(80) NOT NULL,
                "PromptJson" jsonb NOT NULL,
                "ResponseJson" jsonb NULL,
                "AiPrimaryAnswer" text NULL,
                "AiConfidence" decimal(5,4) NULL,
                "LocalModelAnswer" text NULL,
                "LocalModelConfidence" decimal(5,4) NULL,
                "LocalModelVersion" varchar(100) NULL,
                "UserChosenAnswer" text NULL,
                "UserChosenAt" timestamp NULL,
                "UserAcceptedAi" boolean NULL,
                "SourceEntityType" varchar(100) NULL,
                "SourceEntityId" uuid NULL,
                "Status" integer NOT NULL DEFAULT 1,
                "ProviderUsed" integer NOT NULL DEFAULT 1,
                "ModelVersion" varchar(200) NULL,
                "LatencyMs" integer NULL,
                "InputTokens" integer NULL,
                "OutputTokens" integer NULL,
                "CostUsd" decimal(18,6) NULL,
                "CacheHitOfFeedbackId" uuid NULL,
                "ErrorMessage" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiSuggestionFeedbacks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_AiSuggestionFeedbacks_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_Company_Feature_Date" ON "AiSuggestionFeedbacks" ("CompanyId", "FeatureKey", "CreatedAt" DESC) WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_PromptHash" ON "AiSuggestionFeedbacks" ("PromptHash") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_Feature_UserChosen" ON "AiSuggestionFeedbacks" ("FeatureKey", "UserChosenAt") WHERE "IsDeleted" = false;""",

            // Prompt response cache — tenant-scoped, content-addressed.
            """
            CREATE TABLE IF NOT EXISTS "AiResponseCaches" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PromptHash" varchar(80) NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "ResponseJson" jsonb NOT NULL,
                "ProviderUsed" integer NOT NULL,
                "ModelVersion" varchar(200) NULL,
                "Confidence" decimal(5,4) NULL,
                "CompanyId" uuid NULL,
                "HitCount" integer NOT NULL DEFAULT 0,
                "LastHitAt" timestamp NULL,
                "ExpiresAt" timestamp NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiResponseCaches" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiResponseCaches_Hash_Company" ON "AiResponseCaches" ("PromptHash", "CompanyId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiResponseCaches_ExpiresAt" ON "AiResponseCaches" ("ExpiresAt") WHERE "IsDeleted" = false;""",

            // Per-feature local-model health (one row per FeatureKey).
            """
            CREATE TABLE IF NOT EXISTS "LocalModelHealths" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FeatureKey" varchar(100) NOT NULL,
                "LocalModelVersion" varchar(100) NOT NULL DEFAULT 'v1',
                "SamplesLast30d" integer NOT NULL DEFAULT 0,
                "LocalAccuracy30d" decimal(5,4) NOT NULL DEFAULT 0,
                "AiAccuracy30d" decimal(5,4) NOT NULL DEFAULT 0,
                "AgreementRate30d" decimal(5,4) NOT NULL DEFAULT 0,
                "LastEvaluatedAt" timestamp NOT NULL DEFAULT now(),
                "Status" integer NOT NULL DEFAULT 1,
                "Recommendation" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_LocalModelHealths" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocalModelHealths_FeatureKey" ON "LocalModelHealths" ("FeatureKey") WHERE "IsDeleted" = false;""",

            // Per-feature routing policy — admin sets mode + thresholds
            // per AiFeatureKey. Sparse (rows missing fall back to global
            // defaults in the orchestrator).
            """
            CREATE TABLE IF NOT EXISTS "AiFeatureRoutingConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FeatureKey" varchar(100) NOT NULL,
                "Mode" integer NOT NULL DEFAULT 3,
                "LocalConfidenceThreshold" decimal(5,4) NULL,
                "ProviderSamplingRate" decimal(5,4) NULL,
                "AdminNote" text NULL,
                "LastModifiedBy" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiFeatureRoutingConfigs" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiFeatureRoutingConfigs_FeatureKey" ON "AiFeatureRoutingConfigs" ("FeatureKey") WHERE "IsDeleted" = false;""",

            // Daily usage rollup — drives the admin AI burn widget. One
            // row per (day, provider, feature). Job upserts at end-of-day.
            """
            CREATE TABLE IF NOT EXISTS "AiUsageDailies" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "UsageDate" timestamp NOT NULL,
                "ProviderType" integer NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "CallsAttempted" integer NOT NULL DEFAULT 0,
                "CallsSuccessful" integer NOT NULL DEFAULT 0,
                "CallsCached" integer NOT NULL DEFAULT 0,
                "CallsFailed" integer NOT NULL DEFAULT 0,
                "CallsBudgetBlocked" integer NOT NULL DEFAULT 0,
                "InputTokensTotal" bigint NOT NULL DEFAULT 0,
                "OutputTokensTotal" bigint NOT NULL DEFAULT 0,
                "CostUsdTotal" decimal(18,6) NOT NULL DEFAULT 0,
                "AvgLatencyMs" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiUsageDailies" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiUsageDailies_Day_Provider_Feature" ON "AiUsageDailies" ("UsageDate", "ProviderType", "FeatureKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiUsageDailies_UsageDate" ON "AiUsageDailies" ("UsageDate" DESC) WHERE "IsDeleted" = false;""",

            // SiteSettings — AI master switches.
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiAugmentationEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiReviewConfidenceThreshold" decimal(5,4) NOT NULL DEFAULT 0.6500;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiSamplingRate" decimal(5,4) NOT NULL DEFAULT 0.1000;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiDefaultCacheTtlDays" integer NOT NULL DEFAULT 30;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiStripPiiInPrompts" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiVerifyAgainstThaiComplianceRules" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiLastFeedbackTrainingAt" timestamp NULL;""",

            // OcrScanResult — AI augmentation trail.
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "AiSuggestedContactId" uuid NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "AiSuggestionFeedbackId" uuid NULL;""",
            // OcrScanResult — GL-account (expense) DeepSeek classification trail.
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountUsedAi" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiFeedbackId" uuid NULL;""",
            // Document — Payment Voucher อ้างใบกำกับภาษี (RD §86/4 + §86/14).
            // HasTaxInvoiceReference = flag จาก checkbox "ใช้งานใบกำกับภาษี".
            // SupplierBranchCode = snapshot สาขาผู้ขายตอนออกใบกำกับ
            // (กัน Contact.BranchCode ถูกแก้ภายหลังแล้วรายงานภาษีซื้อย้อนหลังเพี้ยน).
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "HasTaxInvoiceReference" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierBranchCode" varchar(5) NULL;""",

            // EmployeeLeave — half-day support added 2026.
            """ALTER TABLE "EmployeeLeaves" ADD COLUMN IF NOT EXISTS "HalfDayMarker" integer NOT NULL DEFAULT 0;""",

            // CompanySettings — owner-level feature + menu overrides
            // (subtractive only; Subscription.EnabledFeatures is the
            // upper bound, this lets the Owner opt-out features they
            // don't use / hide menus they find noisy).
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnerDisabledFeatures" bigint NOT NULL DEFAULT 0;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnerHiddenMenuIdsJson" text NULL;""",

            // LeaveType — HR-configurable catalog (replaces hardcoded
            // string keys in CompanySettings.LeaveQuotasJson).
            """
            CREATE TABLE IF NOT EXISTS "LeaveTypes" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "NameTh" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "AnnualQuota" decimal(10,2) NOT NULL DEFAULT 0,
                "IsPaid" boolean NOT NULL DEFAULT true,
                "AllowHalfDay" boolean NOT NULL DEFAULT true,
                "CarryForward" boolean NOT NULL DEFAULT false,
                "CarryForwardCap" decimal(10,2) NULL,
                "AdvanceNoticeDays" integer NOT NULL DEFAULT 0,
                "RequiresAttachment" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Color" varchar(20) NOT NULL DEFAULT '#6366f1',
                "Icon" varchar(20) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_LeaveTypes" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_LeaveTypes_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeaveTypes_Company_Code" ON "LeaveTypes" ("CompanyId", "Code") WHERE "IsDeleted" = false;""",

            // PublicHoliday — calendar.
            """
            CREATE TABLE IF NOT EXISTS "PublicHolidays" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Date" timestamp NOT NULL,
                "NameTh" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "Category" varchar(50) NOT NULL DEFAULT 'Public',
                "IsSubstitute" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PublicHolidays" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PublicHolidays_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PublicHolidays_Company_Date" ON "PublicHolidays" ("CompanyId", "Date") WHERE "IsDeleted" = false;""",

            // EmployeeLeaveBalance — carry-forward + manual HR adjustment.
            """
            CREATE TABLE IF NOT EXISTS "EmployeeLeaveBalances" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Year" integer NOT NULL,
                "LeaveTypeCode" varchar(50) NOT NULL,
                "CarriedForwardDays" decimal(10,2) NOT NULL DEFAULT 0,
                "AdjustmentDays" decimal(10,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "Phase" varchar(20) NOT NULL DEFAULT 'Manual',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmployeeLeaveBalances" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ELB_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"),
                CONSTRAINT "FK_ELB_Employees" FOREIGN KEY ("EmployeeId") REFERENCES "Employees"("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ELB_Company_Emp_Year_Type" ON "EmployeeLeaveBalances" ("CompanyId", "EmployeeId", "Year", "LeaveTypeCode") WHERE "IsDeleted" = false;""",

            // Contact — per-contact GL account overrides. Default AR =
            // "113" prefix in FindAccountAsync; specific contacts can pin
            // their own (e.g. ลูกหนี้พนักงาน vs ลูกหนี้การค้า).
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultArAccountId" uuid NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultApAccountId" uuid NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultIrGrAccountId" uuid NULL;""",

            // ExpenseClaim — no-receipt claim (§65 ทวิ) auto-generates a
            // Document(CertificateInLieu) on Approve. New columns added
            // 2026 — idempotent ADD COLUMN IF NOT EXISTS.
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "NoReceipt" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "NoReceiptReason" varchar(500) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "WitnessName" varchar(200) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "WitnessPosition" varchar(200) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "CertificateInLieuDocumentId" uuid NULL;""",

            // AnomalyDetection — lazy AI explanation cache.
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiVerdict" varchar(50) NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiConfidence" decimal(5,4) NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiReasoning" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiSuggestedActionsJson" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiRisksJson" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiFeedbackId" uuid NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiExplainedAt" timestamp NULL;""",

            // ===== Project external sync (partner system linkage) =====
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(200) NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(50) NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "LastSyncedAt" timestamp NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalUrl" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Projects_External" ON "Projects" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== Project allocation rolled out to remaining entities =====
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "ExpenseClaimLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "PettyCashTransactions" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "Cheques" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "StampDutyRecords" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "Budgets" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Payments_Project" ON "Payments" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ExpenseClaims_Project" ON "ExpenseClaims" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",

            // Approval rule per-project scope — lets a company route project X
            // spend through PM Alice and project Y through PM Bob at the same
            // amount tier. Backfilled NULL = applies to all projects (legacy).
            """ALTER TABLE "ApprovalRules" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ApprovalRules_Project" ON "ApprovalRules" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",

            // Document gains supplier-side tax invoice metadata for
            // PurchaseInvoice + credit-term fields. Indexed by
            // (CompanyId, SupplierInvoiceNumber) so partner-statement
            // reconciliation can find a row in one hop.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierInvoiceNumber" varchar(100) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierTaxInvoiceDate" timestamp NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CreditDays" integer NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentTerms" varchar(100) NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Supplier_Invoice" ON "Documents" ("CompanyId", "SupplierInvoiceNumber") WHERE "SupplierInvoiceNumber" IS NOT NULL AND "IsDeleted" = false;""",

            // Reverse-lookup index — finding all child docs of a source
            // (e.g. all DNs spawned from a QT) currently does a full scan.
            // Index lets the detail-modal "เอกสารต่อเนื่อง" section + the
            // ?relatedDocumentId= filter return in O(log n).
            """CREATE INDEX IF NOT EXISTS "IX_Documents_RelatedDocument" ON "Documents" ("CompanyId", "RelatedDocumentId") WHERE "RelatedDocumentId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_DocumentLines_SourceLine" ON "DocumentLines" ("SourceLineId") WHERE "SourceLineId" IS NOT NULL;""",

            // Per-line link from a ProjectCostEntry back to the DocumentLine
            // that spawned it. Lets the doc UI flag each line "🏗️ ลงโครงการแล้ว"
            // without re-parsing the auto-marker in the Description.
            """ALTER TABLE "ProjectCostEntries" ADD COLUMN IF NOT EXISTS "DocumentLineId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProjectCostEntries_DocumentLine" ON "ProjectCostEntries" ("DocumentLineId") WHERE "DocumentLineId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProjectCostEntries_Document" ON "ProjectCostEntries" ("CompanyId", "DocumentId") WHERE "DocumentId" IS NOT NULL AND "IsDeleted" = false;""",

            // Attendance metadata on EmployeeProjectTime — optional
            // fields synced from clock-in / attendance systems. If
            // never set, payroll calc behaves exactly as before.
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "OvertimeHours" decimal(8,2) NULL;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "IsHoliday" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasPerDiem" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasAccommodation" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasOvertimeMeal" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "AttendanceMetadataJson" text NULL;""",

            // Per-employee + company-wide compensation profile tables.
            """
            CREATE TABLE IF NOT EXISTS "EmployeeCompensationProfiles" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "OvertimeRateMultiplierWeekday" decimal(6,3) NULL,
                "OvertimeRateMultiplierHoliday" decimal(6,3) NULL,
                "PerDiemRate" decimal(18,2) NULL,
                "AccommodationAllowance" decimal(18,2) NULL,
                "OvertimeMealAllowance" decimal(18,2) NULL,
                "CustomBenefitsJson" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmployeeCompProfile_Employee" ON "EmployeeCompensationProfiles" ("CompanyId", "EmployeeId") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "CompanyCompensationDefaults" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "OvertimeRateMultiplierWeekday" decimal(6,3) NOT NULL DEFAULT 1.5,
                "OvertimeRateMultiplierHoliday" decimal(6,3) NOT NULL DEFAULT 3.0,
                "PerDiemRate" decimal(18,2) NOT NULL DEFAULT 500,
                "AccommodationAllowance" decimal(18,2) NOT NULL DEFAULT 800,
                "OvertimeMealAllowance" decimal(18,2) NOT NULL DEFAULT 30,
                "StandardWorkHoursPerDay" decimal(6,2) NOT NULL DEFAULT 8,
                "StandardWorkDaysPerMonth" decimal(6,2) NOT NULL DEFAULT 30,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyCompDefaults_Company" ON "CompanyCompensationDefaults" ("CompanyId") WHERE "IsDeleted" = false;""",
            // Daily meal (คนละกรณีกับ OT meal) + custom allowances list
            // ที่บริษัทตั้งเองได้ (โทรศัพท์ ค่าเดินทาง ค่าน้ำมัน ฯลฯ).
            """ALTER TABLE "CompanyCompensationDefaults" ADD COLUMN IF NOT EXISTS "DailyMealAllowance" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "CompanyCompensationDefaults" ADD COLUMN IF NOT EXISTS "CustomAllowancesJson" text NULL;""",
            // PayrollDetail.AdvanceRecovered — per-employee advance repayment
            // recorded at Pay time so Void can restore it precisely. Without
            // this, voiding a payroll silently zeroed each employee's
            // outstanding advance balance.
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "AdvanceRecovered" numeric(18,2) NOT NULL DEFAULT 0;""",
            // Employee tax allowances §47/47ทวิ — ละเอียดขึ้นจากที่เก่า
            // เป็น count × 30K เฉย ๆ (ครอบครัวใหญ่ over-withhold).
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "HasSpouseAllowance" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ChildAllowanceCount" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "SecondAndLaterChildren" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ParentAllowanceCount" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LifeInsurancePremium" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "RmfSsfContribution" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DonationAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== Auto-email scheduling =====
            """
            CREATE TABLE IF NOT EXISTS "EmailScheduleRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Trigger" varchar(40) NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "DocumentType" varchar(40) NULL,
                "OffsetDays" integer NOT NULL DEFAULT 0,
                "SendAtHour" integer NOT NULL DEFAULT 9,
                "RepeatEveryDays" integer NOT NULL DEFAULT 0,
                "SubjectTemplate" text NULL,
                "BodyTemplate" text NULL,
                "BccEmails" text NULL,
                "CreatedByName" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmailScheduleRules" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmailRules_Company_Trigger" ON "EmailScheduleRules" ("CompanyId", "Trigger") WHERE "IsDeleted" = false;""",
            """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "DayOfMonth" integer NOT NULL DEFAULT 5;""",
            """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "AudienceFilter" varchar(100) NULL;""",
            // Refresh-token reuse detection (security hardening).
            """ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PreviousRefreshToken" varchar(500) NULL;""",
            """ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenRevokedAt" timestamp NULL;""",
            // Negative-inventory guard. Default false = strict (refuse OUT
            // that would take CurrentStock below zero).
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AllowNegativeStock" boolean NOT NULL DEFAULT false;""",

            // ===== Payment: per-request payer signature override =====
            // ใช้ใน PV PDF "ผู้จ่ายเงิน" slot สำหรับ caller ที่ user ไม่มีลายเซ็น
            // upload ไว้ (เช่น service account ของระบบเชื่อมต่อ).
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "PayerSignatureBase64" text NULL;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "PayerSignatureName" varchar(200) NULL;""",

            // ===== Payments: settlement-day FX rate (realized FX gain/loss) =====
            // rate ณ วันชำระจริงของเอกสารสกุลต่างประเทศ — ต่างจาก rate เอกสาร →
            // post กำไร/ขาดทุนอัตราแลกเปลี่ยนที่เกิดขึ้นจริง (42600/54950)
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ExchangeRate" numeric(18,6) NULL;""",

            // ===== Contact: credit limit (วงเงินเครดิต) =====
            // null = ไม่จำกัด (เดิม). ใช้ดู AR เทียบเตือนตอนสร้าง Invoice ใหม่.
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "CreditLimit" numeric(18,2) NULL;""",

            // ===== AiSuggestionMemory — online-learning store (train ไปเลย) =====
            // upsert ทันทีเมื่อ user ยืนยัน/แก้ suggestion; suggestion ครั้งถัด
            // อ่านค่าที่เรียนแล้วก่อน ไม่ต้องรอ nightly job / ไม่ต้องกดปุ่ม.
            """
            CREATE TABLE IF NOT EXISTS "AiSuggestionMemories" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FeatureKey" varchar(64) NOT NULL,
                "InputKey" varchar(256) NOT NULL,
                "LearnedAnswer" varchar(512) NOT NULL DEFAULT '',
                "AcceptCount" integer NOT NULL DEFAULT 0,
                "OverrideCount" integer NOT NULL DEFAULT 0,
                "Confidence" numeric(5,4) NOT NULL DEFAULT 0,
                "LastLearnedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiSuggestionMemories_Company_Feature_Input" ON "AiSuggestionMemories" ("CompanyId", "FeatureKey", "InputKey") WHERE "IsDeleted" = false;""",

            // ===== LINE Messaging API per-company (was global in appsettings) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineChannelAccessToken" varchar(2000) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineChannelSecret" varchar(500) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineDefaultGroupId" varchar(100) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineConfigured" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineLastTestedAt" timestamp NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineLastTestStatus" varchar(500) NULL;""",

            // ===== CompanySettings: OCR แหล่งเงิน default (ฝั่ง Cr ของ PV/Receipt) =====
            // null = พฤติกรรมเดิม (auto-pick bank รหัสต่ำสุด). ตั้งเป็น
            // ChartOfAccount.Id ของบัญชีเงินสด/ธนาคารหลัก → OCR ใช้เป็น default.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "DefaultPaymentAccountId" uuid NULL;""",

            // ===== CompanySettings: กองทุนเงินทดแทน (กท.20ก) =====
            // อัตราสมทบนายจ้างฝ่ายเดียว 0.2–1.0% ตามประเภทกิจการ. default 0.2%
            // (หมวด 1 สำนักงาน); ปิดไว้ default เพื่อไม่ดับเบิ้ลโพสต์ข้อมูลเดิม.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WorkersCompensationRatePercent" decimal(4,2) NOT NULL DEFAULT 0.2;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WorkersCompensationEnabled" boolean NOT NULL DEFAULT false;""",

            // ===== CompanySettings: §86/4 hard-block opt-in =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EnforceFullTaxInvoiceFields" boolean NOT NULL DEFAULT false;""",

            // ===== DocumentAdjustingJournalLines (Option 1: เพิ่ม Dr/Cr ลอย) =====
            // ใช้รองรับเคส PV/Doc 1 ใบ มี Dr/Cr เพิ่มเติมที่ไม่ map กับ DocumentLine
            // ปกติ (ค่าธรรมเนียมโอน, สำรอง, ปันส่วน). AutoPost รวมเข้า JE +
            // validate balance Dr=Cr รวม.
            """
            CREATE TABLE IF NOT EXISTS "DocumentAdjustingJournalLines" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "DocumentId" uuid NOT NULL REFERENCES "Documents"("Id") ON DELETE CASCADE,
                "LineOrder" integer NOT NULL DEFAULT 0,
                "AccountId" uuid NOT NULL,
                "DebitAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "CreditAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Description" varchar(500) NULL,
                "ProjectId" uuid NULL,
                "Reason" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentAdjustingJournalLines_Doc" ON "DocumentAdjustingJournalLines" ("DocumentId");""",

            // ===== CompanySettings: ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BookkeeperName" varchar(200) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BookkeeperCpdNumber" varchar(50) NULL;""",

            // ===== PDPA Wave 3: RoPA / Consent / PiiAccessLog / Breach =====
            // RoPA (ม.39) — บันทึกกิจกรรมการประมวลผลข้อมูลส่วนบุคคล
            """
            CREATE TABLE IF NOT EXISTS "PdpaProcessingActivities" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Purpose" varchar(500) NOT NULL,
                "LegalBasis" varchar(100) NOT NULL,
                "DataCategories" varchar(1000) NOT NULL,
                "RetentionPeriod" varchar(500) NOT NULL,
                "Recipients" varchar(1000) NULL,
                "TransfersOutsideThailand" boolean NOT NULL DEFAULT false,
                "TransferSafeguards" varchar(1000) NULL,
                "Notes" text NULL,
                "LastReviewedAt" timestamp NOT NULL DEFAULT now(),
                "LastReviewedBy" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaProcessingActivities_Company" ON "PdpaProcessingActivities" ("CompanyId");""",

            // Consent records (ม.19, 22) — เก็บประวัติยินยอม + ถอน
            """
            CREATE TABLE IF NOT EXISTS "PdpaConsentRecords" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "SubjectUserId" uuid NULL,
                "SubjectContactId" uuid NULL,
                "SubjectContact" varchar(200) NULL,
                "Purpose" varchar(500) NOT NULL,
                "PolicyVersion" varchar(50) NOT NULL DEFAULT '1.0',
                "GrantedAt" timestamp NOT NULL DEFAULT now(),
                "WithdrawnAt" timestamp NULL,
                "WithdrawnReason" varchar(500) NULL,
                "EvidenceHash" varchar(100) NULL,
                "Channel" varchar(50) NULL,
                "IpAddress" varchar(45) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaConsentRecords_Subject" ON "PdpaConsentRecords" ("CompanyId", "SubjectUserId", "SubjectContactId");""",

            // PII access log (ม.37(4)) — เก็บ event อ่าน PII ≥ 1 ปี
            """
            CREATE TABLE IF NOT EXISTS "PdpaPiiAccessLogs" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ActorUserId" uuid NOT NULL,
                "ActorEmail" varchar(200) NOT NULL,
                "SubjectType" varchar(50) NOT NULL,
                "SubjectId" uuid NOT NULL,
                "FieldName" varchar(500) NOT NULL,
                "Operation" varchar(50) NOT NULL DEFAULT 'Read',
                "Purpose" varchar(500) NOT NULL,
                "At" timestamp NOT NULL DEFAULT now(),
                "IpAddress" varchar(45) NULL,
                "UserAgent" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaPiiAccessLogs_At" ON "PdpaPiiAccessLogs" ("CompanyId", "At" DESC);""",
            """CREATE INDEX IF NOT EXISTS "IX_PdpaPiiAccessLogs_Subject" ON "PdpaPiiAccessLogs" ("CompanyId", "SubjectType", "SubjectId");""",

            // ===== ส่งสลิปเงินเดือนทาง LINE (self-service bind + secure token) =====
            // Employee.LineId (LINE userId Uxxxx) — push แจ้งเตือน/สลิปทาง LINE.
            // ใช้มาก่อน (NotificationEngine/PayslipLineDelivery/PayrollService) แต่
            // ไม่เคยมี migration → DB เก่า (สร้างก่อนมี field นี้) จะไม่มีคอลัมน์ →
            // 500 ตอนแจ้งเตือนพนักงาน/ส่งสลิป LINE. IF NOT EXISTS = idempotent.
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LineId" varchar(100) NULL;""",
            // LINE OA basic id (@xxx) ต่อบริษัท — ทำลิงก์/QR เพิ่มเพื่อนให้พนักงาน
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineOaBasicId" varchar(100) NULL;""",
            // รหัสผูก LINE ระดับพนักงาน (6 หลัก, หมดอายุ 24 ชม.)
            """
            CREATE TABLE IF NOT EXISTS "EmployeeLineBindCodes" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Code" varchar(6) NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "UsedAt" timestamp with time zone NULL,
                "UsedByLineUserId" varchar(100) NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeLineBindCodes_Code" ON "EmployeeLineBindCodes" ("Code") WHERE "UsedAt" IS NULL;""",
            // Token เข้าถึงสลิปแบบสาธารณะ (ปุ่มดาวน์โหลดใน LINE)
            """
            CREATE TABLE IF NOT EXISTS "PayslipShareTokens" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "PayrollRunId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Token" varchar(120) NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "RevokedAt" timestamp with time zone NULL,
                "Channel" varchar(30) NOT NULL DEFAULT 'LINE',
                "AccessCount" integer NOT NULL DEFAULT 0,
                "LastAccessedAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayslipShareTokens_Token" ON "PayslipShareTokens" ("Token");""",

            // Breach incidents (ม.37(4)) — แจ้ง PDPC ภายใน 72 ชม.
            """
            CREATE TABLE IF NOT EXISTS "PdpaBreachIncidents" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "IncidentNumber" varchar(50) NOT NULL,
                "DetectedAt" timestamp NOT NULL DEFAULT now(),
                "NotifyPdpcDueBy" timestamp NOT NULL,
                "Severity" varchar(20) NOT NULL DEFAULT 'Medium',
                "Description" text NOT NULL,
                "AffectedDataCategories" varchar(1000) NOT NULL,
                "AffectedSubjectsCount" integer NULL,
                "Status" varchar(50) NOT NULL DEFAULT 'Detected',
                "PdpcNotifiedAt" timestamp NULL,
                "PdpcReferenceNumber" varchar(100) NULL,
                "SubjectsNotifiedAt" timestamp NULL,
                "Mitigation" text NULL,
                "RootCause" text NULL,
                "ReportedByUserId" uuid NULL,
                "ClosedAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaBreachIncidents_Status" ON "PdpaBreachIncidents" ("CompanyId", "Status", "NotifyPdpcDueBy");""",

            // ===== AuditLogs: DB-level immutability (tamper-evident defense-in-depth) =====
            // app layer ตัด AuditLog ออกจาก ChangeTracker อยู่แล้ว (append-only)
            // แต่ DBA/SQL ตรง ๆ ยังลบได้ → เพิ่ม trigger บล็อค DELETE ที่ระดับ DB
            // เพื่อให้ hash-chain ตรวจสอบความถูกต้องได้จริง (PDPA ม.37 + พ.ร.บ.บัญชี).
            // บล็อคเฉพาะ DELETE (UPDATE เผื่อ migration backfill hash ในอนาคต).
            """
            CREATE OR REPLACE FUNCTION block_auditlog_delete() RETURNS TRIGGER AS $func$
            BEGIN RAISE EXCEPTION 'AuditLogs are append-only (tamper-evident) — DELETE blocked'; END;
            $func$ LANGUAGE plpgsql;
            """,
            """
            DROP TRIGGER IF EXISTS audit_log_no_delete ON "AuditLogs";
            """,
            """
            CREATE TRIGGER audit_log_no_delete BEFORE DELETE ON "AuditLogs"
                FOR EACH ROW EXECUTE FUNCTION block_auditlog_delete();
            """,

            // ===== Employees: PDPA ม.26 encrypt bank/SSN — widen cols for ciphertext =====
            // ciphertext = Base64(nonce+ct+tag) ~80 chars สำหรับ input สั้น ๆ →
            // ขยายเป็น 200. Legacy plaintext คงอยู่ + re-save migrate เป็น ciphertext.
            """ALTER TABLE "Employees" ALTER COLUMN "BankAccountNumber" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "SocialSecurityNumber" TYPE varchar(200);""",

            // ===== PayrollRuns: ประกันสังคมรอนำส่ง + กองทุนเงินทดแทน =====
            // SsoSettled* fields ติดตามว่าได้นำส่งให้ สปส. แล้วหรือยัง (กฎหมาย
            // วันที่ 15 ของเดือนถัดไป). เพิ่มเงินทดแทนรวมเพื่อทำ กท.20ก รายปี.
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettledAt" timestamp NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettlementJournalEntryId" uuid NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettlementDocumentId" uuid NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoFilingNumber" varchar(100) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoLateFeeAmount" decimal(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "TotalWorkersCompensation" decimal(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "WorkersCompensation" decimal(18,2) NOT NULL DEFAULT 0;""",

            // ===== PayrollRuns: external import (TakeTime ส่งยอดสำเร็จรูป) =====
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(100) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ExternalRunRef" varchar(200) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "IsExternalImport" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SalaryExpenseAccountCode" varchar(50) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "NetPaymentAccountCode" varchar(50) NULL;""",
            // แหล่งจ่ายเงินสุทธิรายคน (split Cr เงินสด/ธนาคารตอน Pay) — null=ใช้ค่าระดับ run
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "NetPaymentAccountCode" varchar(50) NULL;""",
            // idempotency — unique partial index บน (CompanyId, ExternalRunRef)
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PayrollRuns_ExternalRunRef" ON "PayrollRuns" ("CompanyId", "ExternalRunRef") WHERE "ExternalRunRef" IS NOT NULL;""",

            // ===== StatutoryRemittances: นำส่งภาษี/ประกันสังคมรวม (สปส.1-10/ภงด.1/3/53/ภพ.30) =====
            """
            CREATE TABLE IF NOT EXISTS "StatutoryRemittances" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RemittanceType" varchar(40) NOT NULL,
                "PeriodYear" integer NOT NULL,
                "PeriodMonth" integer NOT NULL,
                "Amount" decimal(18,2) NOT NULL DEFAULT 0,
                "LateFee" decimal(18,2) NOT NULL DEFAULT 0,
                "PayDate" timestamp NOT NULL DEFAULT now(),
                "BankGlAccountId" uuid NULL,
                "JournalEntryId" uuid NULL,
                "DocumentId" uuid NULL,
                "FilingNumber" varchar(100) NULL,
                "ReceiptAttachmentId" uuid NULL,
                "Note" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            // นำส่ง 1 ครั้ง/ประเภท/งวด (กันจ่ายซ้ำ) — เฉพาะ row ที่ยังไม่ลบ
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_StatutoryRemittances_Period" ON "StatutoryRemittances" ("CompanyId", "RemittanceType", "PeriodYear", "PeriodMonth") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "EmailQueues" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RuleId" uuid NULL,
                "EntityType" varchar(40) NOT NULL,
                "EntityId" uuid NOT NULL,
                "ToEmail" varchar(500) NOT NULL,
                "CcEmail" varchar(500) NULL,
                "BccEmail" varchar(500) NULL,
                "Subject" text NOT NULL,
                "Body" text NOT NULL,
                "AttachPdf" boolean NOT NULL DEFAULT true,
                "AttachXml" boolean NOT NULL DEFAULT false,
                "ScheduledFor" timestamp NOT NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "RetryCount" integer NOT NULL DEFAULT 0,
                "SentAt" timestamp NULL,
                "ErrorMessage" text NULL,
                "IdempotencyKey" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmailQueues" PRIMARY KEY ("Id")
            );
            """,
            // Worker scan index: Pending + due soon.
            """CREATE INDEX IF NOT EXISTS "IX_EmailQueue_Status_Schedule" ON "EmailQueues" ("Status", "ScheduledFor") WHERE "IsDeleted" = false;""",
            // Idempotency — กัน enqueue ซ้ำสำหรับ event เดียวกัน.
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmailQueue_Idempotency" ON "EmailQueues" ("CompanyId", "IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL AND "IsDeleted" = false;""",

            // PaymentAllocation — one Payment may settle many Documents.
            // Existing Payment rows stay valid (legacy 1:1 path); new
            // multi-doc payments insert one row per target document.
            """
            CREATE TABLE IF NOT EXISTS "PaymentAllocations" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "PaymentId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "AllocatedAmount" decimal(18,2) NOT NULL,
                "WithholdingTaxAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Note" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PaymentAllocations_Payment" ON "PaymentAllocations" ("PaymentId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PaymentAllocations_Document" ON "PaymentAllocations" ("CompanyId", "DocumentId") WHERE "IsDeleted" = false;""",

            // Inbound cheques need an explicit deposit-bank link so
            // MarkCleared can update bank balance — outbound uses
            // ChequeBook.BankAccount, inbound has no chequeBook.
            """ALTER TABLE "Cheques" ADD COLUMN IF NOT EXISTS "DepositBankAccountId" uuid NULL;""",

            // Orphan NOT NULL columns on CompanySettings — added by past
            // migrations without DEFAULT, never carried into the C# entity.
            // Every GetOrCreateSettingsAsync on a brand-new company exploded
            // because EF's INSERT omits unknown columns; Postgres rejects
            // the row on the NOT NULL constraint. Specific casualties:
            // AllowFreelanceAccess, MaxFreelanceUsers, possibly others.
            //
            // Rather than patch each name individually (we keep finding more),
            // this DO block walks information_schema and SETs a type-
            // appropriate DEFAULT on every NOT NULL column that lacks one,
            // then backfills NULLs (defensive — should be none since the
            // constraint already blocks them, but safe). Idempotent.
            """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT column_name, data_type, udt_name
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'CompanySettings'
                      AND is_nullable = 'NO'
                      AND column_default IS NULL
                LOOP
                    IF r.data_type IN ('boolean') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT false', r.column_name);
                    ELSIF r.data_type IN ('integer','bigint','smallint','numeric','real','double precision') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT 0', r.column_name);
                    ELSIF r.data_type IN ('character varying','varchar','text','character','char') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT ''''', r.column_name);
                    ELSIF r.data_type IN ('timestamp without time zone','timestamp with time zone','timestamptz') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT now()', r.column_name);
                    ELSIF r.data_type IN ('date') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT CURRENT_DATE', r.column_name);
                    ELSIF r.data_type IN ('uuid') THEN
                        -- Don't auto-default uuids; CompanyId etc. should always be supplied.
                        NULL;
                    END IF;
                END LOOP;
            END $$;
            """,

            // Employee EmployeeCode uniqueness is now scoped to active
            // (non-soft-deleted) rows so partners can recreate / restore
            // an employee code after deletion. Drop the legacy unique
            // constraint first, then create the partial unique index.
            """ALTER TABLE "Employees" DROP CONSTRAINT IF EXISTS "AK_Employees_CompanyId_EmployeeCode";""",
            """DROP INDEX IF EXISTS "IX_Employees_CompanyId_EmployeeCode";""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Employees_CompanyId_EmployeeCode_Active" ON "Employees" ("CompanyId", "EmployeeCode") WHERE "IsDeleted" = false;""",

            // HR: employee external-sync + cost-behavior fields. ExternalId/
            // ExternalSystem let attendance / HRIS push or pull rows without
            // name-matching. CostBehavior drives the Fixed-vs-Variable cost
            // report — defaulted to Fixed (monthly salaried is the common
            // case; daily/hourly should be flipped via UpdateEmployee).
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(200) NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(50) NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LastSyncedAt" timestamp NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NOT NULL DEFAULT 'Fixed';""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Employees_ExternalSync" ON "Employees" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ProjectCostEntry cost behavior — Fixed (rent, salaried) vs
            // Variable (hourly labor, materials) — drives the cost report.
            """ALTER TABLE "ProjectCostEntries" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NOT NULL DEFAULT 'Variable';""",

            // ChartOfAccount cost behavior — nullable; lets the report
            // classify non-project GL costs (rent, utilities, depreciation)
            // that bypass ProjectCostEntry. Seeded NULL; admins tag the
            // relevant expense accounts via the COA UI.
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NULL;""",

            // Employee project time allocation — feeds payroll → ProjectCostEntry
            // labor allocation. Sync-friendly (external attendance systems).
            """
            CREATE TABLE IF NOT EXISTS "EmployeeProjectTimes" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "ProjectId" uuid NULL,
                "ProjectTaskId" uuid NULL,
                "WorkDate" timestamp NOT NULL,
                "Hours" decimal(8,2) NOT NULL DEFAULT 0,
                "Description" text NULL,
                "Category" varchar(50) NOT NULL DEFAULT 'Billable',
                "IsAllocated" boolean NOT NULL DEFAULT false,
                "AllocatedPayrollRunId" uuid NULL,
                "ProjectCostEntryId" uuid NULL,
                "ExternalId" varchar(200) NULL,
                "ExternalSystem" varchar(50) NULL,
                "LastSyncedAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeProjectTimes_Employee_Date" ON "EmployeeProjectTimes" ("CompanyId", "EmployeeId", "WorkDate") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeProjectTimes_Project_Date" ON "EmployeeProjectTimes" ("CompanyId", "ProjectId", "WorkDate") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmployeeProjectTimes_ExternalSync" ON "EmployeeProjectTimes" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== DocumentLines: ภาษีซื้อต้องห้าม (Non-claimable Input VAT) =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "IsVatClaimable" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "VatNonClaimableReason" text NULL;""",
            // GL-account AI feedback id — ปิดลูปการสอน local model ตามกฎเหล็ก #1
            // (ตอน user แก้/ยืนยัน AccountId, ระบบเรียก RecordUserChoiceAsync)
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "GlAccountAiFeedbackId" uuid NULL;""",
            // Tax Point §78/§78/1 + Retention §87/3 + §65 ตรี add-back + LateReason §82/3
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliveryDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OwnershipTransferDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ServiceUsedDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "TaxPointDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RetentionUntil" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "NonDeductibleAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "NonDeductibleRuleJson" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LateReason" text NULL;""",
            // Deposit lifecycle: refund (ยกเลิกการจอง) + offset เข้าใบสุดท้าย
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundedAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundedAt" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundReason" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositAppliedToDocumentId" uuid NULL;""",
            // Booking number — ผูกเอกสารหลายใบ (มัดจำ→ใบสุดท้าย→ใบเสร็จ) เข้า booking เดียว
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BookingNumber" varchar(50) NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_BookingNumber" ON "Documents" ("CompanyId", "BookingNumber") WHERE "BookingNumber" IS NOT NULL;""",

            // ===== TaxRuleConfig: configurable PIT brackets + allowances ต่อปี =====
            // Per company × per year. Engine fallback ถ้าไม่มี config → ใช้
            // hard-coded constants (PayrollService.Pit*) เพื่อ backward compat
            // กับบริษัทที่ยังไม่เคยตั้งค่า.
            """
            CREATE TABLE IF NOT EXISTS "TaxRuleConfigs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalYear" integer NOT NULL,
                "BracketsJson" text NOT NULL DEFAULT '',
                "PersonalAllowance" numeric(18,2) NOT NULL DEFAULT 60000,
                "SpouseAllowance" numeric(18,2) NOT NULL DEFAULT 60000,
                "ChildAllowance" numeric(18,2) NOT NULL DEFAULT 30000,
                "ChildAllowancePost2561" numeric(18,2) NOT NULL DEFAULT 60000,
                "ParentAllowance" numeric(18,2) NOT NULL DEFAULT 30000,
                "Section42TwiCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "LifeInsuranceCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "HealthInsuranceCap" numeric(18,2) NOT NULL DEFAULT 25000,
                "PvdCap" numeric(18,2) NOT NULL DEFAULT 500000,
                "MortgageInterestCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "DonationCapPercent" numeric(8,4) NOT NULL DEFAULT 10,
                "Notes" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0,
                CONSTRAINT "UX_TaxRuleConfigs_CompanyYear" UNIQUE ("CompanyId", "FiscalYear")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_TaxRuleConfigs_CompanyId" ON "TaxRuleConfigs" ("CompanyId") WHERE "IsDeleted" = false;""",

            // ===== WithholdingTaxCerts: link to source PayrollRun สำหรับ
            // monthly auto-issue + idempotency check (re-post payroll = re-issue cert).
            """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "SourcePayrollRunId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_WithholdingTaxCerts_SourcePayrollRunId" ON "WithholdingTaxCerts" ("SourcePayrollRunId") WHERE "SourcePayrollRunId" IS NOT NULL;""",

            // ===== PayrollDetails: TaxableGross — รายได้ที่ใช้คำนวณ WHT
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "TaxableGross" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== Companies.PaidUpCapital — ทุนชำระแล้ว สำหรับ §65 ทวิ (4)
            // entertainment cap 0.3% revenue/capital max 10M (F11).
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "PaidUpCapital" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== ChartOfAccounts.CashFlowSection — per-account override
            // ของหมวด Cash Flow (Operating / Investing / Financing).
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "CashFlowSection" smallint NOT NULL DEFAULT 0;""",

            // ===== AuditLogs: F14 hash chain (forensic tamper-evident)
            """ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "PrevHash" text NULL;""",
            """ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "RowHash" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Company_Id" ON "AuditLogs" ("CompanyId", "Id");""",

            // ===== Advanced Thai SME entities =====
            // PostDatedCheck (เช็คล่วงหน้า) — B2B Thai norm. Inbound / Outbound
            // + lifecycle Held → Deposited → Cleared / Dishonored / Returned.
            """
            CREATE TABLE IF NOT EXISTS "PostDatedChecks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Direction" smallint NOT NULL,
                "CheckNumber" text NOT NULL DEFAULT '',
                "BankName" text NOT NULL DEFAULT '',
                "BankBranch" text NULL,
                "Amount" numeric(18,2) NOT NULL DEFAULT 0,
                "CheckDate" timestamp with time zone NOT NULL,
                "IssueDate" timestamp with time zone NOT NULL,
                "ScheduledDepositDate" timestamp with time zone NOT NULL,
                "DepositedAt" timestamp with time zone NULL,
                "ClearedAt" timestamp with time zone NULL,
                "DishonoredAt" timestamp with time zone NULL,
                "DishonorReason" text NULL,
                "Status" smallint NOT NULL DEFAULT 1,
                "ContactId" uuid NULL,
                "SourceDocumentId" uuid NULL,
                "BankAccountId" uuid NULL,
                "RelatedVoucherId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_Company_Status" ON "PostDatedChecks" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_ScheduledDeposit" ON "PostDatedChecks" ("CompanyId", "ScheduledDepositDate") WHERE "IsDeleted" = false AND "Status" IN (1,2);""",
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_Source" ON "PostDatedChecks" ("SourceDocumentId") WHERE "SourceDocumentId" IS NOT NULL;""",

            // CashAdvanceRequest (เบิก-เคลียร์เงินสดล่วงหน้า)
            """
            CREATE TABLE IF NOT EXISTS "CashAdvanceRequests" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "RequestNumber" text NOT NULL DEFAULT '',
                "EmployeeId" uuid NOT NULL,
                "RequestedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ApprovedAmount" numeric(18,2) NULL,
                "DisbursedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ClearedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "RefundAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "RequestDate" timestamp with time zone NOT NULL,
                "ApprovedAt" timestamp with time zone NULL,
                "DisbursedAt" timestamp with time zone NULL,
                "ClearanceDueDate" timestamp with time zone NULL,
                "ClearedAt" timestamp with time zone NULL,
                "Status" smallint NOT NULL DEFAULT 1,
                "Purpose" text NOT NULL DEFAULT '',
                "ApproverNote" text NULL,
                "RejectionReason" text NULL,
                "ApproverUserId" uuid NULL,
                "DisbursementVoucherId" uuid NULL,
                "ClearanceDocumentIdsJson" text NOT NULL DEFAULT '[]',
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_Company_Status" ON "CashAdvanceRequests" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_Employee" ON "CashAdvanceRequests" ("EmployeeId");""",
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_ClearanceDue" ON "CashAdvanceRequests" ("CompanyId", "ClearanceDueDate") WHERE "Status" IN (3,4);""",

            // EarlyPaymentDiscountTerm (ส่วนลดเงินสด 2/10 net 30)
            """
            CREATE TABLE IF NOT EXISTS "EarlyPaymentDiscountTerms" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Code" text NOT NULL DEFAULT '',
                "DisplayName" text NOT NULL DEFAULT '',
                "DiscountWindowDays" integer NOT NULL DEFAULT 0,
                "DiscountPercent" numeric(8,4) NOT NULL DEFAULT 0,
                "NetTermDays" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EarlyPaymentDiscountTerms_Company" ON "EarlyPaymentDiscountTerms" ("CompanyId") WHERE "IsDeleted" = false;""",

            // ===== Document: EarlyPaymentDiscountTermId — link เอกสารกับ
            // discount term ที่ใช้ (auto-apply ตอน receipt มาถึงในช่วงเวลา).
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "EarlyPaymentDiscountTermId" uuid NULL;""",

            // ===== Document: IsForeignService — ภ.พ.36 self-assess VAT flag
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsForeignService" boolean NOT NULL DEFAULT false;""",

            // ===== StockMovement: warehouse + lot/serial tracking
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "WarehouseId" uuid NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "LotNumber" text NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "SerialNumber" text NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "TransferPairId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_StockMovements_WhProduct" ON "StockMovements" ("WarehouseId", "ProductId") WHERE "WarehouseId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_StockMovements_Lot" ON "StockMovements" ("LotNumber") WHERE "LotNumber" IS NOT NULL;""",
            // NOTE: StockTransfers + StockTransferLines tables มีอยู่แล้ว
            // (สร้างจาก AdvancedOperations migration เดิม) — ไม่สร้างซ้ำ.

            // ===== DocumentComments — collaboration on documents (#23)
            """
            CREATE TABLE IF NOT EXISTS "DocumentComments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EntityType" text NOT NULL DEFAULT 'Document',
                "EntityId" uuid NOT NULL,
                "AuthorUserId" uuid NOT NULL,
                "Body" text NOT NULL DEFAULT '',
                "MentionedUserIdsJson" text NOT NULL DEFAULT '[]',
                "ParentCommentId" uuid NULL,
                "EditedAt" timestamp with time zone NULL,
                "AttachmentUrl" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentComments_Entity" ON "DocumentComments" ("CompanyId", "EntityType", "EntityId") WHERE "IsDeleted" = false;""",

            // ===== ScheduledReports — email digest schedule (#15)
            """
            CREATE TABLE IF NOT EXISTS "ScheduledReports" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Name" text NOT NULL DEFAULT '',
                "ReportCode" text NOT NULL DEFAULT '',
                "Frequency" text NOT NULL DEFAULT 'Weekly',
                "DayOfWeek" integer NULL,
                "DayOfMonth" integer NULL,
                "HourBangkok" integer NOT NULL DEFAULT 8,
                "RecipientsJson" text NOT NULL DEFAULT '[]',
                "IsActive" boolean NOT NULL DEFAULT true,
                "LastSentAt" timestamp with time zone NULL,
                "LastResult" text NULL,
                "Format" text NOT NULL DEFAULT 'PDF',
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ScheduledReports_Company" ON "ScheduledReports" ("CompanyId") WHERE "IsDeleted" = false AND "IsActive" = true;""",

            // ===== ImportConflicts — duplicate detection during import flows
            """
            CREATE TABLE IF NOT EXISTS "ImportConflicts" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "SessionId" uuid NULL,
                "SessionRef" text NULL,
                "RowNumber" integer NOT NULL DEFAULT 0,
                "EntityType" text NOT NULL DEFAULT '',
                "StagedDataJson" text NOT NULL DEFAULT '{}',
                "ExistingEntityId" uuid NULL,
                "ExistingDataJson" text NOT NULL DEFAULT '{}',
                "MatchScore" double precision NOT NULL DEFAULT 0,
                "MatchReason" text NOT NULL DEFAULT '',
                "Resolution" integer NOT NULL DEFAULT 0,
                "ResolvedAt" timestamp with time zone NULL,
                "ResolvedBy" text NULL,
                "MergeChoicesJson" text NULL,
                "UserNote" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_Session" ON "ImportConflicts" ("SessionId") WHERE "SessionId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_SessionRef" ON "ImportConflicts" ("CompanyId", "SessionRef") WHERE "SessionRef" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_Pending" ON "ImportConflicts" ("CompanyId", "Resolution") WHERE "Resolution" = 0 AND "IsDeleted" = false;""",

            // ===== ProductLot — FIFO/FEFO tracking
            """
            CREATE TABLE IF NOT EXISTS "ProductLots" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "ProductId" uuid NOT NULL,
                "LotNumber" text NOT NULL DEFAULT '',
                "ManufactureDate" timestamp with time zone NULL,
                "ExpirationDate" timestamp with time zone NULL,
                "QuantityOnHand" numeric(18,4) NOT NULL DEFAULT 0,
                "UnitCost" numeric(18,4) NOT NULL DEFAULT 0,
                "WarehouseId" uuid NULL,
                "SupplierBatchRef" text NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductLots_Product" ON "ProductLots" ("CompanyId", "ProductId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductLots_Expiry" ON "ProductLots" ("CompanyId", "ExpirationDate") WHERE "ExpirationDate" IS NOT NULL AND "QuantityOnHand" > 0;""",
        };

        foreach (var sql in statements)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
            }
            catch
            {
                // Index may already exist or table may not exist — safe to skip
            }
        }
    }
}
