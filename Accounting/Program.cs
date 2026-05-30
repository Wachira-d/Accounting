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
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

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
builder.Services.AddMemoryCache();

// ===== Services (DI) =====
// Core
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountingService, AccountingService>();
builder.Services.AddScoped<IMigrationWizardService, MigrationWizardService>();
// Accountant tools (Phase I-N)
builder.Services.AddScoped<SubLedgerReconciliationService>();
builder.Services.AddScoped<PreCloseChecklistService>();
builder.Services.AddScoped<DocumentCompletenessService>();
builder.Services.AddScoped<GlobalSearchService>();
builder.Services.AddSingleton<Accounting.Helpers.ISecretProtector, Accounting.Helpers.SecretProtector>();
builder.Services.AddScoped<Accounting.Services.Interfaces.ILineBotService, Accounting.Services.Implementations.LineBotService>();
builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<ITaxService, TaxService>();
builder.Services.AddScoped<ITaxFilingExportService, TaxFilingExportService>();

// New modules
builder.Services.AddScoped<IProductService, ProductService>();
// Per-SKU demand forecast + reorder recommendation (Croston). Cheap
// enough to run on demand from the stock UI.
builder.Services.AddScoped<Accounting.Services.Implementations.Inventory.IInventoryReorderForecastService,
    Accounting.Services.Implementations.Inventory.InventoryReorderForecastService>();
// Local rule-based pre-checker for Thai tax filings — runs before
// DeepSeek narrative so DeepSeek only has to explain, not verify.
builder.Services.AddScoped<Accounting.Services.Implementations.Tax.ITaxComplianceChecker,
    Accounting.Services.Implementations.Tax.TaxComplianceChecker>();
// Inventory costing (Weighted-Average + FIFO + Standard) — called by
// every stock-IN / stock-OUT path so COGS posts at the correct value.
builder.Services.AddScoped<Accounting.Services.Implementations.Inventory.IInventoryCostingService,
    Accounting.Services.Implementations.Inventory.InventoryCostingService>();
// 3-way match — PO ↔ GRN ↔ Invoice. Blocks AP overpayment before
// the cheque goes out.
builder.Services.AddScoped<Accounting.Services.Implementations.Procurement.IGrnMatchService,
    Accounting.Services.Implementations.Procurement.GrnMatchService>();
// Cheque lifecycle — book ordering, issuance, clearing, bouncing,
// outstanding-cheque report.
builder.Services.AddScoped<Accounting.Services.Implementations.Cheque.IChequeService,
    Accounting.Services.Implementations.Cheque.ChequeService>();
// Stamp duty (อากรแสตมป์) tracker — schedule-aware computation +
// payment status tracking.
builder.Services.AddScoped<Accounting.Services.Implementations.Tax.IStampDutyService,
    Accounting.Services.Implementations.Tax.StampDutyService>();
// Petty cash + Stock count + FX revaluation — Tier 2 SME modules.
builder.Services.AddScoped<Accounting.Services.Implementations.PettyCash.IPettyCashService,
    Accounting.Services.Implementations.PettyCash.PettyCashService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Inventory.IStockCountService,
    Accounting.Services.Implementations.Inventory.StockCountService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Forex.IFxRevaluationService,
    Accounting.Services.Implementations.Forex.FxRevaluationService>();
// PDPA + Quick search (Tier 3).
builder.Services.AddScoped<Accounting.Services.Implementations.Pdpa.IPdpaService,
    Accounting.Services.Implementations.Pdpa.PdpaService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Search.IQuickSearchService,
    Accounting.Services.Implementations.Search.QuickSearchService>();
// Vendor portal — token-based access for counterparties without
// full user accounts. AP issues magic-links via the admin endpoint;
// vendors hit /vendor-portal.html with the token in the URL.
builder.Services.AddScoped<Accounting.Services.Implementations.Portal.IVendorPortalService,
    Accounting.Services.Implementations.Portal.VendorPortalService>();
// Competitor migration framework — Express / PEAK / FlowAccount.
// Each adapter sniffs the file format + maps to canonical Contacts.
builder.Services.AddScoped<Accounting.Services.Implementations.Migration.ICompetitorImportAdapter,
    Accounting.Services.Implementations.Migration.ExpressContactsAdapter>();
builder.Services.AddScoped<Accounting.Services.Implementations.Migration.ICompetitorImportAdapter,
    Accounting.Services.Implementations.Migration.PeakContactsAdapter>();
builder.Services.AddScoped<Accounting.Services.Implementations.Migration.ICompetitorImportAdapter,
    Accounting.Services.Implementations.Migration.FlowAccountContactsAdapter>();
builder.Services.AddScoped<Accounting.Services.Implementations.Migration.ICompetitorImportCoordinator,
    Accounting.Services.Implementations.Migration.CompetitorImportCoordinator>();
builder.Services.AddScoped<Accounting.Services.Interfaces.ISensitivityService, Accounting.Services.Implementations.SensitivityService>();
builder.Services.AddSingleton<Accounting.Services.Interfaces.IImageProcessingService, Accounting.Services.Implementations.ImageProcessingService>();
builder.Services.AddScoped<IBankService, BankService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();

