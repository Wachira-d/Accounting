using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ผังบัญชี (Chart of Accounts)
/// </summary>
public class ChartOfAccount : TenantEntity
{
    public string AccountCode { get; set; } = null!;   // e.g. "1100", "4100"
    public string AccountName { get; set; } = null!;    // e.g. "เงินสด"
    public string? AccountNameEn { get; set; }
    public AccountType AccountType { get; set; }
    public Guid? ParentAccountId { get; set; }
    public ChartOfAccount? ParentAccount { get; set; }
    public int Level { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsSystemAccount { get; set; } = false;  // บัญชีที่ระบบสร้างให้
    public string? Description { get; set; }

    // Navigation
    public ICollection<ChartOfAccount> ChildAccounts { get; set; } = new List<ChartOfAccount>();
    public ICollection<JournalEntryLine> JournalEntryLines { get; set; } = new List<JournalEntryLine>();
}
