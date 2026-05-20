using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ผังบัญชีต้นแบบกลาง (System-wide master Chart of Accounts template).
/// Admin-editable replacement for the hard-coded common-accounts list in
/// <see cref="Services.ChartOfAccountTemplates"/>. NOT tenant-scoped — one
/// shared master that every newly-created company seeds its books from.
///
/// When this table has rows, AccountingService.SeedDefaultAccountsAsync
/// uses them as the common base (codes 1/2/4/5) and still appends the
/// business-type equity block + industry block from code. When empty,
/// seeding falls back entirely to the built-in static template — so an
/// un-initialised system keeps working exactly as before.
/// </summary>
public class SystemAccountTemplate : BaseEntity
{
    public string AccountCode { get; set; } = null!;
    public string AccountNameTh { get; set; } = null!;
    public string? AccountNameEn { get; set; }
    public AccountType AccountType { get; set; }
    /// <summary>Hierarchy depth 1-4 — drives ParentAccountId resolution by
    /// code-prefix during company seeding.</summary>
    public int Level { get; set; }
    public bool IsActive { get; set; } = true;
}
