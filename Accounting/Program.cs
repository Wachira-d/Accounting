using System.Text;
using Accounting.Data;
using Accounting.Hubs;
using Accounting.Middleware;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using FluentValidation;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// PostgreSQL: Allow DateTime without explicit UTC Kind (legacy timestamp behavior)
// This prevents "Cannot write DateTime with Kind=Unspecified" and
// "Cannot apply binary operation on timestamp with/without time zone" errors
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// ===== Database (PostgreSQL) =====
builder.Services.AddDbContext<AccountingDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            // Split queries for multi-Include chains (prevents cartesian explosion)
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            // Command timeout for complex reports/aggregations
            npgsqlOptions.CommandTimeout(120);
        })
    // Log slow queries in development
    .EnableSensitiveDataLogging(builder.Environment.IsDevelopment())
    .EnableDetailedErrors(builder.Environment.IsDevelopment())
);

// ===== Authentication (JWT) =====
// JWT secret: MUST be set via env var in production. Config file fallback for dev only.
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? builder.Configuration["Jwt:Secret"];
if (string.IsNullOrEmpty(jwtSecret))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException("JWT_SECRET environment variable is required in production!");
    // Dev-only auto-generated secret (changes each restart — tokens won't persist)
    jwtSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    Console.WriteLine("⚠ WARNING: Using auto-generated JWT secret. Set JWT_SECRET env var for persistent sessions.");
}
// Write back to configuration so JwtHelper.GenerateToken() uses the same key
builder.Configuration["Jwt:Secret"] = jwtSecret;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // Support SignalR token via query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ===== Services (DI) =====
// Core
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountingService, AccountingService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<ITaxService, TaxService>();
builder.Services.AddScoped<ITaxFilingExportService, TaxFilingExportService>();

// New modules
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IBankService, BankService>();
builder.Services.AddScoped<IFreelanceService, FreelanceService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();

// Additional modules
builder.Services.AddScoped<IRecurringTransactionService, RecurringTransactionService>();
builder.Services.AddScoped<IFixedAssetService, FixedAssetService>();
builder.Services.AddScoped<IApprovalService, ApprovalService>();
builder.Services.AddScoped<IFileAttachmentService, FileAttachmentService>();
builder.Services.AddScoped<ICurrencyService, CurrencyService>();
builder.Services.AddScoped<IBudgetService, BudgetService>();

// Analytics & Reporting modules
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IAgingReportService, AgingReportService>();
builder.Services.AddScoped<IArApAnalysisService, ArApAnalysisService>();
builder.Services.AddScoped<IExpenseClaimService, ExpenseClaimService>();
builder.Services.AddScoped<IImportExportService, ImportExportService>();
builder.Services.AddScoped<IAuditTrailService, AuditTrailService>();
builder.Services.AddScoped<IWithholdingTaxCertService, WithholdingTaxCertService>();

// PDF, Document Templates & e-Tax
builder.Services.AddScoped<IDocumentTemplateService, DocumentTemplateService>();
builder.Services.AddScoped<IPdfGenerationService, PdfGenerationService>();
builder.Services.AddScoped<IEtaxInvoiceService, EtaxInvoiceService>();

// Phase 1: Dimensional Accounting & Branches
builder.Services.AddScoped<IDimensionalAccountingService, DimensionalAccountingService>();

// Phase 2: Core Business
builder.Services.AddScoped<IIntercompanyService, IntercompanyService>();
builder.Services.AddScoped<IConsolidationService, ConsolidationService>();
builder.Services.AddScoped<IPayrollService, PayrollService>();
builder.Services.AddScoped<ITaxCalendarService, TaxCalendarService>();
builder.Services.AddScoped<IAdvancedArApService, AdvancedArApService>();

// Phase 3: Advanced Operations
builder.Services.AddScoped<IProjectAccountingService, ProjectAccountingService>();
builder.Services.AddScoped<IWarehouseService, WarehouseService>();
builder.Services.AddScoped<IRevenueRecognitionService, RevenueRecognitionService>();
builder.Services.AddScoped<ILoanService, LoanService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();

