namespace Accounting.Models.Entities;

/// <summary>Tracks DISTINCT tenants who have contributed data to a
/// <see cref="SystemOcrVendorIntelligence"/> row. Used purely as a
/// uniqueness counter — never read for routing. A unique index on
/// (SystemIntelId, CompanyId) prevents the same tenant from inflating
/// TenantContributionCount by repeated training calls.</summary>
public class SystemOcrVendorIntelTenantSeen : BaseEntity
{
    public Guid SystemIntelId { get; set; }
    public Guid CompanyId { get; set; }
}

/// <summary>Federated cross-tenant pattern: which TargetDocumentType
/// the SaaS as a whole books when scanning a given (Vendor, ScannedType)
/// pair. Read by the document workflow predictor as a fallback when the
/// local tenant lacks history. Activates after k=3 distinct tenants
/// agree.</summary>
public class GlobalDocWorkflowPattern : BaseEntity   // NOT TenantEntity
{
    public const int MinTenantsForActive = 3;
    public const int MinConfirmsForActive = 5;

    public string VendorKey { get; set; } = null!;
    public string ScannedDocType { get; set; } = null!;
    public string TargetDocType { get; set; } = null!;
    public int TenantCount { get; set; }
    public int TotalConfirms { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "candidate";
}

public class GlobalDocWorkflowTenantSeen : BaseEntity
{
    public Guid PatternId { get; set; }
    public Guid CompanyId { get; set; }
}
