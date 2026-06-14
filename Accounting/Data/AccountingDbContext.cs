using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

public class AccountingDbContext : DbContext
{
    public AccountingDbContext(DbContextOptions<AccountingDbContext> options) : base(options) { }

    // Core
    public DbSet<User> Users => Set<User>();
    public DbSet<LineBindCode> LineBindCodes => Set<LineBindCode>();
    public DbSet<LineUserState> LineUserStates => Set<LineUserState>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyUser> CompanyUsers => Set<CompanyUser>();
    public DbSet<CompanyInvitation> CompanyInvitations => Set<CompanyInvitation>();
    public DbSet<CompanyRole> CompanyRoles => Set<CompanyRole>();
    public DbSet<CompanyRolePermission> CompanyRolePermissions => Set<CompanyRolePermission>();
    public DbSet<SensitivityAccessRule> SensitivityAccessRules => Set<SensitivityAccessRule>();
    public DbSet<VatFilingHistory> VatFilingHistories => Set<VatFilingHistory>();
    public DbSet<PosFloorPlan> PosFloorPlans => Set<PosFloorPlan>();
    public DbSet<PosTable> PosTables => Set<PosTable>();
    public DbSet<PosReservation> PosReservations => Set<PosReservation>();

    // Subscription & Trial
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<AccountSubscription> AccountSubscriptions => Set<AccountSubscription>();
    public DbSet<TrialConfig> TrialConfigs => Set<TrialConfig>();
    public DbSet<SubscriptionHistory> SubscriptionHistories => Set<SubscriptionHistory>();
    public DbSet<PlanTemplate> PlanTemplates => Set<PlanTemplate>();
    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();

    // Accounting
    public DbSet<ChartOfAccount> ChartOfAccounts => Set<ChartOfAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();

    // Documents
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentLine> DocumentLines => Set<DocumentLine>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();

    // Tax
    public DbSet<TaxReport> TaxReports => Set<TaxReport>();
    public DbSet<TaxReportLine> TaxReportLines => Set<TaxReportLine>();

    // Audit
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();
    public DbSet<SystemAccountTemplate> SystemAccountTemplates => Set<SystemAccountTemplate>();

    // Products & Inventory
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<ProductAlias> ProductAliases => Set<ProductAlias>();
    public DbSet<ProductNegativeAlias> ProductNegativeAliases => Set<ProductNegativeAlias>();
    public DbSet<GlobalProductPattern> GlobalProductPatterns => Set<GlobalProductPattern>();
    public DbSet<GlobalProductPatternTenantSeen> GlobalProductPatternTenantSeens => Set<GlobalProductPatternTenantSeen>();
    public DbSet<GlobalExpenseCategoryPattern> GlobalExpenseCategoryPatterns => Set<GlobalExpenseCategoryPattern>();
    public DbSet<GlobalExpenseCategoryTenantSeen> GlobalExpenseCategoryTenantSeens => Set<GlobalExpenseCategoryTenantSeen>();
    public DbSet<SystemOcrVendorIntelTenantSeen> SystemOcrVendorIntelTenantSeens => Set<SystemOcrVendorIntelTenantSeen>();
    public DbSet<GlobalDocWorkflowPattern> GlobalDocWorkflowPatterns => Set<GlobalDocWorkflowPattern>();
    public DbSet<GlobalDocWorkflowTenantSeen> GlobalDocWorkflowTenantSeens => Set<GlobalDocWorkflowTenantSeen>();
    public DbSet<GlobalAssetCategoryPattern> GlobalAssetCategoryPatterns => Set<GlobalAssetCategoryPattern>();
    public DbSet<GlobalAssetCategoryTenantSeen> GlobalAssetCategoryTenantSeens => Set<GlobalAssetCategoryTenantSeen>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<InventorySnapshot> InventorySnapshots => Set<InventorySnapshot>();
    public DbSet<InventorySnapshotLine> InventorySnapshotLines => Set<InventorySnapshotLine>();
    public DbSet<SuppliesUsageLog> SuppliesUsageLogs => Set<SuppliesUsageLog>();

    // Financial Management
    public DbSet<PrepaidExpense> PrepaidExpenses => Set<PrepaidExpense>();
    public DbSet<PrepaidAmortizationSchedule> PrepaidAmortizationSchedules => Set<PrepaidAmortizationSchedule>();
    public DbSet<DepositTransaction> DepositTransactions => Set<DepositTransaction>();
    public DbSet<DepositRefund> DepositRefunds => Set<DepositRefund>();
    public DbSet<BadDebtAllowance> BadDebtAllowances => Set<BadDebtAllowance>();
    public DbSet<BadDebtAllowanceLine> BadDebtAllowanceLines => Set<BadDebtAllowanceLine>();
    public DbSet<AccruedExpense> AccruedExpenses => Set<AccruedExpense>();
    public DbSet<InventoryObsolescenceAllowance> InventoryObsolescenceAllowances => Set<InventoryObsolescenceAllowance>();
    public DbSet<InventoryObsolescenceLine> InventoryObsolescenceLines => Set<InventoryObsolescenceLine>();
    public DbSet<CorporateIncomeTax> CorporateIncomeTaxes => Set<CorporateIncomeTax>();
    public DbSet<ProfitAppropriation> ProfitAppropriations => Set<ProfitAppropriation>();
    public DbSet<CapitalTransaction> CapitalTransactions => Set<CapitalTransaction>();
    public DbSet<ShortTermInvestment> ShortTermInvestments => Set<ShortTermInvestment>();

    // Bank
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<ReconciliationGroup> ReconciliationGroups => Set<ReconciliationGroup>();
    public DbSet<ReconciliationGroupItem> ReconciliationGroupItems => Set<ReconciliationGroupItem>();
    public DbSet<BankReconciliationPattern> BankReconciliationPatterns => Set<BankReconciliationPattern>();
    public DbSet<BankMatchExclusion> BankMatchExclusions => Set<BankMatchExclusion>();
    public DbSet<BankMatchAuditLog> BankMatchAuditLogs => Set<BankMatchAuditLog>();
    public DbSet<ChequeBook> ChequeBooks => Set<ChequeBook>();
    public DbSet<Cheque> Cheques => Set<Cheque>();
    public DbSet<StampDutyRecord> StampDutyRecords => Set<StampDutyRecord>();
    public DbSet<PettyCashFund> PettyCashFunds => Set<PettyCashFund>();
    public DbSet<PettyCashTransaction> PettyCashTransactions => Set<PettyCashTransaction>();
    public DbSet<PdpaDataSubjectRequest> PdpaDataSubjectRequests => Set<PdpaDataSubjectRequest>();
    public DbSet<BillOfMaterials> BillsOfMaterials => Set<BillOfMaterials>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<ConsignmentRecord> ConsignmentRecords => Set<ConsignmentRecord>();
    public DbSet<VendorPortalToken> VendorPortalTokens => Set<VendorPortalToken>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();

    // ERP upgrade — Task 1 (periods, year-end, migration) + Task 4 (VAT deferral, e-Filing)
    // + Task 5 (OCR compliance log)
    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();
    public DbSet<YearEndClosing> YearEndClosings => Set<YearEndClosing>();
    public DbSet<MigrationSession> MigrationSessions => Set<MigrationSession>();
    public DbSet<AccountMapping> AccountMappings => Set<AccountMapping>();
    public DbSet<VatDeferral> VatDeferrals => Set<VatDeferral>();
    public DbSet<EFilingExport> EFilingExports => Set<EFilingExport>();
    public DbSet<OcrValidationLog> OcrValidationLogs => Set<OcrValidationLog>();

    // Recurring
    public DbSet<RecurringTransaction> RecurringTransactions => Set<RecurringTransaction>();

    // Fixed Assets
    public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
    public DbSet<AssetDepreciation> AssetDepreciations => Set<AssetDepreciation>();

    // Approval Workflow
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalAction> ApprovalActions => Set<ApprovalAction>();

    // File Attachments
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();

    // Notifications
    public DbSet<Notification> Notifications => Set<Notification>();

    // Number Series
    public DbSet<NumberSeries> NumberSeries => Set<NumberSeries>();

    // Currency
    public DbSet<CurrencyRate> CurrencyRates => Set<CurrencyRate>();
    public DbSet<CompanyCurrency> CompanyCurrencies => Set<CompanyCurrency>();

    // Budget
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();

    // Company Settings
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();

    // Document Templates
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

    // e-Tax Invoices
    public DbSet<EtaxInvoice> EtaxInvoices => Set<EtaxInvoice>();

    // Document Email Logs (sent emails for documents and e-Tax)
    public DbSet<DocumentEmailLog> DocumentEmailLogs => Set<DocumentEmailLog>();

    // Expense Claims
    public DbSet<ExpenseClaim> ExpenseClaims => Set<ExpenseClaim>();
    public DbSet<ExpenseClaimLine> ExpenseClaimLines => Set<ExpenseClaimLine>();

    // Withholding Tax Certificates
    public DbSet<WithholdingTaxCert> WithholdingTaxCerts => Set<WithholdingTaxCert>();
    public DbSet<WithholdingTaxCertLine> WithholdingTaxCertLines => Set<WithholdingTaxCertLine>();

    // API Keys
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Project Accounting
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectCostEntry> ProjectCostEntries => Set<ProjectCostEntry>();
    public DbSet<EmployeeProjectTime> EmployeeProjectTimes => Set<EmployeeProjectTime>();
    public DbSet<EmployeeCompensationProfile> EmployeeCompensationProfiles => Set<EmployeeCompensationProfile>();
    public DbSet<CompanyCompensationDefaults> CompanyCompensationDefaults => Set<CompanyCompensationDefaults>();

    // Warehouse Management
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<WarehouseStock> WarehouseStocks => Set<WarehouseStock>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferLine> StockTransferLines => Set<StockTransferLine>();

    // Revenue Recognition
    public DbSet<RevenueContract> RevenueContracts => Set<RevenueContract>();
    public DbSet<PerformanceObligation> PerformanceObligations => Set<PerformanceObligation>();
    public DbSet<RevenueSchedule> RevenueSchedules => Set<RevenueSchedule>();

    // Loan & Financing
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanSchedule> LoanSchedules => Set<LoanSchedule>();
    public DbSet<LoanPayment> LoanPayments => Set<LoanPayment>();

    // Commission
    public DbSet<CommissionPlan> CommissionPlans => Set<CommissionPlan>();
    public DbSet<CommissionTier> CommissionTiers => Set<CommissionTier>();
    public DbSet<CommissionAssignment> CommissionAssignments => Set<CommissionAssignment>();
    public DbSet<CommissionCalculation> CommissionCalculations => Set<CommissionCalculation>();

    // AI Features
    public DbSet<AutoCategorizationRule> AutoCategorizationRules => Set<AutoCategorizationRule>();
    public DbSet<CategorizationResult> CategorizationResults => Set<CategorizationResult>();
    public DbSet<AnomalyDetection> AnomalyDetections => Set<AnomalyDetection>();
    public DbSet<CashFlowForecast> CashFlowForecasts => Set<CashFlowForecast>();
    public DbSet<CashFlowForecastLine> CashFlowForecastLines => Set<CashFlowForecastLine>();

    // Dimensional Accounting
    public DbSet<AccountingDimension> AccountingDimensions => Set<AccountingDimension>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<JournalLineDimension> JournalLineDimensions => Set<JournalLineDimension>();

    // Intercompany
    public DbSet<IntercompanyTransaction> IntercompanyTransactions => Set<IntercompanyTransaction>();
    public DbSet<IntercompanyTransactionLine> IntercompanyTransactionLines => Set<IntercompanyTransactionLine>();

    // Consolidation
    public DbSet<ConsolidationGroup> ConsolidationGroups => Set<ConsolidationGroup>();
    public DbSet<ConsolidationMember> ConsolidationMembers => Set<ConsolidationMember>();
    public DbSet<ConsolidationReport> ConsolidationReports => Set<ConsolidationReport>();

    // Payroll
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollDetail> PayrollDetails => Set<PayrollDetail>();
    public DbSet<EmployeeLeave> EmployeeLeaves => Set<EmployeeLeave>();
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<EmployeeLeaveBalance> EmployeeLeaveBalances => Set<EmployeeLeaveBalance>();
    public DbSet<PayrollItem> PayrollItems => Set<PayrollItem>();
    public DbSet<SsoYearConfig> SsoYearConfigs => Set<SsoYearConfig>();
    public DbSet<EmailScheduleRule> EmailScheduleRules => Set<EmailScheduleRule>();
    public DbSet<EmailQueue> EmailQueues => Set<EmailQueue>();
    public DbSet<SalaryAdvance> SalaryAdvances => Set<SalaryAdvance>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<NotificationSetting> NotificationSettings => Set<NotificationSetting>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    // Tax Calendar
    public DbSet<TaxCalendarEvent> TaxCalendarEvents => Set<TaxCalendarEvent>();

    // Advanced AR/AP
    public DbSet<ContactCreditSetting> ContactCreditSettings => Set<ContactCreditSetting>();
    public DbSet<DunningLetter> DunningLetters => Set<DunningLetter>();
    public DbSet<DunningLetterLine> DunningLetterLines => Set<DunningLetterLine>();
    public DbSet<PaymentReminder> PaymentReminders => Set<PaymentReminder>();

    // OCR
    public DbSet<OcrScanResult> OcrScanResults => Set<OcrScanResult>();
    public DbSet<OcrLearnedPattern> OcrLearnedPatterns => Set<OcrLearnedPattern>();
    public DbSet<VendorKnownGoodValue> VendorKnownGoodValues => Set<VendorKnownGoodValue>();
    public DbSet<OcrCreditPurchase> OcrCreditPurchases => Set<OcrCreditPurchase>();
    public DbSet<OcrCategoryMapping> OcrCategoryMappings => Set<OcrCategoryMapping>();
    public DbSet<OcrVendorIntelligence> OcrVendorIntelligence => Set<OcrVendorIntelligence>();
    public DbSet<SystemOcrCategoryMapping> SystemOcrCategoryMappings => Set<SystemOcrCategoryMapping>();
    public DbSet<SystemOcrVendorIntelligence> SystemOcrVendorIntelligence => Set<SystemOcrVendorIntelligence>();
    public DbSet<SystemOcrAssociationRule> SystemOcrAssociationRules => Set<SystemOcrAssociationRule>();

    // Custom Reports
    public DbSet<CustomReport> CustomReports => Set<CustomReport>();

    // Portal
    public DbSet<PortalAccess> PortalAccesses => Set<PortalAccess>();
    public DbSet<PortalActivity> PortalActivities => Set<PortalActivity>();

    // FP&A
    public DbSet<FinancialScenario> FinancialScenarios => Set<FinancialScenario>();
    public DbSet<ScenarioAssumption> ScenarioAssumptions => Set<ScenarioAssumption>();
    public DbSet<ScenarioResult> ScenarioResults => Set<ScenarioResult>();
    public DbSet<FinancialKpi> FinancialKpis => Set<FinancialKpi>();
    public DbSet<KpiSnapshot> KpiSnapshots => Set<KpiSnapshot>();

    // Open Banking
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankFeedImport> BankFeedImports => Set<BankFeedImport>();

    // Compliance
    public DbSet<ComplianceFiling> ComplianceFilings => Set<ComplianceFiling>();

    // Time & Billing
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<BillingRate> BillingRates => Set<BillingRate>();

    // Webhooks
    public DbSet<WebhookRegistration> WebhookRegistrations => Set<WebhookRegistration>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    // Mobile
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<SyncQueue> SyncQueues => Set<SyncQueue>();

    // Smart Import
    public DbSet<SmartImportSession> SmartImportSessions => Set<SmartImportSession>();
    public DbSet<SmartImportColumnMapping> SmartImportColumnMappings => Set<SmartImportColumnMapping>();

    // POS
    public DbSet<PosTerminal> PosTerminals => Set<PosTerminal>();
    public DbSet<PosSession> PosSessions => Set<PosSession>();
    public DbSet<PosOrder> PosOrders => Set<PosOrder>();
    public DbSet<PosOrderItem> PosOrderItems => Set<PosOrderItem>();
    public DbSet<PosOrderItemModifier> PosOrderItemModifiers => Set<PosOrderItemModifier>();
    public DbSet<PosPayment> PosPayments => Set<PosPayment>();
    public DbSet<ServicePackage> ServicePackages => Set<ServicePackage>();
    public DbSet<ServiceComponent> ServiceComponents => Set<ServiceComponent>();
    public DbSet<PosServiceActivity> PosServiceActivities => Set<PosServiceActivity>();
    public DbSet<ProductModifierGroup> ProductModifierGroups => Set<ProductModifierGroup>();
    public DbSet<ProductModifierGroupLink> ProductModifierGroupLinks => Set<ProductModifierGroupLink>();
    public DbSet<ProductModifierOption> ProductModifierOptions => Set<ProductModifierOption>();
    public DbSet<StaffCommissionSummary> StaffCommissionSummaries => Set<StaffCommissionSummary>();

    // Contact Inquiries (public contact form)
    public DbSet<ContactInquiry> ContactInquiries => Set<ContactInquiry>();

    // Site Settings (global, singleton)
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();

    // AI Integration — provider registry, per-call feedback (training set),
    // prompt cache, per-feature local-model health, and daily usage rollup.
    public DbSet<AiProviderConfig> AiProviderConfigs => Set<AiProviderConfig>();
    public DbSet<AiSuggestionFeedback> AiSuggestionFeedbacks => Set<AiSuggestionFeedback>();
    public DbSet<AiResponseCache> AiResponseCaches => Set<AiResponseCache>();
    public DbSet<LocalModelHealth> LocalModelHealths => Set<LocalModelHealth>();
    public DbSet<AiFeatureRoutingConfig> AiFeatureRoutingConfigs => Set<AiFeatureRoutingConfig>();
    public DbSet<AiUsageDaily> AiUsageDailies => Set<AiUsageDaily>();

    // External Integration
    public DbSet<ExternalIntegration> ExternalIntegrations => Set<ExternalIntegration>();
    public DbSet<IntegrationSyncLog> IntegrationSyncLogs => Set<IntegrationSyncLog>();
    public DbSet<IntegrationAccountMapping> IntegrationAccountMappings => Set<IntegrationAccountMapping>();
    public DbSet<IntegrationUserMapping> IntegrationUserMappings => Set<IntegrationUserMapping>();

    // Signature & Approval
    public DbSet<UserSignature> UserSignatures => Set<UserSignature>();
    public DbSet<DocumentApproval> DocumentApprovals => Set<DocumentApproval>();
    public DbSet<DocumentSignature> DocumentSignatures => Set<DocumentSignature>();

    // ===== Cross-tenant B2B workflow =====
    public DbSet<TradingPartnership> TradingPartnerships => Set<TradingPartnership>();
    public DbSet<CrossTenantDocumentLink> CrossTenantDocumentLinks => Set<CrossTenantDocumentLink>();
    public DbSet<WorkflowAutomationConfig> WorkflowAutomationConfigs => Set<WorkflowAutomationConfig>();