// Phase 4: Intelligence
builder.Services.AddScoped<IAiService, AiService>();
builder.Services.AddScoped<IOcrService, OcrService>();
builder.Services.AddScoped<IReportBuilderService, ReportBuilderService>();
builder.Services.AddScoped<IPortalService, PortalService>();

// Phase 5: World-Class
builder.Services.AddScoped<IFpaService, FpaService>();
builder.Services.AddScoped<IOpenBankingService, OpenBankingService>();
builder.Services.AddScoped<IComplianceService, ComplianceService>();
builder.Services.AddScoped<ITimeBillingService, TimeBillingService>();
builder.Services.AddScoped<IWebhookService, WebhookService>();
builder.Services.AddScoped<IMobileApiService, MobileApiService>();
builder.Services.AddScoped<IDbdLookupService, DbdLookupService>();
builder.Services.AddHttpClient();

// Email service
builder.Services.AddScoped<IEmailService, EmailService>();

// Error logging service
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<IPosService, PosService>();
builder.Services.AddScoped<ILineNotifyService, LineNotifyService>();

// External Integration (TakeTime, PMS, etc.)
builder.Services.AddScoped<IIntegrationService, IntegrationService>();

// SignalR for real-time notifications
builder.Services.AddSignalR();

// Background job scheduler
builder.Services.AddHostedService<BackgroundJobService>();

// ===== Validation =====
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();

// ===== Controllers =====
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ===== Swagger =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Accounting Platform API",
        Version = "v1",
        Description = "ระบบบัญชี SaaS Platform ที่ดีที่สุด - 46 Services, 40+ Controllers | Core Accounting, Tax, Documents, Payroll, Multi-Branch, Cost Center, Intercompany, Consolidation, AR/AP, Project Accounting, Warehouse, Revenue Recognition (TFRS15), Loan, Commission, AI Auto-Categorization, OCR, Report Builder, Customer Portal, FP&A, Open Banking, Compliance, Time & Billing, Webhooks, Mobile API"
    });

    // JWT Bearer Auth
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    // API Key Auth
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key: X-Api-Key {key}",
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });

    // Use full type names to avoid schema ID conflicts between DTOs in different namespaces
    c.CustomSchemaIds(type => type.FullName?.Replace("+", "."));
});

// ===== CORS =====
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:3000" };
        policy.WithOrigins(origins)
            .WithHeaders("Authorization", "Content-Type", "X-Api-Key", "X-Company-Id", "X-Requested-With")
            .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
            .AllowCredentials();
    });
});

// ===== Security Headers =====
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

var app = builder.Build();

// ===== Middleware Pipeline (order matters!) =====

// 1. Exception handling (outermost)
app.UseMiddleware<ExceptionMiddleware>();

// 2. Rate limiting (protect from abuse early)
app.UseMiddleware<RateLimitMiddleware>();

// 3. Security headers & path protection
app.UseMiddleware<SecurityMiddleware>();

// 3.5 Input sanitization (SQL injection / XSS defense-in-depth)
app.UseMiddleware<InputSanitizationMiddleware>();

// 4. Swagger (before auth)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Accounting Platform API v1"));
}

// Enforce HTTPS in production
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}
app.UseCors();

// Static files (frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

// Serve uploaded files (logos, attachments)
var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

// 5. API Key middleware (before JWT auth - alternative auth method)
app.UseMiddleware<ApiKeyMiddleware>();

// 6. Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// 7. Tenant access control (after auth)
app.UseMiddleware<TenantAccessMiddleware>();

// 8. Subscription check
app.UseMiddleware<SubscriptionCheckMiddleware>();

// 9. Audit logging (innermost - logs after response)
app.UseMiddleware<AuditMiddleware>();

app.MapControllers();

