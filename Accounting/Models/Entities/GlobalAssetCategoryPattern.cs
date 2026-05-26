namespace Accounting.Models.Entities;

/// <summary>
/// Federated cross-tenant prediction of "what asset category + useful
/// life should this OCR'd line item be registered as?" — mirrors
/// <see cref="GlobalProductPattern"/> shape (anonymized, k-anonymity
/// floor before reads).
///
/// Read path (FixedAssetDetector / OCR stock-preview): when the
/// per-tenant detector returns a low-confidence guess, consult this
/// table for the consensus category + useful life across tenants who
/// registered an asset for the same wording.
///
/// Write path: every time the OCR stock-import flow commits a row as
/// destination = FixedAsset, the normalized description and chosen
/// category + useful life feed back into this table. Status promotes
/// to "active" after k=3 distinct tenants agree.
/// </summary>
public class GlobalAssetCategoryPattern : BaseEntity   // NOT TenantEntity — system-wide
{
    public const int MinTenantsForActive = 3;
    public const int MinConfirmsForActive = 5;

    /// <summary>Normalized line-item description — same shape as
    /// <c>ProductMatcher.Normalize</c> so the keys collide on
    /// equivalent wordings ("Toyota Hilux 2.4 4WD", "TOYOTA HILUX 2.4 4WD").</summary>
    public string NormalizedKey { get; set; } = null!;

    /// <summary>Most-frequent category string tenants applied to
    /// assets matching this wording (e.g. "ยานพาหนะ", "เครื่องใช้สำนักงาน",
    /// "คอมพิวเตอร์"). Free-text — categories vary by tenant industry.</summary>
    public string Category { get; set; } = null!;

    /// <summary>Median useful-life-in-months across confirms. Lets us
    /// resist outliers (one tenant entering 240 months for a laptop
    /// pulls less weight than 9 tenants saying 36).</summary>
    public int UsefulLifeMonths { get; set; }

    /// <summary>Most common depreciation method ("StraightLine",
    /// "DecliningBalance", "DoubleDecliningBalance"). NULL until at
    /// least one tenant has explicitly set it.</summary>
    public string? DepreciationMethod { get; set; }

    public int TenantCount { get; set; }
    public int TotalConfirms { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "candidate";
}

public class GlobalAssetCategoryTenantSeen : BaseEntity
{
    public Guid PatternId { get; set; }
    public Guid CompanyId { get; set; }
}
