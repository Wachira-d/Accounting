using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Adds missing columns to existing tables that were created before
/// new entity properties were added. EnsureCreated() does not alter
/// existing tables, so we run ALTER TABLE … ADD COLUMN IF NOT EXISTS guards.
/// </summary>
public static class DatabaseMigrationHelper
{
    public static void ApplyMissingColumns(AccountingDbContext db)
    {
        var statements = GetAlterStatements();
        foreach (var sql in statements)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
            }
            catch
            {
                // Column may already exist or table may not exist yet — safe to skip
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
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxServiceProvider" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxXmlOutputPath" varchar(500) NULL;
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

            // ===== OcrScanResults: duplicate detection fields =====
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "FileHash" varchar(64) NULL;
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
            // OCR self-learning idempotency watermark — set when VendorIntelligenceService
            // counts this document into the per-vendor stats; prevents double-counting on
            // re-approval (Draft → Approved → Rejected → Draft → Approved).
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrIntelTrainedAt" timestamp NULL;
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
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Documents_DocumentNumber_trgm"
            ON "Documents" USING gin ("DocumentNumber" gin_trgm_ops);
            """,

            // === Contacts: searched by Name, TaxId ===
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Contacts_Name_trgm"
            ON "Contacts" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Contacts_TaxId_trgm"
            ON "Contacts" USING gin ("TaxId" gin_trgm_ops);
            """,

            // === Products: searched by Name, Code ===
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Products_Name_trgm"
            ON "Products" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Products_Code_trgm"
            ON "Products" USING gin ("Code" gin_trgm_ops);
            """,

            // === ChartOfAccounts: searched by AccountCode, AccountName ===
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ChartOfAccounts_AccountName_trgm"
            ON "ChartOfAccounts" USING gin ("AccountName" gin_trgm_ops);
            """,

            // === FixedAssets: searched by Name, AssetCode ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'FixedAssets') THEN
                EXECUTE 'CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_FixedAssets_Name_trgm" ON "FixedAssets" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Loans: searched by LoanNumber, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Loans') THEN
                EXECUTE 'CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Loans_Name_trgm" ON "Loans" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Projects: searched by Code, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Projects') THEN
                EXECUTE 'CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Projects_Name_trgm" ON "Projects" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Employees: searched by EmployeeCode, FirstNameTh, LastNameTh, FirstNameEn ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Employees') THEN
                EXECUTE 'CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Employees_Names_trgm" ON "Employees" USING gin (("EmployeeCode" || '' '' || COALESCE("FirstNameTh",'''') || '' '' || COALESCE("LastNameTh",'''') || '' '' || COALESCE("FirstNameEn",'''')) gin_trgm_ops)';
            END IF;
            END $$;
            """,
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