// SignalR hubs
app.MapHub<NotificationHub>("/hubs/notifications");

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// DB diagnostic endpoint — checks if critical columns/tables exist (bypasses EF)
app.MapGet("/health/db", (IConfiguration config) =>
{
    var connStr = config.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connStr)) return Results.Ok(new { status = "no_connection_string" });
    try
    {
        using var conn = new Npgsql.NpgsqlConnection(connStr);
        conn.Open();
        var checks = new Dictionary<string, string>();
        // Check IndustryType column
        using (var cmd = new Npgsql.NpgsqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM information_schema.columns WHERE lower(table_name)='companies' AND lower(column_name)='industrytype') THEN 'OK' ELSE 'MISSING' END", conn))
            checks["Companies.IndustryType"] = (string)cmd.ExecuteScalar()!;
        // Check POS tables
        foreach (var tbl in new[] { "PosTerminals", "PosSessions", "PosOrders", "PosOrderItems", "PosPayments", "ServicePackages", "ServiceComponents", "PosServiceActivities", "ProductModifierGroups", "ProductModifierOptions", "ProductModifierGroupLinks", "PosOrderItemModifiers", "StaffCommissionSummaries" })
        {
            using var cmd2 = new Npgsql.NpgsqlCommand($"SELECT CASE WHEN EXISTS(SELECT 1 FROM information_schema.tables WHERE lower(table_name)=lower('{tbl}')) THEN 'OK' ELSE 'MISSING' END", conn);
            checks[tbl] = (string)cmd2.ExecuteScalar()!;
        }
        // Test EF model building
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            _ = db.Model; // Force model building
            checks["EF_ModelBuilding"] = "OK";
        }
        catch (Exception efEx)
        {
            checks["EF_ModelBuilding"] = $"FAILED: {efEx.Message}";
        }
        return Results.Ok(new { status = "connected", checks });
    }
    catch (Exception ex) { return Results.Ok(new { status = "error", message = ex.Message }); }
});

// SPA fallback - serve app.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

