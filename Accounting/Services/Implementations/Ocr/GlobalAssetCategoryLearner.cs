using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Federated cross-tenant consensus on "what asset category + useful-
/// life should this OCR'd wording be registered under?". Same shape as
/// <see cref="GlobalProductLearner"/> — anonymized key + tenant-count
/// counter + k=3 active-floor.
///
/// Read in <c>OcrController.StockPreview</c> when a line is flagged as
/// a potential fixed asset by <c>FixedAssetDetector</c> — augments the
/// per-tenant detector's local guess with cross-tenant consensus
/// (e.g. 12 tenants agree that "Toyota Hilux 2.4" is "ยานพาหนะ" with
/// 60-month useful life — surface that as the default).
///
/// Written by <c>OcrController.ImportStock</c> when a row commits with
/// Destination = FixedAsset — the chosen category + useful life feed
/// back into the pool inside try/catch so federation outages don't
/// break asset creation.
/// </summary>
public class GlobalAssetCategoryLearner
{
    private readonly AccountingDbContext _db;
    public GlobalAssetCategoryLearner(AccountingDbContext db) { _db = db; }

    public async Task<GlobalAssetCategoryPattern?> GetActivePatternAsync(string normalizedKey)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return null;
        return await _db.GlobalAssetCategoryPatterns
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == "active" && p.NormalizedKey == normalizedKey)
            .OrderByDescending(p => p.TenantCount)
            .ThenByDescending(p => p.TotalConfirms)
            .FirstOrDefaultAsync();
    }

    public async Task RecordConfirmAsync(
        Guid companyId,
        string normalizedKey,
        string category,
        int usefulLifeMonths,
        string? depreciationMethod)
    {
        if (string.IsNullOrEmpty(normalizedKey) || string.IsNullOrEmpty(category) || usefulLifeMonths <= 0) return;

        // Bucket per (key, category, useful-life). Different lifes/categories
        // for the same wording are separate rows — consensus then picks the
        // one with the most tenants behind it.
        var pattern = await _db.GlobalAssetCategoryPatterns
            .FirstOrDefaultAsync(p => !p.IsDeleted
                && p.NormalizedKey == normalizedKey
                && p.Category == category
                && p.UsefulLifeMonths == usefulLifeMonths);
        if (pattern == null)
        {
            pattern = new GlobalAssetCategoryPattern
            {
                NormalizedKey = normalizedKey.Length > 500 ? normalizedKey[..500] : normalizedKey,
                Category = category.Length > 200 ? category[..200] : category,
                UsefulLifeMonths = usefulLifeMonths,
                DepreciationMethod = depreciationMethod,
                Status = "candidate"
            };
            _db.GlobalAssetCategoryPatterns.Add(pattern);
            await _db.SaveChangesAsync();
        }

        var seenBefore = await _db.GlobalAssetCategoryTenantSeens
            .AnyAsync(s => !s.IsDeleted && s.PatternId == pattern.Id && s.CompanyId == companyId);
        if (!seenBefore)
        {
            _db.GlobalAssetCategoryTenantSeens.Add(new GlobalAssetCategoryTenantSeen
            {
                PatternId = pattern.Id,
                CompanyId = companyId
            });
            pattern.TenantCount += 1;
        }
        pattern.TotalConfirms += 1;
        pattern.LastConfirmedAt = DateTime.UtcNow;
        pattern.UpdatedAt = DateTime.UtcNow;

        if (string.IsNullOrEmpty(pattern.DepreciationMethod) && !string.IsNullOrEmpty(depreciationMethod))
            pattern.DepreciationMethod = depreciationMethod;

        if (pattern.Status == "candidate"
            && pattern.TenantCount >= GlobalAssetCategoryPattern.MinTenantsForActive
            && pattern.TotalConfirms >= GlobalAssetCategoryPattern.MinConfirmsForActive)
        {
            pattern.Status = "active";
        }
    }
}