// Additional modules
builder.Services.AddScoped<IRecurringTransactionService, RecurringTransactionService>();
builder.Services.AddScoped<IFixedAssetService, FixedAssetService>();
builder.Services.AddScoped<IFinancialManagementService, FinancialManagementService>();
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
builder.Services.AddScoped<ISalaryAdvanceService, SalaryAdvanceService>();
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<INotificationEngine, NotificationEngine>();
builder.Services.AddScoped<INotificationConfigService, NotificationConfigService>();
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
builder.Services.AddScoped<IOcrQuotaService, OcrQuotaService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.AzureDocumentIntelligenceService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.OcrSelfCorrectionService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.ExpenseCategoryLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.AssociationRuleMiner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.TfIdfNaiveBayesClassifier>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.RecurringExpenseDetector>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.DocumentWorkflowPredictor>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.VendorClusteringService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.ProductMatcher>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.GlobalProductLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.GlobalExpenseCategoryLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.GlobalVendorIntelLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.GlobalDocWorkflowLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.GlobalAssetCategoryLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.SystemOcrKnowledgeSeeder>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.RdComplianceValidator>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.AzureDiPatternLearner>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.VendorKnownGoodCorrector>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.CrossTenantKnowledgeAggregator>();
builder.Services.AddScoped<Accounting.Services.Implementations.Ocr.ActiveLearningRanker>();
// Nightly batch — aggregator + miner — runs in-process via IHostedService
builder.Services.AddHostedService<Accounting.Services.Implementations.OcrMlBackgroundService>();
builder.Services.AddHostedService<Accounting.Services.Implementations.Jobs.AiFeedbackTrainingJob>();
builder.Services.AddScoped<Accounting.Services.Implementations.CrossTenantWorkflowService>();
// Embedded OCR is a singleton — the TesseractEngine is expensive to construct,
// and the service maintains a thread-local engine pool for thread safety.
builder.Services.AddSingleton<Accounting.Services.Implementations.Ocr.EmbeddedTesseractOcrService>();
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

// ───── AI integration (DeepSeek + swappable providers + orchestrator) ─────
// Provider implementations are registered as IAiProvider so the
// orchestrator can pick the active one by AiProviderType. Adding a new
// provider = drop a class in Services/Ai/Providers + add a line here.
builder.Services.AddScoped<Accounting.Services.Ai.IAiProvider, Accounting.Services.Ai.Providers.DeepSeekProvider>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiProvider, Accounting.Services.Ai.Providers.OpenAiProvider>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiProvider, Accounting.Services.Ai.Providers.OpenAiCompatibleProvider>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiProvider, Accounting.Services.Ai.Providers.LocalLlamaProvider>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiPromptSanitizer, Accounting.Services.Ai.AiPromptSanitizer>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiResponseCacheService, Accounting.Services.Ai.AiResponseCacheService>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiBudgetGuard, Accounting.Services.Ai.AiBudgetGuard>();
builder.Services.AddScoped<Accounting.Services.Ai.IAiFeedbackRecorder, Accounting.Services.Ai.AiFeedbackRecorder>();
// Per-feature routing policy — SINGLETON so the 1-minute cache survives
// across HTTP requests. The orchestrator consults this on every AI call
// to decide local vs provider; the admin UI writes through it.
builder.Services.AddSingleton<Accounting.Services.Ai.IAiFeatureRoutingResolver,
    Accounting.Services.Ai.AiFeatureRoutingResolver>();
// Cold-start corpus seeder — runs once at startup to populate
// SystemOcrCategoryMapping with curated Thai vendor patterns so the
// resolver has useful answers for tenants with zero feedback yet.
builder.Services.AddScoped<Accounting.Services.Implementations.Ai.IDistillationCorpusSeeder,
    Accounting.Services.Implementations.Ai.DistillationCorpusSeeder>();
// Knowledge-distillation local student models — SINGLETON because each
// holds an in-memory (CompanyId, key) → ranked candidates dictionary
// that the nightly AiFeedbackTrainingJob rebuilds from feedback rows.
// Scoped lifetime would discard learned state on every HTTP request.
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.VendorCanonDistillationModel>();
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.GlAccountDistillationModel>();
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.BankMatchDistillationModel>();
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.DuplicateDocumentDistillationModel>();
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.AnomalyExplanationDistillationModel>();
builder.Services.AddSingleton<Accounting.Services.Ai.Distillation.ILocalDistillationModel,
    Accounting.Services.Ai.Distillation.ApprovalWarningDistillationModel>();
// Risk scoring & smart approval routing — surfaces decisions the
// admin/AR/AP teams use directly + feeds the corresponding AI narrative
// features (AgingExplanation, ApprovalWarningFixSuggestion).
builder.Services.AddScoped<Accounting.Services.Implementations.Risk.ICustomerPaymentRiskService,
    Accounting.Services.Implementations.Risk.CustomerPaymentRiskService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Risk.IVendorRiskScoringService,
    Accounting.Services.Implementations.Risk.VendorRiskScoringService>();
builder.Services.AddScoped<Accounting.Services.Implementations.Risk.ISmartApprovalRoutingService,
    Accounting.Services.Implementations.Risk.SmartApprovalRoutingService>();
// One-shot whole-month bank reconciliation — bundles bank txns + open
// docs/JEs/payments + company context into a single DeepSeek call.
builder.Services.AddScoped<Accounting.Services.Implementations.Bank.IBulkBankAiMatchService,
    Accounting.Services.Implementations.Bank.BulkBankAiMatchService>();