// ===== PHASE 0: Raw ADO.NET schema fix (bypass EF model building entirely) =====
// This ensures critical columns exist BEFORE EF tries to build its model.
// If EF model building fails (e.g. new entity configs), ApplyMissingColumns via EF also fails,
// creating a chicken-and-egg problem where login breaks with no error log.
{
    var connStr = app.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrEmpty(connStr))
    {
        try
        {
            using var rawConn = new Npgsql.NpgsqlConnection(connStr);
            rawConn.Open();
            var rawSqlStatements = new[]
            {
                // IndustryType column on Companies
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""IndustryType"" integer NOT NULL DEFAULT 0;",
                // Users: SSO & Security columns
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""AuthProvider"" text NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""AuthProviderId"" text NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""IsSystemAdmin"" boolean NOT NULL DEFAULT false;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""FailedLoginAttempts"" integer NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LockoutEnd"" timestamp NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""PasswordResetToken"" text NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""PasswordResetTokenExpiry"" timestamp NULL;",
                @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""EmailVerified"" boolean NOT NULL DEFAULT false;",
                // Companies: Business Registration & Setup
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""BranchName"" text NULL;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""JuristicId"" text NULL;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""IsVatRegistered"" boolean NOT NULL DEFAULT false;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""VatRate"" decimal(18,2) NOT NULL DEFAULT 7;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""IsWhtRegistered"" boolean NOT NULL DEFAULT true;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""IsSocialSecurityRegistered"" boolean NOT NULL DEFAULT false;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""SocialSecurityAccountNo"" text NULL;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""Fax"" text NULL;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""Website"" text NULL;",
                @"ALTER TABLE ""Companies"" ADD COLUMN IF NOT EXISTS ""IsSetupComplete"" boolean NOT NULL DEFAULT false;",
                // DocumentLines: WHT income type
                @"ALTER TABLE ""DocumentLines"" ADD COLUMN IF NOT EXISTS ""IncomeTypeCode"" text NULL;",
                // Subscriptions: Notification settings
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyBeforeExpiry"" boolean NOT NULL DEFAULT true;",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyDaysBeforeExpiry"" text NOT NULL DEFAULT '30,15,7,3,1';",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyOnExpiry"" boolean NOT NULL DEFAULT true;",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyAfterExpiry"" boolean NOT NULL DEFAULT true;",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyDaysAfterExpiry"" text NOT NULL DEFAULT '1,3,7';",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""DeactivationDaysAfterExpiry"" integer NOT NULL DEFAULT 14;",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyBeforeDeactivation"" boolean NOT NULL DEFAULT true;",
                @"ALTER TABLE ""Subscriptions"" ADD COLUMN IF NOT EXISTS ""NotifyDaysBeforeDeactivation"" text NOT NULL DEFAULT '7,3,1';",
                // POS tables (create if missing - ordered by FK dependency)
                @"CREATE TABLE IF NOT EXISTS ""PosTerminals"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""Name"" varchar(200) NOT NULL, ""BusinessMode"" integer NOT NULL DEFAULT 1,
                      ""IsActive"" boolean NOT NULL DEFAULT true, ""Location"" varchar(500) NULL, ""SettingsJson"" text NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosTerminals"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosTerminals_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosSessions"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""TerminalId"" uuid NOT NULL, ""OpenedByUserId"" uuid NOT NULL,
                      ""ClosedByUserId"" uuid NULL, ""OpenedAt"" timestamp NOT NULL DEFAULT now(),
                      ""ClosedAt"" timestamp NULL, ""OpeningBalance"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""ClosingBalance"" decimal(18,2) NOT NULL DEFAULT 0, ""ExpectedBalance"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""Status"" integer NOT NULL DEFAULT 1, ""Notes"" text NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosSessions"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosSessions_PosTerminals"" FOREIGN KEY (""TerminalId"") REFERENCES ""PosTerminals""(""Id""),
                      CONSTRAINT ""FK_PosSessions_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""ServicePackages"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""Name"" varchar(500) NOT NULL, ""NameEn"" text NULL, ""Description"" text NULL,
                      ""Sku"" varchar(50) NULL, ""Category"" varchar(200) NULL,
                      ""Price"" decimal(18,2) NOT NULL DEFAULT 0, ""CostPrice"" decimal(18,2) NULL,
                      ""DurationMinutes"" integer NOT NULL DEFAULT 0, ""IsActive"" boolean NOT NULL DEFAULT true,
                      ""IsVatIncluded"" boolean NOT NULL DEFAULT true, ""RevenueAccountId"" uuid NULL,
                      ""ImageUrl"" text NULL, ""SortOrder"" integer NOT NULL DEFAULT 0,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ServicePackages"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ServicePackages_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosOrders"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""SessionId"" uuid NOT NULL, ""OrderNumber"" varchar(50) NOT NULL,
                      ""OrderType"" integer NOT NULL DEFAULT 0, ""Status"" integer NOT NULL DEFAULT 0,
                      ""CustomerId"" uuid NULL, ""CustomerName"" text NULL,
                      ""TableNumber"" text NULL, ""GuestCount"" integer NULL, ""QueueNumber"" text NULL,
                      ""AppointmentTime"" timestamp NULL, ""PrimaryStaffId"" uuid NULL,
                      ""SubTotal"" decimal(18,2) NOT NULL DEFAULT 0, ""DiscountAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""DiscountPercent"" decimal(5,2) NOT NULL DEFAULT 0, ""ServiceChargePercent"" decimal(5,2) NOT NULL DEFAULT 0,
                      ""ServiceChargeAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""VatAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""TotalAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""RoundingAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""NetAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""Notes"" text NULL, ""Reference"" text NULL,
                      ""JournalEntryId"" uuid NULL, ""DocumentId"" uuid NULL, ""CompletedAt"" timestamp NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosOrders"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosOrders_PosSessions"" FOREIGN KEY (""SessionId"") REFERENCES ""PosSessions""(""Id""),
                      CONSTRAINT ""FK_PosOrders_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""ServiceComponents"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""PackageId"" uuid NOT NULL, ""StepOrder"" integer NOT NULL DEFAULT 0,
                      ""Name"" varchar(500) NOT NULL, ""NameEn"" text NULL, ""Description"" text NULL,
                      ""DurationMinutes"" integer NOT NULL DEFAULT 0, ""CommissionType"" integer NOT NULL DEFAULT 1,
                      ""CommissionValue"" decimal(18,2) NOT NULL DEFAULT 0, ""RequiresStaff"" boolean NOT NULL DEFAULT true,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ServiceComponents"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ServiceComponents_ServicePackages"" FOREIGN KEY (""PackageId"") REFERENCES ""ServicePackages""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosOrderItems"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""OrderId"" uuid NOT NULL, ""ProductId"" uuid NULL, ""ServicePackageId"" uuid NULL,
                      ""ItemName"" text NOT NULL, ""ItemCode"" text NULL,
                      ""Quantity"" decimal(18,4) NOT NULL DEFAULT 1, ""Unit"" text NULL,
                      ""UnitPrice"" decimal(18,2) NOT NULL DEFAULT 0, ""DiscountAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""DiscountPercent"" decimal(5,2) NOT NULL DEFAULT 0, ""SubTotal"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""VatAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""TotalAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""LineOrder"" integer NOT NULL DEFAULT 0, ""Status"" integer NOT NULL DEFAULT 0, ""Notes"" text NULL,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosOrderItems"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosOrderItems_PosOrders"" FOREIGN KEY (""OrderId"") REFERENCES ""PosOrders""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""ProductModifierGroups"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""Name"" varchar(200) NOT NULL, ""NameEn"" text NULL,
                      ""IsRequired"" boolean NOT NULL DEFAULT false, ""AllowMultiple"" boolean NOT NULL DEFAULT false, ""SortOrder"" integer NOT NULL DEFAULT 0,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ProductModifierGroups"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ProductModifierGroups_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""ProductModifierOptions"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""GroupId"" uuid NOT NULL, ""Name"" varchar(200) NOT NULL, ""NameEn"" text NULL,
                      ""PriceAdjustment"" decimal(18,2) NOT NULL DEFAULT 0, ""IsDefault"" boolean NOT NULL DEFAULT false,
                      ""SortOrder"" integer NOT NULL DEFAULT 0, ""IsActive"" boolean NOT NULL DEFAULT true,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ProductModifierOptions"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ProductModifierOptions_ProductModifierGroups"" FOREIGN KEY (""GroupId"") REFERENCES ""ProductModifierGroups""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosOrderItemModifiers"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""OrderItemId"" uuid NOT NULL, ""ModifierOptionId"" uuid NULL,
                      ""ModifierGroupName"" varchar(200) NOT NULL, ""ModifierName"" varchar(200) NOT NULL,
                      ""PriceAdjustment"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosOrderItemModifiers"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosOrderItemModifiers_PosOrderItems"" FOREIGN KEY (""OrderItemId"") REFERENCES ""PosOrderItems""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosPayments"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""OrderId"" uuid NOT NULL, ""PaymentMethod"" integer NOT NULL DEFAULT 0,
                      ""Amount"" decimal(18,2) NOT NULL DEFAULT 0, ""ReceivedAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""ChangeAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""ReferenceNo"" varchar(200) NULL, ""CardLastFour"" varchar(4) NULL,
                      ""PaidAt"" timestamp NOT NULL DEFAULT now(),
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosPayments"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosPayments_PosOrders"" FOREIGN KEY (""OrderId"") REFERENCES ""PosOrders""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""PosServiceActivities"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""OrderItemId"" uuid NOT NULL, ""ComponentId"" uuid NOT NULL,
                      ""StaffId"" uuid NULL, ""StaffName"" varchar(200) NULL,
                      ""Status"" integer NOT NULL DEFAULT 0, ""StartedAt"" timestamp NULL, ""CompletedAt"" timestamp NULL,
                      ""CommissionAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""Notes"" text NULL,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_PosServiceActivities"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_PosServiceActivities_PosOrderItems"" FOREIGN KEY (""OrderItemId"") REFERENCES ""PosOrderItems""(""Id""),
                      CONSTRAINT ""FK_PosServiceActivities_ServiceComponents"" FOREIGN KEY (""ComponentId"") REFERENCES ""ServiceComponents""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""ProductModifierGroupLinks"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""ProductId"" uuid NOT NULL, ""ModifierGroupId"" uuid NOT NULL,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ProductModifierGroupLinks"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ProductModifierGroupLinks_Products"" FOREIGN KEY (""ProductId"") REFERENCES ""Products""(""Id""),
                      CONSTRAINT ""FK_ProductModifierGroupLinks_ProductModifierGroups"" FOREIGN KEY (""ModifierGroupId"") REFERENCES ""ProductModifierGroups""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""StaffCommissionSummaries"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""StaffId"" uuid NOT NULL, ""StaffName"" varchar(200) NOT NULL,
                      ""PeriodStart"" timestamp NOT NULL, ""PeriodEnd"" timestamp NOT NULL,
                      ""TotalActivities"" integer NOT NULL DEFAULT 0, ""TotalCommission"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""PaidAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""RemainingAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""IsPaid"" boolean NOT NULL DEFAULT false, ""JournalEntryId"" uuid NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_StaffCommissionSummaries"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_StaffCommissionSummaries_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                // Contact Inquiries (public contact form)
                @"CREATE TABLE IF NOT EXISTS ""ContactInquiries"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""Name"" varchar(500) NOT NULL, ""Email"" varchar(500) NOT NULL,
                      ""Phone"" varchar(50) NULL, ""Company"" varchar(500) NULL,
                      ""Subject"" varchar(1000) NOT NULL, ""Message"" text NOT NULL,
                      ""IsRead"" boolean NOT NULL DEFAULT false, ""IsReplied"" boolean NOT NULL DEFAULT false,
                      ""IpAddress"" varchar(100) NULL,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ContactInquiries"" PRIMARY KEY (""Id"")
                  );",
                // External Integration tables
                @"CREATE TABLE IF NOT EXISTS ""ExternalIntegrations"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""SystemName"" varchar(200) NOT NULL, ""SystemType"" varchar(50) NOT NULL DEFAULT 'PMS',
                      ""SystemVersion"" text NULL, ""BaseUrl"" text NULL,
                      ""ApiKey"" text NOT NULL DEFAULT '', ""ApiKeyHash"" text NOT NULL DEFAULT '',
                      ""ApiKeyPrefix"" varchar(20) NOT NULL DEFAULT '', ""SecretKey"" text NULL,
                      ""IsActive"" boolean NOT NULL DEFAULT true,
                      ""LastSyncAt"" timestamp NULL, ""TotalSyncCount"" integer NOT NULL DEFAULT 0,
                      ""ErrorCount"" integer NOT NULL DEFAULT 0, ""ConsecutiveErrors"" integer NOT NULL DEFAULT 0,
                      ""MappingConfigJson"" jsonb NULL, ""SettingsJson"" jsonb NULL,
                      ""RateLimitPerMinute"" integer NOT NULL DEFAULT 60,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_ExternalIntegrations"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_ExternalIntegrations_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""IntegrationSyncLogs"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""IntegrationId"" uuid NOT NULL,
                      ""EventType"" varchar(100) NOT NULL, ""ExternalId"" text NULL, ""ExternalRef"" text NULL,
                      ""Status"" varchar(50) NOT NULL DEFAULT 'Pending',
                      ""RequestPayloadJson"" jsonb NULL, ""ResponseJson"" jsonb NULL, ""ErrorMessage"" text NULL,
                      ""CreatedDocumentId"" uuid NULL, ""CreatedContactId"" uuid NULL,
                      ""CreatedJournalEntryId"" uuid NULL, ""CreatedPaymentId"" uuid NULL,
                      ""ProcessingTimeMs"" integer NOT NULL DEFAULT 0,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_IntegrationSyncLogs"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_IntegrationSyncLogs_ExternalIntegrations"" FOREIGN KEY (""IntegrationId"") REFERENCES ""ExternalIntegrations""(""Id""),
                      CONSTRAINT ""FK_IntegrationSyncLogs_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                @"CREATE TABLE IF NOT EXISTS ""IntegrationAccountMappings"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""IntegrationId"" uuid NOT NULL,
                      ""ExternalCategory"" varchar(200) NOT NULL, ""ExternalCode"" varchar(100) NULL,
                      ""ExternalDescription"" text NULL,
                      ""DebitAccountId"" uuid NULL, ""CreditAccountId"" uuid NULL,
                      ""JournalDescription"" text NULL,
                      ""IsActive"" boolean NOT NULL DEFAULT true, ""AutoCreateJournal"" boolean NOT NULL DEFAULT true,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_IntegrationAccountMappings"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_IntegrationAccountMappings_ExternalIntegrations"" FOREIGN KEY (""IntegrationId"") REFERENCES ""ExternalIntegrations""(""Id""),
                      CONSTRAINT ""FK_IntegrationAccountMappings_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );"
            };
            foreach (var sql in rawSqlStatements)
            {
                try
                {
                    using var cmd = new Npgsql.NpgsqlCommand(sql, rawConn);
                    cmd.ExecuteNonQuery();
                }
                catch { /* table/column already exists or FK target missing — safe to skip */ }
            }

            // Fix timestamp column types: convert any 'timestamptz' to 'timestamp without time zone'
            // This prevents "Cannot apply binary operation on timestamp with/without time zone" errors
            // when columns were created before Npgsql.EnableLegacyTimestampBehavior was set.
            try
            {
                var fixTimestampSql = @"
                    DO $$
                    DECLARE r RECORD;
                    BEGIN
                        FOR r IN
                            SELECT table_name, column_name
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND data_type = 'timestamp with time zone'
                        LOOP
                            EXECUTE format('ALTER TABLE %I ALTER COLUMN %I TYPE timestamp without time zone USING %I AT TIME ZONE ''UTC''',
                                r.table_name, r.column_name, r.column_name);
                        END LOOP;
                    END $$;";
                using var fixCmd = new Npgsql.NpgsqlCommand(fixTimestampSql, rawConn);
                fixCmd.CommandTimeout = 120;
                fixCmd.ExecuteNonQuery();
            }
            catch (Exception tsEx)
            {
                var tsLogger = app.Services.GetRequiredService<ILogger<Program>>();
                tsLogger.LogWarning(tsEx, "Timestamp column type fix had issues (non-critical)");
            }

            rawConn.Close();
        }
        catch (Exception rawEx)
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(rawEx, "Raw ADO.NET schema fix failed (DB may not exist yet)");
        }
    }
}

