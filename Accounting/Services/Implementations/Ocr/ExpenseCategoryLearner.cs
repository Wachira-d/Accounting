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

        // ─── Tier 1: exact (vendor + full keyword) match — strongest signal ───
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

        // ─── Tier 2: token-based fuzzy match — split description into meaningful
        // words and look for any past mapping whose keyword shares tokens. Handles
        // "ค่าน้ำมันเบนซิน 95" matching "ค่าน้ำมัน" or "เบนซิน" when the exact
        // string differs but the vendor + topic is the same. ───
        var tokens = ExtractTokens(description);
        if (tokens.Count > 0)
        {
            var vendorMappings = await _db.OcrCategoryMappings
                .Where(m => m.CompanyId == companyId && m.VendorKey == vendorKey && !m.IsDeleted)
                .Select(m => new { m.DescriptionKeyword, m.AccountCode, m.AccountName, m.TimesUsed })
                .ToListAsync();

            // Score each mapping by overlap of tokens
            var best = vendorMappings
                .Select(m => new
                {
                    m.AccountCode, m.AccountName, m.TimesUsed,
                    Overlap = tokens.Count(t => m.DescriptionKeyword.Contains(t))
                })
                .Where(x => x.Overlap > 0)
                .GroupBy(x => new { x.AccountCode, x.AccountName })
                .Select(g => new
                {
                    g.Key.AccountCode, g.Key.AccountName,
                    Score = g.Sum(x => x.Overlap * x.TimesUsed),
                    SumUsed = g.Sum(x => x.TimesUsed)
                })
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (best != null)
            {
                // Lower confidence than exact match — token overlap is fuzzier
                var conf = ScoreConfidence(best.SumUsed) * 0.75m;
                return (best.AccountCode, best.AccountName, conf);
            }
        }

        // ─── Tier 3: vendor-only fallback — vendor X almost always books to account Y ───
        var vendorOnly = await _db.OcrCategoryMappings
            .Where(m => m.CompanyId == companyId
                && m.VendorKey == vendorKey
                && !m.IsDeleted)
            .GroupBy(m => new { m.AccountCode, m.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Sum = g.Sum(x => x.TimesUsed) })
            .OrderByDescending(g => g.Sum)
            .FirstOrDefaultAsync();
        if (vendorOnly != null)
            return (vendorOnly.AccountCode, vendorOnly.AccountName, ScoreConfidence(vendorOnly.Sum) * 0.65m);

        // ─── Tier 4: system-wide fallback — SystemAdmin-trained knowledge base ───
        // Tenant has no row for this vendor at all. Consult the shared system
        // mappings (trained by SystemAdmin in /admin/ocr-config). Confidence is
        // halved relative to per-tenant signals because the system data reflects
        // generic best-practice, not this company's actual booking habits.
        if (!string.IsNullOrEmpty(keyword))
        {
            var sysExact = await _db.SystemOcrCategoryMappings
                .Where(m => m.VendorKey == vendorKey && m.DescriptionKeyword == keyword && !m.IsDeleted)
                .OrderByDescending(m => m.TimesUsed)
                .FirstOrDefaultAsync();
            if (sysExact != null)
                return (sysExact.AccountCode, sysExact.AccountName, ScoreConfidence(sysExact.TimesUsed) * 0.5m);
        }

        var sysVendor = await _db.SystemOcrCategoryMappings
            .Where(m => m.VendorKey == vendorKey && !m.IsDeleted)
            .GroupBy(m => new { m.AccountCode, m.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Sum = g.Sum(x => x.TimesUsed) })
            .OrderByDescending(g => g.Sum)
            .FirstOrDefaultAsync();
        if (sysVendor != null)
            return (sysVendor.AccountCode, sysVendor.AccountName, ScoreConfidence(sysVendor.Sum) * 0.35m);

        return (null, null, 0m);
    }

    /// <summary>
    /// SystemAdmin variant of RecordAsync — writes to the system-wide
    /// SystemOcrCategoryMappings table (no CompanyId). Trained mappings here
    /// are consulted as a fallback for every tenant.
    /// </summary>
    public async Task RecordSystemAsync(string? vendorTaxId, string? vendorName,
        string? description, string accountCode, string? accountName, Guid? userId = null, int weight = 1)
    {
        if (string.IsNullOrEmpty(accountCode)) return;
        var vendorKey = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(vendorKey)) return;
        if (weight < 1) weight = 1;
        var keyword = NormalizeDescription(description);

        var existing = await _db.SystemOcrCategoryMappings
            .FirstOrDefaultAsync(m => m.VendorKey == vendorKey
                && m.DescriptionKeyword == keyword
                && m.AccountCode == accountCode
                && !m.IsDeleted);

        if (existing != null)
        {
            existing.TimesUsed += weight;
            existing.LastUsedAt = DateTime.UtcNow;
            if (string.IsNullOrEmpty(existing.AccountName) && !string.IsNullOrEmpty(accountName))
                existing.AccountName = accountName;
        }
        else
        {
            _db.SystemOcrCategoryMappings.Add(new SystemOcrCategoryMapping
            {
                VendorKey = vendorKey,
                DescriptionKeyword = keyword,
                AccountCode = accountCode,
                AccountName = accountName,
                TimesUsed = weight,
                LastUsedAt = DateTime.UtcNow,
                TrainedByUserId = userId,
                CreatedBy = userId?.ToString() ?? "system-admin",
            });
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("Learned SYSTEM category mapping: vendor={V} keyword={K} → {Code} (+{W})",
            vendorKey, keyword, accountCode, weight);
    }

    /// <summary>
    /// Extract meaningful tokens from a Thai/English description for fuzzy matching.
    /// Strips punctuation, splits on whitespace, drops common stopwords + tokens
    /// shorter than 2 chars. Both Thai and English work — for Thai we don't have
    /// a tokenizer, but most line descriptions in invoices already have whitespace
    /// or punctuation between concepts ("ค่าน้ำมัน เบนซิน 95 ลิตร").
    /// </summary>
    private static HashSet<string> ExtractTokens(string? description)
    {
        var tokens = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(description)) return tokens;

        // Split on whitespace + common Thai/English punctuation
        var raw = description.ToLowerInvariant()
            .Replace(",", " ").Replace(".", " ").Replace("-", " ")
            .Replace("(", " ").Replace(")", " ").Replace("/", " ")
            .Replace(":", " ").Replace(";", " ");

        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Drop tokens that are pure numbers (quantities, sizes) and short noise
            if (part.Length < 2) continue;
            if (part.All(char.IsDigit)) continue;
            if (Stopwords.Contains(part)) continue;
            tokens.Add(part);
        }
        return tokens;
    }

    private static readonly HashSet<string> Stopwords = new()
    {
        // Thai
        "และ", "หรือ", "เป็น", "ของ", "ให้", "กับ", "ใน", "ที่", "จาก",
        "ค่า", "ค่ะ", "ครับ", "ตาม", "โดย", "เพื่อ", "ทั้ง", "ทุก",
        // English
        "and", "or", "the", "a", "an", "of", "in", "to", "for", "with",
        "on", "at", "by", "from", "as", "is", "this", "that"
    };

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
