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
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "IsDuplicate" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "DuplicateOfScanId" uuid NULL;
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
