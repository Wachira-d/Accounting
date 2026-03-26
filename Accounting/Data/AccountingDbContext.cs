using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

public class AccountingDbContext : DbContext
{
    public AccountingDbContext(DbContextOptions<AccountingDbContext> options) : base(options) { }

    // Core
    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyUser> CompanyUsers => Set<CompanyUser>();

    // Subscription & Trial
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
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

    // Tax
    public DbSet<TaxReport> TaxReports => Set<TaxReport>();
    public DbSet<TaxReportLine> TaxReportLines => Set<TaxReportLine>();

    // Audit
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    // Products & Inventory
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    // Bank
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

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

    // Expense Claims
    public DbSet<ExpenseClaim> ExpenseClaims => Set<ExpenseClaim>();
    public DbSet<ExpenseClaimLine> ExpenseClaimLines => Set<ExpenseClaimLine>();

    // Withholding Tax Certificates
    public DbSet<WithholdingTaxCert> WithholdingTaxCerts => Set<WithholdingTaxCert>();
    public DbSet<WithholdingTaxCertLine> WithholdingTaxCertLines => Set<WithholdingTaxCertLine>();

    // API Keys
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Freelance Management
    public DbSet<FreelanceInvitation> FreelanceInvitations => Set<FreelanceInvitation>();
    public DbSet<FreelanceAccess> FreelanceAccesses => Set<FreelanceAccess>();
    public DbSet<FreelanceTask> FreelanceTasks => Set<FreelanceTask>();
    public DbSet<FreelanceTaskComment> FreelanceTaskComments => Set<FreelanceTaskComment>();
    public DbSet<FreelanceTimeLog> FreelanceTimeLogs => Set<FreelanceTimeLog>();
    public DbSet<FreelanceActivityLog> FreelanceActivityLogs => Set<FreelanceActivityLog>();

    // Project Accounting
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectCostEntry> ProjectCostEntries => Set<ProjectCostEntry>();

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
    public DbSet<PayrollItem> PayrollItems => Set<PayrollItem>();

    // Tax Calendar
    public DbSet<TaxCalendarEvent> TaxCalendarEvents => Set<TaxCalendarEvent>();

    // Advanced AR/AP
    public DbSet<ContactCreditSetting> ContactCreditSettings => Set<ContactCreditSetting>();
    public DbSet<DunningLetter> DunningLetters => Set<DunningLetter>();
    public DbSet<DunningLetterLine> DunningLetterLines => Set<DunningLetterLine>();
    public DbSet<PaymentReminder> PaymentReminders => Set<PaymentReminder>();