    // ===== CMS & Multi-Site =====
    // Core
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<SiteDomain> SiteDomains => Set<SiteDomain>();
    public DbSet<SiteTheme> SiteThemes => Set<SiteTheme>();
    public DbSet<SiteLocale> SiteLocales => Set<SiteLocale>();
    public DbSet<SiteStaffAccess> SiteStaffAccesses => Set<SiteStaffAccess>();
    public DbSet<SiteCookieConsent> SiteCookieConsents => Set<SiteCookieConsent>();

    // Content
    public DbSet<SitePage> SitePages => Set<SitePage>();
    public DbSet<SitePageTranslation> SitePageTranslations => Set<SitePageTranslation>();
    public DbSet<PageBlock> PageBlocks => Set<PageBlock>();
    public DbSet<PageBlockTranslation> PageBlockTranslations => Set<PageBlockTranslation>();
    public DbSet<BlockTemplate> BlockTemplates => Set<BlockTemplate>();
    public DbSet<SiteNavigation> SiteNavigations => Set<SiteNavigation>();
    public DbSet<SiteMenuItem> SiteMenuItems => Set<SiteMenuItem>();
    public DbSet<SiteMedia> SiteMediaItems => Set<SiteMedia>();
    public DbSet<SiteSeoRedirect> SiteSeoRedirects => Set<SiteSeoRedirect>();

    // Commerce
    public DbSet<CmsLead> CmsLeads => Set<CmsLead>();
    public DbSet<SiteProduct> SiteProducts => Set<SiteProduct>();
    public DbSet<SiteProductTranslation> SiteProductTranslations => Set<SiteProductTranslation>();
    public DbSet<SitePricingTier> SitePricingTiers => Set<SitePricingTier>();
    public DbSet<SiteCategory> SiteCategories => Set<SiteCategory>();
    public DbSet<SiteCart> SiteCarts => Set<SiteCart>();
    public DbSet<SiteCartItem> SiteCartItems => Set<SiteCartItem>();
    public DbSet<SiteOrder> SiteOrders => Set<SiteOrder>();
    public DbSet<SiteOrderLine> SiteOrderLines => Set<SiteOrderLine>();
    public DbSet<SiteOrderPayment> SiteOrderPayments => Set<SiteOrderPayment>();
    public DbSet<SitePaymentGateway> SitePaymentGateways => Set<SitePaymentGateway>();

    // Commerce Extensions
    public DbSet<SiteCoupon> SiteCoupons => Set<SiteCoupon>();
    public DbSet<SiteCouponUsage> SiteCouponUsages => Set<SiteCouponUsage>();
    public DbSet<SiteShippingZone> SiteShippingZones => Set<SiteShippingZone>();
    public DbSet<SiteShippingRate> SiteShippingRates => Set<SiteShippingRate>();
    public DbSet<SiteProductVariant> SiteProductVariants => Set<SiteProductVariant>();
    public DbSet<SiteProductOption> SiteProductOptions => Set<SiteProductOption>();
    public DbSet<SiteProductOptionValue> SiteProductOptionValues => Set<SiteProductOptionValue>();
    public DbSet<SiteProductReview> SiteProductReviews => Set<SiteProductReview>();
    public DbSet<SiteCommerceConfig> SiteCommerceConfigs => Set<SiteCommerceConfig>();

    // Booking
    public DbSet<SiteBookingService> SiteBookingServices => Set<SiteBookingService>();
    public DbSet<SiteBookingServiceTranslation> SiteBookingServiceTranslations => Set<SiteBookingServiceTranslation>();
    public DbSet<SiteBookingSlot> SiteBookingSlots => Set<SiteBookingSlot>();
    public DbSet<SiteBooking> SiteBookings => Set<SiteBooking>();
    public DbSet<SiteBookingPayment> SiteBookingPayments => Set<SiteBookingPayment>();

