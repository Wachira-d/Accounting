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
    private readonly GlobalExpenseCategoryLearner? _global;

    public ExpenseCategoryLearner(AccountingDbContext db, ILogger<ExpenseCategoryLearner> logger,
        GlobalExpenseCategoryLearner? global = null)
    {
        _db = db;
        _logger = logger;
        _global = global;
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

        // Per-tenant bonus multiplier: company can configure how strongly
        // their OWN training outweighs cross-tenant aggregates. Default
        // 2.0 means a tenant's own learned mapping confidence is doubled
        // before being compared to system-wide fallback. The bonus is
        // applied to every tenant tier (1-3) so own data always wins
        // unless the system aggregate has substantially more evidence.
        var tenantBonus = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => s.OwnTrainingBonusMultiplier)
            .FirstOrDefaultAsync();
        if (tenantBonus <= 0) tenantBonus = 2.0m;

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
                return (exact.AccountCode, exact.AccountName, Boost(ScoreConfidence(exact.TimesUsed), tenantBonus));
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
                var conf = Boost(ScoreConfidence(best.SumUsed) * 0.75m, tenantBonus);
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
            return (vendorOnly.AccountCode, vendorOnly.AccountName,
                Boost(ScoreConfidence(vendorOnly.Sum) * 0.65m, tenantBonus));

        // ─── Tier 4: system-wide fallback — SystemAdmin-trained knowledge base ───
        // Tenant has no row for this vendor at all. Consult the shared system
        // mappings (trained by SystemAdmin in /admin/ocr-config). Confidence is
        // halved relative to per-tenant signals because the system data reflects
        // generic best-practice, not this company's actual booking habits.
        // System-tier rows carry an IndustryBreakdownJson that records
        // which industries the contributing tenants belonged to. We
        // weight each row by how strongly its contributors share OUR
        // tenant's industry — same-industry data is trusted more,
        // cross-industry data dampened further. Seeded rows with no
        // breakdown stay at the neutral factor (1.0).
        var ourIndustry = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId && !c.IsDeleted)
            .Select(c => c.IndustryType)
            .FirstOrDefaultAsync();
        var ourIndustryName = ourIndustry.ToString();

        if (!string.IsNullOrEmpty(keyword))
        {
            var sysExact = await _db.SystemOcrCategoryMappings
                .Where(m => m.VendorKey == vendorKey && m.DescriptionKeyword == keyword && !m.IsDeleted)
                .OrderByDescending(m => m.TimesUsed)
                .FirstOrDefaultAsync();
            if (sysExact != null)
            {
                var indFactor = ComputeIndustryWeight(sysExact.IndustryBreakdownJson, ourIndustryName);
                return (sysExact.AccountCode, sysExact.AccountName,
                    ScoreConfidence(sysExact.TimesUsed) * 0.5m * indFactor);
            }
        }

        // Vendor-only fallback — same industry weighting, lower base
        var sysVendorRows = await _db.SystemOcrCategoryMappings
            .Where(m => m.VendorKey == vendorKey && !m.IsDeleted)
            .ToListAsync();
        if (sysVendorRows.Count > 0)
        {
            // Aggregate by account, summing TimesUsed weighted by industry
            // factor PER ROW so accounts trained by same-industry tenants
            // win even when total raw counts are smaller.
            var best = sysVendorRows
                .Select(r => new
                {
                    r.AccountCode, r.AccountName,
                    WeightedScore = r.TimesUsed
                        * (double)ComputeIndustryWeight(r.IndustryBreakdownJson, ourIndustryName),
                })
                .GroupBy(x => new { x.AccountCode, x.AccountName })
                .Select(g => new
                {
                    g.Key.AccountCode, g.Key.AccountName,
                    Score = g.Sum(x => x.WeightedScore),
                })
                .OrderByDescending(x => x.Score)
                .First();
            // Convert weighted score back to a confidence-like value
            var confidence = ScoreConfidence((int)Math.Round(best.Score)) * 0.35m;
            return (best.AccountCode, best.AccountName, confidence);
        }

        // ── Final fallback: federated cross-tenant consensus ──
        // Only consults patterns that crossed the k-anonymity gate
        // (≥3 distinct tenants, ≥5 confirms). Tiny confidence so a
        // weak per-tenant signal still wins, but better than nothing
        // for brand-new tenants with zero local history.
        if (_global != null)
        {
            try
            {
                var (gCode, gName, gTenants, gConfirms) = await _global.PredictAsync(vendorKey, NormalizeDescription(description));
                if (!string.IsNullOrEmpty(gCode))
                {
                    // Tiered confidence based on how many tenants agreed.
                    var globalConf = gTenants >= 10 ? 0.55m
                                   : gTenants >= 5 ? 0.45m
                                   : 0.35m;
                    return (gCode, gName, globalConf);
                }
            }
            catch { /* federation outage — fall through */ }
        }

        return (null, null, 0m);
    }

    /// <summary>
    /// Compute the industry-similarity multiplier (0.5 – 1.5) for a
    /// system-tier row given the consuming tenant's IndustryType.
    ///   100% same-industry contributors → 1.5 (trust strongly)
    ///   ~50% same-industry              → 1.0
    ///   0% same-industry                → 0.5 (cross-industry, dampened)
    ///   no breakdown (seeded/universal) → 1.0 (neutral default)
    /// Multiplied with the existing base dampening (0.5 exact, 0.35
    /// vendor-only) at the call site so cross-industry data falls below
    /// half-weight while same-industry data approaches full weight.
    /// </summary>
    private static decimal ComputeIndustryWeight(string? breakdownJson, string ourIndustryName)
    {
        if (string.IsNullOrEmpty(breakdownJson)) return 1.0m;
        try
        {
            var breakdown = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(breakdownJson);
            if (breakdown == null || breakdown.Count == 0) return 1.0m;
            int total = breakdown.Values.Sum();
            if (total <= 0) return 1.0m;
            int sameCount = breakdown.GetValueOrDefault(ourIndustryName, 0);
            var fraction = (decimal)sameCount / total;
            // Linear blend from 0.5 (no overlap) to 1.5 (all same industry)
            return 0.5m + fraction;
        }
        catch
        {
            return 1.0m;
        }
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

        // Federated contribution — anonymized, k-anonymity gate inside.
        // Failure non-fatal: per-tenant write above already committed.
        if (_global != null)
        {
            try
            {
                await _global.RecordConfirmAsync(companyId, vendorKey, keyword, accountCode, accountName);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Federated expense-category learner write failed (non-fatal)");
            }
        }
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

    /// <summary>Apply the tenant-bonus multiplier without ever exceeding
    /// 0.99 — a single learned mapping cannot be more certain than 99%.
    /// Mirrors the asymptote in ScoreConfidence so the curve stays smooth
    /// even when the bonus would otherwise push past 1.0.</summary>
    private static decimal Boost(decimal baseConfidence, decimal bonus)
        => Math.Min(0.99m, baseConfidence * bonus);
}
