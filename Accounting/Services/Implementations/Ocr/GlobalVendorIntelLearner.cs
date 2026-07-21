using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Federated layer for VendorIntelligenceService — accumulates anonymous
/// per-vendor signals across all tenants into the existing
/// <see cref="SystemOcrVendorIntelligence"/> row, AND tracks the
/// distinct-tenant contribution count so privacy-sensitive aggregates
/// (AvgTotalAmount, MedianTotalAmount, MinTotalAmount, MaxTotalAmount)
/// can be gated behind a k=3 anonymity floor at READ time.
///
/// Why this lives separately from the existing
/// <c>SystemOcrKnowledgeSeeder</c>: the seeder is an admin-time import
/// of a curated training corpus, run once. This learner is the runtime
/// hook called from <c>VendorIntelligenceService.TrainFromDocumentAsync</c>
/// after each approved document — so the global pool grows organically
/// with real platform usage instead of staying frozen at seed-time.
///
/// Privacy posture (vendor identity = public business info — tax IDs
/// are published by the Revenue Department, so per-vendor behavioural
/// data is NOT customer data). What IS customer data is the per-tenant
/// payment volume to that vendor; those numbers are guarded by the
/// k=3 read-time gate inside VendorIntelligenceService.PredictAsync.
/// </summary>
public class GlobalVendorIntelLearner
{
    private readonly AccountingDbContext _db;
    public GlobalVendorIntelLearner(AccountingDbContext db) { _db = db; }

    public const int MinTenantsForFullDisclosure = 3;

    /// <summary>Called after the per-tenant VendorIntel row is saved.
    /// Increments the distinct-tenant counter on the global SystemOcr
    /// row for the same vendor. Idempotent per (CompanyId, VendorKey)
    /// — at-most-once contribution per tenant per vendor, even if the
    /// tenant approves 100 documents from that supplier.</summary>
    public async Task RecordContributionAsync(
        Guid companyId,
        string vendorKey,
        string? vendorName,
        string? vendorTaxId)
    {
        if (string.IsNullOrWhiteSpace(vendorKey)) return;

        var sys = await _db.SystemOcrVendorIntelligence
            .FirstOrDefaultAsync(v => !v.IsDeleted && v.VendorKey == vendorKey);

        if (sys == null)
        {
            sys = new SystemOcrVendorIntelligence
            {
                VendorKey = vendorKey,
                VendorName = vendorName,
                VendorTaxId = vendorTaxId,
                TenantContributionCount = 0,
                LastTrainedAt = DateTime.UtcNow
            };
            _db.SystemOcrVendorIntelligence.Add(sys);
            await _db.SaveChangesAsync();
        }

        var seenBefore = await _db.SystemOcrVendorIntelTenantSeens
            .AnyAsync(s => !s.IsDeleted && s.SystemIntelId == sys.Id && s.CompanyId == companyId);
        if (!seenBefore)
        {
            _db.SystemOcrVendorIntelTenantSeens.Add(new SystemOcrVendorIntelTenantSeen
            {
                SystemIntelId = sys.Id,
                CompanyId = companyId
            });
            sys.TenantContributionCount += 1;
            sys.LastTrainedAt = DateTime.UtcNow;
            sys.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>How much of the row should be exposed to a reader? At
    /// the k-anonymity floor, only behavioural fields (doc type, WHT
    /// habit, payment terms) leave the row — money aggregates stay
    /// suppressed. Above the floor, everything is fair game because
    /// the data is averaged across ≥3 unrelated tenants.</summary>
    public static bool CanShareMoneyAggregates(SystemOcrVendorIntelligence sys)
        => sys.TenantContributionCount >= MinTenantsForFullDisclosure;
}

/// <summary>
/// Federated cross-tenant prediction of "given we scanned this
/// (Vendor, ScannedDocType) pair, which TargetDocumentType do tenants
/// across the SaaS typically book?" — e.g. for vendor X always booking
/// scanned Receipts as PaymentVouchers. Anonymized + k=3 floor.
/// </summary>
public class GlobalDocWorkflowLearner
{
    private readonly AccountingDbContext _db;
    public GlobalDocWorkflowLearner(AccountingDbContext db) { _db = db; }

    public async Task<(string? TargetDocType, int TenantCount)> PredictAsync(
        string? vendorKey, string? scannedDocType)
    {
        if (string.IsNullOrWhiteSpace(vendorKey) || string.IsNullOrWhiteSpace(scannedDocType))
            return (null, 0);
        var hit = await _db.GlobalDocWorkflowPatterns
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == "active"
                     && p.VendorKey == vendorKey && p.ScannedDocType == scannedDocType)
            .OrderByDescending(p => p.TenantCount)
            .ThenByDescending(p => p.TotalConfirms)
            .FirstOrDefaultAsync();
        return hit == null ? (null, 0) : (hit.TargetDocType, hit.TenantCount);
    }

    public async Task RecordConfirmAsync(Guid companyId, string vendorKey, string scannedDocType, string targetDocType)
    {
        if (string.IsNullOrWhiteSpace(vendorKey) || string.IsNullOrWhiteSpace(scannedDocType) || string.IsNullOrWhiteSpace(targetDocType))
            return;
        var pattern = await _db.GlobalDocWorkflowPatterns
            .FirstOrDefaultAsync(p => !p.IsDeleted
                && p.VendorKey == vendorKey
                && p.ScannedDocType == scannedDocType
                && p.TargetDocType == targetDocType);
        if (pattern == null)
        {
            pattern = new GlobalDocWorkflowPattern
            {
                VendorKey = vendorKey.Length > 200 ? vendorKey[..200] : vendorKey,
                ScannedDocType = scannedDocType,
                TargetDocType = targetDocType,
                Status = "candidate"
            };
            _db.GlobalDocWorkflowPatterns.Add(pattern);
            await _db.SaveChangesAsync();
        }

        var seenBefore = await _db.GlobalDocWorkflowTenantSeens
            .AnyAsync(s => !s.IsDeleted && s.PatternId == pattern.Id && s.CompanyId == companyId);
        if (!seenBefore)
        {
            _db.GlobalDocWorkflowTenantSeens.Add(new GlobalDocWorkflowTenantSeen
            {
                PatternId = pattern.Id,
                CompanyId = companyId
            });
            pattern.TenantCount += 1;
        }
        pattern.TotalConfirms += 1;
        pattern.LastConfirmedAt = DateTime.UtcNow;
        pattern.UpdatedAt = DateTime.UtcNow;

        if (pattern.Status == "candidate"
            && pattern.TenantCount >= GlobalDocWorkflowPattern.MinTenantsForActive
            && pattern.TotalConfirms >= GlobalDocWorkflowPattern.MinConfirmsForActive)
        {
            pattern.Status = "active";
        }
    }
}
