using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Federated cross-tenant prediction for "what expense account does this
/// vendor's invoice usually post to?" — mirrors <see cref="GlobalProductLearner"/>
/// for the expense-category domain. The per-tenant ExpenseCategoryLearner
/// already runs a 4-tier fallback; this layer adds a 5th tier ("the SaaS
/// as a whole agrees on the same account") with k-anonymity privacy.
///
/// Wire-up:
///   • Read: ExpenseCategoryLearner.PredictAsync calls
///     <see cref="PredictAsync"/> when its own per-tenant lookup is weak.
///   • Write: ExpenseCategoryLearner.RecordAsync (invoked from the OCR
///     correction endpoint, OcrService.cs:2184) calls
///     <see cref="RecordConfirmAsync"/> after persisting the per-tenant
///     row.
/// </summary>
public class GlobalExpenseCategoryLearner
{
    private readonly AccountingDbContext _db;
    public GlobalExpenseCategoryLearner(AccountingDbContext db) { _db = db; }

    /// <summary>Look up the consensus account-code for this
    /// (VendorKey, optional DescriptionKeyword) tuple. Returns null
    /// when no active pattern exists yet. Confidence reflects how
    /// many tenants agreed.</summary>
    public async Task<(string? AccountCode, string? AccountName, int TenantCount, int TotalConfirms)> PredictAsync(
        string? vendorKey, string? descriptionKeyword)
    {
        if (string.IsNullOrWhiteSpace(vendorKey)) return (null, null, 0, 0);
        var normVendor = vendorKey.Trim().ToLowerInvariant();
        var normDesc = string.IsNullOrWhiteSpace(descriptionKeyword) ? null : descriptionKeyword.Trim().ToLowerInvariant();

        // First try the most specific lookup: vendor + keyword.
        // Fall back to vendor-only.
        var query = _db.GlobalExpenseCategoryPatterns
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == "active" && p.VendorKey == normVendor);

        GlobalExpenseCategoryPattern? hit = null;
        if (normDesc != null)
        {
            hit = await query.Where(p => p.DescriptionKeyword == normDesc)
                .OrderByDescending(p => p.TenantCount)
                .ThenByDescending(p => p.TotalConfirms)
                .FirstOrDefaultAsync();
        }
        if (hit == null)
        {
            hit = await query.Where(p => p.DescriptionKeyword == null)
                .OrderByDescending(p => p.TenantCount)
                .ThenByDescending(p => p.TotalConfirms)
                .FirstOrDefaultAsync();
        }
        if (hit == null) return (null, null, 0, 0);
        return (hit.AccountCode, hit.AccountName, hit.TenantCount, hit.TotalConfirms);
    }

    /// <summary>Anonymously contribute one confirmation. Caller is the
    /// per-tenant ExpenseCategoryLearner.RecordAsync — we run after it
    /// so a federation outage doesn't break per-tenant learning.</summary>
    public async Task RecordConfirmAsync(
        Guid companyId,
        string? vendorKey,
        string? descriptionKeyword,
        string accountCode,
        string? accountName)
    {
        if (string.IsNullOrWhiteSpace(vendorKey) || string.IsNullOrWhiteSpace(accountCode)) return;
        var normVendor = vendorKey.Trim().ToLowerInvariant();
        var normDesc = string.IsNullOrWhiteSpace(descriptionKeyword) ? null : descriptionKeyword.Trim().ToLowerInvariant();

        var pattern = await _db.GlobalExpenseCategoryPatterns
            .FirstOrDefaultAsync(p => !p.IsDeleted
                && p.VendorKey == normVendor
                && p.DescriptionKeyword == normDesc
                && p.AccountCode == accountCode);

        if (pattern == null)
        {
            pattern = new GlobalExpenseCategoryPattern
            {
                VendorKey = Trim(normVendor, 200),
                DescriptionKeyword = normDesc != null ? Trim(normDesc, 200) : null,
                AccountCode = Trim(accountCode, 50),
                AccountName = accountName != null ? Trim(accountName, 200) : null,
                Status = "candidate"
            };
            _db.GlobalExpenseCategoryPatterns.Add(pattern);
            await _db.SaveChangesAsync();   // need Id for the seen-row
        }

        var seenBefore = await _db.GlobalExpenseCategoryTenantSeens
            .AnyAsync(s => !s.IsDeleted && s.PatternId == pattern.Id && s.CompanyId == companyId);
        if (!seenBefore)
        {
            _db.GlobalExpenseCategoryTenantSeens.Add(new GlobalExpenseCategoryTenantSeen
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
            && pattern.TenantCount >= GlobalExpenseCategoryPattern.MinTenantsForActive
            && pattern.TotalConfirms >= GlobalExpenseCategoryPattern.MinConfirmsForActive)
        {
            pattern.Status = "active";
        }
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