// Sentence-embedding service for Thai short text (vendor names, line
// descriptions). Try ONNX MiniLM first — if the LFS-tracked model file
// is present and loadable, register it; otherwise transparently fall
// back to HashingEmbeddingService so dev clones without `git lfs pull`
// (or CI runners without LFS support) still build + run.
{
    var modelPath = Path.Combine(builder.Environment.ContentRootPath, "models", "sentence-encoder.onnx");
    var vocabPath = Path.Combine(builder.Environment.ContentRootPath, "models", "vocab.txt");
    var onnxOk = false;
    try
    {
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 1_000_000
            && File.Exists(vocabPath) && new FileInfo(vocabPath).Length > 1024)
        {
            builder.Services.AddSingleton<Accounting.Services.Ai.Embedding.IEmbeddingService>(
                _ => new Accounting.Services.Ai.Embedding.OnnxSentenceEmbeddingService(modelPath, vocabPath));
            onnxOk = true;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ONNX embedding init failed, falling back to hashing: {ex.Message}");
    }
    if (!onnxOk)
    {
        builder.Services.AddSingleton<Accounting.Services.Ai.Embedding.IEmbeddingService,
            Accounting.Services.Ai.Embedding.HashingEmbeddingService>();
    }
}
builder.Services.AddScoped<Accounting.Services.Ai.IAiOrchestrator, Accounting.Services.Ai.AiOrchestrator>();
builder.Services.AddScoped<Accounting.Services.Ai.IOcrAiAugmenter, Accounting.Services.Ai.OcrAiAugmenter>();
builder.Services.AddScoped<Accounting.Services.Ai.IDocumentAiAugmenter, Accounting.Services.Ai.DocumentAiAugmenter>();
builder.Services.AddScoped<Accounting.Services.Ai.IBankAiAugmenter, Accounting.Services.Ai.BankAiAugmenter>();
builder.Services.AddScoped<Accounting.Services.Ai.IAdvancedAiAugmenter, Accounting.Services.Ai.AdvancedAiAugmenter>();

// Email service
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IEmailSenderFactory, Accounting.Services.Implementations.Email.EmailSenderFactory>();
builder.Services.AddScoped<IDocumentEmailService, DocumentEmailService>();

// Error logging service
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<IPosService, PosService>();
builder.Services.AddScoped<ILineNotifyService, LineNotifyService>();

// External Integration (TakeTime, PMS, E-Commerce, Bank Feeds)
builder.Services.AddScoped<IIntegrationService, IntegrationService>();
builder.Services.AddScoped<IBankFeedService, BankFeedService>();
builder.Services.AddScoped<IECommerceService, ECommerceService>();

// Signature & Approval
builder.Services.AddScoped<ISignatureApprovalService, SignatureApprovalService>();

// Executive Reports
builder.Services.AddScoped<IExecutiveReportService, ExecutiveReportService>();

// CMS & Multi-Site
builder.Services.AddScoped<ICmsSiteService, CmsSiteService>();
builder.Services.AddScoped<ICmsContentService, CmsContentService>();
builder.Services.AddScoped<ICmsCommerceService, CmsCommerceService>();
builder.Services.AddScoped<ICmsBookingService, CmsBookingService>();
builder.Services.AddScoped<CmsLeadService>();
builder.Services.AddScoped<ICmsCustomerService, CmsCustomerService>();
builder.Services.AddScoped<ICmsRenderingService, CmsRenderingService>();
builder.Services.AddScoped<ICmsQuotaService, CmsQuotaService>();
builder.Services.AddScoped<ICmsCouponService, CmsCouponService>();
builder.Services.AddScoped<ICmsShippingService, CmsShippingService>();
builder.Services.AddScoped<ICmsVariantService, CmsVariantService>();
builder.Services.AddScoped<ICmsReviewService, CmsReviewService>();
builder.Services.AddScoped<ICmsWishlistService, CmsWishlistService>();

// Thai Government & Public Service Integrations
builder.Services.AddScoped<IBotExchangeRateService, BotExchangeRateService>();
builder.Services.AddSingleton<IThaiAddressService, ThaiAddressService>();
builder.Services.AddScoped<IThaiGovIntegrationService, ThaiGovIntegrationService>();
builder.Services.AddScoped<IShippingTrackingService, ShippingTrackingService>();
builder.Services.AddScoped<IRolePermissionService, RolePermissionService>();
builder.Services.AddSingleton<IPromptPayService, PromptPayService>();

// SignalR for real-time notifications
builder.Services.AddSignalR();

// Background job scheduler
builder.Services.AddHostedService<BackgroundJobService>();
builder.Services.AddHostedService<AbandonedCartService>();
builder.Services.AddHostedService<Accounting.Services.Background.DocumentAgingBackgroundService>();
builder.Services.AddHostedService<Accounting.Services.Background.DepreciationBackgroundService>();
builder.Services.AddHostedService<Accounting.Services.Background.AccountPlanExpiryReminderJob>();

// ===== Validation =====
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();

// ===== Controllers =====
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<Accounting.Filters.DateRangeValidationFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// Raise the global request body limit so large bank-statement / attachment
// uploads don't fail with an opaque 400 before reaching the controller.
// Default Kestrel limit is 30MB; 100MB covers year-long statements (.xlsx
// base64-encoded inside JSON adds ~33% overhead). Individual endpoints can
// override further via [RequestSizeLimit].
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    o.Limits.MaxRequestBodySize = 100_000_000;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 100_000_000;
    o.ValueLengthLimit = 100_000_000;
    o.MemoryBufferThreshold = 1_000_000;
});
builder.Services.Configure<Microsoft.AspNetCore.Builder.IISServerOptions>(o =>
{
    o.MaxRequestBodySize = 100_000_000;
});

// ===== Swagger =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Accounting Platform API",
        Version = "v1",
        Description = "ระบบบัญชี SaaS Platform - 115+ Services, 70+ Controllers | Core Accounting, Tax (ภพ.30/ภงด./e-Tax), Document Engine, Payroll + Leave + Salary Advance + Expense Claim, Multi-Branch / Cost Center, Intercompany & Consolidation, AR/AP, Project Accounting, Warehouse, Revenue Recognition (TFRS15), Loan, Commission, AI Auto-Categorization, OCR, Report Builder, Customer Portal, FP&A, Open Banking, Compliance, Time & Billing, Webhooks, Mobile API, CMS Multi-Site, Configurable Omnichannel Notification Engine, Organization Structure & Granular RBAC"
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