    // OCR
    public DbSet<OcrScanResult> OcrScanResults => Set<OcrScanResult>();

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
            e.HasOne(j => j.FiscalPeriod).WithMany(f => f.JournalEntries).HasForeignKey(j => j.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(j => !j.IsDeleted);
        });

        // ===== JournalEntryLine =====
        modelBuilder.Entity<JournalEntryLine>(e =>
        {
            e.HasOne(l => l.JournalEntry).WithMany(j => j.Lines).HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany(a => a.JournalEntryLines).HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.DebitAmount).HasPrecision(18, 2);
            e.Property(l => l.CreditAmount).HasPrecision(18, 2);
        });

        // ===== FiscalPeriod =====
        modelBuilder.Entity<FiscalPeriod>(e =>
        {
            e.HasIndex(f => new { f.CompanyId, f.Year, f.Month }).IsUnique();
            e.Property(f => f.Name).HasMaxLength(20);
            e.HasQueryFilter(f => !f.IsDeleted);
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
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ===== DocumentLine =====
        modelBuilder.Entity<DocumentLine>(e =>
        {
            e.HasOne(l => l.Document).WithMany(d => d.Lines).HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasQueryFilter(c => !c.IsDeleted);
        });

        // ===== Payment =====
        modelBuilder.Entity<Payment>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.PaymentNumber }).IsUnique();
            e.Property(p => p.PaymentNumber).HasMaxLength(50);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.HasOne(p => p.Document).WithMany(d => d.Payments).HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasIndex(a => new { a.CompanyId, a.Timestamp });
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
            e.HasIndex(t => new { t.BankAccountId, t.TransactionDate });
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.BalanceAfter).HasPrecision(18, 2);
            e.HasOne(t => t.BankAccount).WithMany(a => a.Transactions).HasForeignKey(t => t.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== RecurringTransaction =====
        modelBuilder.Entity<RecurringTransaction>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(256);
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
            e.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
            e.Property(n => n.Title).HasMaxLength(500);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== NumberSeries =====
        modelBuilder.Entity<NumberSeries>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.DocumentType }).IsUnique().HasFilter("[IsActive] = 1");
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
            e.HasIndex(t => new { t.CompanyId, t.DocumentType, t.IsDefault }).HasFilter("[IsDefault] = 1 AND [IsDeleted] = 0");
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
            e.HasQueryFilter(ec => !ec.IsDeleted);
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

        // ===== FreelanceInvitation =====
        modelBuilder.Entity<FreelanceInvitation>(e =>
        {
            e.HasIndex(i => i.InvitationToken).IsUnique();
            e.Property(i => i.InviteeEmail).HasMaxLength(256);
            e.Property(i => i.InviteeName).HasMaxLength(256);
            e.HasOne(i => i.Company).WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.InvitedByUser).WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceAccess =====
        modelBuilder.Entity<FreelanceAccess>(e =>
        {
            e.HasIndex(fa => new { fa.CompanyId, fa.UserId }).IsUnique();
            e.HasOne(fa => fa.Company).WithMany().HasForeignKey(fa => fa.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(fa => fa.User).WithMany().HasForeignKey(fa => fa.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceTask =====
        modelBuilder.Entity<FreelanceTask>(e =>
        {
            e.Property(t => t.Title).HasMaxLength(500);
            e.Property(t => t.EstimatedHours).HasPrecision(8, 2);
            e.Property(t => t.ActualHours).HasPrecision(8, 2);
            e.Property(t => t.AgreedRate).HasPrecision(18, 2);
            e.Property(t => t.TotalCost).HasPrecision(18, 2);
            e.HasOne(t => t.FreelanceAccess).WithMany(fa => fa.Tasks).HasForeignKey(t => t.FreelanceAccessId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Company).WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceTaskComment =====
        modelBuilder.Entity<FreelanceTaskComment>(e =>
        {
            e.HasOne(c => c.FreelanceTask).WithMany(t => t.Comments).HasForeignKey(c => c.FreelanceTaskId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceTimeLog =====
        modelBuilder.Entity<FreelanceTimeLog>(e =>
        {
            e.Property(l => l.Hours).HasPrecision(8, 2);
            e.HasOne(l => l.FreelanceTask).WithMany(t => t.TimeLogs).HasForeignKey(l => l.FreelanceTaskId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceActivityLog =====
        modelBuilder.Entity<FreelanceActivityLog>(e =>
        {
            e.HasIndex(l => new { l.CompanyId, l.Timestamp });
            e.HasIndex(l => new { l.FreelanceAccessId, l.Timestamp });
            e.Property(l => l.Action).HasMaxLength(200);
            e.HasOne(l => l.FreelanceAccess).WithMany(fa => fa.ActivityLogs).HasForeignKey(l => l.FreelanceAccessId).OnDelete(DeleteBehavior.Restrict);
        });

        // ======================================================================
        // Advanced Operations & World-Class Features
        // ======================================================================

        // ===== Employee =====
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasIndex(emp => new { emp.CompanyId, emp.EmployeeCode }).IsUnique();
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

        // Freelance
        modelBuilder.Entity<FreelanceInvitation>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<FreelanceAccess>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<FreelanceTask>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<FreelanceTaskComment>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<FreelanceTimeLog>().HasQueryFilter(e => !e.IsDeleted);

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
