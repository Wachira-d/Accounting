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
        ];
    }
}
