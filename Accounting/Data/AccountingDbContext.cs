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
