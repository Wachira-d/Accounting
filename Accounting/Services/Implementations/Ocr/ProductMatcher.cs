using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Maps a free-text OCR'd product description ("น้ำมันพืช ตรา A 1 ลิตร")
/// to the best-matching Product in the company's catalog. Sits behind a
/// 5-stage cascade so the cheapest signal wins and we only fall back to
/// expensive fuzzy comparison when the deterministic checks fail.
///
///   1. Exact alias (vendor-bound) — alias.ContactId == vendor &amp;&amp; alias.NormalizedName == norm(desc) → 1.0
///   2. Exact alias (any vendor)    — alias.ContactId IS NULL  &amp;&amp; alias.NormalizedName == norm(desc) → 0.95
///   3. Code/SKU/Barcode in description → 0.92 (e.g. "A-OIL-1L 12 ขวด" hits SKU "A-OIL-1L")
///   4. Trigram SIMILARITY() via pg_trgm — top-N candidates joined across
///      Products + ProductAliases, picking the highest score; threshold 0.45
///   5. Levenshtein fuzzy on the normalized form, threshold 0.65
///
/// Returns the top-1 plus up to N alternatives so the UI can show
/// "did you mean…?" without another round trip. RecordMatchAsync writes
/// a ProductAlias when the user confirms a match — that next OCR run
/// short-circuits at step 1 above.
///
/// Normalization (specific to product descriptions, distinct from
/// FuzzyMatcher's contact-name normalization):
///   • lowercase
///   • drop punctuation, collapse whitespace
///   • canonicalize Thai/English unit suffixes — "1 ลิตร" / "1ลิตร" /
///     "1L" / "1 ltr" / "1 liter" → "1l"; "500 มล" / "500ml" → "500ml";
///     "1 กก." / "1 กก" / "1kg" → "1kg"; "12 ชิ้น" / "12pcs" → "12pcs"
///   • token sort so "น้ำมัน A 1L" and "A 1L น้ำมัน" share a key
/// </summary>
public class ProductMatcher
{
    private readonly AccountingDbContext _db;

    public ProductMatcher(AccountingDbContext db) { _db = db; }

    private const double VendorAliasScore   = 1.00;
    private const double GlobalAliasScore   = 0.95;
    private const double CodeHitScore       = 0.92;
    private const double TrigramThreshold   = 0.45;
    private const double FuzzyThreshold     = 0.65;
    private const double AutoAcceptThreshold = 0.85;     // UI pre-selects when ≥ this

    // ===================================================================
    // Normalization — must match between caller and the stored alias
    // ===================================================================