    // Customer & CRM
    public DbSet<SiteCustomer> SiteCustomers => Set<SiteCustomer>();
    public DbSet<SiteCustomerAddress> SiteCustomerAddresses => Set<SiteCustomerAddress>();
    public DbSet<SiteWishlistItem> SiteWishlistItems => Set<SiteWishlistItem>();
    public DbSet<SiteForm> SiteForms => Set<SiteForm>();
    public DbSet<SiteFormField> SiteFormFields => Set<SiteFormField>();
    public DbSet<SiteFormSubmission> SiteFormSubmissions => Set<SiteFormSubmission>();
    public DbSet<SiteCustomerMerge> SiteCustomerMerges => Set<SiteCustomerMerge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ===== User =====
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.FullName).HasMaxLength(256);
            e.HasQueryFilter(u => !u.IsDeleted);
        });

        // ===== Company =====
        modelBuilder.Entity<Company>(e =>
        {
            e.HasIndex(c => c.TaxId);
            e.Property(c => c.Name).HasMaxLength(500);
            e.Property(c => c.TaxId).HasMaxLength(13);
            e.Property(c => c.BaseCurrency).HasMaxLength(3);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== CompanyUser (many-to-many) =====
        modelBuilder.Entity<CompanyUser>(e =>
        {
            e.HasKey(cu => new { cu.UserId, cu.CompanyId });
            e.HasOne(cu => cu.User).WithMany(u => u.CompanyUsers).HasForeignKey(cu => cu.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(cu => cu.Company).WithMany(c => c.CompanyUsers).HasForeignKey(cu => cu.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(cu => cu.CompanyRole).WithMany(r => r.CompanyUsers).HasForeignKey(cu => cu.CompanyRoleId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== CompanyRole (custom roles per company) =====
        modelBuilder.Entity<CompanyRole>(e =>
        {
            e.HasIndex(r => new { r.CompanyId, r.Name }).IsUnique();
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.Color).HasMaxLength(20);
            e.Property(r => r.Icon).HasMaxLength(10);
            e.HasQueryFilter(r => !r.IsDeleted);
        });

        // ===== CompanyRolePermission =====
        modelBuilder.Entity<CompanyRolePermission>(e =>
        {
            e.HasIndex(p => new { p.CompanyRoleId, p.MenuItemId }).IsUnique();
            e.Property(p => p.MenuItemId).HasMaxLength(100);
            e.HasOne(p => p.CompanyRole).WithMany(r => r.Permissions).HasForeignKey(p => p.CompanyRoleId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== Subscription =====
        modelBuilder.Entity<Subscription>(e =>
        {
            e.HasIndex(s => s.CompanyId).IsUnique();
            e.HasOne(s => s.Company).WithOne(c => c.Subscription).HasForeignKey<Subscription>(s => s.CompanyId);
            e.Property(s => s.PricePerCycle).HasPrecision(18, 2);
            e.Property(s => s.Currency).HasMaxLength(3);
            e.Property(s => s.NotifyDaysBeforeExpiry).HasMaxLength(100);
            e.Property(s => s.NotifyDaysAfterExpiry).HasMaxLength(100);
            e.Property(s => s.NotifyDaysBeforeDeactivation).HasMaxLength(100);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        // ===== CompanyInvitation =====
        modelBuilder.Entity<CompanyInvitation>(e =>
        {
            e.HasOne(i => i.Company).WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.InvitedBy).WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(i => i.Token).IsUnique();
            // Email-lookup happens during invite acceptance — narrow index.
            e.HasIndex(i => new { i.CompanyId, i.Email });
            e.HasQueryFilter(i => !i.IsDeleted);
        });

        // ===== AccountSubscription =====
        // Without explicit FK binding, EF's convention names the FK for the
        // `Owner` nav `OwnerId` (NavName + "Id") and creates a SHADOW column
        // of that name. The schema migration adds an `OwnerUserId` column but
        // EF inserts use the shadow `OwnerId` set to Guid.Empty, which fails
        // FK_AccountSubscriptions_Users_OwnerId. Force EF to use OwnerUserId.
        modelBuilder.Entity<AccountSubscription>(e =>
        {
            e.HasOne(a => a.Owner).WithMany().HasForeignKey(a => a.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.PlanTemplate).WithMany().HasForeignKey(a => a.PlanTemplateId).OnDelete(DeleteBehavior.Restrict);
            e.Property(a => a.MonthlyPrice).HasPrecision(18, 2);
            e.Property(a => a.AnnualPrice).HasPrecision(18, 2);
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // ===== TrialConfig =====
        modelBuilder.Entity<TrialConfig>(e =>
        {
            e.HasIndex(t => t.SubscriptionId).IsUnique();
            e.HasOne(t => t.Subscription).WithOne(s => s.TrialConfig).HasForeignKey<TrialConfig>(t => t.SubscriptionId);
            e.Property(t => t.TrialMessage).HasMaxLength(500);
            e.Property(t => t.ConversionPromoCode).HasMaxLength(50);
            e.Property(t => t.DiscountPercentOnConversion).HasPrecision(5, 2);
        });

        // ===== SubscriptionHistory =====
        modelBuilder.Entity<SubscriptionHistory>(e =>
        {
            e.HasOne(h => h.Subscription).WithMany(s => s.History).HasForeignKey(h => h.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(h => h.Action).HasMaxLength(100);
        });

        // ===== PlanTemplate =====
        modelBuilder.Entity<PlanTemplate>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(100);
            e.Property(p => p.MonthlyPrice).HasPrecision(18, 2);
            e.Property(p => p.QuarterlyPrice).HasPrecision(18, 2);
            e.Property(p => p.SemiAnnualPrice).HasPrecision(18, 2);
            e.Property(p => p.AnnualPrice).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
        });

        // ===== SubscriptionPayment =====
        modelBuilder.Entity<SubscriptionPayment>(e =>
        {
            e.HasIndex(p => p.PaymentNumber).IsUnique();
            e.HasIndex(p => new { p.SubscriptionId, p.Status });
            e.Property(p => p.PaymentNumber).HasMaxLength(50);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.FromBankName).HasMaxLength(100);
            e.Property(p => p.FromAccountNumber).HasMaxLength(50);
            e.Property(p => p.ToBankName).HasMaxLength(100);
            e.Property(p => p.ToAccountNumber).HasMaxLength(50);
            e.Property(p => p.TransferReference).HasMaxLength(100);
            e.Property(p => p.SlipFileName).HasMaxLength(500);
            e.Property(p => p.SlipOriginalFileName).HasMaxLength(500);
            e.Property(p => p.SlipContentType).HasMaxLength(100);
            e.Property(p => p.ReviewNotes).HasMaxLength(1000);
            e.Property(p => p.RejectionReason).HasMaxLength(1000);
            e.Property(p => p.CustomerNotes).HasMaxLength(1000);
            e.HasOne(p => p.Subscription).WithMany(s => s.SubscriptionPayments).HasForeignKey(p => p.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.ReviewedByUser).WithMany().HasForeignKey(p => p.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ChartOfAccount =====
        modelBuilder.Entity<ChartOfAccount>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.AccountCode }).IsUnique();
            e.Property(a => a.AccountCode).HasMaxLength(20);
            e.Property(a => a.AccountName).HasMaxLength(256);
            e.HasOne(a => a.ParentAccount).WithMany(a => a.ChildAccounts).HasForeignKey(a => a.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // ===== JournalEntry =====
        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.HasIndex(j => new { j.CompanyId, j.EntryNumber }).IsUnique();
            e.Property(j => j.EntryNumber).HasMaxLength(50);
            e.Property(j => j.TotalDebit).HasPrecision(18, 2);
            e.Property(j => j.TotalCredit).HasPrecision(18, 2);
            e.Property(j => j.Note).HasMaxLength(2000);
            e.Property(j => j.Tags).HasMaxLength(500);
            e.HasOne(j => j.FiscalPeriod).WithMany(f => f.JournalEntries).HasForeignKey(j => j.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(j => j.Project).WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(j => new { j.CompanyId, j.ProjectId }).HasDatabaseName("IX_JournalEntries_CompanyId_ProjectId");
            e.HasIndex(j => new { j.CompanyId, j.BranchId }).HasDatabaseName("IX_JournalEntries_CompanyId_BranchId");
            e.HasIndex(j => new { j.CompanyId, j.DimensionId }).HasDatabaseName("IX_JournalEntries_CompanyId_DimensionId");
            e.HasIndex(j => new { j.CompanyId, j.SourceDocumentId }).HasDatabaseName("IX_JournalEntries_CompanyId_SourceDocumentId");
            e.HasQueryFilter(j => !j.IsDeleted);
        });

        // ===== JournalEntryLine =====
        modelBuilder.Entity<JournalEntryLine>(e =>
        {
            e.HasOne(l => l.JournalEntry).WithMany(j => j.Lines).HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany(a => a.JournalEntryLines).HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Project).WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.Property(l => l.DebitAmount).HasPrecision(18, 2);
            e.Property(l => l.CreditAmount).HasPrecision(18, 2);
            e.Property(l => l.Tags).HasMaxLength(500);
            e.HasIndex(l => l.ProjectId).HasDatabaseName("IX_JournalEntryLines_ProjectId");
        });

        // ===== FiscalPeriod =====
        modelBuilder.Entity<FiscalPeriod>(e =>
        {
            e.HasIndex(f => new { f.CompanyId, f.Year, f.Month }).IsUnique();
            e.Property(f => f.Name).HasMaxLength(20);
            e.HasQueryFilter(f => !f.IsDeleted);
        });

        // ===== ERP Upgrade entities (Task 1, 4, 5) =====
        modelBuilder.Entity<OpeningBalance>(e =>
        {
            e.Property(o => o.OpeningDebit).HasPrecision(18, 2);
            e.Property(o => o.OpeningCredit).HasPrecision(18, 2);
            e.HasOne(o => o.FiscalPeriod).WithMany().HasForeignKey(o => o.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Account).WithMany().HasForeignKey(o => o.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(o => new { o.CompanyId, o.FiscalPeriodId, o.AccountId }).IsUnique();
            e.HasQueryFilter(o => !o.IsDeleted);
        });
        modelBuilder.Entity<YearEndClosing>(e =>
        {
            e.Property(y => y.TransferredAmount).HasPrecision(18, 2);
            e.HasOne(y => y.RetainedEarningsAccount).WithMany().HasForeignKey(y => y.RetainedEarningsAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(y => y.ClosingJournalEntry).WithMany().HasForeignKey(y => y.ClosingJournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(y => new { y.CompanyId, y.FiscalYear }).IsUnique();
            e.HasQueryFilter(y => !y.IsDeleted);
        });
        modelBuilder.Entity<MigrationSession>(e =>
        {
            e.Property(m => m.SessionName).HasMaxLength(200);
            e.HasIndex(m => new { m.CompanyId, m.Status });
            e.HasQueryFilter(m => !m.IsDeleted);
        });
        modelBuilder.Entity<AccountMapping>(e =>
        {
            e.Property(a => a.LegacyCode).HasMaxLength(50);
            e.Property(a => a.LegacyName).HasMaxLength(200);
            e.Property(a => a.LegacyDebit).HasPrecision(18, 2);
            e.Property(a => a.LegacyCredit).HasPrecision(18, 2);
            e.HasOne(a => a.MigrationSession).WithMany().HasForeignKey(a => a.MigrationSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.MappedAccount).WithMany().HasForeignKey(a => a.MappedAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(a => new { a.MigrationSessionId, a.LegacyCode });
        });
        modelBuilder.Entity<VatDeferral>(e =>
        {
            e.Property(v => v.DeferredAmount).HasPrecision(18, 2);
            e.Property(v => v.DeferralReason).HasMaxLength(500);
            e.HasOne(v => v.Document).WithMany().HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(v => v.OriginalTaxReport).WithMany().HasForeignKey(v => v.OriginalTaxReportId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => new { v.CompanyId, v.DeferredToPeriod });
            e.HasQueryFilter(v => !v.IsDeleted);
        });
        modelBuilder.Entity<EFilingExport>(e =>
        {
            e.Property(x => x.FormType).HasMaxLength(20);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.Property(x => x.TotalTax).HasPrecision(18, 2);
            e.HasIndex(x => new { x.CompanyId, x.FormType, x.PeriodYear, x.PeriodMonth });
            e.HasQueryFilter(x => !x.IsDeleted);
        });
        modelBuilder.Entity<OcrValidationLog>(e =>
        {
            e.Property(o => o.RuleCode).HasMaxLength(50);
            e.Property(o => o.Severity).HasMaxLength(20);
            e.HasOne(o => o.Document).WithMany().HasForeignKey(o => o.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(o => o.DocumentId);
            e.HasQueryFilter(o => !o.IsDeleted);
        });

        // ===== Document =====
        modelBuilder.Entity<Document>(e =>
        {
            e.HasIndex(d => new { d.CompanyId, d.DocumentNumber }).IsUnique();
            e.Property(d => d.DocumentNumber).HasMaxLength(50);
            e.Property(d => d.SubTotal).HasPrecision(18, 2);
            e.Property(d => d.DiscountAmount).HasPrecision(18, 2);
            e.Property(d => d.VatAmount).HasPrecision(18, 2);
            e.Property(d => d.WithholdingTaxAmount).HasPrecision(18, 2);
            e.Property(d => d.TotalAmount).HasPrecision(18, 2);
            e.Property(d => d.PaidAmount).HasPrecision(18, 2);
            e.Property(d => d.BalanceDue).HasPrecision(18, 2);
            e.Property(d => d.Currency).HasMaxLength(3);
            e.HasOne(d => d.Contact).WithMany(c => c.Documents).HasForeignKey(d => d.ContactId).OnDelete(DeleteBehavior.Restrict);
            // Project link (nullable) — preserves doc when project is deleted
            e.HasOne(d => d.Project).WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(d => d.ProjectId);
            // Bank account link — which bank account money flows through
            e.HasOne(d => d.BankAccount).WithMany().HasForeignKey(d => d.BankAccountId).OnDelete(DeleteBehavior.SetNull);
            // Payment account — direct GL for non-bank money flow (cash, director advance, etc.)
            e.HasOne(d => d.PaymentAccount).WithMany().HasForeignKey(d => d.PaymentAccountId).OnDelete(DeleteBehavior.SetNull);
            // Expense category — header-level chart of account for expense documents
            e.HasOne(d => d.ExpenseCategory).WithMany().HasForeignKey(d => d.ExpenseCategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ===== DocumentLine =====
        modelBuilder.Entity<DocumentLine>(e =>
        {
            e.HasOne(l => l.Document).WithMany(d => d.Lines).HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Project).WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(l => l.ProjectId);
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.Property(l => l.VatAmount).HasPrecision(18, 2);
            e.Property(l => l.WithholdingTaxAmount).HasPrecision(18, 2);
            e.Property(l => l.DiscountAmount).HasPrecision(18, 2);
            e.Property(l => l.DiscountPercent).HasPrecision(5, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 2);
            e.Property(l => l.WithholdingTaxRate).HasPrecision(5, 2);
            e.Property(l => l.Unit).HasMaxLength(50);
            e.Property(l => l.Description).HasMaxLength(1000);
        });

        // ===== Contact =====
        modelBuilder.Entity<Contact>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(500);
            e.Property(c => c.TaxId).HasMaxLength(13);
            // Per-contact GL account overrides — SetNull on delete so
            // deleting an account doesn't cascade-orphan the contact.
            e.HasOne(c => c.DefaultArAccount).WithMany()
                .HasForeignKey(c => c.DefaultArAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.DefaultApAccount).WithMany()
                .HasForeignKey(c => c.DefaultApAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.DefaultIrGrAccount).WithMany()
                .HasForeignKey(c => c.DefaultIrGrAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== Payment =====
        modelBuilder.Entity<Payment>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.PaymentNumber }).IsUnique();
            e.Property(p => p.PaymentNumber).HasMaxLength(50);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.HasOne(p => p.Document).WithMany(d => d.Payments).HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.BankAccountEntity).WithMany().HasForeignKey(p => p.BankAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== TaxReport =====
        modelBuilder.Entity<TaxReport>(e =>
        {
            e.HasIndex(t => new { t.CompanyId, t.TaxType, t.Year, t.Month }).IsUnique();
            e.Property(t => t.OutputVat).HasPrecision(18, 2);
            e.Property(t => t.InputVat).HasPrecision(18, 2);
            e.Property(t => t.NetVat).HasPrecision(18, 2);
            e.Property(t => t.TotalIncome).HasPrecision(18, 2);
            e.Property(t => t.TotalTaxWithheld).HasPrecision(18, 2);
            e.HasQueryFilter(t => !t.IsDeleted);
        });

        // ===== TaxReportLine =====
        modelBuilder.Entity<TaxReportLine>(e =>
        {
            e.HasOne(l => l.TaxReport).WithMany(r => r.Lines).HasForeignKey(l => l.TaxReportId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.IncomeAmount).HasPrecision(18, 2);
            e.Property(l => l.TaxRate).HasPrecision(5, 2);
            e.Property(l => l.TaxAmount).HasPrecision(18, 2);
        });

        // ===== AuditLog =====
        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.Timestamp);
            e.Property(a => a.EntityType).HasMaxLength(100);
        });

        // ===== ErrorLog =====
        modelBuilder.Entity<ErrorLog>(e =>
        {
            e.HasIndex(l => l.Timestamp);
            e.Property(l => l.ExceptionType).HasMaxLength(500);
            e.Property(l => l.RequestPath).HasMaxLength(2000);
            e.Property(l => l.HttpMethod).HasMaxLength(10);
        });

        // ===== Product =====
        modelBuilder.Entity<Product>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();
            e.Property(p => p.Code).HasMaxLength(50);
            e.Property(p => p.Name).HasMaxLength(500);
            e.Property(p => p.SellingPrice).HasPrecision(18, 2);
            e.Property(p => p.CostPrice).HasPrecision(18, 2);
            e.Property(p => p.VatRate).HasPrecision(5, 2);
            e.Property(p => p.CurrentStock).HasPrecision(18, 4);
            e.Property(p => p.MinimumStock).HasPrecision(18, 4);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== StockMovement =====
        modelBuilder.Entity<StockMovement>(e =>
        {
            e.Property(m => m.Quantity).HasPrecision(18, 4);
            e.Property(m => m.UnitCost).HasPrecision(18, 2);
            e.Property(m => m.BalanceAfter).HasPrecision(18, 4);
            e.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== UnitConversion =====
        modelBuilder.Entity<UnitConversion>(e =>
        {
            e.Property(u => u.FromUnit).HasMaxLength(50);
            e.Property(u => u.ToUnit).HasMaxLength(50);
            e.Property(u => u.ConversionRate).HasPrecision(18, 6);
            e.Property(u => u.SellingPrice).HasPrecision(18, 2);
            e.Property(u => u.CostPrice).HasPrecision(18, 2);
            e.Property(u => u.Barcode).HasMaxLength(100);
            e.HasOne(u => u.Product).WithMany().HasForeignKey(u => u.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(u => !u.IsDeleted);
        });

        // ===== ProductCategory =====
        modelBuilder.Entity<ProductCategory>(e =>
        {
            e.Property(c => c.Code).HasMaxLength(50);
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasIndex(c => new { c.CompanyId, c.Code }).IsUnique();
            e.HasOne(c => c.ParentCategory).WithMany().HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== StockCount =====
        modelBuilder.Entity<StockCount>(e =>
        {
            e.Property(c => c.CountNumber).HasMaxLength(50);
            e.Property(c => c.Status).HasMaxLength(20);
            e.HasIndex(c => new { c.CompanyId, c.CountNumber }).IsUnique();
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        modelBuilder.Entity<StockCountLine>(e =>
        {
            e.Property(l => l.SystemQty).HasPrecision(18, 4);
            e.Property(l => l.CountedQty).HasPrecision(18, 4);
            e.Property(l => l.Variance).HasPrecision(18, 4);
            e.HasOne(l => l.StockCount).WithMany(c => c.Lines).HasForeignKey(l => l.StockCountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        // ===== InventorySnapshot =====
        modelBuilder.Entity<InventorySnapshot>(e =>
        {
            e.Property(s => s.Status).HasMaxLength(20);
            e.Property(s => s.TotalValue).HasPrecision(18, 2);
            e.HasOne(s => s.JournalEntry).WithMany().HasForeignKey(s => s.JournalEntryId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        modelBuilder.Entity<InventorySnapshotLine>(e =>
        {
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.Property(l => l.UnitCost).HasPrecision(18, 2);
            e.Property(l => l.TotalValue).HasPrecision(18, 2);
            e.HasOne(l => l.Snapshot).WithMany(s => s.Lines).HasForeignKey(l => l.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        // ===== BankAccount =====
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.Property(a => a.AccountName).HasMaxLength(256);
            e.Property(a => a.BankName).HasMaxLength(100);
            e.Property(a => a.AccountNumber).HasMaxLength(50);
            e.Property(a => a.CurrentBalance).HasPrecision(18, 2);
            e.Property(a => a.Currency).HasMaxLength(3);
            e.HasOne(a => a.LinkedAccount).WithMany().HasForeignKey(a => a.LinkedAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // ===== BankTransaction =====
        modelBuilder.Entity<BankTransaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.BalanceAfter).HasPrecision(18, 2);
            e.HasOne(t => t.BankAccount).WithMany(a => a.Transactions).HasForeignKey(t => t.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.ReconciliationGroup).WithMany().HasForeignKey(t => t.ReconciliationGroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(t => !t.IsDeleted);
        });

        // ===== ReconciliationGroup + Items (M:N + net-off bank matching) =====
        modelBuilder.Entity<ReconciliationGroup>(e =>
        {
            e.Property(g => g.GroupNumber).HasMaxLength(40);
            e.Property(g => g.TotalBankAmount).HasPrecision(18, 2);
            e.Property(g => g.TotalMatchedAmount).HasPrecision(18, 2);
            e.HasIndex(g => new { g.CompanyId, g.GroupNumber }).IsUnique();
            e.HasOne(g => g.BankAccount).WithMany().HasForeignKey(g => g.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(g => !g.IsDeleted);
        });
        modelBuilder.Entity<ReconciliationGroupItem>(e =>
        {
            e.Property(i => i.AllocatedAmount).HasPrecision(18, 2);
            e.HasOne(i => i.Group).WithMany(g => g.Items).HasForeignKey(i => i.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.ItemType, i.ItemId });
        });

        modelBuilder.Entity<BankReconciliationPattern>(e =>
        {
            e.Property(p => p.DescriptionSignature).HasMaxLength(500);
            e.Property(p => p.AmountBucket).HasMaxLength(20);
            e.Property(p => p.TargetAccountCode).HasMaxLength(20);
            e.Property(p => p.AvgAmount).HasPrecision(18, 2);
            e.Property(p => p.MinAmount).HasPrecision(18, 2);
            e.Property(p => p.MaxAmount).HasPrecision(18, 2);
            e.HasIndex(p => new { p.CompanyId, p.BankAccountId, p.DescriptionSignature, p.AmountBucket })
                .HasDatabaseName("IX_BankReconciliationPatterns_Lookup");
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        modelBuilder.Entity<BankMatchExclusion>(e =>
        {
            e.Property(x => x.CandidateType).HasMaxLength(32);
            e.HasIndex(x => new { x.CompanyId, x.BankTransactionId, x.CandidateId })
                .HasDatabaseName("IX_BankMatchExclusions_Lookup");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<BankMatchAuditLog>(e =>
        {
            e.Property(x => x.ConfidenceAtApply).HasPrecision(5, 4);
            e.HasIndex(x => new { x.CompanyId, x.BankTransactionId })
                .HasDatabaseName("IX_BankMatchAuditLog_Lookup");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // ===== RecurringTransaction =====
        modelBuilder.Entity<RecurringTransaction>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(256);
            e.HasIndex(r => new { r.CompanyId, r.Status, r.NextRunDate })
                .HasDatabaseName("IX_RecurringTransactions_CompanyId_Status_NextRunDate");
            e.HasQueryFilter(r => !r.IsDeleted);
        });

        // ===== FixedAsset =====
        modelBuilder.Entity<FixedAsset>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.AssetCode }).IsUnique();
            e.Property(a => a.AssetCode).HasMaxLength(50);
            e.Property(a => a.Name).HasMaxLength(500);
            e.Property(a => a.PurchaseCost).HasPrecision(18, 2);
            e.Property(a => a.SalvageValue).HasPrecision(18, 2);
            e.Property(a => a.AccumulatedDepreciation).HasPrecision(18, 2);
            e.Property(a => a.NetBookValue).HasPrecision(18, 2);
            e.Property(a => a.DisposalAmount).HasPrecision(18, 2);
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // ===== AssetDepreciation =====
        modelBuilder.Entity<AssetDepreciation>(e =>
        {
            e.Property(d => d.Amount).HasPrecision(18, 2);
            e.Property(d => d.AccumulatedAmount).HasPrecision(18, 2);
            e.Property(d => d.NetBookValue).HasPrecision(18, 2);
            e.HasOne(d => d.FixedAsset).WithMany(a => a.Depreciations).HasForeignKey(d => d.FixedAssetId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ApprovalRule =====
        modelBuilder.Entity<ApprovalRule>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(256);
            e.Property(r => r.MinAmount).HasPrecision(18, 2);
            e.Property(r => r.MaxAmount).HasPrecision(18, 2);
            e.HasQueryFilter(r => !r.IsDeleted);
        });

        // ===== ApprovalStep =====
        modelBuilder.Entity<ApprovalStep>(e =>
        {
            e.HasOne(s => s.ApprovalRule).WithMany(r => r.Steps).HasForeignKey(s => s.ApprovalRuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.ApproverUser).WithMany().HasForeignKey(s => s.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ApprovalRequest =====
        modelBuilder.Entity<ApprovalRequest>(e =>
        {
            e.HasIndex(r => new { r.EntityType, r.EntityId });
            e.HasOne(r => r.ApprovalRule).WithMany().HasForeignKey(r => r.ApprovalRuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.RequestedByUser).WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ApprovalAction =====
        modelBuilder.Entity<ApprovalAction>(e =>
        {
            e.HasOne(a => a.ApprovalRequest).WithMany(r => r.Actions).HasForeignKey(a => a.ApprovalRequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.ApproverUser).WithMany().HasForeignKey(a => a.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FileAttachment =====
        modelBuilder.Entity<FileAttachment>(e =>
        {
            e.HasIndex(f => new { f.EntityType, f.EntityId });
            e.Property(f => f.FileName).HasMaxLength(500);
            e.Property(f => f.ContentType).HasMaxLength(100);
            e.HasOne(f => f.UploadedByUser).WithMany().HasForeignKey(f => f.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== Notification =====
        modelBuilder.Entity<Notification>(e =>
        {
            e.Property(n => n.Title).HasMaxLength(500);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== NumberSeries =====
        modelBuilder.Entity<NumberSeries>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.DocumentType }).IsUnique().HasFilter("\"IsActive\" = true");
            e.Property(n => n.Prefix).HasMaxLength(20);
            e.Property(n => n.Format).HasMaxLength(200);
        });

        // ===== CurrencyRate =====
        modelBuilder.Entity<CurrencyRate>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.FromCurrency, c.ToCurrency, c.EffectiveDate });
            e.Property(c => c.FromCurrency).HasMaxLength(3);
            e.Property(c => c.ToCurrency).HasMaxLength(3);
            e.Property(c => c.BuyRate).HasPrecision(18, 6);
            e.Property(c => c.SellRate).HasPrecision(18, 6);
            e.Property(c => c.MidRate).HasPrecision(18, 6);
        });

        // ===== CompanyCurrency =====
        modelBuilder.Entity<CompanyCurrency>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.CurrencyCode }).IsUnique();
            e.Property(c => c.CurrencyCode).HasMaxLength(3);
            e.Property(c => c.CurrencyName).HasMaxLength(50);
        });

        // ===== Budget =====
        modelBuilder.Entity<Budget>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(256);
            e.HasQueryFilter(b => !b.IsDeleted);
        });

        // ===== BudgetLine =====
        modelBuilder.Entity<BudgetLine>(e =>
        {
            e.HasOne(l => l.Budget).WithMany(b => b.Lines).HasForeignKey(l => l.BudgetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            for (int i = 1; i <= 12; i++)
                e.Property($"Month{i}").HasPrecision(18, 2);
            e.Property(l => l.TotalBudget).HasPrecision(18, 2);
        });

        // ===== CompanySettings =====
        modelBuilder.Entity<CompanySettings>(e =>
        {
            e.HasIndex(s => s.CompanyId).IsUnique();
            e.Property(s => s.DefaultVatRate).HasPrecision(5, 2);
            e.Property(s => s.ApprovalThresholdAmount).HasPrecision(18, 2);
        });

        // ===== ApiKey =====
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.KeyPrefix);
            e.Property(k => k.Name).HasMaxLength(256);
            e.Property(k => k.KeyPrefix).HasMaxLength(20);
            e.HasOne(k => k.Company).WithMany().HasForeignKey(k => k.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(k => k.CreatedByUser).WithMany().HasForeignKey(k => k.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== DocumentTemplate =====
        modelBuilder.Entity<DocumentTemplate>(e =>
        {
            e.HasIndex(t => new { t.CompanyId, t.DocumentType, t.IsDefault }).HasFilter("\"IsDefault\" = true AND \"IsDeleted\" = false");
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.PaperSize).HasMaxLength(10);
            e.Property(t => t.Orientation).HasMaxLength(20);
            e.Property(t => t.FontFamily).HasMaxLength(100);
            e.Property(t => t.Language).HasMaxLength(10);
            e.Property(t => t.MarginTop).HasPrecision(5, 2);
            e.Property(t => t.MarginBottom).HasPrecision(5, 2);
            e.Property(t => t.MarginLeft).HasPrecision(5, 2);
            e.Property(t => t.MarginRight).HasPrecision(5, 2);
            e.Property(t => t.LogoWidth).HasPrecision(5, 2);
            e.Property(t => t.LogoHeight).HasPrecision(5, 2);
            e.Property(t => t.WatermarkOpacity).HasPrecision(3, 2);
            e.HasQueryFilter(t => !t.IsDeleted);
        });

        // ===== EtaxInvoice =====
        modelBuilder.Entity<EtaxInvoice>(e =>
        {
            e.HasIndex(ei => new { ei.CompanyId, ei.EtaxRefNumber }).IsUnique();
            e.HasIndex(ei => new { ei.CompanyId, ei.DocumentId });
            e.Property(ei => ei.EtaxRefNumber).HasMaxLength(100);
            e.Property(ei => ei.SellerName).HasMaxLength(500);
            e.Property(ei => ei.SellerTaxId).HasMaxLength(13);
            e.Property(ei => ei.BuyerName).HasMaxLength(500);
            e.Property(ei => ei.BuyerTaxId).HasMaxLength(13);
            e.HasOne(ei => ei.Document).WithMany().HasForeignKey(ei => ei.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(ei => !ei.IsDeleted);
        });

        // ===== DocumentEmailLog =====
        modelBuilder.Entity<DocumentEmailLog>(e =>
        {
            e.HasIndex(l => new { l.CompanyId, l.DocumentId });
            e.HasIndex(l => new { l.CompanyId, l.EtaxInvoiceId });
            e.Property(l => l.ToEmail).HasMaxLength(500);
            e.Property(l => l.CcEmail).HasMaxLength(1000);
            e.Property(l => l.BccEmail).HasMaxLength(1000);
            e.Property(l => l.Subject).HasMaxLength(500);
            e.Property(l => l.ProviderMessageId).HasMaxLength(200);
            e.HasOne(l => l.Document).WithMany().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(l => l.EtaxInvoice).WithMany().HasForeignKey(l => l.EtaxInvoiceId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        // ===== ExpenseClaim =====
        modelBuilder.Entity<ExpenseClaim>(e =>
        {
            e.HasIndex(ec => new { ec.CompanyId, ec.ClaimNumber }).IsUnique();
            e.Property(ec => ec.ClaimNumber).HasMaxLength(50);
            e.Property(ec => ec.Title).HasMaxLength(500);
            e.Property(ec => ec.SubTotal).HasPrecision(18, 2);
            e.Property(ec => ec.VatAmount).HasPrecision(18, 2);
            e.Property(ec => ec.WithholdingTaxAmount).HasPrecision(18, 2);
            e.Property(ec => ec.TotalAmount).HasPrecision(18, 2);
            e.HasOne(ec => ec.SubmittedByUser).WithMany().HasForeignKey(ec => ec.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(ec => ec.ApprovedByUser).WithMany().HasForeignKey(ec => ec.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(ec => ec.PaymentVoucherDocument).WithMany().HasForeignKey(ec => ec.PaymentVoucherDocumentId).OnDelete(DeleteBehavior.SetNull);
            // Auto-generated CertificateInLieu (§65 ทวิ) — SetNull on the
            // claim row when the document is deleted; the certificate
            // entity owns its own GL posting, the claim is just an HR
            // claim record that references it.
            e.HasOne(ec => ec.CertificateInLieuDocument).WithMany()
                .HasForeignKey(ec => ec.CertificateInLieuDocumentId)
                .OnDelete(DeleteBehavior.SetNull);
            e.Property(ec => ec.NoReceiptReason).HasMaxLength(500);
            e.Property(ec => ec.WitnessName).HasMaxLength(200);
            e.Property(ec => ec.WitnessPosition).HasMaxLength(200);
            e.HasQueryFilter(ec => !ec.IsDeleted);
        });

        // ===== NotificationSetting =====
        modelBuilder.Entity<NotificationSetting>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.EventKey, n.RecipientRole }).IsUnique();
            e.Property(n => n.EventKey).HasMaxLength(80);
            e.Property(n => n.RecipientRole).HasMaxLength(50);
            e.HasQueryFilter(n => !n.IsDeleted);
        });

        // ===== NotificationPreference =====
        modelBuilder.Entity<NotificationPreference>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.UserId, p.EventKey }).IsUnique();
            e.Property(p => p.EventKey).HasMaxLength(80);
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== Department =====
        modelBuilder.Entity<Department>(e =>
        {
            e.HasIndex(d => new { d.CompanyId, d.Code }).IsUnique();
            e.Property(d => d.Code).HasMaxLength(50);
            e.Property(d => d.Name).HasMaxLength(200);
            e.HasOne(d => d.ParentDepartment).WithMany().HasForeignKey(d => d.ParentDepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Branch).WithMany().HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(d => d.Dimension).WithMany().HasForeignKey(d => d.DimensionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(d => d.DefaultExpenseAccount).WithMany().HasForeignKey(d => d.DefaultExpenseAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ===== Position =====
        modelBuilder.Entity<Position>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();
            e.Property(p => p.Code).HasMaxLength(50);
            e.Property(p => p.Title).HasMaxLength(200);
            e.Property(p => p.MinSalary).HasPrecision(18, 2);
            e.Property(p => p.MaxSalary).HasPrecision(18, 2);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== Employee org-structure FKs (additive — string fields retained) =====
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasOne(emp => emp.DepartmentRef).WithMany().HasForeignKey(emp => emp.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(emp => emp.PositionRef).WithMany().HasForeignKey(emp => emp.PositionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(emp => emp.DirectManager).WithMany().HasForeignKey(emp => emp.DirectManagerId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== SalaryAdvance =====
        modelBuilder.Entity<SalaryAdvance>(e =>
        {
            e.HasIndex(sa => new { sa.CompanyId, sa.AdvanceNumber }).IsUnique();
            e.Property(sa => sa.AdvanceNumber).HasMaxLength(50);
            e.Property(sa => sa.Amount).HasPrecision(18, 2);
            e.Property(sa => sa.MonthlyDeduction).HasPrecision(18, 2);
            e.Property(sa => sa.ClearedAmount).HasPrecision(18, 2);
            e.Property(sa => sa.OutstandingAmount).HasPrecision(18, 2);
            e.HasOne(sa => sa.Employee).WithMany().HasForeignKey(sa => sa.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(sa => sa.ApprovedByUser).WithMany().HasForeignKey(sa => sa.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(sa => sa.DisbursementDocument).WithMany().HasForeignKey(sa => sa.DisbursementDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(sa => !sa.IsDeleted);
        });

        // ===== ExpenseClaimLine =====
        modelBuilder.Entity<ExpenseClaimLine>(e =>
        {
            e.HasOne(l => l.ExpenseClaim).WithMany(ec => ec.Lines).HasForeignKey(l => l.ExpenseClaimId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 2);
            e.Property(l => l.VatAmount).HasPrecision(18, 2);
            e.Property(l => l.WithholdingTaxRate).HasPrecision(5, 2);
            e.Property(l => l.WithholdingTaxAmount).HasPrecision(18, 2);
            e.Property(l => l.NetAmount).HasPrecision(18, 2);
            e.Property(l => l.Description).HasMaxLength(1000);
            e.Property(l => l.Category).HasMaxLength(200);
        });

        // ===== WithholdingTaxCert =====
        modelBuilder.Entity<WithholdingTaxCert>(e =>
        {
            e.HasIndex(w => new { w.CompanyId, w.CertificateNumber }).IsUnique();
            e.Property(w => w.CertificateNumber).HasMaxLength(50);
            e.Property(w => w.TotalIncomeAmount).HasPrecision(18, 2);
            e.Property(w => w.TotalTaxAmount).HasPrecision(18, 2);
            e.HasOne(w => w.PayeeContact).WithMany().HasForeignKey(w => w.PayeeContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(w => w.Document).WithMany().HasForeignKey(w => w.DocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(w => !w.IsDeleted);
        });

        // ===== WithholdingTaxCertLine =====
        modelBuilder.Entity<WithholdingTaxCertLine>(e =>
        {
            e.HasOne(l => l.WithholdingTaxCert).WithMany(w => w.Lines).HasForeignKey(l => l.WithholdingTaxCertId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.IncomeAmount).HasPrecision(18, 2);
            e.Property(l => l.TaxRate).HasPrecision(5, 2);
            e.Property(l => l.TaxAmount).HasPrecision(18, 2);
            e.Property(l => l.IncomeTypeCode).HasMaxLength(10);
            e.Property(l => l.IncomeDescription).HasMaxLength(500);
        });

        // ======================================================================
        // Advanced Operations & World-Class Features
        // ======================================================================

        // ===== Employee =====
        modelBuilder.Entity<Employee>(e =>
        {
            // Unique-per-active scope: a soft-deleted row keeps the slot
            // when modelled at the plain index level, so partners can't
            // recreate (or restore-after-create-collision) a code. The
            // filtered unique index lets soft-deleted EmployeeCode rows
            // coexist with a new active one. Restore must check + bail
            // when an active duplicate already exists.
            e.HasIndex(emp => new { emp.CompanyId, emp.EmployeeCode })
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false");
            e.Property(emp => emp.EmployeeCode).HasMaxLength(50);
            e.Property(emp => emp.TitleTh).HasMaxLength(20);
            e.Property(emp => emp.FirstNameTh).HasMaxLength(200);
            e.Property(emp => emp.LastNameTh).HasMaxLength(200);
            e.Property(emp => emp.FirstNameEn).HasMaxLength(200);
            e.Property(emp => emp.LastNameEn).HasMaxLength(200);
            e.Property(emp => emp.CitizenId).HasMaxLength(13);
            e.Property(emp => emp.TaxId).HasMaxLength(13);
            e.Property(emp => emp.BaseSalary).HasPrecision(18, 2);
            e.Property(emp => emp.ProvidentFundEmployeePercent).HasPrecision(5, 2);
            e.Property(emp => emp.ProvidentFundEmployerPercent).HasPrecision(5, 2);
            e.HasOne(emp => emp.User).WithMany().HasForeignKey(emp => emp.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(emp => !emp.IsDeleted);
        });

        // ===== PayrollRun =====
        modelBuilder.Entity<PayrollRun>(e =>
        {
            e.HasIndex(pr => new { pr.CompanyId, pr.PayrollNumber }).IsUnique();
            e.Property(pr => pr.PayrollNumber).HasMaxLength(50);
            e.Property(pr => pr.Name).HasMaxLength(256);
            e.Property(pr => pr.TotalGrossSalary).HasPrecision(18, 2);
            e.Property(pr => pr.TotalDeductions).HasPrecision(18, 2);
            e.Property(pr => pr.TotalNetPay).HasPrecision(18, 2);
            e.Property(pr => pr.TotalSocialSecurityEmployee).HasPrecision(18, 2);
            e.Property(pr => pr.TotalSocialSecurityEmployer).HasPrecision(18, 2);
            e.Property(pr => pr.TotalWithholdingTax).HasPrecision(18, 2);
            e.Property(pr => pr.TotalProvidentFundEmployee).HasPrecision(18, 2);
            e.Property(pr => pr.TotalProvidentFundEmployer).HasPrecision(18, 2);
            e.HasQueryFilter(pr => !pr.IsDeleted);
        });

        // ===== PayrollDetail =====
        modelBuilder.Entity<PayrollDetail>(e =>
        {
            e.HasOne(pd => pd.PayrollRun).WithMany(pr => pr.Details).HasForeignKey(pd => pd.PayrollRunId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(pd => pd.Employee).WithMany().HasForeignKey(pd => pd.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(pd => pd.BaseSalary).HasPrecision(18, 2);
            e.Property(pd => pd.OvertimePay).HasPrecision(18, 2);
            e.Property(pd => pd.Allowances).HasPrecision(18, 2);
            e.Property(pd => pd.Commission).HasPrecision(18, 2);
            e.Property(pd => pd.Bonus).HasPrecision(18, 2);
            e.Property(pd => pd.OtherIncome).HasPrecision(18, 2);
            e.Property(pd => pd.GrossIncome).HasPrecision(18, 2);
            e.Property(pd => pd.SocialSecurityEmployee).HasPrecision(18, 2);
            e.Property(pd => pd.SocialSecurityEmployer).HasPrecision(18, 2);
            e.Property(pd => pd.WithholdingTax).HasPrecision(18, 2);
            e.Property(pd => pd.ProvidentFundEmployee).HasPrecision(18, 2);
            e.Property(pd => pd.ProvidentFundEmployer).HasPrecision(18, 2);
            e.Property(pd => pd.LoanDeduction).HasPrecision(18, 2);
            e.Property(pd => pd.OtherDeductions).HasPrecision(18, 2);
            e.Property(pd => pd.TotalDeductions).HasPrecision(18, 2);
            e.Property(pd => pd.NetPay).HasPrecision(18, 2);
            e.Property(pd => pd.CumulativeIncomeYTD).HasPrecision(18, 2);
            e.Property(pd => pd.CumulativeTaxYTD).HasPrecision(18, 2);
            e.Property(pd => pd.EstimatedAnnualIncome).HasPrecision(18, 2);
            e.Property(pd => pd.EstimatedAnnualTax).HasPrecision(18, 2);
            e.Property(pd => pd.OvertimeHours).HasPrecision(8, 2);
        });

        // ===== EmployeeLeave =====
        modelBuilder.Entity<EmployeeLeave>(e =>
        {
            e.HasOne(el => el.Employee).WithMany(emp => emp.Leaves).HasForeignKey(el => el.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(el => el.TotalDays).HasPrecision(5, 1);
            e.HasQueryFilter(el => !el.IsDeleted);
        });

        // ===== PayrollItem =====
        modelBuilder.Entity<PayrollItem>(e =>
        {
            e.HasIndex(pi => new { pi.CompanyId, pi.Code }).IsUnique();
            e.Property(pi => pi.Code).HasMaxLength(50);
            e.Property(pi => pi.Name).HasMaxLength(256);
            e.Property(pi => pi.FixedAmount).HasPrecision(18, 2);
            e.Property(pi => pi.Percentage).HasPrecision(5, 2);
            e.HasQueryFilter(pi => !pi.IsDeleted);
        });

        // ===== AccountingDimension =====
        modelBuilder.Entity<AccountingDimension>(e =>
        {
            e.HasIndex(d => new { d.CompanyId, d.Code }).IsUnique();
            e.Property(d => d.Code).HasMaxLength(50);
            e.Property(d => d.Name).HasMaxLength(256);
            e.Property(d => d.AnnualBudget).HasPrecision(18, 2);
            e.HasOne(d => d.Parent).WithMany(d => d.Children).HasForeignKey(d => d.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ===== Branch =====
        modelBuilder.Entity<Branch>(e =>
        {
            e.HasIndex(b => new { b.CompanyId, b.Code }).IsUnique();
            e.Property(b => b.Code).HasMaxLength(50);
            e.Property(b => b.Name).HasMaxLength(256);
            e.Property(b => b.TaxBranchCode).HasMaxLength(10);
            e.HasQueryFilter(b => !b.IsDeleted);
        });

        // ===== JournalLineDimension =====
        modelBuilder.Entity<JournalLineDimension>(e =>
        {
            e.HasOne(jld => jld.JournalEntryLine).WithMany().HasForeignKey(jld => jld.JournalEntryLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(jld => jld.Dimension).WithMany().HasForeignKey(jld => jld.DimensionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(jld => jld.AllocatedAmount).HasPrecision(18, 2);
            e.Property(jld => jld.AllocatedPercent).HasPrecision(5, 2);
        });

        // ===== IntercompanyTransaction =====
        modelBuilder.Entity<IntercompanyTransaction>(e =>
        {
            e.HasIndex(ic => new { ic.CompanyId, ic.TransactionNumber }).IsUnique();
            e.Property(ic => ic.TransactionNumber).HasMaxLength(50);
            e.Property(ic => ic.Amount).HasPrecision(18, 2);
            e.Property(ic => ic.Currency).HasMaxLength(3);
            e.HasOne(ic => ic.TargetCompany).WithMany().HasForeignKey(ic => ic.TargetCompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(ic => !ic.IsDeleted);
        });

        // ===== IntercompanyTransactionLine =====
        modelBuilder.Entity<IntercompanyTransactionLine>(e =>
        {
            e.HasOne(l => l.IntercompanyTransaction).WithMany(ic => ic.Lines).HasForeignKey(l => l.IntercompanyTransactionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 2);
            e.Property(l => l.VatAmount).HasPrecision(18, 2);
        });

        // ===== ConsolidationGroup =====
        modelBuilder.Entity<ConsolidationGroup>(e =>
        {
            e.Property(cg => cg.Name).HasMaxLength(256);
            e.Property(cg => cg.Currency).HasMaxLength(3);
            e.HasOne(cg => cg.ParentCompany).WithMany().HasForeignKey(cg => cg.ParentCompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ConsolidationMember =====
        modelBuilder.Entity<ConsolidationMember>(e =>
        {
            e.HasIndex(cm => new { cm.ConsolidationGroupId, cm.CompanyId }).IsUnique();
            e.HasOne(cm => cm.Group).WithMany(cg => cg.Members).HasForeignKey(cm => cm.ConsolidationGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(cm => cm.Company).WithMany().HasForeignKey(cm => cm.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(cm => cm.OwnershipPercent).HasPrecision(5, 2);
        });

        // ===== ConsolidationReport =====
        modelBuilder.Entity<ConsolidationReport>(e =>
        {
            e.HasOne(cr => cr.Group).WithMany().HasForeignKey(cr => cr.ConsolidationGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== Project =====
        modelBuilder.Entity<Project>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();
            e.Property(p => p.Code).HasMaxLength(50);
            e.Property(p => p.Name).HasMaxLength(500);
            e.Property(p => p.BudgetAmount).HasPrecision(18, 2);
            e.Property(p => p.ContractAmount).HasPrecision(18, 2);
            e.Property(p => p.ActualCost).HasPrecision(18, 2);
            e.Property(p => p.ActualRevenue).HasPrecision(18, 2);
            e.Property(p => p.BilledAmount).HasPrecision(18, 2);
            e.Property(p => p.CompletionPercent).HasPrecision(5, 2);
            e.HasOne(p => p.Contact).WithMany().HasForeignKey(p => p.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== ProjectTask =====
        modelBuilder.Entity<ProjectTask>(e =>
        {
            e.HasOne(pt => pt.Project).WithMany(p => p.Tasks).HasForeignKey(pt => pt.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.Property(pt => pt.Name).HasMaxLength(500);
            e.Property(pt => pt.EstimatedHours).HasPrecision(8, 2);
            e.Property(pt => pt.ActualHours).HasPrecision(8, 2);
            e.Property(pt => pt.EstimatedCost).HasPrecision(18, 2);
            e.Property(pt => pt.ActualCost).HasPrecision(18, 2);
            e.Property(pt => pt.CompletionPercent).HasPrecision(5, 2);
        });

        // ===== ProjectCostEntry =====
        modelBuilder.Entity<ProjectCostEntry>(e =>
        {
            e.HasOne(pce => pce.Project).WithMany(p => p.CostEntries).HasForeignKey(pce => pce.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.Property(pce => pce.Quantity).HasPrecision(18, 4);
            e.Property(pce => pce.UnitCost).HasPrecision(18, 2);
            e.Property(pce => pce.Amount).HasPrecision(18, 2);
        });

        // ===== RevenueContract =====
        modelBuilder.Entity<RevenueContract>(e =>
        {
            e.HasIndex(rc => new { rc.CompanyId, rc.ContractNumber }).IsUnique();
            e.Property(rc => rc.ContractNumber).HasMaxLength(50);
            e.Property(rc => rc.Name).HasMaxLength(500);
            e.Property(rc => rc.TotalContractValue).HasPrecision(18, 2);
            e.HasOne(rc => rc.Contact).WithMany().HasForeignKey(rc => rc.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(rc => rc.Project).WithMany(p => p.RevenueContracts).HasForeignKey(rc => rc.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(rc => !rc.IsDeleted);
        });

        // ===== PerformanceObligation =====
        modelBuilder.Entity<PerformanceObligation>(e =>
        {
            e.HasOne(po => po.Contract).WithMany(rc => rc.Obligations).HasForeignKey(po => po.RevenueContractId).OnDelete(DeleteBehavior.Restrict);
            e.Property(po => po.Name).HasMaxLength(500);
            e.Property(po => po.StandaloneSellingPrice).HasPrecision(18, 2);
            e.Property(po => po.AllocatedPrice).HasPrecision(18, 2);
            e.Property(po => po.CompletionPercent).HasPrecision(5, 2);
            e.Property(po => po.RecognizedRevenue).HasPrecision(18, 2);
            e.Property(po => po.DeferredRevenue).HasPrecision(18, 2);
        });

        // ===== RevenueSchedule =====
        modelBuilder.Entity<RevenueSchedule>(e =>
        {
            e.HasOne(rs => rs.Contract).WithMany(rc => rc.Schedules).HasForeignKey(rs => rs.RevenueContractId).OnDelete(DeleteBehavior.Restrict);
            e.Property(rs => rs.Amount).HasPrecision(18, 2);
        });

        // ===== Warehouse =====
        modelBuilder.Entity<Warehouse>(e =>
        {
            e.HasIndex(w => new { w.CompanyId, w.Code }).IsUnique();
            e.Property(w => w.Code).HasMaxLength(50);
            e.Property(w => w.Name).HasMaxLength(256);
            e.HasQueryFilter(w => !w.IsDeleted);
        });

        // ===== WarehouseStock =====
        modelBuilder.Entity<WarehouseStock>(e =>
        {
            e.HasIndex(ws => new { ws.WarehouseId, ws.ProductId, ws.LotNumber }).IsUnique();
            e.HasOne(ws => ws.Warehouse).WithMany(w => w.Stocks).HasForeignKey(ws => ws.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(ws => ws.Product).WithMany().HasForeignKey(ws => ws.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ws => ws.Quantity).HasPrecision(18, 4);
            e.Property(ws => ws.ReservedQuantity).HasPrecision(18, 4);
            e.Property(ws => ws.AvailableQuantity).HasPrecision(18, 4);
        });

        // ===== StockTransfer =====
        modelBuilder.Entity<StockTransfer>(e =>
        {
            e.HasIndex(st => new { st.CompanyId, st.TransferNumber }).IsUnique();
            e.Property(st => st.TransferNumber).HasMaxLength(50);
            e.HasOne(st => st.FromWarehouse).WithMany().HasForeignKey(st => st.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(st => st.ToWarehouse).WithMany().HasForeignKey(st => st.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(st => !st.IsDeleted);
        });

        // ===== StockTransferLine =====
        modelBuilder.Entity<StockTransferLine>(e =>
        {
            e.HasOne(l => l.StockTransfer).WithMany(st => st.Lines).HasForeignKey(l => l.StockTransferId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.Property(l => l.ReceivedQuantity).HasPrecision(18, 4);
        });

        // ===== Loan =====
        modelBuilder.Entity<Loan>(e =>
        {
            e.HasIndex(l => new { l.CompanyId, l.LoanNumber }).IsUnique();
            e.Property(l => l.LoanNumber).HasMaxLength(50);
            e.Property(l => l.Name).HasMaxLength(500);
            e.Property(l => l.PrincipalAmount).HasPrecision(18, 2);
            e.Property(l => l.InterestRate).HasPrecision(8, 4);
            e.Property(l => l.MonthlyPayment).HasPrecision(18, 2);
            e.Property(l => l.OutstandingPrincipal).HasPrecision(18, 2);
            e.Property(l => l.TotalInterestPaid).HasPrecision(18, 2);
            e.Property(l => l.TotalPrincipalPaid).HasPrecision(18, 2);
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        // ===== LoanSchedule =====
        modelBuilder.Entity<LoanSchedule>(e =>
        {
            e.HasOne(ls => ls.Loan).WithMany(l => l.Schedules).HasForeignKey(ls => ls.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ls => ls.PaymentAmount).HasPrecision(18, 2);
            e.Property(ls => ls.PrincipalPortion).HasPrecision(18, 2);
            e.Property(ls => ls.InterestPortion).HasPrecision(18, 2);
            e.Property(ls => ls.RemainingBalance).HasPrecision(18, 2);
        });

        // ===== LoanPayment =====
        modelBuilder.Entity<LoanPayment>(e =>
        {
            e.HasOne(lp => lp.Loan).WithMany(l => l.Payments).HasForeignKey(lp => lp.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.Property(lp => lp.PrincipalPaid).HasPrecision(18, 2);
            e.Property(lp => lp.InterestPaid).HasPrecision(18, 2);
            e.Property(lp => lp.TotalPaid).HasPrecision(18, 2);
            e.Property(lp => lp.LateFee).HasPrecision(18, 2);
        });

        // ===== CommissionPlan =====
        modelBuilder.Entity<CommissionPlan>(e =>
        {
            e.Property(cp => cp.Name).HasMaxLength(256);
            e.Property(cp => cp.FlatRate).HasPrecision(5, 2);
            e.HasQueryFilter(cp => !cp.IsDeleted);
        });

        // ===== CommissionTier =====
        modelBuilder.Entity<CommissionTier>(e =>
        {
            e.HasOne(ct => ct.Plan).WithMany(cp => cp.Tiers).HasForeignKey(ct => ct.CommissionPlanId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ct => ct.FromAmount).HasPrecision(18, 2);
            e.Property(ct => ct.ToAmount).HasPrecision(18, 2);
            e.Property(ct => ct.Rate).HasPrecision(8, 4);
        });

        // ===== CommissionAssignment =====
        modelBuilder.Entity<CommissionAssignment>(e =>
        {
            e.HasOne(ca => ca.Plan).WithMany(cp => cp.Assignments).HasForeignKey(ca => ca.CommissionPlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(ca => ca.Employee).WithMany().HasForeignKey(ca => ca.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== CommissionCalculation =====
        modelBuilder.Entity<CommissionCalculation>(e =>
        {
            e.HasIndex(cc => new { cc.CompanyId, cc.Year, cc.Month, cc.EmployeeId });
            e.Property(cc => cc.BasisAmount).HasPrecision(18, 2);
            e.Property(cc => cc.CommissionAmount).HasPrecision(18, 2);
        });

        // ===== TaxCalendarEvent =====
        modelBuilder.Entity<TaxCalendarEvent>(e =>
        {
            e.HasIndex(tce => new { tce.CompanyId, tce.TaxFormCode, tce.Year, tce.Month });
            e.Property(tce => tce.TaxFormCode).HasMaxLength(20);
            e.Property(tce => tce.TaxFormName).HasMaxLength(200);
            e.Property(tce => tce.TaxAmount).HasPrecision(18, 2);
        });

        // ===== ContactCreditSetting =====
        modelBuilder.Entity<ContactCreditSetting>(e =>
        {
            e.HasIndex(ccs => new { ccs.CompanyId, ccs.ContactId }).IsUnique();
            e.HasOne(ccs => ccs.Contact).WithMany().HasForeignKey(ccs => ccs.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ccs => ccs.CreditLimit).HasPrecision(18, 2);
            e.Property(ccs => ccs.CurrentBalance).HasPrecision(18, 2);
            e.Property(ccs => ccs.AvailableCredit).HasPrecision(18, 2);
        });

        // ===== DunningLetter =====
        modelBuilder.Entity<DunningLetter>(e =>
        {
            e.HasIndex(dl => new { dl.CompanyId, dl.LetterNumber }).IsUnique();
            e.Property(dl => dl.LetterNumber).HasMaxLength(50);
            e.Property(dl => dl.TotalOverdueAmount).HasPrecision(18, 2);
            e.HasOne(dl => dl.Contact).WithMany().HasForeignKey(dl => dl.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(dl => !dl.IsDeleted);
        });

        // ===== DunningLetterLine =====
        modelBuilder.Entity<DunningLetterLine>(e =>
        {
            e.HasOne(l => l.DunningLetter).WithMany(dl => dl.Lines).HasForeignKey(l => l.DunningLetterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Document).WithMany().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.Property(l => l.PaidAmount).HasPrecision(18, 2);
            e.Property(l => l.OverdueAmount).HasPrecision(18, 2);
        });

        // ===== PaymentReminder =====
        modelBuilder.Entity<PaymentReminder>(e =>
        {
            e.HasOne(pr => pr.Document).WithMany().HasForeignKey(pr => pr.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(pr => !pr.IsDeleted);
        });

        // ===== AutoCategorizationRule =====
        modelBuilder.Entity<AutoCategorizationRule>(e =>
        {
            e.Property(r => r.RuleName).HasMaxLength(256);
            e.Property(r => r.MatchPattern).HasMaxLength(500);
            e.Property(r => r.MinAmount).HasPrecision(18, 2);
            e.Property(r => r.MaxAmount).HasPrecision(18, 2);
            e.Property(r => r.ConfidenceThreshold).HasPrecision(5, 2);
            e.HasQueryFilter(r => !r.IsDeleted);
        });

        // ===== CategorizationResult =====
        modelBuilder.Entity<CategorizationResult>(e =>
        {
            e.HasIndex(cr => new { cr.EntityType, cr.EntityId });
            e.Property(cr => cr.Confidence).HasPrecision(5, 2);
        });

        // ===== AnomalyDetection =====
        modelBuilder.Entity<AnomalyDetection>(e =>
        {
            e.HasIndex(ad => new { ad.CompanyId, ad.DetectedAt });
            e.HasIndex(ad => new { ad.EntityType, ad.EntityId });
            e.Property(ad => ad.ExpectedValue).HasPrecision(18, 2);
            e.Property(ad => ad.ActualValue).HasPrecision(18, 2);
            e.Property(ad => ad.DeviationPercent).HasPrecision(8, 2);
        });

        // ===== CashFlowForecast =====
        modelBuilder.Entity<CashFlowForecast>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(256);
            e.Property(f => f.OpeningBalance).HasPrecision(18, 2);
            e.Property(f => f.ProjectedInflows).HasPrecision(18, 2);
            e.Property(f => f.ProjectedOutflows).HasPrecision(18, 2);
            e.Property(f => f.ProjectedClosingBalance).HasPrecision(18, 2);
            e.Property(f => f.ActualClosingBalance).HasPrecision(18, 2);
            e.Property(f => f.AccuracyPercent).HasPrecision(5, 2);
            e.HasQueryFilter(f => !f.IsDeleted);
        });

        // ===== CashFlowForecastLine =====
        modelBuilder.Entity<CashFlowForecastLine>(e =>
        {
            e.HasOne(l => l.Forecast).WithMany(f => f.Lines).HasForeignKey(l => l.CashFlowForecastId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.ProjectedAmount).HasPrecision(18, 2);
            e.Property(l => l.ActualAmount).HasPrecision(18, 2);
            e.Property(l => l.Confidence).HasPrecision(5, 2);
        });

        // ===== OcrScanResult =====
        modelBuilder.Entity<OcrScanResult>(e =>
        {
            e.Property(o => o.OriginalFileName).HasMaxLength(500);
            e.Property(o => o.Confidence).HasPrecision(5, 2);
            e.Property(o => o.ExtractedVendorTaxId).HasMaxLength(13);
            e.Property(o => o.ExtractedSubTotal).HasPrecision(18, 2);
            e.Property(o => o.ExtractedVatAmount).HasPrecision(18, 2);
            e.Property(o => o.ExtractedTotalAmount).HasPrecision(18, 2);
            e.Property(o => o.FileHash).HasMaxLength(64);
            e.Property(o => o.WhtRate).HasPrecision(5, 2);
            e.HasIndex(o => new { o.CompanyId, o.FileHash });
            e.HasIndex(o => new { o.CompanyId, o.ExtractedDocumentNumber, o.ExtractedTotalAmount });
        });

        // ===== OcrLearnedPattern =====
        modelBuilder.Entity<OcrLearnedPattern>(e =>
        {
            e.Property(p => p.FieldName).HasMaxLength(50);
            e.Property(p => p.ContextKeyword).HasMaxLength(200);
            e.Property(p => p.VendorTaxId).HasMaxLength(13);
            e.HasIndex(p => new { p.CompanyId, p.VendorTaxId, p.FieldName });
        });

        // ===== VendorKnownGoodValue =====
        modelBuilder.Entity<VendorKnownGoodValue>(e =>
        {
            e.Property(p => p.FieldName).HasMaxLength(50);
            e.Property(p => p.VendorTaxId).HasMaxLength(13);
            e.Property(p => p.Source).HasMaxLength(20);
            e.HasIndex(p => new { p.CompanyId, p.VendorTaxId, p.FieldName });
        });

        // ===== OcrCreditPurchase =====
        modelBuilder.Entity<OcrCreditPurchase>(e =>
        {
            e.Property(p => p.Status).HasMaxLength(20);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.HasIndex(p => new { p.CompanyId, p.SubscriptionId });
        });

        // ===== OcrCategoryMapping =====
        modelBuilder.Entity<OcrCategoryMapping>(e =>
        {
            e.Property(p => p.VendorKey).HasMaxLength(200);
            e.Property(p => p.DescriptionKeyword).HasMaxLength(200);
            e.Property(p => p.AccountCode).HasMaxLength(20);
            // Composite lookup index: scan-time queries filter by (CompanyId, VendorKey)
            e.HasIndex(p => new { p.CompanyId, p.VendorKey, p.DescriptionKeyword });
        });

        // ===== OcrVendorIntelligence =====
        modelBuilder.Entity<OcrVendorIntelligence>(e =>
        {
            e.Property(p => p.VendorKey).HasMaxLength(200);
            e.Property(p => p.VendorName).HasMaxLength(300);
            e.Property(p => p.VendorTaxId).HasMaxLength(20);
            e.Property(p => p.MostCommonDocumentType).HasMaxLength(50);
            e.Property(p => p.MostCommonDebitAccountCode).HasMaxLength(20);
            // One row per vendor — must be unique so training upsert is safe
            e.HasIndex(p => new { p.CompanyId, p.VendorKey }).IsUnique();
            // Soft delete — consistent with every other entity
            e.HasQueryFilter(v => !v.IsDeleted);
        });

        // ===== SystemOcrCategoryMapping (system-wide, no CompanyId) =====
        modelBuilder.Entity<SystemOcrCategoryMapping>(e =>
        {
            e.Property(p => p.VendorKey).HasMaxLength(200);
            e.Property(p => p.DescriptionKeyword).HasMaxLength(200);
            e.Property(p => p.AccountCode).HasMaxLength(20);
            e.HasIndex(p => new { p.VendorKey, p.DescriptionKeyword });
            e.HasQueryFilter(m => !m.IsDeleted);
        });

        // ===== SystemOcrVendorIntelligence (system-wide, no CompanyId) =====
        modelBuilder.Entity<SystemOcrVendorIntelligence>(e =>
        {
            e.Property(p => p.VendorKey).HasMaxLength(200);
            e.Property(p => p.VendorName).HasMaxLength(300);
            e.Property(p => p.VendorTaxId).HasMaxLength(20);
            e.Property(p => p.MostCommonDocumentType).HasMaxLength(50);
            e.Property(p => p.MostCommonDebitAccountCode).HasMaxLength(20);
            // One row per vendor across the entire system
            e.HasIndex(p => p.VendorKey).IsUnique();
            e.HasQueryFilter(v => !v.IsDeleted);
        });

        // ===== SystemOcrAssociationRule (basket-analysis output) =====
        modelBuilder.Entity<SystemOcrAssociationRule>(e =>
        {
            e.Property(p => p.Consequent).HasMaxLength(100);
            e.Property(p => p.Support).HasPrecision(8, 6);
            e.Property(p => p.Confidence).HasPrecision(8, 6);
            e.Property(p => p.Lift).HasPrecision(10, 4);
            // Index by consequent so "show me all rules → 5402" is fast
            e.HasIndex(p => p.Consequent);
            e.HasQueryFilter(r => !r.IsDeleted);
        });

        // ===== TradingPartnership =====
        modelBuilder.Entity<TradingPartnership>(e =>
        {
            e.HasOne(p => p.CompanyA).WithMany().HasForeignKey(p => p.CompanyAId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.CompanyB).WithMany().HasForeignKey(p => p.CompanyBId).OnDelete(DeleteBehavior.Restrict);
            // Canonical pair — unique combination since we always order
            // (CompanyAId, CompanyBId) so the smaller GUID comes first.
            e.HasIndex(p => new { p.CompanyAId, p.CompanyBId }).IsUnique();
            e.Property(p => p.AutoApproveAmountLimit).HasPrecision(18, 2);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== CrossTenantDocumentLink =====
        modelBuilder.Entity<CrossTenantDocumentLink>(e =>
        {
            e.HasOne(l => l.TradingPartnership).WithMany().HasForeignKey(l => l.TradingPartnershipId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.SourceDocument).WithMany().HasForeignKey(l => l.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.TargetDocument).WithMany().HasForeignKey(l => l.TargetDocumentId).OnDelete(DeleteBehavior.SetNull);
            // Inbox query "show me incoming links for company X" by Target+Status
            e.HasIndex(l => new { l.TargetCompanyId, l.Status });
            // Outbox query "show me what we sent" by Source+Status
            e.HasIndex(l => new { l.SourceCompanyId, l.Status });
            e.HasIndex(l => l.SourceDocumentId);
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        // ===== WorkflowAutomationConfig =====
        modelBuilder.Entity<WorkflowAutomationConfig>(e =>
        {
            e.HasOne(c => c.Company).WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.DefaultApproverUser).WithMany().HasForeignKey(c => c.DefaultApproverUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(c => c.DefaultSignature).WithMany().HasForeignKey(c => c.DefaultSignatureId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(c => c.CompanyId).IsUnique();
            e.Property(c => c.AutoApproveMinAmount).HasPrecision(18, 2);
            e.Property(c => c.AutoApproveMaxAmount).HasPrecision(18, 2);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== CustomReport =====
        modelBuilder.Entity<CustomReport>(e =>
        {
            e.Property(cr => cr.Name).HasMaxLength(256);
            e.HasQueryFilter(cr => !cr.IsDeleted);
        });

        // ===== PortalAccess =====
        modelBuilder.Entity<PortalAccess>(e =>
        {
            e.HasIndex(pa => new { pa.CompanyId, pa.ContactId, pa.Email }).IsUnique();
            e.Property(pa => pa.Email).HasMaxLength(256);
            e.HasOne(pa => pa.Contact).WithMany().HasForeignKey(pa => pa.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(pa => !pa.IsDeleted);
        });

        // ===== PortalActivity =====
        modelBuilder.Entity<PortalActivity>(e =>
        {
            e.HasIndex(a => new { a.PortalAccessId, a.ActivityAt });
            e.HasOne(a => a.PortalAccess).WithMany().HasForeignKey(a => a.PortalAccessId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FinancialScenario =====
        modelBuilder.Entity<FinancialScenario>(e =>
        {
            e.Property(fs => fs.Name).HasMaxLength(256);
            e.HasQueryFilter(fs => !fs.IsDeleted);
        });

        // ===== ScenarioAssumption =====
        modelBuilder.Entity<ScenarioAssumption>(e =>
        {
            e.HasOne(sa => sa.Scenario).WithMany(fs => fs.Assumptions).HasForeignKey(sa => sa.FinancialScenarioId).OnDelete(DeleteBehavior.Restrict);
            e.Property(sa => sa.AdjustmentValue).HasPrecision(18, 4);
        });

        // ===== ScenarioResult =====
        modelBuilder.Entity<ScenarioResult>(e =>
        {
            e.HasOne(sr => sr.Scenario).WithMany(fs => fs.Results).HasForeignKey(sr => sr.FinancialScenarioId).OnDelete(DeleteBehavior.Restrict);
            e.Property(sr => sr.BaselineAmount).HasPrecision(18, 2);
            e.Property(sr => sr.ScenarioAmount).HasPrecision(18, 2);
            e.Property(sr => sr.Variance).HasPrecision(18, 2);
            e.Property(sr => sr.VariancePercent).HasPrecision(8, 2);
        });

        // ===== FinancialKpi =====
        modelBuilder.Entity<FinancialKpi>(e =>
        {
            e.HasIndex(k => new { k.CompanyId, k.Code }).IsUnique();
            e.Property(k => k.Code).HasMaxLength(50);
            e.Property(k => k.Name).HasMaxLength(256);
            e.Property(k => k.TargetValue).HasPrecision(18, 4);
            e.Property(k => k.WarningThreshold).HasPrecision(18, 4);
            e.Property(k => k.CriticalThreshold).HasPrecision(18, 4);
        });

        // ===== KpiSnapshot =====
        modelBuilder.Entity<KpiSnapshot>(e =>
        {
            e.HasIndex(ks => new { ks.FinancialKpiId, ks.Year, ks.Month }).IsUnique();
            e.HasOne(ks => ks.Kpi).WithMany().HasForeignKey(ks => ks.FinancialKpiId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ks => ks.Value).HasPrecision(18, 4);
        });

        // ===== BankConnection =====
        modelBuilder.Entity<BankConnection>(e =>
        {
            e.Property(bc => bc.BankCode).HasMaxLength(20);
            e.Property(bc => bc.BankName).HasMaxLength(100);
            e.HasQueryFilter(bc => !bc.IsDeleted);
        });

        // ===== BankFeedImport =====
        modelBuilder.Entity<BankFeedImport>(e =>
        {
            e.HasOne(bfi => bfi.Connection).WithMany().HasForeignKey(bfi => bfi.BankConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== ComplianceFiling =====
        modelBuilder.Entity<ComplianceFiling>(e =>
        {
            e.HasIndex(cf => new { cf.CompanyId, cf.FilingType, cf.Year, cf.Month });
            e.Property(cf => cf.FormCode).HasMaxLength(50);
            e.Property(cf => cf.TaxAmount).HasPrecision(18, 2);
            e.Property(cf => cf.PenaltyAmount).HasPrecision(18, 2);
            e.HasQueryFilter(cf => !cf.IsDeleted);
        });

        // ===== TimeEntry =====
        modelBuilder.Entity<TimeEntry>(e =>
        {
            e.HasIndex(te => new { te.CompanyId, te.EntryDate, te.EmployeeId });
            e.Property(te => te.Hours).HasPrecision(8, 2);
            e.Property(te => te.BillingRate).HasPrecision(18, 2);
            e.Property(te => te.BillableAmount).HasPrecision(18, 2);
            e.HasOne(te => te.Employee).WithMany().HasForeignKey(te => te.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(te => te.Project).WithMany().HasForeignKey(te => te.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== BillingRate =====
        modelBuilder.Entity<BillingRate>(e =>
        {
            e.Property(br => br.Name).HasMaxLength(256);
            e.Property(br => br.HourlyRate).HasPrecision(18, 2);
            e.Property(br => br.DailyRate).HasPrecision(18, 2);
            e.Property(br => br.Currency).HasMaxLength(3);
        });

        // ===== WebhookRegistration =====
        modelBuilder.Entity<WebhookRegistration>(e =>
        {
            e.Property(wr => wr.Name).HasMaxLength(256);
            e.Property(wr => wr.Url).HasMaxLength(2000);
            e.HasQueryFilter(wr => !wr.IsDeleted);
        });

        // ===== WebhookDelivery =====
        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.HasIndex(wd => new { wd.WebhookRegistrationId, wd.DeliveredAt });
            e.HasOne(wd => wd.Registration).WithMany().HasForeignKey(wd => wd.WebhookRegistrationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(wd => wd.DurationMs).HasPrecision(10, 2);
        });

        // ===== UserDevice =====
        modelBuilder.Entity<UserDevice>(e =>
        {
            e.HasIndex(ud => new { ud.UserId, ud.DeviceToken }).IsUnique();
            e.HasOne(ud => ud.User).WithMany().HasForeignKey(ud => ud.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(ud => ud.DeviceToken).HasMaxLength(500);
            e.Property(ud => ud.Platform).HasMaxLength(20);
        });

        // ===== SyncQueue =====
        modelBuilder.Entity<SyncQueue>(e =>
        {
            e.HasIndex(sq => new { sq.CompanyId, sq.IsProcessed, sq.QueuedAt });
            e.Property(sq => sq.EntityType).HasMaxLength(100);
            e.Property(sq => sq.OperationType).HasMaxLength(20);
        });

        // ===== SmartImportSession =====
        modelBuilder.Entity<SmartImportSession>(e =>
        {
            e.HasIndex(s => new { s.CompanyId, s.Status });
            e.Property(s => s.EntityType).HasMaxLength(50);
            e.Property(s => s.FileName).HasMaxLength(255);
            e.Property(s => s.FileFormat).HasMaxLength(20);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        // ===== SmartImportColumnMapping =====
        modelBuilder.Entity<SmartImportColumnMapping>(e =>
        {
            e.HasIndex(m => new { m.SessionId, m.SourceIndex }).IsUnique();
            e.Property(m => m.SourceHeader).HasMaxLength(255);
            e.Property(m => m.TargetField).HasMaxLength(100);
            e.HasOne(m => m.Session)
                .WithMany(s => s.ColumnMappings)
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(m => !m.IsDeleted);
        });

        // ===== Global Query Filters for Child Entities =====
        // Add matching soft-delete filters to child entities whose parent has a filter,
        // preventing EF Core warning 10622 about mismatched query filters.

        // Subscription children
        modelBuilder.Entity<TrialConfig>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<SubscriptionHistory>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<SubscriptionPayment>().HasQueryFilter(e => !e.IsDeleted);

        // Accounting detail entities
        modelBuilder.Entity<JournalEntryLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DocumentLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<TaxReportLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<BudgetLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ExpenseClaimLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<WithholdingTaxCertLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<JournalLineDimension>().HasQueryFilter(e => !e.IsDeleted);

        // Bank & Currency
        modelBuilder.Entity<BankTransaction>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CurrencyRate>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CompanyCurrency>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<BankFeedImport>().HasQueryFilter(e => !e.IsDeleted);

        // Fixed Assets
        modelBuilder.Entity<AssetDepreciation>().HasQueryFilter(e => !e.IsDeleted);

        // Approval Workflow
        modelBuilder.Entity<ApprovalStep>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ApprovalRequest>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ApprovalAction>().HasQueryFilter(e => !e.IsDeleted);

        // File, Notification, NumberSeries
        modelBuilder.Entity<FileAttachment>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Notification>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<NumberSeries>().HasQueryFilter(e => !e.IsDeleted);

        // Company Settings & API
        modelBuilder.Entity<CompanySettings>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(e => !e.IsDeleted);

        // Project & Revenue Recognition
        modelBuilder.Entity<ProjectTask>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ProjectCostEntry>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<PerformanceObligation>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<RevenueSchedule>().HasQueryFilter(e => !e.IsDeleted);

        // Warehouse
        modelBuilder.Entity<WarehouseStock>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<StockTransferLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<StockMovement>().HasQueryFilter(e => !e.IsDeleted);

        // Loan
        modelBuilder.Entity<LoanSchedule>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<LoanPayment>().HasQueryFilter(e => !e.IsDeleted);

        // Commission
        modelBuilder.Entity<CommissionTier>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CommissionAssignment>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CommissionCalculation>().HasQueryFilter(e => !e.IsDeleted);

        // AI & Intelligence
        modelBuilder.Entity<AnomalyDetection>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CategorizationResult>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CashFlowForecastLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<OcrScanResult>().HasQueryFilter(e => !e.IsDeleted);

        // Intercompany & Consolidation
        modelBuilder.Entity<IntercompanyTransactionLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ConsolidationGroup>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ConsolidationMember>().HasQueryFilter(e => !e.IsDeleted);

        // Payroll
        modelBuilder.Entity<PayrollDetail>().HasQueryFilter(e => !e.IsDeleted);

        // Advanced AR/AP
        modelBuilder.Entity<ContactCreditSetting>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DunningLetterLine>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<TaxCalendarEvent>().HasQueryFilter(e => !e.IsDeleted);

        // FP&A
        modelBuilder.Entity<ScenarioAssumption>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ScenarioResult>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<FinancialKpi>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<KpiSnapshot>().HasQueryFilter(e => !e.IsDeleted);

        // Portal & Custom Reports
        modelBuilder.Entity<PortalActivity>().HasQueryFilter(e => !e.IsDeleted);

        // Time & Billing
        modelBuilder.Entity<TimeEntry>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<BillingRate>().HasQueryFilter(e => !e.IsDeleted);

        // Webhooks & Mobile
        modelBuilder.Entity<WebhookDelivery>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<UserDevice>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<SyncQueue>().HasQueryFilter(e => !e.IsDeleted);

        // Financial Management
        modelBuilder.Entity<PrepaidExpense>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<PrepaidAmortizationSchedule>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DepositTransaction>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DepositRefund>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<BadDebtAllowance>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<BadDebtAllowanceLine>(e =>
        {
            e.HasQueryFilter(x => !x.IsDeleted);
            e.Property(x => x.OutstandingAmount).HasPrecision(18, 2);
            e.Property(x => x.AllowancePercentage).HasPrecision(5, 2);
            e.Property(x => x.AllowanceAmount).HasPrecision(18, 2);
            e.HasOne(x => x.BadDebtAllowance).WithMany(p => p.Lines)
                .HasForeignKey(x => x.BadDebtAllowanceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AccruedExpense>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<InventoryObsolescenceAllowance>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<InventoryObsolescenceLine>(e =>
        {
            e.HasQueryFilter(x => !x.IsDeleted);
            e.Property(x => x.CurrentStock).HasPrecision(18, 4);
            e.Property(x => x.StockValue).HasPrecision(18, 2);
            e.Property(x => x.AllowancePercentage).HasPrecision(5, 2);
            e.Property(x => x.AllowanceAmount).HasPrecision(18, 2);
            e.HasOne(x => x.Allowance).WithMany(p => p.Lines)
                .HasForeignKey(x => x.AllowanceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CorporateIncomeTax>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ProfitAppropriation>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CapitalTransaction>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<ShortTermInvestment>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<SuppliesUsageLog>().HasQueryFilter(e => !e.IsDeleted);

        // Signature & Approval
        modelBuilder.Entity<UserSignature>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DocumentApproval>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<DocumentSignature>().HasQueryFilter(e => !e.IsDeleted);

        // ===== POS Terminal =====
        modelBuilder.Entity<PosTerminal>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Location).HasMaxLength(500);
            e.HasQueryFilter(t => !t.IsDeleted);
        });

        // ===== POS Session =====
        modelBuilder.Entity<PosSession>(e =>
        {
            e.HasOne(s => s.Terminal).WithMany(t => t.Sessions).HasForeignKey(s => s.TerminalId).OnDelete(DeleteBehavior.Restrict);
            e.Property(s => s.OpeningBalance).HasPrecision(18, 2);
            e.Property(s => s.ClosingBalance).HasPrecision(18, 2);
            e.Property(s => s.ExpectedBalance).HasPrecision(18, 2);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        // ===== POS Order =====
        modelBuilder.Entity<PosOrder>(e =>
        {
            e.HasIndex(o => new { o.CompanyId, o.OrderNumber }).IsUnique();
            e.Property(o => o.OrderNumber).HasMaxLength(50);
            e.Property(o => o.SubTotal).HasPrecision(18, 2);
            e.Property(o => o.DiscountAmount).HasPrecision(18, 2);
            e.Property(o => o.DiscountPercent).HasPrecision(5, 2);
            e.Property(o => o.ServiceChargePercent).HasPrecision(5, 2);
            e.Property(o => o.ServiceChargeAmount).HasPrecision(18, 2);
            e.Property(o => o.VatAmount).HasPrecision(18, 2);
            e.Property(o => o.TotalAmount).HasPrecision(18, 2);
            e.Property(o => o.RoundingAmount).HasPrecision(18, 2);
            e.Property(o => o.NetAmount).HasPrecision(18, 2);
            e.HasOne(o => o.Session).WithMany(s => s.Orders).HasForeignKey(o => o.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.JournalEntry).WithMany().HasForeignKey(o => o.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(o => !o.IsDeleted);
        });

        // ===== POS Order Item =====
        modelBuilder.Entity<PosOrderItem>(e =>
        {
            e.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.ServicePackage).WithMany().HasForeignKey(i => i.ServicePackageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(i => i.Quantity).HasPrecision(18, 4);
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
            e.Property(i => i.DiscountAmount).HasPrecision(18, 2);
            e.Property(i => i.DiscountPercent).HasPrecision(5, 2);
            e.Property(i => i.SubTotal).HasPrecision(18, 2);
            e.Property(i => i.VatAmount).HasPrecision(18, 2);
            e.Property(i => i.TotalAmount).HasPrecision(18, 2);
        });

        // ===== POS Order Item Modifier =====
        modelBuilder.Entity<PosOrderItemModifier>(e =>
        {
            e.HasOne(m => m.OrderItem).WithMany(i => i.Modifiers).HasForeignKey(m => m.OrderItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.ModifierOption).WithMany().HasForeignKey(m => m.ModifierOptionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(m => m.PriceAdjustment).HasPrecision(18, 2);
            e.Property(m => m.ModifierGroupName).HasMaxLength(200);
            e.Property(m => m.ModifierName).HasMaxLength(200);
        });

        // ===== POS Payment =====
        modelBuilder.Entity<PosPayment>(e =>
        {
            e.HasOne(p => p.Order).WithMany(o => o.Payments).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.ReceivedAmount).HasPrecision(18, 2);
            e.Property(p => p.ChangeAmount).HasPrecision(18, 2);
            e.Property(p => p.ReferenceNo).HasMaxLength(200);
            e.Property(p => p.CardLastFour).HasMaxLength(4);
        });

        // ===== Service Package =====
        modelBuilder.Entity<ServicePackage>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(500);
            e.Property(p => p.Sku).HasMaxLength(50);
            e.Property(p => p.Category).HasMaxLength(200);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.CostPrice).HasPrecision(18, 2);
            e.HasOne(p => p.RevenueAccount).WithMany().HasForeignKey(p => p.RevenueAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== Service Component =====
        modelBuilder.Entity<ServiceComponent>(e =>
        {
            e.HasOne(c => c.Package).WithMany(p => p.Components).HasForeignKey(c => c.PackageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(c => c.Name).HasMaxLength(500);
            e.Property(c => c.CommissionValue).HasPrecision(18, 2);
        });

        // ===== POS Service Activity =====
        modelBuilder.Entity<PosServiceActivity>(e =>
        {
            e.HasOne(a => a.OrderItem).WithMany(i => i.ServiceActivities).HasForeignKey(a => a.OrderItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Component).WithMany().HasForeignKey(a => a.ComponentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(a => a.StaffName).HasMaxLength(200);
            e.Property(a => a.CommissionAmount).HasPrecision(18, 2);
        });

        // ===== Product Modifier Group =====
        modelBuilder.Entity<ProductModifierGroup>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(200);
            e.HasQueryFilter(g => !g.IsDeleted);
        });

        // ===== Product Modifier Group Link =====
        modelBuilder.Entity<ProductModifierGroupLink>(e =>
        {
            e.HasIndex(l => new { l.ProductId, l.ModifierGroupId }).IsUnique();
            e.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.ModifierGroup).WithMany(g => g.ProductLinks).HasForeignKey(l => l.ModifierGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== Product Modifier Option =====
        modelBuilder.Entity<ProductModifierOption>(e =>
        {
            e.HasOne(o => o.Group).WithMany(g => g.Options).HasForeignKey(o => o.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.Property(o => o.Name).HasMaxLength(200);
            e.Property(o => o.PriceAdjustment).HasPrecision(18, 2);
        });

        // ===== Staff Commission Summary =====
        modelBuilder.Entity<StaffCommissionSummary>(e =>
        {
            e.HasIndex(s => new { s.CompanyId, s.StaffId, s.PeriodStart }).IsUnique();
            e.Property(s => s.StaffName).HasMaxLength(200);
            e.Property(s => s.TotalCommission).HasPrecision(18, 2);
            e.Property(s => s.PaidAmount).HasPrecision(18, 2);
            e.Property(s => s.RemainingAmount).HasPrecision(18, 2);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        // =================================================================
        // PostgreSQL Performance Optimizations
        // =================================================================

        // --- JSONB columns (PostgreSQL native JSON with indexing support) ---
        modelBuilder.Entity<CompanySettings>(e =>
            e.Property(s => s.LandingServicesJson).HasColumnType("jsonb"));
        modelBuilder.Entity<Models.Entities.PosTerminal>(e =>
            e.Property(t => t.SettingsJson).HasColumnType("jsonb"));
        modelBuilder.Entity<Models.Entities.SmartImportSession>(e =>
        {
            e.Property(s => s.RawDataJson).HasColumnType("jsonb");
            e.Property(s => s.ErrorsJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<Models.Entities.SmartImportColumnMapping>(e =>
        {
            e.Property(m => m.SampleValuesJson).HasColumnType("jsonb");
            e.Property(m => m.SuggestionsJson).HasColumnType("jsonb");
        });

        // SiteSettings: JSONB for services
        modelBuilder.Entity<SiteSettings>(e =>
        {
            e.Property(s => s.ServicesJson).HasColumnType("jsonb");
        });

        // ===== AI Integration =====
        modelBuilder.Entity<AiProviderConfig>(e =>
        {
            // Partial unique index — only one row at a time may be active.
            // PostgreSQL filter expression enforces "at most one active
            // provider" without complicating the read path.
            e.HasIndex(p => p.IsActive)
                .HasFilter(@"""IsActive"" = true")
                .HasDatabaseName("IX_AiProviderConfigs_OneActive")
                .IsUnique();
            e.HasIndex(p => p.ProviderType).HasDatabaseName("IX_AiProviderConfigs_ProviderType");
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        modelBuilder.Entity<AiSuggestionFeedback>(e =>
        {
            // Per-feature accuracy reporting + retrain selection.
            e.HasIndex(f => new { f.CompanyId, f.FeatureKey, f.CreatedAt })
                .HasDatabaseName("IX_AiSuggestionFeedbacks_Company_Feature_Date");
            // Lookup by prompt hash for cache-bypass debugging.
            e.HasIndex(f => f.PromptHash).HasDatabaseName("IX_AiSuggestionFeedbacks_PromptHash");
            // Find unreviewed rows (UserChosenAt NULL, older than 7d → discard).
            e.HasIndex(f => new { f.FeatureKey, f.UserChosenAt })
                .HasDatabaseName("IX_AiSuggestionFeedbacks_Feature_UserChosen");
            e.Property(f => f.PromptJson).HasColumnType("jsonb");
            e.Property(f => f.ResponseJson).HasColumnType("jsonb");
            e.HasQueryFilter(f => !f.IsDeleted);
        });

        modelBuilder.Entity<AiResponseCache>(e =>
        {
            // Hot lookup — every AI-augmented call site hashes the prompt
            // and queries this index before invoking the provider.
            e.HasIndex(c => new { c.PromptHash, c.CompanyId })
                .HasDatabaseName("IX_AiResponseCaches_Hash_Company")
                .IsUnique();
            e.HasIndex(c => c.ExpiresAt).HasDatabaseName("IX_AiResponseCaches_ExpiresAt");
            e.Property(c => c.ResponseJson).HasColumnType("jsonb");
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        modelBuilder.Entity<LocalModelHealth>(e =>
        {
            e.HasIndex(h => h.FeatureKey)
                .HasDatabaseName("IX_LocalModelHealths_FeatureKey")
                .IsUnique();
            e.HasQueryFilter(h => !h.IsDeleted);
        });

        modelBuilder.Entity<AiFeatureRoutingConfig>(e =>
        {
            e.HasIndex(c => c.FeatureKey)
                .HasDatabaseName("IX_AiFeatureRoutingConfigs_FeatureKey")
                .IsUnique();
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        modelBuilder.Entity<AiUsageDaily>(e =>
        {
            // Composite unique — one row per (day, provider, feature).
            e.HasIndex(u => new { u.UsageDate, u.ProviderType, u.FeatureKey })
                .HasDatabaseName("IX_AiUsageDailies_Day_Provider_Feature")
                .IsUnique();
            e.HasIndex(u => u.UsageDate).HasDatabaseName("IX_AiUsageDailies_UsageDate");
            e.HasQueryFilter(u => !u.IsDeleted);
        });

        // External Integration
        modelBuilder.Entity<ExternalIntegration>(e =>
        {
            e.HasIndex(i => new { i.CompanyId, i.IsActive }).HasDatabaseName("IX_ExternalIntegrations_CompanyId_IsActive");
            e.HasIndex(i => i.ApiKeyPrefix).HasDatabaseName("IX_ExternalIntegrations_ApiKeyPrefix");
            e.Property(i => i.MappingConfigJson).HasColumnType("jsonb");
            e.Property(i => i.SettingsJson).HasColumnType("jsonb");
            e.HasQueryFilter(i => !i.IsDeleted);
        });
        modelBuilder.Entity<IntegrationSyncLog>(e =>
        {
            e.HasIndex(l => new { l.CompanyId, l.IntegrationId, l.CreatedAt }).HasDatabaseName("IX_IntegrationSyncLogs_Company_Integration_Date");
            e.HasIndex(l => l.ExternalId).HasDatabaseName("IX_IntegrationSyncLogs_ExternalId");
            e.Property(l => l.RequestPayloadJson).HasColumnType("jsonb");
            e.Property(l => l.ResponseJson).HasColumnType("jsonb");
            e.HasOne(l => l.Integration).WithMany(i => i.SyncLogs).HasForeignKey(l => l.IntegrationId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(l => !l.IsDeleted);
        });
        modelBuilder.Entity<IntegrationAccountMapping>(e =>
        {
            e.HasIndex(m => new { m.IntegrationId, m.ExternalCategory }).HasDatabaseName("IX_IntegrationAccountMappings_Integration_Category");
            e.HasOne(m => m.Integration).WithMany(i => i.AccountMappings).HasForeignKey(m => m.IntegrationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.DebitAccount).WithMany().HasForeignKey(m => m.DebitAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.CreditAccount).WithMany().HasForeignKey(m => m.CreditAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(m => !m.IsDeleted);
        });
        modelBuilder.Entity<IntegrationUserMapping>(e =>
        {
            e.Property(m => m.ExternalUserKey).HasMaxLength(256);
            e.HasIndex(m => new { m.IntegrationId, m.ExternalUserKey })
                .HasDatabaseName("IX_IntegrationUserMappings_Integration_Key");
            e.HasOne(m => m.Integration).WithMany(i => i.UserMappings).HasForeignKey(m => m.IntegrationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(m => !m.IsDeleted);
        });

        // --- Covering indexes for hot query paths ---

        // JournalEntry: most queried table — filter by company + date range + status
        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.HasIndex(j => new { j.CompanyId, j.EntryDate })
                .HasDatabaseName("IX_JournalEntries_CompanyId_EntryDate");
            e.HasIndex(j => new { j.CompanyId, j.Status, j.EntryDate })
                .HasDatabaseName("IX_JournalEntries_CompanyId_Status_EntryDate");
        });

        // JournalEntryLine: heaviest table — joins + aggregations
        modelBuilder.Entity<JournalEntryLine>(e =>
        {
            e.HasIndex(l => l.AccountId)
                .HasDatabaseName("IX_JournalEntryLines_AccountId");
            e.HasIndex(l => new { l.JournalEntryId, l.AccountId })
                .HasDatabaseName("IX_JournalEntryLines_JournalEntryId_AccountId");
        });

        // Document: filter by company + type + status + date
        modelBuilder.Entity<Document>(e =>
        {
            e.HasIndex(d => new { d.CompanyId, d.DocumentType, d.Status })
                .HasDatabaseName("IX_Documents_CompanyId_DocType_Status");
            e.HasIndex(d => new { d.CompanyId, d.DocumentDate })
                .HasDatabaseName("IX_Documents_CompanyId_DocumentDate");
            e.HasIndex(d => d.ContactId)
                .HasDatabaseName("IX_Documents_ContactId");
            // Partial index: only overdue documents (for dashboard counts)
            e.HasIndex(d => new { d.CompanyId, d.DueDate })
                .HasFilter("\"Status\" = 7") // DocumentStatus.Overdue
                .HasDatabaseName("IX_Documents_Overdue");
        });

        // Contact: search by name, filter by company
        modelBuilder.Entity<Contact>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.IsCustomer })
                .HasDatabaseName("IX_Contacts_CompanyId_IsCustomer");
            e.HasIndex(c => new { c.CompanyId, c.IsSupplier })
                .HasDatabaseName("IX_Contacts_CompanyId_IsSupplier");
        });

        // BankTransaction: reconciliation + date range queries
        modelBuilder.Entity<BankTransaction>(e =>
        {
            e.HasIndex(t => new { t.BankAccountId, t.TransactionDate })
                .HasDatabaseName("IX_BankTransactions_BankAccId_TxDate");
            e.HasIndex(t => new { t.BankAccountId, t.ReconciliationStatus })
                .HasDatabaseName("IX_BankTransactions_BankAccId_ReconStatus");
            e.HasIndex(t => t.MatchedPaymentId)
                .HasDatabaseName("IX_BankTransactions_MatchedPaymentId");
        });

        // Payment: lookup by document
        modelBuilder.Entity<Payment>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.DocumentId })
                .HasDatabaseName("IX_Payments_CompanyId_DocumentId");
            e.HasIndex(p => new { p.CompanyId, p.PaymentDate })
                .HasDatabaseName("IX_Payments_CompanyId_PaymentDate");
        });

        // ChartOfAccount: frequently queried with parent lookups
        modelBuilder.Entity<ChartOfAccount>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.AccountType })
                .HasDatabaseName("IX_ChartOfAccounts_CompanyId_AccType");
            e.HasIndex(a => a.ParentAccountId)
                .HasDatabaseName("IX_ChartOfAccounts_ParentAccountId");
        });

        // AuditLog: timestamp-based queries, BRIN index ideal for append-only
        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.Timestamp })
                .HasDatabaseName("IX_AuditLogs_CompanyId_Timestamp");
        });

        // ErrorLog: recent errors lookup
        modelBuilder.Entity<ErrorLog>(e =>
        {
            e.HasIndex(e2 => e2.Timestamp)
                .HasDatabaseName("IX_ErrorLogs_Timestamp")
                .IsDescending();
        });

        // POS: session + order hot paths
        modelBuilder.Entity<Models.Entities.PosSession>(e =>
        {
            e.HasIndex(s => new { s.TerminalId, s.Status })
                .HasDatabaseName("IX_PosSessions_TerminalId_Status");
        });
        modelBuilder.Entity<Models.Entities.PosOrder>(e =>
        {
            e.HasIndex(o => new { o.SessionId, o.Status })
                .HasDatabaseName("IX_PosOrders_SessionId_Status");
            e.HasIndex(o => new { o.CompanyId, o.CreatedAt })
                .HasDatabaseName("IX_PosOrders_CompanyId_CreatedAt");
        });

        // Notification: user inbox query
        modelBuilder.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt })
                .HasDatabaseName("IX_Notifications_UserId_IsRead_CreatedAt");
        });

        // Product: search + category filter
        modelBuilder.Entity<Product>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.IsActive })
                .HasDatabaseName("IX_Products_CompanyId_IsActive");
        });

        // FiscalPeriod: period lookups
        modelBuilder.Entity<FiscalPeriod>(e =>
        {
            e.HasIndex(f => new { f.CompanyId, f.StartDate, f.EndDate })
                .HasDatabaseName("IX_FiscalPeriods_CompanyId_Dates");
        });

        // FixedAsset: depreciation queries
        modelBuilder.Entity<FixedAsset>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.Status })
                .HasDatabaseName("IX_FixedAssets_CompanyId_Status");
        });

        // WithholdingTaxCert: tax period queries
        modelBuilder.Entity<WithholdingTaxCert>(e =>
        {
            e.HasIndex(w => new { w.CompanyId, w.TaxMonth, w.TaxYear })
                .HasDatabaseName("IX_WhtCerts_CompanyId_TaxMonth_TaxYear");
        });

        // ExpenseClaim: status-based queries
        modelBuilder.Entity<ExpenseClaim>(e =>
        {
            e.HasIndex(ec => new { ec.CompanyId, ec.Status })
                .HasDatabaseName("IX_ExpenseClaims_CompanyId_Status");
        });

        // ==================== CMS & Multi-Site ====================

        // ===== Site =====
        modelBuilder.Entity<Site>(e =>
        {
            e.HasIndex(s => new { s.CompanyId, s.Subdomain }).IsUnique().HasDatabaseName("IX_Sites_CompanyId_Subdomain");
            e.HasIndex(s => s.CustomDomain).IsUnique().HasFilter("\"CustomDomain\" IS NOT NULL").HasDatabaseName("IX_Sites_CustomDomain");
            e.HasIndex(s => new { s.CompanyId, s.Slug }).IsUnique().HasDatabaseName("IX_Sites_CompanyId_Slug");
            e.Property(s => s.Name).HasMaxLength(256);
            e.Property(s => s.NameEn).HasMaxLength(256);
            e.Property(s => s.Slug).HasMaxLength(128);
            e.Property(s => s.Subdomain).HasMaxLength(63);
            e.Property(s => s.CustomDomain).HasMaxLength(256);
            e.Property(s => s.DefaultLanguage).HasMaxLength(10);
            e.Property(s => s.DefaultCurrency).HasMaxLength(3);
            e.Property(s => s.GoogleAnalyticsId).HasMaxLength(50);
            e.Property(s => s.GoogleTagManagerId).HasMaxLength(50);
            e.Property(s => s.MetaPixelId).HasMaxLength(50);
            e.HasOne(s => s.Branch).WithMany().HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.DefaultWarehouse).WithMany().HasForeignKey(s => s.DefaultWarehouseId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.Theme).WithMany(t => t.Sites).HasForeignKey(s => s.ThemeId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(s => !s.IsDeleted);
        });

        // ===== SiteDomain =====
        modelBuilder.Entity<SiteDomain>(e =>
        {
            e.HasIndex(d => d.Domain).IsUnique().HasDatabaseName("IX_SiteDomains_Domain");
            e.Property(d => d.Domain).HasMaxLength(256);
            e.Property(d => d.VerificationToken).HasMaxLength(256);
            e.HasOne(d => d.Site).WithMany(s => s.Domains).HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteTheme =====
        modelBuilder.Entity<SiteTheme>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(128);
            e.Property(t => t.PrimaryColor).HasMaxLength(9);
            e.Property(t => t.SecondaryColor).HasMaxLength(9);
            e.Property(t => t.AccentColor).HasMaxLength(9);
            e.Property(t => t.BackgroundColor).HasMaxLength(9);
            e.Property(t => t.SurfaceColor).HasMaxLength(9);
            e.Property(t => t.TextColor).HasMaxLength(9);
            e.Property(t => t.TextSecondaryColor).HasMaxLength(9);
            e.Property(t => t.SuccessColor).HasMaxLength(9);
            e.Property(t => t.WarningColor).HasMaxLength(9);
            e.Property(t => t.DangerColor).HasMaxLength(9);
            e.Property(t => t.HeadingFont).HasMaxLength(128);
            e.Property(t => t.BodyFont).HasMaxLength(128);
            e.Property(t => t.MonoFont).HasMaxLength(128);
        });

        // ===== SiteLocale =====
        modelBuilder.Entity<SiteLocale>(e =>
        {
            e.HasIndex(l => new { l.SiteId, l.LanguageCode }).IsUnique().HasDatabaseName("IX_SiteLocales_SiteId_Lang");
            e.Property(l => l.LanguageCode).HasMaxLength(10);
            e.Property(l => l.LanguageName).HasMaxLength(64);
            e.Property(l => l.CurrencyCode).HasMaxLength(3);
            e.Property(l => l.CurrencySymbol).HasMaxLength(10);
            e.Property(l => l.ExchangeRateToBase).HasPrecision(18, 6);
            e.HasOne(l => l.Site).WithMany(s => s.Locales).HasForeignKey(l => l.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteStaffAccess =====
        modelBuilder.Entity<SiteStaffAccess>(e =>
        {
            e.HasIndex(a => new { a.SiteId, a.UserId }).IsUnique().HasDatabaseName("IX_SiteStaffAccess_SiteUser");
            e.HasOne(a => a.Site).WithMany(s => s.StaffAccess).HasForeignKey(a => a.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteCookieConsent =====
        modelBuilder.Entity<SiteCookieConsent>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.Provider).HasMaxLength(128);
            e.HasOne(c => c.Site).WithMany().HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SitePage =====
        modelBuilder.Entity<SitePage>(e =>
        {
            e.HasIndex(p => new { p.SiteId, p.Slug }).IsUnique().HasDatabaseName("IX_SitePages_SiteId_Slug");
            e.Property(p => p.Title).HasMaxLength(512);
            e.Property(p => p.Slug).HasMaxLength(256);
            e.Property(p => p.TemplateLayout).HasMaxLength(64);
            e.HasOne(p => p.Site).WithMany(s => s.Pages).HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.ParentPage).WithMany(p => p.ChildPages).HasForeignKey(p => p.ParentPageId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(p => !p.IsDeleted);
        });

        // ===== SitePageTranslation =====
        modelBuilder.Entity<SitePageTranslation>(e =>
        {
            e.HasIndex(t => new { t.PageId, t.LanguageCode }).IsUnique().HasDatabaseName("IX_SitePageTrans_PageId_Lang");
            e.Property(t => t.LanguageCode).HasMaxLength(10);
            e.Property(t => t.Title).HasMaxLength(512);
            e.Property(t => t.Slug).HasMaxLength(256);
            e.HasOne(t => t.Page).WithMany(p => p.Translations).HasForeignKey(t => t.PageId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== PageBlock =====
        modelBuilder.Entity<PageBlock>(e =>
        {
            e.HasIndex(b => new { b.PageId, b.SortOrder }).HasDatabaseName("IX_PageBlocks_PageId_Sort");
            e.HasOne(b => b.Page).WithMany(p => p.Blocks).HasForeignKey(b => b.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.BlockTemplate).WithMany().HasForeignKey(b => b.BlockTemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== PageBlockTranslation =====
        modelBuilder.Entity<PageBlockTranslation>(e =>
        {
            e.HasIndex(t => new { t.PageBlockId, t.LanguageCode }).IsUnique().HasDatabaseName("IX_PageBlockTrans_BlockId_Lang");
            e.Property(t => t.LanguageCode).HasMaxLength(10);
            e.HasOne(t => t.PageBlock).WithMany(b => b.Translations).HasForeignKey(t => t.PageBlockId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== BlockTemplate =====
        modelBuilder.Entity<BlockTemplate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.Category).HasMaxLength(64);
            e.Property(t => t.Tags).HasMaxLength(500);
        });

        // ===== SiteNavigation =====
        modelBuilder.Entity<SiteNavigation>(e =>
        {
            e.HasIndex(n => new { n.SiteId, n.Location }).HasDatabaseName("IX_SiteNavigations_SiteId_Loc");
            e.Property(n => n.Name).HasMaxLength(128);
            e.Property(n => n.Location).HasMaxLength(64);
            e.HasOne(n => n.Site).WithMany(s => s.Navigations).HasForeignKey(n => n.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteMenuItem =====
        modelBuilder.Entity<SiteMenuItem>(e =>
        {
            e.Property(m => m.Label).HasMaxLength(256);
            e.Property(m => m.LabelEn).HasMaxLength(256);
            e.Property(m => m.Url).HasMaxLength(1024);
            e.HasOne(m => m.Navigation).WithMany(n => n.Items).HasForeignKey(m => m.NavigationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.ParentItem).WithMany(m => m.Children).HasForeignKey(m => m.ParentItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Page).WithMany().HasForeignKey(m => m.PageId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteMedia =====
        modelBuilder.Entity<SiteMedia>(e =>
        {
            e.HasIndex(m => new { m.SiteId, m.FolderPath }).HasDatabaseName("IX_SiteMedia_SiteId_Folder");
            e.Property(m => m.FileName).HasMaxLength(512);
            e.Property(m => m.OriginalFileName).HasMaxLength(512);
            e.Property(m => m.ContentType).HasMaxLength(128);
            e.Property(m => m.FolderPath).HasMaxLength(512);
            e.Property(m => m.Tags).HasMaxLength(500);
            e.HasOne(m => m.Site).WithMany(s => s.Media).HasForeignKey(m => m.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteSeoRedirect =====
        modelBuilder.Entity<SiteSeoRedirect>(e =>
        {
            e.HasIndex(r => new { r.SiteId, r.FromPath }).IsUnique().HasDatabaseName("IX_SiteSeoRedirects_SiteId_From");
            e.Property(r => r.FromPath).HasMaxLength(1024);
            e.Property(r => r.ToPath).HasMaxLength(1024);
            e.HasOne(r => r.Site).WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteProduct =====
        modelBuilder.Entity<SiteProduct>(e =>
        {
            e.HasIndex(p => new { p.SiteId, p.ProductId }).IsUnique().HasDatabaseName("IX_SiteProducts_SiteId_ProductId");
            e.HasIndex(p => new { p.SiteId, p.Slug }).IsUnique().HasFilter("\"Slug\" IS NOT NULL").HasDatabaseName("IX_SiteProducts_SiteId_Slug");
            e.Property(p => p.DisplayName).HasMaxLength(512);
            e.Property(p => p.Slug).HasMaxLength(256);
            e.Property(p => p.OverrideSellingPrice).HasPrecision(18, 2);
            e.Property(p => p.CompareAtPrice).HasPrecision(18, 2);
            e.HasOne(p => p.Site).WithMany(s => s.Products).HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Product).WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.SiteCategory).WithMany(c => c.Products).HasForeignKey(p => p.SiteCategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteProductTranslation =====
        modelBuilder.Entity<SiteProductTranslation>(e =>
        {
            e.HasIndex(t => new { t.SiteProductId, t.LanguageCode }).IsUnique().HasDatabaseName("IX_SiteProductTrans_ProdId_Lang");
            e.Property(t => t.LanguageCode).HasMaxLength(10);
            e.Property(t => t.DisplayName).HasMaxLength(512);
            e.HasOne(t => t.SiteProduct).WithMany(p => p.Translations).HasForeignKey(t => t.SiteProductId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SitePricingTier =====
        modelBuilder.Entity<SitePricingTier>(e =>
        {
            e.Property(t => t.TierName).HasMaxLength(128);
            e.Property(t => t.UnitPrice).HasPrecision(18, 2);
            e.Property(t => t.CustomerGroupTag).HasMaxLength(64);
            e.HasOne(t => t.SiteProduct).WithMany(p => p.PricingTiers).HasForeignKey(t => t.SiteProductId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteCategory =====
        modelBuilder.Entity<SiteCategory>(e =>
        {
            e.HasIndex(c => new { c.SiteId, c.Slug }).IsUnique().HasDatabaseName("IX_SiteCategories_SiteId_Slug");
            e.Property(c => c.Name).HasMaxLength(256);
            e.Property(c => c.NameEn).HasMaxLength(256);
            e.Property(c => c.Slug).HasMaxLength(128);
            e.HasOne(c => c.Site).WithMany(s => s.Categories).HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.ParentCategory).WithMany(c => c.Children).HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== SiteCart =====
        modelBuilder.Entity<SiteCart>(e =>
        {
            e.HasIndex(c => new { c.SiteId, c.CustomerId }).HasDatabaseName("IX_SiteCarts_SiteId_CustomerId");
            e.HasIndex(c => c.SessionToken).HasDatabaseName("IX_SiteCarts_SessionToken");
            e.Property(c => c.Currency).HasMaxLength(3);
            e.Property(c => c.SessionToken).HasMaxLength(128);
            e.Property(c => c.CouponCode).HasMaxLength(64);
            e.Property(c => c.SubTotal).HasPrecision(18, 2);
            e.Property(c => c.DiscountAmount).HasPrecision(18, 2);
            e.Property(c => c.VatAmount).HasPrecision(18, 2);
            e.Property(c => c.TotalAmount).HasPrecision(18, 2);
            e.HasOne(c => c.Site).WithMany().HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Customer).WithMany(cu => cu.Carts).HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteCartItem =====
        modelBuilder.Entity<SiteCartItem>(e =>
        {
            e.Property(i => i.Quantity).HasPrecision(18, 4);
            e.Property(i => i.UnitPrice).HasPrecision(18, 2);
            e.Property(i => i.TotalPrice).HasPrecision(18, 2);
            e.HasOne(i => i.Cart).WithMany(c => c.Items).HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.SiteProduct).WithMany().HasForeignKey(i => i.SiteProductId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== SiteOrder =====
        modelBuilder.Entity<SiteOrder>(e =>
        {
            e.HasIndex(o => new { o.SiteId, o.OrderNumber }).IsUnique().HasDatabaseName("IX_SiteOrders_SiteId_OrderNo");
            e.HasIndex(o => new { o.SiteId, o.Status }).HasDatabaseName("IX_SiteOrders_SiteId_Status");
            e.Property(o => o.OrderNumber).HasMaxLength(50);
            e.Property(o => o.Currency).HasMaxLength(3);
            e.Property(o => o.SubTotal).HasPrecision(18, 2);
            e.Property(o => o.DiscountAmount).HasPrecision(18, 2);
            e.Property(o => o.ShippingAmount).HasPrecision(18, 2);
            e.Property(o => o.VatAmount).HasPrecision(18, 2);
            e.Property(o => o.TotalAmount).HasPrecision(18, 2);
            e.Property(o => o.PaidAmount).HasPrecision(18, 2);
            e.Property(o => o.CouponCode).HasMaxLength(64);
            e.Property(o => o.BillingTaxId).HasMaxLength(13);
            e.Property(o => o.BillingBranchCode).HasMaxLength(5);
            e.Property(o => o.TrackingNumber).HasMaxLength(128);
            e.HasOne(o => o.Site).WithMany(s => s.Orders).HasForeignKey(o => o.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(o => o.PaymentGateway).WithMany().HasForeignKey(o => o.PaymentGatewayId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(o => o.ErpDocument).WithMany().HasForeignKey(o => o.ErpDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(o => !o.IsDeleted);
        });

        // ===== SiteOrderLine =====
        modelBuilder.Entity<SiteOrderLine>(e =>
        {
            e.Property(l => l.ProductName).HasMaxLength(512);
            e.Property(l => l.ProductSku).HasMaxLength(64);
            e.Property(l => l.Unit).HasMaxLength(20);
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.DiscountAmount).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 2);
            e.Property(l => l.VatAmount).HasPrecision(18, 2);
            e.Property(l => l.TotalAmount).HasPrecision(18, 2);
            e.HasOne(l => l.Order).WithMany(o => o.Lines).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.SiteProduct).WithMany().HasForeignKey(l => l.SiteProductId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== SiteOrderPayment =====
        modelBuilder.Entity<SiteOrderPayment>(e =>
        {
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.GatewayTransactionId).HasMaxLength(256);
            e.Property(p => p.Reference).HasMaxLength(256);
            e.HasOne(p => p.Order).WithMany(o => o.Payments).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SitePaymentGateway =====
        modelBuilder.Entity<SitePaymentGateway>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(128);
            e.Property(g => g.MerchantId).HasMaxLength(128);
            e.Property(g => g.PromptPayId).HasMaxLength(20);
            e.Property(g => g.BankAccountNumber).HasMaxLength(50);
            e.Property(g => g.BankAccountName).HasMaxLength(256);
            e.Property(g => g.BankName).HasMaxLength(128);
            e.Property(g => g.SupportedCurrencies).HasMaxLength(100);
            e.HasOne(g => g.Site).WithMany(s => s.PaymentGateways).HasForeignKey(g => g.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteCoupon =====
        modelBuilder.Entity<SiteCoupon>(e =>
        {
            e.HasIndex(c => new { c.SiteId, c.Code }).IsUnique().HasDatabaseName("IX_SiteCoupons_SiteId_Code");
            e.HasIndex(c => new { c.SiteId, c.IsActive }).HasDatabaseName("IX_SiteCoupons_SiteId_IsActive");
            e.Property(c => c.Code).HasMaxLength(64);
            e.Property(c => c.Description).HasMaxLength(512);
            e.Property(c => c.DiscountValue).HasPrecision(18, 2);
            e.Property(c => c.MaxDiscountAmount).HasPrecision(18, 2);
            e.Property(c => c.MinOrderAmount).HasPrecision(18, 2);
            e.Property(c => c.CustomerGroupTag).HasMaxLength(128);
            e.HasOne(c => c.Site).WithMany().HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteCouponUsage>(e =>
        {
            e.HasIndex(u => new { u.CouponId, u.CustomerId }).HasDatabaseName("IX_CouponUsages_Coupon_Customer");
            e.HasIndex(u => u.OrderId).HasDatabaseName("IX_CouponUsages_OrderId");
            e.Property(u => u.DiscountApplied).HasPrecision(18, 2);
            e.HasOne(u => u.Coupon).WithMany(c => c.Usages).HasForeignKey(u => u.CouponId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(u => u.Order).WithMany().HasForeignKey(u => u.OrderId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(u => u.Customer).WithMany().HasForeignKey(u => u.CustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteShippingZone & Rate =====
        modelBuilder.Entity<SiteShippingZone>(e =>
        {
            e.HasIndex(z => new { z.SiteId, z.IsActive }).HasDatabaseName("IX_ShippingZones_SiteId_IsActive");
            e.Property(z => z.Name).HasMaxLength(128);
            e.Property(z => z.Description).HasMaxLength(512);
            e.Property(z => z.CountryCodes).HasMaxLength(512);
            e.Property(z => z.Provinces).HasMaxLength(2048);
            e.Property(z => z.PostalCodePatterns).HasMaxLength(1024);
            e.HasOne(z => z.Site).WithMany().HasForeignKey(z => z.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteShippingRate>(e =>
        {
            e.HasIndex(r => new { r.ZoneId, r.IsActive }).HasDatabaseName("IX_ShippingRates_Zone_Active");
            e.Property(r => r.Name).HasMaxLength(128);
            e.Property(r => r.CarrierName).HasMaxLength(128);
            e.Property(r => r.BaseRate).HasPrecision(18, 2);
            e.Property(r => r.PerKgRate).HasPrecision(18, 2);
            e.Property(r => r.FreeAboveAmount).HasPrecision(18, 2);
            e.Property(r => r.MinWeightKg).HasPrecision(10, 3);
            e.Property(r => r.MaxWeightKg).HasPrecision(10, 3);
            e.HasOne(r => r.Zone).WithMany(z => z.Rates).HasForeignKey(r => r.ZoneId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteProductVariant =====
        modelBuilder.Entity<SiteProductVariant>(e =>
        {
            e.HasIndex(v => new { v.SiteProductId, v.Sku }).IsUnique().HasDatabaseName("IX_Variants_Product_Sku");
            e.Property(v => v.Sku).HasMaxLength(128);
            e.Property(v => v.Name).HasMaxLength(256);
            e.Property(v => v.Barcode).HasMaxLength(128);
            e.Property(v => v.Price).HasPrecision(18, 2);
            e.Property(v => v.CompareAtPrice).HasPrecision(18, 2);
            e.Property(v => v.Stock).HasPrecision(18, 4);
            e.Property(v => v.WeightKg).HasPrecision(10, 3);
            e.HasOne(v => v.SiteProduct).WithMany(p => p.Variants).HasForeignKey(v => v.SiteProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(v => v.ErpProduct).WithMany().HasForeignKey(v => v.ErpProductId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SiteProductOption>(e =>
        {
            e.HasIndex(o => new { o.SiteProductId, o.Name }).HasDatabaseName("IX_ProductOptions_Product_Name");
            e.Property(o => o.Name).HasMaxLength(64);
            e.Property(o => o.NameEn).HasMaxLength(64);
            e.Property(o => o.DisplayType).HasMaxLength(32);
            e.HasOne(o => o.SiteProduct).WithMany(p => p.Options).HasForeignKey(o => o.SiteProductId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteProductOptionValue>(e =>
        {
            e.HasIndex(v => new { v.OptionId, v.SortOrder }).HasDatabaseName("IX_OptionValues_Option_Sort");
            e.Property(v => v.Value).HasMaxLength(64);
            e.Property(v => v.ValueEn).HasMaxLength(64);
            e.Property(v => v.ColorHex).HasMaxLength(16);
            e.HasOne(v => v.Option).WithMany(o => o.Values).HasForeignKey(v => v.OptionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteCommerceConfig =====
        modelBuilder.Entity<SiteCommerceConfig>(e =>
        {
            e.HasIndex(c => c.SiteId).IsUnique().HasDatabaseName("IX_CommerceConfig_SiteId");
            e.Property(c => c.DefaultVatRate).HasPrecision(5, 2);
            e.Property(c => c.FlatShippingRate).HasPrecision(18, 2);
            e.Property(c => c.FreeShippingThreshold).HasPrecision(18, 2);
            e.Property(c => c.OrderNumberPrefix).HasMaxLength(16);
            e.Property(c => c.OrderNotificationEmails).HasMaxLength(1024);
            e.Property(c => c.DefaultProductSort).HasMaxLength(32);
            e.Property(c => c.OrderConfirmMessageTh).HasMaxLength(2048);
            e.Property(c => c.OrderConfirmMessageEn).HasMaxLength(2048);
            e.Property(c => c.QuotationMessageTh).HasMaxLength(2048);
            e.Property(c => c.QuotationMessageEn).HasMaxLength(2048);
            e.HasOne(c => c.Site).WithOne(s => s.CommerceConfig).HasForeignKey<SiteCommerceConfig>(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteProductReview =====
        modelBuilder.Entity<SiteProductReview>(e =>
        {
            e.HasIndex(r => new { r.SiteId, r.IsApproved, r.IsHidden }).HasDatabaseName("IX_Reviews_Site_Status");
            e.HasIndex(r => new { r.SiteProductId, r.IsApproved, r.IsHidden }).HasDatabaseName("IX_Reviews_Product_Status");
            e.HasIndex(r => r.CustomerId).HasDatabaseName("IX_Reviews_CustomerId");
            e.Property(r => r.ReviewerName).HasMaxLength(128);
            e.Property(r => r.ReviewerEmail).HasMaxLength(256);
            e.Property(r => r.Title).HasMaxLength(256);
            e.HasOne(r => r.Site).WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.SiteProduct).WithMany(p => p.Reviews).HasForeignKey(r => r.SiteProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Order).WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteBookingService =====
        modelBuilder.Entity<SiteBookingService>(e =>
        {
            e.HasIndex(bs => new { bs.SiteId, bs.Slug }).IsUnique().HasDatabaseName("IX_SiteBookingSvc_SiteId_Slug");
            e.Property(bs => bs.Name).HasMaxLength(256);
            e.Property(bs => bs.NameEn).HasMaxLength(256);
            e.Property(bs => bs.Slug).HasMaxLength(128);
            e.Property(bs => bs.Price).HasPrecision(18, 2);
            e.Property(bs => bs.DepositAmount).HasPrecision(18, 2);
            e.Property(bs => bs.DepositPercent).HasPrecision(5, 2);
            e.Property(bs => bs.CancellationFeePercent).HasPrecision(5, 2);
            e.Property(bs => bs.Currency).HasMaxLength(3);
            e.Property(bs => bs.Category).HasMaxLength(128);
            e.Property(bs => bs.Tags).HasMaxLength(500);
            e.HasOne(bs => bs.Site).WithMany(s => s.BookingServices).HasForeignKey(bs => bs.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(bs => bs.Product).WithMany().HasForeignKey(bs => bs.ProductId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteBookingServiceTranslation =====
        modelBuilder.Entity<SiteBookingServiceTranslation>(e =>
        {
            e.HasIndex(t => new { t.BookingServiceId, t.LanguageCode }).IsUnique().HasDatabaseName("IX_SiteBookingSvcTrans_SvcId_Lang");
            e.Property(t => t.LanguageCode).HasMaxLength(10);
            e.Property(t => t.Name).HasMaxLength(256);
            e.HasOne(t => t.BookingService).WithMany(bs => bs.Translations).HasForeignKey(t => t.BookingServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteBookingSlot =====
        modelBuilder.Entity<SiteBookingSlot>(e =>
        {
            e.HasOne(sl => sl.BookingService).WithMany(bs => bs.Slots).HasForeignKey(sl => sl.BookingServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteBooking =====
        modelBuilder.Entity<SiteBooking>(e =>
        {
            e.HasIndex(b => new { b.SiteId, b.BookingNumber }).IsUnique().HasDatabaseName("IX_SiteBookings_SiteId_BookNo");
            e.HasIndex(b => new { b.SiteId, b.BookingDate, b.Status }).HasDatabaseName("IX_SiteBookings_SiteId_Date_Status");
            e.Property(b => b.BookingNumber).HasMaxLength(50);
            e.Property(b => b.GuestName).HasMaxLength(256);
            e.Property(b => b.GuestEmail).HasMaxLength(256);
            e.Property(b => b.GuestPhone).HasMaxLength(20);
            e.Property(b => b.TotalAmount).HasPrecision(18, 2);
            e.Property(b => b.DepositAmount).HasPrecision(18, 2);
            e.Property(b => b.PaidAmount).HasPrecision(18, 2);
            e.Property(b => b.Currency).HasMaxLength(3);
            e.HasOne(b => b.Site).WithMany().HasForeignKey(b => b.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.BookingService).WithMany(bs => bs.Bookings).HasForeignKey(b => b.BookingServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Customer).WithMany(c => c.Bookings).HasForeignKey(b => b.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(b => b.ErpDocument).WithMany().HasForeignKey(b => b.ErpDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(b => !b.IsDeleted);
        });

        // ===== SiteBookingPayment =====
        modelBuilder.Entity<SiteBookingPayment>(e =>
        {
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.GatewayTransactionId).HasMaxLength(256);
            e.Property(p => p.Reference).HasMaxLength(256);
            e.HasOne(p => p.Booking).WithMany(b => b.Payments).HasForeignKey(p => p.BookingId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteCustomer =====
        modelBuilder.Entity<SiteCustomer>(e =>
        {
            e.HasIndex(c => new { c.SiteId, c.Email }).IsUnique().HasDatabaseName("IX_SiteCustomers_SiteId_Email");
            e.HasIndex(c => new { c.CompanyId, c.Email }).HasDatabaseName("IX_SiteCustomers_CompanyId_Email");
            e.Property(c => c.Email).HasMaxLength(256);
            e.Property(c => c.FullName).HasMaxLength(256);
            e.Property(c => c.Phone).HasMaxLength(20);
            e.Property(c => c.TaxId).HasMaxLength(13);
            e.Property(c => c.BranchCode).HasMaxLength(5);
            e.Property(c => c.CompanyName).HasMaxLength(256);
            e.Property(c => c.PreferredLanguage).HasMaxLength(10);
            e.Property(c => c.PreferredCurrency).HasMaxLength(3);
            e.Property(c => c.CustomerGroup).HasMaxLength(64);
            e.Property(c => c.Tags).HasMaxLength(500);
            e.HasOne(c => c.Site).WithMany(s => s.Customers).HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Contact).WithMany().HasForeignKey(c => c.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== Soft-delete filters for entities created via raw-SQL
        //       migration (no other EF config beyond the DbSet). Without
        //       these, eager-loaded nav properties on Payment.Allocations
        //       and Employee.CompensationProfile would surface soft-
        //       deleted rows.
        modelBuilder.Entity<PaymentAllocation>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<EmployeeProjectTime>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<EmployeeCompensationProfile>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<CompanyCompensationDefaults>().HasQueryFilter(e => !e.IsDeleted);

        // ===== SiteCustomerAddress =====
        modelBuilder.Entity<SiteCustomerAddress>(e =>
        {
            e.Property(a => a.Label).HasMaxLength(64);
            e.Property(a => a.RecipientName).HasMaxLength(256);
            e.Property(a => a.Phone).HasMaxLength(20);
            e.Property(a => a.PostalCode).HasMaxLength(10);
            e.Property(a => a.CountryCode).HasMaxLength(2);
            e.HasOne(a => a.Customer).WithMany(c => c.Addresses).HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteWishlistItem =====
        modelBuilder.Entity<SiteWishlistItem>(e =>
        {
            e.HasIndex(w => new { w.CustomerId, w.SiteProductId }).IsUnique().HasDatabaseName("IX_SiteWishlist_CustId_ProdId");
            e.HasOne(w => w.Customer).WithMany(c => c.WishlistItems).HasForeignKey(w => w.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(w => w.SiteProduct).WithMany().HasForeignKey(w => w.SiteProductId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteForm =====
        modelBuilder.Entity<SiteForm>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(256);
            e.Property(f => f.NotifyEmails).HasMaxLength(1000);
            e.Property(f => f.SuccessMessage).HasMaxLength(1000);
            e.Property(f => f.RedirectUrl).HasMaxLength(1024);
            e.HasOne(f => f.Site).WithMany(s => s.Forms).HasForeignKey(f => f.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteFormField =====
        modelBuilder.Entity<SiteFormField>(e =>
        {
            e.Property(f => f.FieldName).HasMaxLength(128);
            e.Property(f => f.Label).HasMaxLength(256);
            e.Property(f => f.LabelEn).HasMaxLength(256);
            e.Property(f => f.Placeholder).HasMaxLength(256);
            e.Property(f => f.ValidationPattern).HasMaxLength(512);
            e.Property(f => f.Width).HasMaxLength(20);
            e.HasOne(f => f.Form).WithMany(fm => fm.Fields).HasForeignKey(f => f.FormId).OnDelete(DeleteBehavior.Cascade);
        });

        // ===== SiteFormSubmission =====
        modelBuilder.Entity<SiteFormSubmission>(e =>
        {
            e.HasIndex(s => new { s.FormId, s.Status }).HasDatabaseName("IX_SiteFormSubs_FormId_Status");
            e.Property(s => s.IpAddress).HasMaxLength(45);
            e.Property(s => s.UserAgent).HasMaxLength(512);
            e.HasOne(s => s.Form).WithMany(f => f.Submissions).HasForeignKey(s => s.FormId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Customer).WithMany().HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.ErpDocument).WithMany().HasForeignKey(s => s.ErpDocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        // ===== SiteCustomerMerge =====
        modelBuilder.Entity<SiteCustomerMerge>(e =>
        {
            e.Property(m => m.MergeReason).HasMaxLength(64);
            e.Property(m => m.MergedBy).HasMaxLength(256);
            e.HasOne(m => m.PrimaryCustomer).WithMany().HasForeignKey(m => m.PrimaryCustomerId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        var auditEntries = Services.Implementations.AuditTrailService.CaptureAuditEntries(ChangeTracker, null, null, null);
        var result = base.SaveChanges();
        if (auditEntries.Count > 0)
        {
            AuditLogs.AddRange(auditEntries);
            base.SaveChanges();
        }
        return result;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        var auditEntries = Services.Implementations.AuditTrailService.CaptureAuditEntries(ChangeTracker, null, null, null);
        var result = await base.SaveChangesAsync(cancellationToken);
        if (auditEntries.Count > 0)
        {
            AuditLogs.AddRange(auditEntries);
            await base.SaveChangesAsync(cancellationToken);
        }
        return result;
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries<BaseEntity>();
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTime.UtcNow;
        }
    }
}
