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

// ===== Database (MSSQL) =====
builder.Services.AddDbContext<AccountingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ===== Authentication (JWT) =====
// JWT secret: prefer environment variable, fallback to config
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT secret is not configured. Set JWT_SECRET environment variable or Jwt:Secret in appsettings.");

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

// 3. Security headers
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

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
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        conn.Open();
        var checks = new Dictionary<string, string>();
        // Check IndustryType column
        using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('Companies') AND name='IndustryType') THEN 'OK' ELSE 'MISSING' END", conn))
            checks["Companies.IndustryType"] = (string)cmd.ExecuteScalar()!;
        // Check POS tables
        foreach (var tbl in new[] { "PosTerminals", "PosSessions", "PosOrders", "PosOrderItems", "PosPayments", "ServicePackages", "ServiceComponents", "PosServiceActivities", "ProductModifierGroups", "ProductModifierOptions", "ProductModifierGroupLinks", "PosOrderItemModifiers", "StaffCommissionSummaries" })
        {
            using var cmd2 = new Microsoft.Data.SqlClient.SqlCommand($"SELECT CASE WHEN EXISTS(SELECT 1 FROM sys.tables WHERE name='{tbl}') THEN 'OK' ELSE 'MISSING' END", conn);
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
            using var rawConn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            rawConn.Open();
            var rawSqlStatements = new[]
            {
                // IndustryType column on Companies
                @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Companies') AND name = 'IndustryType')
                  ALTER TABLE [Companies] ADD [IndustryType] int NOT NULL DEFAULT 0;",
                // POS tables (create if missing - ordered by FK dependency)
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTerminals')
                  CREATE TABLE [PosTerminals] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [Name] nvarchar(200) NOT NULL, [BusinessMode] int NOT NULL DEFAULT 1,
                      [IsActive] bit NOT NULL DEFAULT 1, [Location] nvarchar(500) NULL, [SettingsJson] nvarchar(max) NULL,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosTerminals] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosTerminals_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosSessions')
                  CREATE TABLE [PosSessions] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [TerminalId] uniqueidentifier NOT NULL, [OpenedByUserId] uniqueidentifier NOT NULL,
                      [ClosedByUserId] uniqueidentifier NULL, [OpenedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [ClosedAt] datetime2 NULL, [OpeningBalance] decimal(18,2) NOT NULL DEFAULT 0,
                      [ClosingBalance] decimal(18,2) NOT NULL DEFAULT 0, [ExpectedBalance] decimal(18,2) NOT NULL DEFAULT 0,
                      [Status] int NOT NULL DEFAULT 1, [Notes] nvarchar(max) NULL,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosSessions] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosSessions_PosTerminals] FOREIGN KEY ([TerminalId]) REFERENCES [PosTerminals]([Id]),
                      CONSTRAINT [FK_PosSessions_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ServicePackages')
                  CREATE TABLE [ServicePackages] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [Name] nvarchar(500) NOT NULL, [NameEn] nvarchar(max) NULL, [Description] nvarchar(max) NULL,
                      [Sku] nvarchar(50) NULL, [Category] nvarchar(200) NULL,
                      [Price] decimal(18,2) NOT NULL DEFAULT 0, [CostPrice] decimal(18,2) NULL,
                      [DurationMinutes] int NOT NULL DEFAULT 0, [IsActive] bit NOT NULL DEFAULT 1,
                      [IsVatIncluded] bit NOT NULL DEFAULT 1, [RevenueAccountId] uniqueidentifier NULL,
                      [ImageUrl] nvarchar(max) NULL, [SortOrder] int NOT NULL DEFAULT 0,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_ServicePackages] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_ServicePackages_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrders')
                  CREATE TABLE [PosOrders] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [SessionId] uniqueidentifier NOT NULL, [OrderNumber] nvarchar(50) NOT NULL,
                      [OrderType] int NOT NULL DEFAULT 0, [Status] int NOT NULL DEFAULT 0,
                      [CustomerId] uniqueidentifier NULL, [CustomerName] nvarchar(max) NULL,
                      [TableNumber] nvarchar(max) NULL, [GuestCount] int NULL, [QueueNumber] nvarchar(max) NULL,
                      [AppointmentTime] datetime2 NULL, [PrimaryStaffId] uniqueidentifier NULL,
                      [SubTotal] decimal(18,2) NOT NULL DEFAULT 0, [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [DiscountPercent] decimal(5,2) NOT NULL DEFAULT 0, [ServiceChargePercent] decimal(5,2) NOT NULL DEFAULT 0,
                      [ServiceChargeAmount] decimal(18,2) NOT NULL DEFAULT 0, [VatAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [TotalAmount] decimal(18,2) NOT NULL DEFAULT 0, [RoundingAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [NetAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [Notes] nvarchar(max) NULL, [Reference] nvarchar(max) NULL,
                      [JournalEntryId] uniqueidentifier NULL, [DocumentId] uniqueidentifier NULL, [CompletedAt] datetime2 NULL,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosOrders] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosOrders_PosSessions] FOREIGN KEY ([SessionId]) REFERENCES [PosSessions]([Id]),
                      CONSTRAINT [FK_PosOrders_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ServiceComponents')
                  CREATE TABLE [ServiceComponents] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [PackageId] uniqueidentifier NOT NULL, [StepOrder] int NOT NULL DEFAULT 0,
                      [Name] nvarchar(500) NOT NULL, [NameEn] nvarchar(max) NULL, [Description] nvarchar(max) NULL,
                      [DurationMinutes] int NOT NULL DEFAULT 0, [CommissionType] int NOT NULL DEFAULT 1,
                      [CommissionValue] decimal(18,2) NOT NULL DEFAULT 0, [RequiresStaff] bit NOT NULL DEFAULT 1,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_ServiceComponents] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_ServiceComponents_ServicePackages] FOREIGN KEY ([PackageId]) REFERENCES [ServicePackages]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrderItems')
                  CREATE TABLE [PosOrderItems] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [OrderId] uniqueidentifier NOT NULL, [ProductId] uniqueidentifier NULL, [ServicePackageId] uniqueidentifier NULL,
                      [ItemName] nvarchar(max) NOT NULL, [ItemCode] nvarchar(max) NULL,
                      [Quantity] decimal(18,4) NOT NULL DEFAULT 1, [Unit] nvarchar(max) NULL,
                      [UnitPrice] decimal(18,2) NOT NULL DEFAULT 0, [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [DiscountPercent] decimal(5,2) NOT NULL DEFAULT 0, [SubTotal] decimal(18,2) NOT NULL DEFAULT 0,
                      [VatAmount] decimal(18,2) NOT NULL DEFAULT 0, [TotalAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [LineOrder] int NOT NULL DEFAULT 0, [Status] int NOT NULL DEFAULT 0, [Notes] nvarchar(max) NULL,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosOrderItems] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosOrderItems_PosOrders] FOREIGN KEY ([OrderId]) REFERENCES [PosOrders]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierGroups')
                  CREATE TABLE [ProductModifierGroups] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [Name] nvarchar(200) NOT NULL, [NameEn] nvarchar(max) NULL,
                      [IsRequired] bit NOT NULL DEFAULT 0, [AllowMultiple] bit NOT NULL DEFAULT 0, [SortOrder] int NOT NULL DEFAULT 0,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_ProductModifierGroups] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_ProductModifierGroups_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierOptions')
                  CREATE TABLE [ProductModifierOptions] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [GroupId] uniqueidentifier NOT NULL, [Name] nvarchar(200) NOT NULL, [NameEn] nvarchar(max) NULL,
                      [PriceAdjustment] decimal(18,2) NOT NULL DEFAULT 0, [IsDefault] bit NOT NULL DEFAULT 0,
                      [SortOrder] int NOT NULL DEFAULT 0, [IsActive] bit NOT NULL DEFAULT 1,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_ProductModifierOptions] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_ProductModifierOptions_ProductModifierGroups] FOREIGN KEY ([GroupId]) REFERENCES [ProductModifierGroups]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosOrderItemModifiers')
                  CREATE TABLE [PosOrderItemModifiers] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [OrderItemId] uniqueidentifier NOT NULL, [ModifierOptionId] uniqueidentifier NULL,
                      [ModifierGroupName] nvarchar(200) NOT NULL, [ModifierName] nvarchar(200) NOT NULL,
                      [PriceAdjustment] decimal(18,2) NOT NULL DEFAULT 0,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosOrderItemModifiers] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosOrderItemModifiers_PosOrderItems] FOREIGN KEY ([OrderItemId]) REFERENCES [PosOrderItems]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosPayments')
                  CREATE TABLE [PosPayments] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [OrderId] uniqueidentifier NOT NULL, [PaymentMethod] int NOT NULL DEFAULT 0,
                      [Amount] decimal(18,2) NOT NULL DEFAULT 0, [ReceivedAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [ChangeAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [ReferenceNo] nvarchar(200) NULL, [CardLastFour] nvarchar(4) NULL,
                      [PaidAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosPayments] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosPayments_PosOrders] FOREIGN KEY ([OrderId]) REFERENCES [PosOrders]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosServiceActivities')
                  CREATE TABLE [PosServiceActivities] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [OrderItemId] uniqueidentifier NOT NULL, [ComponentId] uniqueidentifier NOT NULL,
                      [StaffId] uniqueidentifier NULL, [StaffName] nvarchar(200) NULL,
                      [Status] int NOT NULL DEFAULT 0, [StartedAt] datetime2 NULL, [CompletedAt] datetime2 NULL,
                      [CommissionAmount] decimal(18,2) NOT NULL DEFAULT 0, [Notes] nvarchar(max) NULL,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_PosServiceActivities] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_PosServiceActivities_PosOrderItems] FOREIGN KEY ([OrderItemId]) REFERENCES [PosOrderItems]([Id]),
                      CONSTRAINT [FK_PosServiceActivities_ServiceComponents] FOREIGN KEY ([ComponentId]) REFERENCES [ServiceComponents]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductModifierGroupLinks')
                  CREATE TABLE [ProductModifierGroupLinks] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [ProductId] uniqueidentifier NOT NULL, [ModifierGroupId] uniqueidentifier NOT NULL,
                      [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_ProductModifierGroupLinks] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_ProductModifierGroupLinks_Products] FOREIGN KEY ([ProductId]) REFERENCES [Products]([Id]),
                      CONSTRAINT [FK_ProductModifierGroupLinks_ProductModifierGroups] FOREIGN KEY ([ModifierGroupId]) REFERENCES [ProductModifierGroups]([Id])
                  );",
                @"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StaffCommissionSummaries')
                  CREATE TABLE [StaffCommissionSummaries] (
                      [Id] uniqueidentifier NOT NULL DEFAULT NEWSEQUENTIALID(),
                      [StaffId] uniqueidentifier NOT NULL, [StaffName] nvarchar(200) NOT NULL,
                      [PeriodStart] datetime2 NOT NULL, [PeriodEnd] datetime2 NOT NULL,
                      [TotalActivities] int NOT NULL DEFAULT 0, [TotalCommission] decimal(18,2) NOT NULL DEFAULT 0,
                      [PaidAmount] decimal(18,2) NOT NULL DEFAULT 0, [RemainingAmount] decimal(18,2) NOT NULL DEFAULT 0,
                      [IsPaid] bit NOT NULL DEFAULT 0, [JournalEntryId] uniqueidentifier NULL,
                      [CompanyId] uniqueidentifier NOT NULL, [CreatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                      [UpdatedAt] datetime2 NULL, [CreatedBy] nvarchar(max) NULL, [UpdatedBy] nvarchar(max) NULL, [IsDeleted] bit NOT NULL DEFAULT 0,
                      CONSTRAINT [PK_StaffCommissionSummaries] PRIMARY KEY ([Id]),
                      CONSTRAINT [FK_StaffCommissionSummaries_Companies] FOREIGN KEY ([CompanyId]) REFERENCES [Companies]([Id])
                  );"
            };
            foreach (var sql in rawSqlStatements)
            {
                try
                {
                    using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, rawConn);
                    cmd.ExecuteNonQuery();
                }
                catch { /* table/column already exists or FK target missing — safe to skip */ }
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
                var statements = script.Split(["GO"], StringSplitOptions.RemoveEmptyEntries);
                foreach (var statement in statements)
                {
                    if (string.IsNullOrWhiteSpace(statement)) continue;
                    try
                    {
                        db.Database.ExecuteSqlRaw(statement);
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