// CMS Site Routing — must run BEFORE UseStaticFiles so we can rewrite the
// request path to /storefront.html and have static-files serve it for
// visitor-facing site hosts (subdomain or custom domain).
app.UseCmsSiteRouting();

// Static files (frontend) — no-cache for HTML/JS/CSS to prevent stale content
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name.ToLower();
        if (path.EndsWith(".html") || path.EndsWith(".js") || path.EndsWith(".css"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
        else
        {
            // Cache images/fonts for 7 days
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=604800";
        }
    }
});

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

// 7.5 CMS RBAC (site-level staff access check)
app.UseCmsRbac();

// 8. Subscription check
app.UseMiddleware<SubscriptionCheckMiddleware>();

// 9. Audit logging (innermost - logs after response)
app.UseMiddleware<AuditMiddleware>();

// 10. API error logging (logs 4xx/5xx that aren't exceptions to ErrorLog)
app.UseMiddleware<ApiErrorLoggingMiddleware>();

app.MapControllers();

// CMS storefront — explicit endpoint that ALWAYS serves storefront.html for
// /site/{key} and /site/{key}/{**slug} URLs, regardless of whether the
// CmsSiteRoutingMiddleware rewrote the path. Without this, a stale build
// or a middleware short-circuit could land users on the main marketing
// index.html. The storefront's JS then re-parses location.pathname and
// calls /api/cms/resolve/subdomain/{key} to fetch the site content (or
// render its own 404 page if the key doesn't match a site).
app.MapGet("/site/{**catchAll}", context =>
{
    context.Response.Headers["X-CMS-Site-Match"] = "explicit-endpoint:storefront.html";
    // Prevent the browser + any intermediate cache from holding onto a stale
    // marketing-page response under this URL.
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    return Results.File(
        Path.Combine(app.Environment.WebRootPath, "storefront.html"),
        "text/html"
    ).ExecuteAsync(context);
});

// SignalR hubs
app.MapHub<NotificationHub>("/hubs/notifications");

// Client-side error reporting endpoint (anonymous — frontend can report errors even without auth)
app.MapPost("/api/error-log/client", async (HttpContext ctx, IConfiguration config) =>
{
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>();
        if (body == null) return Results.BadRequest();

        var connStr = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connStr)) return Results.Ok(new { logged = false });

        using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();
        var sql = @"INSERT INTO ""ErrorLogs"" (""RequestPath"",""HttpMethod"",""StatusCode"",""ExceptionType"",""Message"",""IpAddress"",""UserAgent"",""Timestamp"")
            SELECT @p,@m,@s,'CLIENT_ERROR',@msg,@ip,@ua,now()
            WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE lower(table_name)='errorlogs')";
        using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@p", body.GetValueOrDefault("requestPath")?.ToString() ?? "");
        cmd.Parameters.AddWithValue("@m", body.GetValueOrDefault("httpMethod")?.ToString() ?? "");
        cmd.Parameters.AddWithValue("@s", int.TryParse(body.GetValueOrDefault("statusCode")?.ToString(), out var sc) ? sc : 0);
        cmd.Parameters.AddWithValue("@msg", $"[{body.GetValueOrDefault("source")}] {body.GetValueOrDefault("message")}");
        cmd.Parameters.AddWithValue("@ip", (object?)ctx.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ua", ctx.Request.Headers.UserAgent.ToString());
        await cmd.ExecuteNonQueryAsync();
        return Results.Ok(new { logged = true });
    }
    catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to log client-side error: {ex.Message}"); return Results.Ok(new { logged = false }); }
});

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

