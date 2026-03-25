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

// SPA fallback - serve app.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

// ===== Auto-migrate & seed data =====
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

    // Use Migrate() if migrations exist, fallback to EnsureCreated()
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

    // Add any missing columns to existing tables (no-op if already present)
    DatabaseMigrationHelper.ApplyMissingColumns(db);

    // Seed default plan templates & admin user
    await SeedPlanTemplates.SeedAsync(db);
    await SeedAdminUser.SeedAsync(db, app.Configuration);
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Failed to initialize database. Check your ConnectionStrings:DefaultConnection in appsettings.Production.json");

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
