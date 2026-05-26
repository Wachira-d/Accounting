namespace Accounting.Models.Entities;

/// <summary>
/// Federated cross-tenant expense-category prediction. Mirrors
/// <see cref="GlobalProductPattern"/> shape — anonymized, system-wide,
/// k-anonymity floor before reads. Stores the consensus mapping from
/// (VendorKey, DescriptionKeyword) → ChartOfAccount code used by the
/// majority of tenants who scanned similar receipts.
///
/// Read path (ExpenseCategoryLearner.PredictAsync): when the per-tenant
/// lookup misses, consult this table. Higher-TenantCount entries rank
/// higher. Only "active" rows (≥ MinTenants distinct tenants + ≥
/// MinConfirms total) are surfaced cross-tenant.
///
/// Write path: every confirmed correction in OcrService (the
/// _categoryLearner.RecordAsync call site, OcrService.cs:2184) ALSO
/// upserts here so the pool grows with usage.
/// </summary>
public class GlobalExpenseCategoryPattern : BaseEntity   // NOT TenantEntity — system-wide
{
    public const int MinTenantsForActive = 3;
    public const int MinConfirmsForActive = 5;

    /// <summary>Vendor identifier — tax-id if known, else normalized
    /// vendor name (lowercased, suffix-stripped via FuzzyMatcher).</summary>
    public string VendorKey { get; set; } = null!;

    /// <summary>Optional keyword from the line description — short
    /// canonical token (e.g. "electricity", "ไฟฟ้า"). Null when the
    /// prediction is header-level (no per-line description).</summary>
    public string? DescriptionKeyword { get; set; }

    public string AccountCode { get; set; } = null!;
    public string? AccountName { get; set; }

    public int TenantCount { get; set; }
    public int TotalConfirms { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "candidate";
}

public class GlobalExpenseCategoryTenantSeen : BaseEntity
{
    public Guid PatternId { get; set; }
    public Guid CompanyId { get; set; }
}