// SPA fallback - serve index.html for non-API, non-file routes
// For /api/ paths: return JSON 404 so frontend gets proper error instead of HTML
app.MapFallback(context =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { success = false, message = "API endpoint not found: " + path });
    }
    // CMS storefront safety-net: if a /site/{key} URL slips through to the
    // fallback (typically because CmsSiteRoutingMiddleware didn't run — a
    // mis-ordered pipeline or a stale deploy), serve storefront.html directly
    // so the public viewer at least renders its own 404 instead of the main
    // marketing landing page. The JS in storefront.html re-parses the path
    // and calls /api/cms/resolve to fetch the site.
    if (path.StartsWith("/site/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["X-CMS-Site-Match"] = "fallback:storefront.html (middleware did not rewrite)";
        return Results.File(
            Path.Combine(app.Environment.WebRootPath, "storefront.html"),
            "text/html"
        ).ExecuteAsync(context);
    }

    // Deep-link rewrite for API callbacks. External systems creating records
    // via API can ship the user a link like /{companyId}/journals/{entryId}
    // — we rewrite to /pages/{entity}.html?company=X&{paramName}=Y so the
    // existing per-page deep-link handlers (already wired) auto-open the
    // record. 302 keeps the URL in the browser bar clean for sharing.
    var deep = Accounting.Helpers.DeepLinkRewriter.TryMap(path);
    if (deep != null)
    {
        context.Response.Redirect(deep, false);
        return Task.CompletedTask;
    }

    // Non-API routes: serve index.html for SPA client-side routing
    return Results.File(
        Path.Combine(app.Environment.WebRootPath, "index.html"),
        "text/html"
    ).ExecuteAsync(context);
});

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
                      ""WebhookUrl"" text NULL, ""WebhookEnabled"" boolean NOT NULL DEFAULT false,
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
                      CONSTRAINT ""FK_IntegrationAccountMappings_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                      CONSTRAINT ""FK_IntegrationAccountMappings_DebitAccount"" FOREIGN KEY (""DebitAccountId"") REFERENCES ""ChartOfAccounts""(""Id"") ON DELETE SET NULL,
                      CONSTRAINT ""FK_IntegrationAccountMappings_CreditAccount"" FOREIGN KEY (""CreditAccountId"") REFERENCES ""ChartOfAccounts""(""Id"") ON DELETE SET NULL
                  );",
                // Add webhook columns to ExternalIntegrations (safe for existing DBs)
                @"DO $$ BEGIN
                    ALTER TABLE ""ExternalIntegrations"" ADD COLUMN IF NOT EXISTS ""WebhookUrl"" text NULL;
                    ALTER TABLE ""ExternalIntegrations"" ADD COLUMN IF NOT EXISTS ""WebhookEnabled"" boolean NOT NULL DEFAULT false;
                  EXCEPTION WHEN others THEN NULL;
                  END $$;",
                // Add FK constraints for DebitAccountId/CreditAccountId (safe for existing DBs)
                @"DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_name = 'FK_IntegrationAccountMappings_DebitAccount') THEN
                      ALTER TABLE ""IntegrationAccountMappings"" ADD CONSTRAINT ""FK_IntegrationAccountMappings_DebitAccount"" FOREIGN KEY (""DebitAccountId"") REFERENCES ""ChartOfAccounts""(""Id"") ON DELETE SET NULL;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_name = 'FK_IntegrationAccountMappings_CreditAccount') THEN
                      ALTER TABLE ""IntegrationAccountMappings"" ADD CONSTRAINT ""FK_IntegrationAccountMappings_CreditAccount"" FOREIGN KEY (""CreditAccountId"") REFERENCES ""ChartOfAccounts""(""Id"") ON DELETE SET NULL;
                    END IF;
                  EXCEPTION WHEN others THEN NULL;
                  END $$;",
                // Inventory Snapshot tables
                @"CREATE TABLE IF NOT EXISTS ""InventorySnapshots"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""SnapshotDate"" timestamp NOT NULL, ""Status"" varchar(50) NOT NULL DEFAULT 'Draft',
                      ""Description"" text NULL, ""TotalValue"" decimal(18,2) NOT NULL DEFAULT 0,
                      ""TotalProducts"" integer NOT NULL DEFAULT 0,
                      ""JournalEntryId"" uuid NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_InventorySnapshots"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_InventorySnapshots_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                      CONSTRAINT ""FK_InventorySnapshots_JournalEntries"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                  );",
                @"CREATE TABLE IF NOT EXISTS ""InventorySnapshotLines"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""SnapshotId"" uuid NOT NULL, ""ProductId"" uuid NOT NULL,
                      ""Quantity"" decimal(18,4) NOT NULL, ""UnitCost"" decimal(18,4) NOT NULL,
                      ""TotalValue"" decimal(18,2) NOT NULL,
                      ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_InventorySnapshotLines"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_InventorySnapshotLines_Snapshots"" FOREIGN KEY (""SnapshotId"") REFERENCES ""InventorySnapshots""(""Id""),
                      CONSTRAINT ""FK_InventorySnapshotLines_Products"" FOREIGN KEY (""ProductId"") REFERENCES ""Products""(""Id""),
                      CONSTRAINT ""FK_InventorySnapshotLines_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                  );",
                // Supplies columns on Products
                @"DO $$ BEGIN
                    ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""SuppliesAccountId"" uuid NULL;
                    ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""SuppliesExpenseAccountId"" uuid NULL;
                  END $$;",
                // Supplies Usage Log (วัสดุสิ้นเปลือง - บันทึกการเบิกใช้)
                @"CREATE TABLE IF NOT EXISTS ""SuppliesUsageLogs"" (
                      ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                      ""ProductId"" uuid NOT NULL,
                      ""UsageDate"" timestamp NOT NULL DEFAULT now(),
                      ""Quantity"" decimal(18,4) NOT NULL,
                      ""UnitCost"" decimal(18,4) NOT NULL,
                      ""TotalCost"" decimal(18,2) NOT NULL,
                      ""Department"" varchar(200) NULL,
                      ""Purpose"" text NULL,
                      ""Reference"" varchar(100) NULL,
                      ""JournalEntryId"" uuid NULL,
                      ""CompanyId"" uuid NOT NULL,
                      ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                      ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL,
                      ""IsDeleted"" boolean NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_SuppliesUsageLogs"" PRIMARY KEY (""Id""),
                      CONSTRAINT ""FK_SuppliesUsageLogs_Products"" FOREIGN KEY (""ProductId"") REFERENCES ""Products""(""Id""),
                      CONSTRAINT ""FK_SuppliesUsageLogs_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                      CONSTRAINT ""FK_SuppliesUsageLogs_JournalEntries"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                  );",
                // === Financial Management Tables ===
                @"CREATE TABLE IF NOT EXISTS ""PrepaidExpenses"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""Description"" text NOT NULL, ""StartDate"" timestamp NOT NULL, ""EndDate"" timestamp NOT NULL,
                    ""TotalPeriods"" integer NOT NULL, ""TotalAmount"" decimal(18,2) NOT NULL,
                    ""AmortizedAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""RemainingAmount"" decimal(18,2) NOT NULL,
                    ""AmortizationMethod"" varchar(50) NOT NULL DEFAULT 'StraightLine',
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Active',
                    ""PrepaidAccountId"" uuid NOT NULL, ""ExpenseAccountId"" uuid NOT NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_PrepaidExpenses"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_PrepaidExpenses_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_PrepaidExpenses_PrepaidAccount"" FOREIGN KEY (""PrepaidAccountId"") REFERENCES ""ChartOfAccounts""(""Id""),
                    CONSTRAINT ""FK_PrepaidExpenses_ExpenseAccount"" FOREIGN KEY (""ExpenseAccountId"") REFERENCES ""ChartOfAccounts""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""PrepaidAmortizationSchedules"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""PrepaidExpenseId"" uuid NOT NULL,
                    ""PeriodNumber"" integer NOT NULL, ""ScheduledDate"" timestamp NOT NULL,
                    ""Amount"" decimal(18,2) NOT NULL, ""IsProcessed"" boolean NOT NULL DEFAULT false,
                    ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_PrepaidAmortizationSchedules"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_PrepaidAmortSchedules_Prepaid"" FOREIGN KEY (""PrepaidExpenseId"") REFERENCES ""PrepaidExpenses""(""Id""),
                    CONSTRAINT ""FK_PrepaidAmortSchedules_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_PrepaidAmortSchedules_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""DepositTransactions"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""Description"" text NOT NULL, ""Direction"" varchar(20) NOT NULL, ""DepositType"" varchar(50) NOT NULL,
                    ""Amount"" decimal(18,2) NOT NULL, ""RefundedAmount"" decimal(18,2) NOT NULL DEFAULT 0,
                    ""RemainingAmount"" decimal(18,2) NOT NULL, ""Status"" varchar(50) NOT NULL DEFAULT 'Active',
                    ""TransactionDate"" timestamp NOT NULL, ""ExpectedReturnDate"" timestamp NULL,
                    ""ContactId"" uuid NULL, ""ContactName"" varchar(200) NULL,
                    ""DepositAccountId"" uuid NOT NULL, ""CashAccountId"" uuid NULL, ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_DepositTransactions"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_DepositTransactions_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_DepositTransactions_DepositAcc"" FOREIGN KEY (""DepositAccountId"") REFERENCES ""ChartOfAccounts""(""Id""),
                    CONSTRAINT ""FK_DepositTransactions_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""DepositRefunds"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""DepositTransactionId"" uuid NOT NULL,
                    ""RefundDate"" timestamp NOT NULL, ""Amount"" decimal(18,2) NOT NULL, ""Notes"" text NULL,
                    ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_DepositRefunds"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_DepositRefunds_Deposit"" FOREIGN KEY (""DepositTransactionId"") REFERENCES ""DepositTransactions""(""Id""),
                    CONSTRAINT ""FK_DepositRefunds_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_DepositRefunds_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""BadDebtAllowances"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""AllowanceDate"" timestamp NOT NULL, ""Method"" varchar(50) NOT NULL,
                    ""TotalReceivable"" decimal(18,2) NOT NULL, ""AllowanceAmount"" decimal(18,2) NOT NULL,
                    ""PreviousAllowance"" decimal(18,2) NOT NULL DEFAULT 0, ""AdjustmentAmount"" decimal(18,2) NOT NULL,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft', ""Notes"" text NULL, ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_BadDebtAllowances"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_BadDebtAllowances_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_BadDebtAllowances_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""BadDebtAllowanceLines"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""BadDebtAllowanceId"" uuid NOT NULL,
                    ""ContactId"" uuid NULL, ""ContactName"" varchar(200) NULL,
                    ""AgingBucket"" varchar(20) NOT NULL, ""OutstandingAmount"" decimal(18,2) NOT NULL,
                    ""AllowancePercentage"" decimal(5,2) NOT NULL, ""AllowanceAmount"" decimal(18,2) NOT NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_BadDebtAllowanceLines"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_BadDebtAllowanceLines_Parent"" FOREIGN KEY (""BadDebtAllowanceId"") REFERENCES ""BadDebtAllowances""(""Id""),
                    CONSTRAINT ""FK_BadDebtAllowanceLines_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""InventoryObsolescenceAllowances"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""AllowanceDate"" timestamp NOT NULL, ""Method"" varchar(50) NOT NULL,
                    ""TotalInventoryValue"" decimal(18,2) NOT NULL, ""AllowanceAmount"" decimal(18,2) NOT NULL,
                    ""PreviousAllowance"" decimal(18,2) NOT NULL DEFAULT 0, ""AdjustmentAmount"" decimal(18,2) NOT NULL,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft', ""Notes"" text NULL, ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_InventoryObsolescenceAllowances"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_InvObsolescence_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_InvObsolescence_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""InventoryObsolescenceLines"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""AllowanceId"" uuid NOT NULL,
                    ""ProductId"" uuid NOT NULL, ""AgingBucket"" varchar(20) NOT NULL,
                    ""CurrentStock"" decimal(18,4) NOT NULL, ""StockValue"" decimal(18,2) NOT NULL,
                    ""AllowancePercentage"" decimal(5,2) NOT NULL, ""AllowanceAmount"" decimal(18,2) NOT NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_InventoryObsolescenceLines"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_InvObsolescenceLines_Parent"" FOREIGN KEY (""AllowanceId"") REFERENCES ""InventoryObsolescenceAllowances""(""Id""),
                    CONSTRAINT ""FK_InvObsolescenceLines_Products"" FOREIGN KEY (""ProductId"") REFERENCES ""Products""(""Id""),
                    CONSTRAINT ""FK_InvObsolescenceLines_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""AccruedExpenses"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""Description"" text NOT NULL, ""ExpenseType"" varchar(50) NOT NULL,
                    ""AccrualDate"" timestamp NOT NULL, ""Amount"" decimal(18,2) NOT NULL,
                    ""PaidAmount"" decimal(18,2) NOT NULL DEFAULT 0, ""RemainingAmount"" decimal(18,2) NOT NULL,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Accrued',
                    ""IsRecurring"" boolean NOT NULL DEFAULT false, ""RecurringFrequency"" varchar(50) NULL,
                    ""ExpenseAccountId"" uuid NOT NULL, ""AccruedAccountId"" uuid NOT NULL,
                    ""AccrualJournalId"" uuid NULL, ""PaymentJournalId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_AccruedExpenses"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_AccruedExpenses_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_AccruedExpenses_ExpenseAcc"" FOREIGN KEY (""ExpenseAccountId"") REFERENCES ""ChartOfAccounts""(""Id""),
                    CONSTRAINT ""FK_AccruedExpenses_AccruedAcc"" FOREIGN KEY (""AccruedAccountId"") REFERENCES ""ChartOfAccounts""(""Id""),
                    CONSTRAINT ""FK_AccruedExpenses_AccrualJE"" FOREIGN KEY (""AccrualJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_AccruedExpenses_PaymentJE"" FOREIGN KEY (""PaymentJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""CorporateIncomeTaxes"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""TaxYear"" varchar(10) NOT NULL,
                    ""TaxPeriod"" varchar(20) NOT NULL,
                    ""TotalRevenue"" decimal(18,2) NOT NULL, ""TotalExpenses"" decimal(18,2) NOT NULL,
                    ""AccountingProfit"" decimal(18,2) NOT NULL,
                    ""AddBackItems"" decimal(18,2) NOT NULL DEFAULT 0, ""DeductionItems"" decimal(18,2) NOT NULL DEFAULT 0,
                    ""TaxableProfit"" decimal(18,2) NOT NULL, ""TaxRate"" decimal(5,2) NOT NULL DEFAULT 20,
                    ""TaxAmount"" decimal(18,2) NOT NULL, ""WithholdingTaxCredit"" decimal(18,2) NOT NULL DEFAULT 0,
                    ""PrepaidTaxCredit"" decimal(18,2) NOT NULL DEFAULT 0, ""NetTaxPayable"" decimal(18,2) NOT NULL,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft', ""Notes"" text NULL, ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_CorporateIncomeTaxes"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_CIT_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_CIT_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""ProfitAppropriations"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""ApprovalDate"" timestamp NOT NULL, ""FiscalYear"" varchar(10) NOT NULL,
                    ""NetProfit"" decimal(18,2) NOT NULL, ""LegalReserve"" decimal(18,2) NOT NULL,
                    ""DividendAmount"" decimal(18,2) NOT NULL, ""RetainedAmount"" decimal(18,2) NOT NULL,
                    ""DividendPerShare"" decimal(18,4) NOT NULL DEFAULT 0,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft', ""Notes"" text NULL,
                    ""ReserveJournalId"" uuid NULL, ""DividendJournalId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_ProfitAppropriations"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_ProfitAppropriations_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_ProfitAppropriations_ReserveJE"" FOREIGN KEY (""ReserveJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_ProfitAppropriations_DividendJE"" FOREIGN KEY (""DividendJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""CapitalTransactions"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""TransactionDate"" timestamp NOT NULL, ""TransactionType"" varchar(20) NOT NULL,
                    ""ShareQuantity"" decimal(18,4) NOT NULL, ""ParValue"" decimal(18,4) NOT NULL,
                    ""PaidAmount"" decimal(18,2) NOT NULL, ""SharePremium"" decimal(18,2) NOT NULL DEFAULT 0,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft', ""BoardResolutionRef"" varchar(100) NULL,
                    ""DbrRegistrationRef"" varchar(100) NULL, ""Notes"" text NULL, ""JournalEntryId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_CapitalTransactions"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_CapitalTransactions_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_CapitalTransactions_JE"" FOREIGN KEY (""JournalEntryId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""ShortTermInvestments"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(), ""ReferenceNo"" varchar(50) NOT NULL,
                    ""InvestmentType"" varchar(50) NOT NULL, ""Description"" text NOT NULL,
                    ""PurchaseDate"" timestamp NOT NULL, ""MaturityDate"" timestamp NULL, ""SaleDate"" timestamp NULL,
                    ""PurchaseCost"" decimal(18,2) NOT NULL, ""CurrentValue"" decimal(18,2) NOT NULL,
                    ""SaleProceeds"" decimal(18,2) NULL, ""GainLoss"" decimal(18,2) NULL,
                    ""InterestRate"" decimal(5,2) NOT NULL DEFAULT 0,
                    ""Status"" varchar(50) NOT NULL DEFAULT 'Active',
                    ""InstitutionName"" varchar(200) NULL, ""AccountNumber"" varchar(50) NULL,
                    ""InvestmentAccountId"" uuid NOT NULL,
                    ""PurchaseJournalId"" uuid NULL, ""SaleJournalId"" uuid NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_ShortTermInvestments"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_ShortTermInvestments_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id""),
                    CONSTRAINT ""FK_ShortTermInvestments_InvAcc"" FOREIGN KEY (""InvestmentAccountId"") REFERENCES ""ChartOfAccounts""(""Id""),
                    CONSTRAINT ""FK_ShortTermInvestments_PurchaseJE"" FOREIGN KEY (""PurchaseJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_ShortTermInvestments_SaleJE"" FOREIGN KEY (""SaleJournalId"") REFERENCES ""JournalEntries""(""Id"") ON DELETE SET NULL
                );",
                @"CREATE TABLE IF NOT EXISTS ""UserSignatures"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                    ""UserId"" uuid NOT NULL,
                    ""SignatureData"" text NOT NULL,
                    ""SignatureFormat"" varchar(10) NOT NULL DEFAULT 'PNG',
                    ""Label"" varchar(100) NULL,
                    ""IsDefault"" boolean NOT NULL DEFAULT false,
                    ""IsActive"" boolean NOT NULL DEFAULT true,
                    ""CreatedAt"" timestamp NOT NULL DEFAULT now(), ""UpdatedAt"" timestamp NULL,
                    ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_UserSignatures"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_UserSignatures_Users"" FOREIGN KEY (""UserId"") REFERENCES ""Users""(""Id"") ON DELETE RESTRICT
                );",
                @"CREATE TABLE IF NOT EXISTS ""DocumentApprovals"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                    ""DocumentId"" uuid NOT NULL,
                    ""ApprovalType"" varchar(50) NOT NULL,
                    ""ApproverRole"" varchar(50) NOT NULL,
                    ""StepOrder"" integer NOT NULL DEFAULT 1,
                    ""Status"" integer NOT NULL DEFAULT 0,
                    ""ApproverUserId"" uuid NULL,
                    ""ApproverName"" varchar(200) NULL,
                    ""ApproverEmail"" varchar(256) NULL,
                    ""ApproverTitle"" varchar(200) NULL,
                    ""SignatureId"" uuid NULL,
                    ""SignatureData"" text NULL,
                    ""SignatureFormat"" varchar(10) NULL,
                    ""ApprovedAt"" timestamp NULL, ""RejectedAt"" timestamp NULL,
                    ""Comments"" text NULL, ""IpAddress"" varchar(50) NULL,
                    ""PostApprovalAction"" varchar(50) NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_DocumentApprovals"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_DocumentApprovals_Documents"" FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_DocumentApprovals_Users"" FOREIGN KEY (""ApproverUserId"") REFERENCES ""Users""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_DocumentApprovals_Signatures"" FOREIGN KEY (""SignatureId"") REFERENCES ""UserSignatures""(""Id"") ON DELETE SET NULL,
                    CONSTRAINT ""FK_DocumentApprovals_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );",
                @"CREATE TABLE IF NOT EXISTS ""DocumentSignatures"" (
                    ""Id"" uuid NOT NULL DEFAULT gen_random_uuid(),
                    ""DocumentId"" uuid NOT NULL,
                    ""DocumentApprovalId"" uuid NOT NULL,
                    ""SignerRole"" varchar(100) NOT NULL,
                    ""SignerName"" varchar(200) NOT NULL,
                    ""SignerTitle"" varchar(200) NULL,
                    ""SignatureData"" text NOT NULL,
                    ""SignatureFormat"" varchar(10) NOT NULL DEFAULT 'PNG',
                    ""SignedAt"" timestamp NOT NULL DEFAULT now(),
                    ""IpAddress"" varchar(50) NULL,
                    ""CompanyId"" uuid NOT NULL, ""CreatedAt"" timestamp NOT NULL DEFAULT now(),
                    ""UpdatedAt"" timestamp NULL, ""CreatedBy"" text NULL, ""UpdatedBy"" text NULL, ""IsDeleted"" boolean NOT NULL DEFAULT false,
                    CONSTRAINT ""PK_DocumentSignatures"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_DocumentSignatures_Documents"" FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_DocumentSignatures_Approvals"" FOREIGN KEY (""DocumentApprovalId"") REFERENCES ""DocumentApprovals""(""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_DocumentSignatures_Companies"" FOREIGN KEY (""CompanyId"") REFERENCES ""Companies""(""Id"")
                );"
            };
            foreach (var sql in rawSqlStatements)
            {
                try
                {
                    using var cmd = new Npgsql.NpgsqlCommand(sql, rawConn);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Schema migration statement skipped (table/column already exists or FK target missing): {ex.Message}"); }
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
    DatabaseMigrationHelper.ApplyMissingColumns(db, app.Services.GetRequiredService<ILogger<Program>>());

    // PostgreSQL full-text search: pg_trgm GIN indexes for fast LIKE/ILIKE searches
    DatabaseMigrationHelper.ApplyFullTextSearchIndexes(db);

    // Seed default plan templates & admin user
    await SeedPlanTemplates.SeedAsync(db);
    await SeedAdminUser.SeedAsync(db, app.Configuration);

    // Cold-start OCR knowledge seed: only runs on the very first startup
    // after the tables exist (i.e. when SystemOcrAssociationRules is empty).
    // After that, the admin endpoint POST /admin/ocr-config/seed-knowledge
    // is the only way to re-seed — preserves any operator-tuned data.
    try
    {
        if (!await db.SystemOcrAssociationRules.AnyAsync())
        {
            var logger = app.Services.GetRequiredService<ILogger<Accounting.Services.Implementations.Ocr.SystemOcrKnowledgeSeeder>>();
            var seeder = new Accounting.Services.Implementations.Ocr.SystemOcrKnowledgeSeeder(db, logger);
            await seeder.SeedAsync();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "SystemOcrKnowledgeSeeder failed at startup (non-fatal — can be triggered via admin endpoint)");
    }

    // Distillation corpus seed — curated Thai vendor → account mappings
    // so new tenants get useful local-model suggestions BEFORE their
    // first user feedback row exists. Idempotent (skip-or-bump
    // TimesUsed per row) so re-running on every deploy is safe.
    try
    {
        var corpusSeeder = scope.ServiceProvider
            .GetRequiredService<Accounting.Services.Implementations.Ai.IDistillationCorpusSeeder>();
        await corpusSeeder.SeedAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "DistillationCorpusSeeder failed at startup (non-fatal)");
    }
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
        DatabaseMigrationHelper.ApplyMissingColumns(retryDb, logger);
    }
    catch (Exception retryEx) { logger.LogWarning(retryEx, "Last-resort ApplyMissingColumns also failed — DB may be unavailable"); }

    // Try to log startup error to DB if possible
    try
    {
        using var errorScope = app.Services.CreateScope();
        var errorLogService = errorScope.ServiceProvider.GetService<IErrorLogService>();
        if (errorLogService != null)
            await errorLogService.LogErrorAsync(ex, "Program.DatabaseInitialization");
    }
    catch (Exception logEx) { logger.LogWarning(logEx, "Failed to log startup error to DB — DB may be unavailable"); }
}

app.Run();
