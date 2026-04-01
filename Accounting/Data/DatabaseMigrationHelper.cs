using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Adds missing columns to existing tables that were created before
/// new entity properties were added. EnsureCreated() does not alter
/// existing tables, so we run ALTER TABLE … ADD with IF-NOT-EXISTS guards.
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
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'FailedLoginAttempts')
                ALTER TABLE [Users] ADD [FailedLoginAttempts] int NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'LockoutEnd')
                ALTER TABLE [Users] ADD [LockoutEnd] datetime2 NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'PasswordResetToken')
                ALTER TABLE [Users] ADD [PasswordResetToken] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'PasswordResetTokenExpiry')
                ALTER TABLE [Users] ADD [PasswordResetTokenExpiry] datetime2 NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'EmailVerified')
                ALTER TABLE [Users] ADD [EmailVerified] bit NOT NULL DEFAULT 0;
            """,

            // ===== Documents: currency field =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Documents') AND name = 'Currency')
                ALTER TABLE [Documents] ADD [Currency] nvarchar(3) NOT NULL DEFAULT 'THB';
            """,

            // ===== DocumentLines: ProductCode =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('DocumentLines') AND name = 'ProductCode')
                ALTER TABLE [DocumentLines] ADD [ProductCode] nvarchar(50) NULL;
            """,

            // ===== CompanySettings: e-Tax fields =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxEnabled')
                ALTER TABLE [CompanySettings] ADD [EtaxEnabled] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxCertificatePath')
                ALTER TABLE [CompanySettings] ADD [EtaxCertificatePath] nvarchar(500) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxCertificatePassword')
                ALTER TABLE [CompanySettings] ADD [EtaxCertificatePassword] nvarchar(500) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxRdApiKey')
                ALTER TABLE [CompanySettings] ADD [EtaxRdApiKey] nvarchar(500) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxRdApiSecret')
                ALTER TABLE [CompanySettings] ADD [EtaxRdApiSecret] nvarchar(500) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxTestMode')
                ALTER TABLE [CompanySettings] ADD [EtaxTestMode] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxAutoSign')
                ALTER TABLE [CompanySettings] ADD [EtaxAutoSign] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxAutoSubmit')
                ALTER TABLE [CompanySettings] ADD [EtaxAutoSubmit] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxServiceProvider')
                ALTER TABLE [CompanySettings] ADD [EtaxServiceProvider] nvarchar(100) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'EtaxXmlOutputPath')
                ALTER TABLE [CompanySettings] ADD [EtaxXmlOutputPath] nvarchar(500) NULL;
            """,

            // ===== Companies: IndustryType =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IndustryType')
                ALTER TABLE [Companies] ADD [IndustryType] int NOT NULL DEFAULT 0;
            """,

            // ===== Users: SSO columns =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'AuthProvider')
                ALTER TABLE [Users] ADD [AuthProvider] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'AuthProviderId')
                ALTER TABLE [Users] ADD [AuthProviderId] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'IsSystemAdmin')
                ALTER TABLE [Users] ADD [IsSystemAdmin] bit NOT NULL DEFAULT 0;
            """,

            // ===== Companies: Business Registration & Setup =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'BranchName')
                ALTER TABLE [Companies] ADD [BranchName] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'JuristicId')
                ALTER TABLE [Companies] ADD [JuristicId] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IsVatRegistered')
                ALTER TABLE [Companies] ADD [IsVatRegistered] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'VatRate')
                ALTER TABLE [Companies] ADD [VatRate] decimal(18,2) NOT NULL DEFAULT 7;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IsWhtRegistered')
                ALTER TABLE [Companies] ADD [IsWhtRegistered] bit NOT NULL DEFAULT 1;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IsSocialSecurityRegistered')
                ALTER TABLE [Companies] ADD [IsSocialSecurityRegistered] bit NOT NULL DEFAULT 0;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'SocialSecurityAccountNo')
                ALTER TABLE [Companies] ADD [SocialSecurityAccountNo] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'Fax')
                ALTER TABLE [Companies] ADD [Fax] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'Website')
                ALTER TABLE [Companies] ADD [Website] nvarchar(max) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IsSetupComplete')
                ALTER TABLE [Companies] ADD [IsSetupComplete] bit NOT NULL DEFAULT 0;
            """,

            // ===== DocumentLines: WHT income type =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('DocumentLines') AND name = 'IncomeTypeCode')
                ALTER TABLE [DocumentLines] ADD [IncomeTypeCode] nvarchar(max) NULL;
            """,

            // ===== Subscriptions: Notification settings =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyBeforeExpiry')
                ALTER TABLE [Subscriptions] ADD [NotifyBeforeExpiry] bit NOT NULL DEFAULT 1;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyDaysBeforeExpiry')
                ALTER TABLE [Subscriptions] ADD [NotifyDaysBeforeExpiry] nvarchar(max) NOT NULL DEFAULT '30,15,7,3,1';
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyOnExpiry')
                ALTER TABLE [Subscriptions] ADD [NotifyOnExpiry] bit NOT NULL DEFAULT 1;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyAfterExpiry')
                ALTER TABLE [Subscriptions] ADD [NotifyAfterExpiry] bit NOT NULL DEFAULT 1;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyDaysAfterExpiry')
                ALTER TABLE [Subscriptions] ADD [NotifyDaysAfterExpiry] nvarchar(max) NOT NULL DEFAULT '1,3,7';
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'DeactivationDaysAfterExpiry')
                ALTER TABLE [Subscriptions] ADD [DeactivationDaysAfterExpiry] int NOT NULL DEFAULT 14;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyBeforeDeactivation')
                ALTER TABLE [Subscriptions] ADD [NotifyBeforeDeactivation] bit NOT NULL DEFAULT 1;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Subscriptions') AND name = 'NotifyDaysBeforeDeactivation')
                ALTER TABLE [Subscriptions] ADD [NotifyDaysBeforeDeactivation] nvarchar(max) NOT NULL DEFAULT '7,3,1';
            """,

            // ===== CompanySettings: Landing page services =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'LandingContactPhone')
                ALTER TABLE [CompanySettings] ADD [LandingContactPhone] nvarchar(50) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'LandingContactLine')
                ALTER TABLE [CompanySettings] ADD [LandingContactLine] nvarchar(100) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'LandingContactEmail')
                ALTER TABLE [CompanySettings] ADD [LandingContactEmail] nvarchar(200) NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CompanySettings') AND name = 'LandingServicesJson')
                ALTER TABLE [CompanySettings] ADD [LandingServicesJson] nvarchar(max) NULL;
            """,

            // ===== POS Tables =====
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTerminals')
            CREATE TABLE [PosTerminals] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [Name] nvarchar(200) NOT NULL,
                [BusinessMode] int NOT NULL,
                [IsActive] bit NOT NULL DEFAULT 1,
                [Location] nvarchar(500) NULL,
                [SettingsJson] nvarchar(max) NULL,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosTerminals] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosTerminals_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosSessions')
            CREATE TABLE [PosSessions] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [TerminalId] uniqueidentifier NOT NULL,
                [OpenedByUserId] uniqueidentifier NOT NULL,
                [ClosedByUserId] uniqueidentifier NULL,
                [OpenedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [ClosedAt] datetime2 NULL,
                [OpeningBalance] decimal(18,2) NOT NULL,
                [ClosingBalance] decimal(18,2) NOT NULL DEFAULT 0,
                [ExpectedBalance] decimal(18,2) NOT NULL DEFAULT 0,
                [Status] int NOT NULL DEFAULT 1,
                [Notes] nvarchar(max) NULL,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosSessions] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosSessions_PosTerminals] FOREIGN KEY ([TerminalId]) REFERENCES [PosTerminals]([Id]),
                CONSTRAINT [FK_PosSessions_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrders')
            CREATE TABLE [PosOrders] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [SessionId] uniqueidentifier NOT NULL,
                [OrderNumber] nvarchar(50) NOT NULL,
                [OrderType] int NOT NULL,
                [Status] int NOT NULL DEFAULT 0,
                [CustomerId] uniqueidentifier NULL,
                [CustomerName] nvarchar(500) NULL,
                [TableNumber] nvarchar(50) NULL,
                [GuestCount] int NULL,
                [QueueNumber] nvarchar(50) NULL,
                [AppointmentTime] datetime2 NULL,
                [PrimaryStaffId] uniqueidentifier NULL,
                [SubTotal] decimal(18,2) NOT NULL DEFAULT 0,
                [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [DiscountPercent] decimal(5,2) NOT NULL DEFAULT 0,
                [ServiceChargePercent] decimal(5,2) NOT NULL DEFAULT 0,
                [ServiceChargeAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [VatAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [TotalAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [RoundingAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [NetAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [Notes] nvarchar(max) NULL,
                [Reference] nvarchar(200) NULL,
                [JournalEntryId] uniqueidentifier NULL,
                [DocumentId] uniqueidentifier NULL,
                [CompletedAt] datetime2 NULL,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosOrders] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosOrders_PosSessions] FOREIGN KEY ([SessionId]) REFERENCES [PosSessions]([Id]),
                CONSTRAINT [FK_PosOrders_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ServicePackages')
            CREATE TABLE [ServicePackages] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [Name] nvarchar(500) NOT NULL,
                [NameEn] nvarchar(500) NULL,
                [Description] nvarchar(max) NULL,
                [Sku] nvarchar(50) NULL,
                [Category] nvarchar(200) NULL,
                [Price] decimal(18,2) NOT NULL,
                [CostPrice] decimal(18,2) NULL,
                [DurationMinutes] int NOT NULL,
                [IsActive] bit NOT NULL DEFAULT 1,
                [IsVatIncluded] bit NOT NULL DEFAULT 1,
                [RevenueAccountId] uniqueidentifier NULL,
                [ImageUrl] nvarchar(max) NULL,
                [SortOrder] int NOT NULL DEFAULT 0,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ServicePackages] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ServicePackages_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ServiceComponents')
            CREATE TABLE [ServiceComponents] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [PackageId] uniqueidentifier NOT NULL,
                [StepOrder] int NOT NULL,
                [Name] nvarchar(500) NOT NULL,
                [NameEn] nvarchar(500) NULL,
                [Description] nvarchar(max) NULL,
                [DurationMinutes] int NOT NULL,
                [CommissionType] int NOT NULL DEFAULT 1,
                [CommissionValue] decimal(18,2) NOT NULL DEFAULT 0,
                [RequiresStaff] bit NOT NULL DEFAULT 1,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ServiceComponents] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ServiceComponents_ServicePackages] FOREIGN KEY ([PackageId]) REFERENCES [ServicePackages]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrderItems')
            CREATE TABLE [PosOrderItems] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [OrderId] uniqueidentifier NOT NULL,
                [ProductId] uniqueidentifier NULL,
                [ServicePackageId] uniqueidentifier NULL,
                [ItemName] nvarchar(500) NOT NULL,
                [ItemCode] nvarchar(50) NULL,
                [Quantity] decimal(18,4) NOT NULL DEFAULT 1,
                [Unit] nvarchar(50) NULL,
                [UnitPrice] decimal(18,2) NOT NULL,
                [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [DiscountPercent] decimal(5,2) NOT NULL DEFAULT 0,
                [SubTotal] decimal(18,2) NOT NULL DEFAULT 0,
                [VatAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [TotalAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [LineOrder] int NOT NULL DEFAULT 0,
                [Status] int NOT NULL DEFAULT 0,
                [Notes] nvarchar(max) NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosOrderItems] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosOrderItems_PosOrders] FOREIGN KEY ([OrderId]) REFERENCES [PosOrders]([Id]),
                CONSTRAINT [FK_PosOrderItems_Products] FOREIGN KEY ([ProductId]) REFERENCES [Products]([Id]),
                CONSTRAINT [FK_PosOrderItems_ServicePackages] FOREIGN KEY ([ServicePackageId]) REFERENCES [ServicePackages]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrderItemModifiers')
            CREATE TABLE [PosOrderItemModifiers] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [OrderItemId] uniqueidentifier NOT NULL,
                [ModifierOptionId] uniqueidentifier NULL,
                [ModifierGroupName] nvarchar(200) NOT NULL,
                [ModifierName] nvarchar(200) NOT NULL,
                [PriceAdjustment] decimal(18,2) NOT NULL DEFAULT 0,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosOrderItemModifiers] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosOrderItemModifiers_PosOrderItems] FOREIGN KEY ([OrderItemId]) REFERENCES [PosOrderItems]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosPayments')
            CREATE TABLE [PosPayments] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [OrderId] uniqueidentifier NOT NULL,
                [PaymentMethod] int NOT NULL,
                [Amount] decimal(18,2) NOT NULL,
                [ReceivedAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [ChangeAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [ReferenceNo] nvarchar(200) NULL,
                [CardLastFour] nvarchar(4) NULL,
                [PaidAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosPayments] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosPayments_PosOrders] FOREIGN KEY ([OrderId]) REFERENCES [PosOrders]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosServiceActivities')
            CREATE TABLE [PosServiceActivities] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [OrderItemId] uniqueidentifier NOT NULL,
                [ComponentId] uniqueidentifier NOT NULL,
                [StaffId] uniqueidentifier NULL,
                [StaffName] nvarchar(200) NULL,
                [Status] int NOT NULL DEFAULT 0,
                [StartedAt] datetime2 NULL,
                [CompletedAt] datetime2 NULL,
                [CommissionAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [Notes] nvarchar(max) NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_PosServiceActivities] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_PosServiceActivities_PosOrderItems] FOREIGN KEY ([OrderItemId]) REFERENCES [PosOrderItems]([Id]),
                CONSTRAINT [FK_PosServiceActivities_ServiceComponents] FOREIGN KEY ([ComponentId]) REFERENCES [ServiceComponents]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierGroups')
            CREATE TABLE [ProductModifierGroups] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [Name] nvarchar(200) NOT NULL,
                [NameEn] nvarchar(200) NULL,
                [IsRequired] bit NOT NULL DEFAULT 0,
                [AllowMultiple] bit NOT NULL DEFAULT 0,
                [SortOrder] int NOT NULL DEFAULT 0,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ProductModifierGroups] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ProductModifierGroups_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierGroupLinks')
            CREATE TABLE [ProductModifierGroupLinks] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [ProductId] uniqueidentifier NOT NULL,
                [ModifierGroupId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ProductModifierGroupLinks] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ProductModifierGroupLinks_Products] FOREIGN KEY ([ProductId]) REFERENCES [Products]([Id]),
                CONSTRAINT [FK_ProductModifierGroupLinks_Groups] FOREIGN KEY ([ModifierGroupId]) REFERENCES [ProductModifierGroups]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierOptions')
            CREATE TABLE [ProductModifierOptions] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [GroupId] uniqueidentifier NOT NULL,
                [Name] nvarchar(200) NOT NULL,
                [NameEn] nvarchar(200) NULL,
                [PriceAdjustment] decimal(18,2) NOT NULL DEFAULT 0,
                [IsDefault] bit NOT NULL DEFAULT 0,
                [SortOrder] int NOT NULL DEFAULT 0,
                [IsActive] bit NOT NULL DEFAULT 1,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ProductModifierOptions] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ProductModifierOptions_Groups] FOREIGN KEY ([GroupId]) REFERENCES [ProductModifierGroups]([Id])
            );
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StaffCommissionSummaries')
            CREATE TABLE [StaffCommissionSummaries] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [StaffId] uniqueidentifier NOT NULL,
                [StaffName] nvarchar(200) NOT NULL,
                [PeriodStart] datetime2 NOT NULL,
                [PeriodEnd] datetime2 NOT NULL,
                [TotalActivities] int NOT NULL DEFAULT 0,
                [TotalCommission] decimal(18,2) NOT NULL DEFAULT 0,
                [PaidAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [RemainingAmount] decimal(18,2) NOT NULL DEFAULT 0,
                [IsPaid] bit NOT NULL DEFAULT 0,
                [JournalEntryId] uniqueidentifier NULL,
                [CompanyId] uniqueidentifier NOT NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_StaffCommissionSummaries] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_StaffCommissionSummaries_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
            );
            """,

            // Contact Inquiries (public contact form)
            """
            -- ===== JournalEntries: JournalType column =====
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('JournalEntries') AND name = 'JournalType')
                ALTER TABLE [JournalEntries] ADD [JournalType] int NOT NULL DEFAULT 0;
            """,
            """
            -- ===== WithholdingTaxCerts: DocumentId column =====
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WithholdingTaxCerts') AND name = 'DocumentId')
                ALTER TABLE [WithholdingTaxCerts] ADD [DocumentId] uniqueidentifier NULL;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ContactInquiries')
            CREATE TABLE [ContactInquiries] (
                [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                [Name] nvarchar(500) NOT NULL,
                [Email] nvarchar(500) NOT NULL,
                [Phone] nvarchar(50) NULL,
                [Company] nvarchar(500) NULL,
                [Subject] nvarchar(1000) NOT NULL,
                [Message] nvarchar(max) NOT NULL,
                [IsRead] bit NOT NULL DEFAULT 0,
                [IsReplied] bit NOT NULL DEFAULT 0,
                [IpAddress] nvarchar(100) NULL,
                [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                [UpdatedAt] datetime2 NULL,
                [CreatedBy] nvarchar(max) NULL,
                [UpdatedBy] nvarchar(max) NULL,
                [IsDeleted] bit NOT NULL DEFAULT 0,
                CONSTRAINT [PK_ContactInquiries] PRIMARY KEY ([Id])
            );
            """,
        ];
    }
}
