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

    // API Keys
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Freelance Management
    public DbSet<FreelanceInvitation> FreelanceInvitations => Set<FreelanceInvitation>();
    public DbSet<FreelanceAccess> FreelanceAccesses => Set<FreelanceAccess>();
    public DbSet<FreelanceTask> FreelanceTasks => Set<FreelanceTask>();
    public DbSet<FreelanceTaskComment> FreelanceTaskComments => Set<FreelanceTaskComment>();
    public DbSet<FreelanceTimeLog> FreelanceTimeLogs => Set<FreelanceTimeLog>();
    public DbSet<FreelanceActivityLog> FreelanceActivityLogs => Set<FreelanceActivityLog>();

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
            e.HasOne(cu => cu.User).WithMany(u => u.CompanyUsers).HasForeignKey(cu => cu.UserId);
            e.HasOne(cu => cu.Company).WithMany(c => c.CompanyUsers).HasForeignKey(cu => cu.CompanyId);
        });

        // ===== Subscription =====
        modelBuilder.Entity<Subscription>(e =>
        {
            e.HasIndex(s => s.CompanyId).IsUnique();
            e.HasOne(s => s.Company).WithOne(c => c.Subscription).HasForeignKey<Subscription>(s => s.CompanyId);
            e.Property(s => s.PricePerCycle).HasPrecision(18, 2);
            e.Property(s => s.Currency).HasMaxLength(3);
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
            e.HasOne(h => h.Subscription).WithMany(s => s.History).HasForeignKey(h => h.SubscriptionId);
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

        // ===== ChartOfAccount =====
        modelBuilder.Entity<ChartOfAccount>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.AccountCode }).IsUnique();
            e.Property(a => a.AccountCode).HasMaxLength(20);
            e.Property(a => a.AccountName).HasMaxLength(256);
            e.HasOne(a => a.ParentAccount).WithMany(a => a.ChildAccounts).HasForeignKey(a => a.ParentAccountId);
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // ===== JournalEntry =====
        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.HasIndex(j => new { j.CompanyId, j.EntryNumber }).IsUnique();
            e.Property(j => j.EntryNumber).HasMaxLength(50);
            e.Property(j => j.TotalDebit).HasPrecision(18, 2);
            e.Property(j => j.TotalCredit).HasPrecision(18, 2);
            e.HasOne(j => j.FiscalPeriod).WithMany(f => f.JournalEntries).HasForeignKey(j => j.FiscalPeriodId);
            e.HasQueryFilter(j => !j.IsDeleted);
        });

        // ===== JournalEntryLine =====
        modelBuilder.Entity<JournalEntryLine>(e =>
        {
            e.HasOne(l => l.JournalEntry).WithMany(j => j.Lines).HasForeignKey(l => l.JournalEntryId);
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
            e.HasOne(d => d.Contact).WithMany(c => c.Documents).HasForeignKey(d => d.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(d => !d.IsDeleted);
        });

        // ===== DocumentLine =====
        modelBuilder.Entity<DocumentLine>(e =>
        {
            e.HasOne(l => l.Document).WithMany(d => d.Lines).HasForeignKey(l => l.DocumentId);
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
            e.HasOne(l => l.TaxReport).WithMany(r => r.Lines).HasForeignKey(l => l.TaxReportId);
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
            e.HasOne(t => t.BankAccount).WithMany(a => a.Transactions).HasForeignKey(t => t.BankAccountId);
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
            e.HasOne(d => d.FixedAsset).WithMany(a => a.Depreciations).HasForeignKey(d => d.FixedAssetId);
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
            e.HasOne(s => s.ApprovalRule).WithMany(r => r.Steps).HasForeignKey(s => s.ApprovalRuleId);
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
            e.HasOne(a => a.ApprovalRequest).WithMany(r => r.Actions).HasForeignKey(a => a.ApprovalRequestId);
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
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId);
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
            e.HasOne(l => l.Budget).WithMany(b => b.Lines).HasForeignKey(l => l.BudgetId);
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
            e.HasOne(k => k.Company).WithMany().HasForeignKey(k => k.CompanyId);
            e.HasOne(k => k.CreatedByUser).WithMany().HasForeignKey(k => k.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceInvitation =====
        modelBuilder.Entity<FreelanceInvitation>(e =>
        {
            e.HasIndex(i => i.InvitationToken).IsUnique();
            e.Property(i => i.InviteeEmail).HasMaxLength(256);
            e.Property(i => i.InviteeName).HasMaxLength(256);
            e.HasOne(i => i.Company).WithMany().HasForeignKey(i => i.CompanyId);
            e.HasOne(i => i.InvitedByUser).WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceAccess =====
        modelBuilder.Entity<FreelanceAccess>(e =>
        {
            e.HasIndex(fa => new { fa.CompanyId, fa.UserId }).IsUnique();
            e.HasOne(fa => fa.Company).WithMany().HasForeignKey(fa => fa.CompanyId);
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
            e.HasOne(t => t.FreelanceAccess).WithMany(fa => fa.Tasks).HasForeignKey(t => t.FreelanceAccessId);
            e.HasOne(t => t.Company).WithMany().HasForeignKey(t => t.CompanyId);
        });

        // ===== FreelanceTaskComment =====
        modelBuilder.Entity<FreelanceTaskComment>(e =>
        {
            e.HasOne(c => c.FreelanceTask).WithMany(t => t.Comments).HasForeignKey(c => c.FreelanceTaskId);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceTimeLog =====
        modelBuilder.Entity<FreelanceTimeLog>(e =>
        {
            e.Property(l => l.Hours).HasPrecision(8, 2);
            e.HasOne(l => l.FreelanceTask).WithMany(t => t.TimeLogs).HasForeignKey(l => l.FreelanceTaskId);
            e.HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ===== FreelanceActivityLog =====
        modelBuilder.Entity<FreelanceActivityLog>(e =>
        {
            e.HasIndex(l => new { l.CompanyId, l.Timestamp });
            e.HasIndex(l => new { l.FreelanceAccessId, l.Timestamp });
            e.Property(l => l.Action).HasMaxLength(200);
            e.HasOne(l => l.FreelanceAccess).WithMany(fa => fa.ActivityLogs).HasForeignKey(l => l.FreelanceAccessId);
        });
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
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
