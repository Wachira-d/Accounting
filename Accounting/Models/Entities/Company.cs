using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class Company : BaseEntity
{
    public string Name { get; set; } = null!;
    public string? NameEn { get; set; }
    public string TaxId { get; set; } = null!;          // เลขผู้เสียภาษี
    public string? BranchCode { get; set; } = "00000";  // รหัสสาขา
    public BusinessType BusinessType { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.Active;

    // Address
    public string? Address { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    // Settings
    public int FiscalYearStartMonth { get; set; } = 1;  // เดือนเริ่มต้นปีบัญชี
    public string BaseCurrency { get; set; } = "THB";

    // Navigation
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
    public Subscription? Subscription { get; set; }
    public ICollection<ChartOfAccount> ChartOfAccounts { get; set; } = new List<ChartOfAccount>();
    public ICollection<FiscalPeriod> FiscalPeriods { get; set; } = new List<FiscalPeriod>();
    public ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
}
