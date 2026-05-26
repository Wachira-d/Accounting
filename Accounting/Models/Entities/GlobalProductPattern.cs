namespace Accounting.Models.Entities;

/// <summary>
/// Cross-tenant, anonymized product wording knowledge. Every time a
/// tenant confirms a ProductAlias mapping in the OCR import flow, the
/// alias's normalized wording is upserted here together with the
/// ANONYMIZED metadata (brand token, unit, category hint, count of
/// distinct confirming tenants). NO prices, quantities, vendor names,
/// or per-tenant identifiers are stored — only the wording fingerprint
/// + aggregate counts.
///
/// k-Anonymity privacy floor: patterns are only treated as "active"
/// (read by other tenants' matchers) once at least
/// <see cref="MinTenantsForActive"/> distinct tenants have independently
/// confirmed the same key. Below that, status stays "candidate" and the
/// pattern is only used inside the originating tenant.
///
/// This is the foundation layer for federated learning across the SaaS.
/// Adding more learners (vendor-name canonicalizer, expense-category
/// promoter, ...) follows the same shape: store the normalized text key
/// + aggregate counters + privacy floor.
/// </summary>
public class GlobalProductPattern : BaseEntity   // NOT TenantEntity — intentional, this is system-wide
{
    public const int MinTenantsForActive = 3;
    public const int MinConfirmsForActive = 5;

    /// <summary>The normalized form produced by ProductMatcher.Normalize.
    /// Same value across tenants so the key is meaningfully shareable.</summary>
    public string NormalizedKey { get; set; } = null!;

    /// <summary>Most-frequent verbatim wording across tenants. Best-
    /// effort, used by the "create new product" flow to pre-fill a
    /// canonical product name instead of the raw OCR description.</summary>
    public string? CanonicalLabel { get; set; }

    /// <summary>Brand token extracted from the wording, if any —
    /// canonicalized to upper-case ASCII (e.g. "PEPSI", "TOYOTA").
    /// Null when no brand was detectable.</summary>
    public string? Brand { get; set; }

    /// <summary>Most-frequent unit observed alongside this wording
    /// (e.g. "ลิตร"). Helps pre-fill the Unit dropdown when this pattern
    /// drives a new-product creation.</summary>
    public string? Unit { get; set; }

    /// <summary>Most-frequent Category string used by tenants when they
    /// created a Product matching this wording. Helps auto-categorize
    /// new products without forcing the user to choose.</summary>
    public string? CategoryHint { get; set; }

    /// <summary>Count of DISTINCT companies whose users have confirmed
    /// a ProductAlias resolving to this key.</summary>
    public int TenantCount { get; set; }

    /// <summary>Sum of confirmations across all tenants (≥ TenantCount).</summary>
    public int TotalConfirms { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"candidate" until k-anonymity threshold is reached, then
    /// "active". "deprecated" when admin retires a noisy pattern.</summary>
    public string Status { get; set; } = "candidate";
}

/// <summary>
/// One row per (CompanyId, NormalizedKey) so we can count DISTINCT
/// tenants without leaking which tenant. Used purely as a uniqueness
/// counter — never read for routing. A unique index on
/// (PatternId, CompanyId) prevents one tenant from inflating
/// TenantCount by spamming confirmations of the same wording.
/// </summary>
public class GlobalProductPatternTenantSeen : BaseEntity
{
    public Guid PatternId { get; set; }
    public Guid CompanyId { get; set; }
}
