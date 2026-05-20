using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ผังบัญชีต้นแบบกลาง (System-wide master Chart of Accounts template).
/// Admin-editable replacement for the hard-coded templates in
/// <see cref="Services.ChartOfAccountTemplates"/>. NOT tenant-scoped — one
/// shared master that every newly-created company seeds its books from.
///
/// Each row belongs to exactly one <b>scope</b>, decided by the two
/// nullable type columns:
///   • <see cref="BusinessType"/> = null &amp; <see cref="IndustryType"/> = null
///     → common block (codes 1/2/4/5), applies to every company.
///   • <see cref="BusinessType"/> set → equity (code 3) block for that
///     business type only.
///   • <see cref="IndustryType"/> set → industry-specific block for that
///     industry only.
///
/// Seeding falls back <i>per scope</i>: a scope with no rows uses the
/// built-in code-based block, so the admin may override only the scopes
/// they care about and an un-initialised system behaves exactly as before.
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

    /// <summary>Non-null → this row is the equity block for one business
    /// type. Null (together with <see cref="IndustryType"/>) → common block.</summary>
    public BusinessType? BusinessType { get; set; }

    /// <summary>Non-null → this row is part of one industry-specific block.</summary>
    public IndustryType? IndustryType { get; set; }
}