    private static readonly (string Pattern, string Replacement)[] UnitReplacements =
    {
        // Volume
        (@"(\d+(?:\.\d+)?)\s*(?:ลิตร|ltr|liter|liters|l)\b",        "$1l"),
        (@"(\d+(?:\.\d+)?)\s*(?:มิลลิลิตร|มล\.?|ml)\b",              "$1ml"),
        (@"(\d+(?:\.\d+)?)\s*(?:cc|ซีซี)\b",                          "$1ml"),
        // Weight
        (@"(\d+(?:\.\d+)?)\s*(?:กิโลกรัม|กก\.?|kg|kilo|kilogram)\b",  "$1kg"),
        (@"(\d+(?:\.\d+)?)\s*(?:กรัม|กรัมs?|g|gram)\b",                "$1g"),
        // Count
        (@"(\d+(?:\.\d+)?)\s*(?:ชิ้น|อัน|pcs?|pieces?|piece)\b",      "$1pcs"),
        (@"(\d+(?:\.\d+)?)\s*(?:โหล|dozen|dz)\b",                     "$1dz"),
        (@"(\d+(?:\.\d+)?)\s*(?:แพ็ค|แพ็ก|pack|pk)\b",                "$1pk"),
        (@"(\d+(?:\.\d+)?)\s*(?:กล่อง|box)\b",                        "$1box"),
        (@"(\d+(?:\.\d+)?)\s*(?:ขวด|bottle|btl)\b",                   "$1btl"),
        (@"(\d+(?:\.\d+)?)\s*(?:กระป๋อง|can)\b",                      "$1can"),
        (@"(\d+(?:\.\d+)?)\s*(?:ถุง|bag)\b",                          "$1bag"),
        // Length
        (@"(\d+(?:\.\d+)?)\s*(?:เซนติเมตร|ซม\.?|cm)\b",              "$1cm"),
        (@"(\d+(?:\.\d+)?)\s*(?:เมตร|m)\b",                           "$1m"),
        (@"(\d+(?:\.\d+)?)\s*(?:นิ้ว|inch|in|\"")",                  "$1in"),
    };

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.ToLowerInvariant();
        // Replace common Thai/English unit phrasings with a canonical form
        // BEFORE stripping punctuation so the unit regex still anchors.
        foreach (var (pat, rep) in UnitReplacements)
            s = Regex.Replace(s, pat, rep);
        // Drop punctuation, keep alphanumerics + Thai range + whitespace
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || (c >= '฀' && c <= '๿'))
                sb.Append(c);
            else
                sb.Append(' ');
        }
        // Token-sort so word order doesn't matter
        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(tokens, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    /// <summary>Pull a unit hint out of the raw description without
    /// touching the existing OCR Quantity field. Used to pre-fill the
    /// new-product Unit dropdown when the OCR didn't include one.</summary>
    public static string? DetectUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.ToLowerInvariant();
        // Order matters — match the most specific first so "มล." doesn't
        // get swallowed by "ม" (which would imply metres).
        var patterns = new (string Pat, string Unit)[]
        {
            (@"\d+(?:\.\d+)?\s*(ลิตร|ltr|liter|l)\b",  "ลิตร"),
            (@"\d+(?:\.\d+)?\s*(มล\.?|ml|cc|ซีซี)\b",  "มล."),
            (@"\d+(?:\.\d+)?\s*(กก\.?|kg)\b",          "กก."),
            (@"\d+(?:\.\d+)?\s*(กรัม|g)\b",            "กรัม"),
            (@"\d+(?:\.\d+)?\s*(โหล|dozen|dz)\b",      "โหล"),
            (@"\d+(?:\.\d+)?\s*(แพ็ค|แพ็ก|pack|pk)\b", "แพ็ค"),
            (@"\d+(?:\.\d+)?\s*(กล่อง|box)\b",         "กล่อง"),
            (@"\d+(?:\.\d+)?\s*(ขวด|bottle|btl)\b",    "ขวด"),
            (@"\d+(?:\.\d+)?\s*(กระป๋อง|can)\b",       "กระป๋อง"),
            (@"\d+(?:\.\d+)?\s*(ถุง|bag)\b",           "ถุง"),
            (@"\d+(?:\.\d+)?\s*(ม\.?|m)\b",            "เมตร"),
            (@"\d+(?:\.\d+)?\s*(นิ้ว|inch|in)\b",      "นิ้ว"),
            (@"\d+(?:\.\d+)?\s*(ชิ้น|piece|pcs?)\b",   "ชิ้น"),
        };
        foreach (var (pat, unit) in patterns)
            if (Regex.IsMatch(s, pat)) return unit;
        return null;
    }

    /// <summary>Pull a quantity out of the description as a backup when
    /// the OCR's structured Quantity field came back null (common for
    /// hand-written or low-confidence scans).</summary>
    public static decimal? DetectQuantity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // First explicit "x N" or "@ N" patterns, then a leading number.
        var m = Regex.Match(raw, @"(?:x|×|จำนวน)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (m.Success && decimal.TryParse(m.Groups[1].Value, out var q1)) return q1;
        m = Regex.Match(raw, @"^\s*(\d+(?:\.\d+)?)\s+");
        if (m.Success && decimal.TryParse(m.Groups[1].Value, out var q2) && q2 <= 9999) return q2;
        return null;
    }

    // ===================================================================
    // Matching
    // ===================================================================

    /// <summary>Find the best Product candidates for one OCR'd description.
    /// Returns up to <paramref name="maxAlternatives"/>+1 results sorted
    /// by confidence desc. May return an empty list, in which case the
    /// caller should offer to create a new product.</summary>
    public async Task<List<ProductMatchCandidate>> MatchAsync(
        Guid companyId,
        string description,
        Guid? vendorContactId,
        int maxAlternatives = 4)
    {
        if (string.IsNullOrWhiteSpace(description)) return new();
        var norm = Normalize(description);
        if (norm.Length == 0) return new();

        var byProductId = new Dictionary<Guid, ProductMatchCandidate>();

        // === Stage 1+2: alias exact hits ===
        var aliasHits = await _db.ProductAliases
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.NormalizedName == norm)
            .Include(a => a.Product)
            .ToListAsync();
        foreach (var a in aliasHits)
        {
            if (a.Product == null || a.Product.IsDeleted || !a.Product.IsActive) continue;
            var score = (vendorContactId.HasValue && a.ContactId == vendorContactId) ? VendorAliasScore
                       : (a.ContactId == null ? GlobalAliasScore : 0.0);
            if (score == 0.0) continue;
            // Promote frequently-confirmed aliases slightly within the same tier.
            score = Math.Min(1.0, score + Math.Min(0.04, 0.005 * a.TimesUsed));
            var reason = score >= VendorAliasScore ? "exact-alias-vendor" : "exact-alias";
            TryAdd(byProductId, a.Product, score, reason);
        }

        // === Stage 3: Code / SKU / Barcode literal hit in description ===
        // Cheap because we only inspect codes we actually have on file.
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.IsActive)
            .Select(p => new { p.Id, p.Code, p.Name, p.SKU, p.Barcode, p.Unit, p.CostPrice, p.CurrentStock })
            .ToListAsync();
        var lowerDesc = description.ToLowerInvariant();
        foreach (var p in products)
        {
            var hit = false;
            if (!string.IsNullOrWhiteSpace(p.Code) && lowerDesc.Contains(p.Code.ToLowerInvariant()) && p.Code.Length >= 3) hit = true;
            else if (!string.IsNullOrWhiteSpace(p.SKU) && lowerDesc.Contains(p.SKU.ToLowerInvariant()) && p.SKU!.Length >= 3) hit = true;
            else if (!string.IsNullOrWhiteSpace(p.Barcode) && lowerDesc.Contains(p.Barcode!.ToLowerInvariant()) && p.Barcode.Length >= 6) hit = true;
            if (hit)
            {
                if (!byProductId.TryGetValue(p.Id, out var existing) || existing.Confidence < CodeHitScore)
                    byProductId[p.Id] = new ProductMatchCandidate(p.Id, p.Code, p.Name, p.Unit, p.CostPrice, p.CurrentStock, CodeHitScore, "code-hit");
            }
        }

        // === Stage 4: pg_trgm SIMILARITY() — runs in DB, indexed ===
        // Compares norm(desc) against Product.Name + ProductAlias.NormalizedName.
        // We pass `norm` as the input because the index is on the normalized
        // alias form, and Product.Name shares enough tokens after lowercasing
        // to still benefit from the gin_trgm_ops index.
        try
        {
            var trigramSql = @"
                SELECT ""Id"", ""Code"", ""Name"", ""SKU"", ""Barcode"", ""Unit"", ""CostPrice"", ""CurrentStock"",
                       GREATEST(
                           similarity(lower(""Name""), {0}),
                           COALESCE((SELECT MAX(similarity(""NormalizedName"", {0}))
                                     FROM ""ProductAliases"" a
                                     WHERE a.""ProductId"" = p.""Id""
                                       AND a.""IsDeleted"" = false), 0)
                       ) AS sim
                FROM ""Products"" p
                WHERE ""CompanyId"" = {1}
                  AND ""IsDeleted"" = false
                  AND ""IsActive"" = true
                ORDER BY sim DESC
                LIMIT 10";
            var trigramHits = await _db.Database
                .SqlQueryRaw<TrigramRow>(trigramSql, norm, companyId)
                .ToListAsync();
            foreach (var r in trigramHits)
            {
                if (r.sim < TrigramThreshold) continue;
                if (!byProductId.TryGetValue(r.Id, out var existing) || existing.Confidence < r.sim)
                    byProductId[r.Id] = new ProductMatchCandidate(r.Id, r.Code, r.Name, r.Unit, r.CostPrice, r.CurrentStock, Math.Min(0.94, r.sim), "trigram");
            }
        }
        catch
        {
            // pg_trgm extension not available (e.g. unit tests on SQLite). Silently skip — Levenshtein still covers.
        }

        // === Stage 5: Levenshtein fallback on top-N untaken products ===
        // Only consider products we haven't already scored well — saves a
        // few hundred string comparisons per scan in large catalogs.
        foreach (var p in products)
        {
            if (byProductId.TryGetValue(p.Id, out var existing) && existing.Confidence >= FuzzyThreshold) continue;
            var pn = Normalize(p.Name);
            if (pn.Length == 0) continue;
            var sim = FuzzyMatcher.Similarity(norm, pn);
            if (sim < FuzzyThreshold) continue;
            if (!byProductId.TryGetValue(p.Id, out existing) || existing.Confidence < sim)
                byProductId[p.Id] = new ProductMatchCandidate(p.Id, p.Code, p.Name, p.Unit, p.CostPrice, p.CurrentStock, sim, "fuzzy");
        }

        return byProductId.Values
            .OrderByDescending(c => c.Confidence)
            .Take(maxAlternatives + 1)
            .ToList();
    }

    private static void TryAdd(Dictionary<Guid, ProductMatchCandidate> dict, Product p, double score, string reason)
    {
        if (!dict.TryGetValue(p.Id, out var existing) || existing.Confidence < score)
            dict[p.Id] = new ProductMatchCandidate(p.Id, p.Code, p.Name, p.Unit, p.CostPrice, p.CurrentStock, score, reason);
    }

    /// <summary>Save an alias linking <paramref name="ocrDescription"/> to
    /// <paramref name="productId"/>. Idempotent — bumps TimesUsed on
    /// repeat. ContactId scopes the alias to a single supplier.</summary>
    public async Task RecordAliasAsync(
        Guid companyId,
        Guid productId,
        string ocrDescription,
        Guid? vendorContactId,
        string userId,
        string source = "user")
    {
        var norm = Normalize(ocrDescription);
        if (norm.Length == 0) return;

        var existing = await _db.ProductAliases.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId &&
            a.ProductId == productId &&
            a.ContactId == vendorContactId &&
            a.NormalizedName == norm &&
            !a.IsDeleted);

        if (existing != null)
        {
            existing.TimesUsed += 1;
            existing.LastUsedAt = DateTime.UtcNow;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = userId;
            return;
        }

        _db.ProductAliases.Add(new ProductAlias
        {
            CompanyId = companyId,
            ProductId = productId,
            ContactId = vendorContactId,
            AliasName = ocrDescription.Length > 500 ? ocrDescription[..500] : ocrDescription,
            NormalizedName = norm.Length > 500 ? norm[..500] : norm,
            TimesUsed = 1,
            LastUsedAt = DateTime.UtcNow,
            Source = source,
            CreatedBy = userId
        });
    }

    /// <summary>Confidence at-or-above which the UI pre-selects the
    /// candidate (vs. leaving the row in "create new" mode).</summary>
    public static double AutoAcceptThresholdValue => AutoAcceptThreshold;

    // EF projection helper for raw SQL — must be public for Npgsql
    public class TrigramRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? SKU { get; set; }
        public string? Barcode { get; set; }
        public string Unit { get; set; } = "";
        public decimal CostPrice { get; set; }
        public decimal CurrentStock { get; set; }
        public double sim { get; set; }
    }
}