// ===== Auto-migrate & seed data =====
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

    // Use Migrate() if migrations exist, fallback to EnsureCreated()
    try
    {
        if (db.Database.GetPendingMigrations().Any())
        {
            db.Database.Migrate();
        }
        else
        {
            var created = db.Database.EnsureCreated();
            if (!created)
            {
                // Database already exists but may be missing new tables.
                // EnsureCreated() only creates schema when the DB is brand-new.
                // Split the full DDL script and execute each statement individually
                // so missing tables/indexes get created while existing ones are safely skipped.
                var script = db.Database.GenerateCreateScript();
                // PostgreSQL uses semicolons, not GO batches
                var statements = script.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var statement in statements)
                {
                    if (string.IsNullOrWhiteSpace(statement)) continue;
                    try
                    {
                        db.Database.ExecuteSqlRaw(statement + ";");
                    }
                    catch
                    {
                        // Table/index/constraint already exists — safe to ignore
                    }
                }
            }
        }
    }
    catch (Exception migrateEx)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(migrateEx, "Schema migration/creation had issues, continuing with ApplyMissingColumns");
    }

    // CRITICAL: Always run ApplyMissingColumns even if the above migration fails.
    // This ensures new columns (like IndustryType) are added to existing tables.
    DatabaseMigrationHelper.ApplyMissingColumns(db);

    // PostgreSQL full-text search: pg_trgm GIN indexes for fast LIKE/ILIKE searches
    DatabaseMigrationHelper.ApplyFullTextSearchIndexes(db);

    // Seed default plan templates & admin user
    await SeedPlanTemplates.SeedAsync(db);
    await SeedAdminUser.SeedAsync(db, app.Configuration);
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Failed to initialize database. Check your ConnectionStrings:DefaultConnection in appsettings.Production.json");

    // Last resort: try ApplyMissingColumns in a separate scope
    try
    {
        using var retryScope = app.Services.CreateScope();
        var retryDb = retryScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        DatabaseMigrationHelper.ApplyMissingColumns(retryDb);
    }
    catch { /* DB itself may be unavailable */ }

    // Try to log startup error to DB if possible
    try
    {
        using var errorScope = app.Services.CreateScope();
        var errorLogService = errorScope.ServiceProvider.GetService<IErrorLogService>();
        if (errorLogService != null)
            await errorLogService.LogErrorAsync(ex, "Program.DatabaseInitialization");
    }
    catch { /* DB itself may be unavailable */ }
}

app.Run();
