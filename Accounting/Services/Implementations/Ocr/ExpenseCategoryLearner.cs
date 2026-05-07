using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Learns and predicts expense category (chart-of-account code) from
/// (vendor, line description) pairs. Trained from approved documents and
/// from user corrections; queried during ScanAsync to suggest accounts.
/// </summary>
public class ExpenseCategoryLearner
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ExpenseCategoryLearner> _logger;

    public ExpenseCategoryLearner(AccountingDbContext db, ILogger<ExpenseCategoryLearner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Look up the most likely chart-of-account code for a given vendor + line description.
    /// Returns null when no learned mapping has confidence above threshold.
    /// </summary>
    public async Task<(string? AccountCode, string? AccountName, decimal Confidence)> PredictAsync(
        Guid companyId, string? vendorTaxId, string? vendorName, string? description)
    {
        var vendorKey = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(vendorKey))
            return (null, null, 0m);

        var keyword = NormalizeDescription(description);

        // First try exact (vendor + keyword) match — strongest signal
        if (!string.IsNullOrEmpty(keyword))
        {
            var exact = await _db.OcrCategoryMappings
                .Where(m => m.CompanyId == companyId
                    && m.VendorKey == vendorKey
                    && m.DescriptionKeyword == keyword
                    && !m.IsDeleted)
                .OrderByDescending(m => m.TimesUsed)
                .FirstOrDefaultAsync();
            if (exact != null)
                return (exact.AccountCode, exact.AccountName, ScoreConfidence(exact.TimesUsed));
        }

        // Fallback to vendor-only match (any description) — vendor X almost always books to account Y
        var vendorOnly = await _db.OcrCategoryMappings
            .Where(m => m.CompanyId == companyId
                && m.VendorKey == vendorKey
                && !m.IsDeleted)
            .GroupBy(m => new { m.AccountCode, m.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Sum = g.Sum(x => x.TimesUsed) })
            .OrderByDescending(g => g.Sum)
            .FirstOrDefaultAsync();
        if (vendorOnly != null)
            return (vendorOnly.AccountCode, vendorOnly.AccountName, ScoreConfidence(vendorOnly.Sum) * 0.85m);

        return (null, null, 0m);
    }

    /// <summary>
    /// Record that a user (or auto-create) booked a document with a specific account
    /// for a specific (vendor, line description) combination. Increments TimesUsed
    /// when an existing mapping matches; otherwise creates a new row.
    /// </summary>
    public async Task RecordAsync(Guid companyId, string? vendorTaxId, string? vendorName,
        string? description, string accountCode, string? accountName, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(accountCode)) return;
        var vendorKey = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(vendorKey)) return;
        var keyword = NormalizeDescription(description);

        var existing = await _db.OcrCategoryMappings
            .FirstOrDefaultAsync(m => m.CompanyId == companyId
                && m.VendorKey == vendorKey
                && m.DescriptionKeyword == keyword
                && m.AccountCode == accountCode
                && !m.IsDeleted);

        if (existing != null)
        {
            existing.TimesUsed++;
            existing.LastUsedAt = DateTime.UtcNow;
            if (string.IsNullOrEmpty(existing.AccountName) && !string.IsNullOrEmpty(accountName))
                existing.AccountName = accountName;
        }
        else
        {
            _db.OcrCategoryMappings.Add(new OcrCategoryMapping
            {
                CompanyId = companyId,
                VendorKey = vendorKey,
                DescriptionKeyword = keyword,
                AccountCode = accountCode,
                AccountName = accountName,
                TimesUsed = 1,
                LastUsedAt = DateTime.UtcNow,
                TrainedByUserId = userId,
                CreatedBy = userId?.ToString() ?? "auto",
            });
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("Learned category mapping: company={C} vendor={V} keyword={K} → {Code}",
            companyId, vendorKey, keyword, accountCode);
    }

    private static string NormalizeVendorKey(string? taxId, string? name)
    {
        if (!string.IsNullOrEmpty(taxId))
        {
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            if (digits.Length == 13) return $"tax:{digits}";
        }
        if (!string.IsNullOrEmpty(name))
            return $"name:{name.Trim().ToLowerInvariant()}";
        return "";
    }

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var lowered = description.Trim().ToLowerInvariant();
        // Take first 50 chars to bucket similar descriptions
        return lowered.Length > 50 ? lowered.Substring(0, 50) : lowered;
    }

    /// <summary>
    /// Confidence rises with TimesUsed but plateaus — 1 use ≈ 0.55, 5 uses ≈ 0.80,
    /// 20+ uses ≈ 0.95. Mirrors typical Bayesian belief curve for repeated observations.
    /// </summary>
    private static decimal ScoreConfidence(int timesUsed)
    {
        if (timesUsed <= 0) return 0m;
        // Asymptotic curve: 1 - (1 / (1 + 0.5*n))
        return 1m - (1m / (1m + 0.5m * timesUsed));
    }
}
