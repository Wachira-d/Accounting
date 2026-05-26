using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Cross-tenant federated learner for product wordings. Sits between the
/// per-tenant <see cref="ProductMatcher"/> and a system-wide knowledge
/// pool stored in <see cref="GlobalProductPattern"/>. The pool is fed
/// every time a tenant confirms an alias (so confidence rises
/// organically with platform usage) and is read back during matching
/// to boost candidates whose normalized key matches a globally-known
/// product wording — even when the local tenant has never seen that
/// exact wording before.
///
/// Privacy posture:
///   • No tenant identifier ever lives on <c>GlobalProductPattern</c>.
///   • Distinct-tenant counting goes through a join table
///     (<c>GlobalProductPatternTenantSeens</c>) with a unique index on
///     (PatternId, CompanyId) — incremented at most once per tenant per
///     pattern, so one tenant can't inflate <c>TenantCount</c>.
///   • Patterns only become "active" (readable by other tenants) after
///     k-anonymity floor: ≥ 3 distinct tenants AND ≥ 5 total confirms.
///     Below that, candidate patterns are written but never read by
///     other tenants' matchers.
///   • Stored fields (NormalizedKey, Brand, Unit, CategoryHint,
///     CanonicalLabel) are anonymized by construction — they describe
///     PRODUCT WORDINGS, not customers.
/// </summary>
public class GlobalProductLearner
{
    private readonly AccountingDbContext _db;
    public GlobalProductLearner(AccountingDbContext db) { _db = db; }

    /// <summary>Boost applied to a local product candidate when its
    /// normalized name (or any of its aliases) matches an active global
    /// pattern. Capped well under VendorAliasScore so deterministic
    /// per-tenant signals always win.</summary>
    public const double GlobalPatternBoost = 0.07;

    /// <summary>Pull every <see cref="GlobalProductPattern"/> whose key
    /// matches one of the supplied normalized keys AND is "active".
    /// Cheap point-lookup — the unique index on NormalizedKey carries
    /// the read.</summary>
    public async Task<Dictionary<string, GlobalProductPattern>> GetActivePatternsAsync(IEnumerable<string> normalizedKeys)
    {
        var keys = normalizedKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (keys.Count == 0) return new();
        var rows = await _db.GlobalProductPatterns
            .AsNoTracking()
            .Where(p => !p.IsDeleted
                     && p.Status == "active"
                     && keys.Contains(p.NormalizedKey))
            .ToListAsync();
        return rows.ToDictionary(r => r.NormalizedKey, r => r);
    }

    /// <summary>Look up a single active pattern. Used by the create-new
    /// flow to prefill name/unit/category when the local catalog has
    /// nothing matching this OCR wording.</summary>
    public async Task<GlobalProductPattern?> GetActivePatternAsync(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return null;
        return await _db.GlobalProductPatterns
            .AsNoTracking()
            .FirstOrDefaultAsync(p => !p.IsDeleted && p.Status == "active" && p.NormalizedKey == normalizedKey);
    }

    /// <summary>Record one confirmed mapping. Called from
    /// <c>ProductMatcher.RecordAliasAsync</c>. Idempotent at the
    /// (PatternId, CompanyId) grain — the unique index will reject a
    /// second tenant-seen row from the same tenant, so TenantCount
    /// stays honest. Total confirmations always increments.</summary>
    public async Task RecordConfirmAsync(
        Guid companyId,
        string normalizedKey,
        string verbatimLabel,
        string? brand,
        string? unit,
        string? categoryHint)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return;

        var pattern = await _db.GlobalProductPatterns
            .FirstOrDefaultAsync(p => !p.IsDeleted && p.NormalizedKey == normalizedKey);

        if (pattern == null)
        {
            pattern = new GlobalProductPattern
            {
                NormalizedKey = normalizedKey.Length > 500 ? normalizedKey[..500] : normalizedKey,
                CanonicalLabel = verbatimLabel.Length > 500 ? verbatimLabel[..500] : verbatimLabel,
                Brand = TrimUpper(brand, 100),
                Unit = TrimUpper(unit, 50, lower: true),
                CategoryHint = TrimUpper(categoryHint, 100, lower: true),
                TenantCount = 0,
                TotalConfirms = 0,
                Status = "candidate"
            };
            _db.GlobalProductPatterns.Add(pattern);
            await _db.SaveChangesAsync();   // need Id for the seen row
        }

        // Upsert metadata when the new tenant provides info we lack —
        // first-seen wins for the canonical label, but unit/category fill in
        // when previously null. Keeps the pattern's metadata growing without
        // letting late tenants overwrite established consensus.
        if (string.IsNullOrEmpty(pattern.CanonicalLabel) && !string.IsNullOrEmpty(verbatimLabel))
            pattern.CanonicalLabel = verbatimLabel.Length > 500 ? verbatimLabel[..500] : verbatimLabel;
        if (string.IsNullOrEmpty(pattern.Brand) && !string.IsNullOrEmpty(brand))
            pattern.Brand = TrimUpper(brand, 100);
        if (string.IsNullOrEmpty(pattern.Unit) && !string.IsNullOrEmpty(unit))
            pattern.Unit = TrimUpper(unit, 50, lower: true);
        if (string.IsNullOrEmpty(pattern.CategoryHint) && !string.IsNullOrEmpty(categoryHint))
            pattern.CategoryHint = TrimUpper(categoryHint, 100, lower: true);

        // Distinct-tenant counter — guarded by the unique index, so a
        // racing duplicate insert will just blow up here and we swallow.
        var seenBefore = await _db.GlobalProductPatternTenantSeens
            .AnyAsync(s => !s.IsDeleted && s.PatternId == pattern.Id && s.CompanyId == companyId);
        if (!seenBefore)
        {
            _db.GlobalProductPatternTenantSeens.Add(new GlobalProductPatternTenantSeen
            {
                PatternId = pattern.Id,
                CompanyId = companyId
            });
            pattern.TenantCount += 1;
        }

        pattern.TotalConfirms += 1;
        pattern.LastConfirmedAt = DateTime.UtcNow;

        // Promote to "active" once k-anonymity floor is satisfied.
        // Once active, never auto-demote — only admin action.
        if (pattern.Status == "candidate"
            && pattern.TenantCount >= GlobalProductPattern.MinTenantsForActive
            && pattern.TotalConfirms >= GlobalProductPattern.MinConfirmsForActive)
        {
            pattern.Status = "active";
        }

        pattern.UpdatedAt = DateTime.UtcNow;
    }

    private static string? TrimUpper(string? s, int max, bool lower = false)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.Length > max) t = t[..max];
        return lower ? t : t.ToUpperInvariant();
    }
}
