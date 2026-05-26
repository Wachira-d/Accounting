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

    // ── Boundary fix for Thai units ───────────────────────────────────
    // Thai code-point chars are NOT word-chars for .NET regex, so `\b`
    // fails between digit and Thai letter — e.g. "1ลิตร" wouldn't match
    // `\d+\s*ลิตร\b`. Replacing with a negative lookahead that excludes
    // both Thai letters AND ASCII letters covers every case we care about
    // ("1ลิตร" with no space, "1ลิตรน้ำ" where Thai letter follows, "1l"
    // at end, "1l foo" with space).
    private const string ThaiBoundary = @"(?![฀-๿a-zA-Z])";

    private static readonly (string Pattern, string Replacement)[] UnitReplacements =
    {
        // Volume
        (@"(\d+(?:\.\d+)?)\s*(?:ลิตร|ltr|liter|liters|l)" + ThaiBoundary,        "$1l"),
        (@"(\d+(?:\.\d+)?)\s*(?:มิลลิลิตร|มล\.?|ml)" + ThaiBoundary,              "$1ml"),
        (@"(\d+(?:\.\d+)?)\s*(?:cc|ซีซี)" + ThaiBoundary,                          "$1ml"),
        // Weight
        (@"(\d+(?:\.\d+)?)\s*(?:กิโลกรัม|กก\.?|kg|kilo|kilogram)" + ThaiBoundary,  "$1kg"),
        (@"(\d+(?:\.\d+)?)\s*(?:กรัม|กรัมs?|g|gram)" + ThaiBoundary,                "$1g"),
        // Count
        (@"(\d+(?:\.\d+)?)\s*(?:ชิ้น|อัน|pcs?|pieces?|piece)" + ThaiBoundary,      "$1pcs"),
        (@"(\d+(?:\.\d+)?)\s*(?:โหล|dozen|dz)" + ThaiBoundary,                     "$1dz"),
        (@"(\d+(?:\.\d+)?)\s*(?:แพ็ค|แพ็ก|pack|pk)" + ThaiBoundary,                "$1pk"),
        (@"(\d+(?:\.\d+)?)\s*(?:กล่อง|box)" + ThaiBoundary,                        "$1box"),
        (@"(\d+(?:\.\d+)?)\s*(?:ขวด|bottle|btl)" + ThaiBoundary,                   "$1btl"),
        (@"(\d+(?:\.\d+)?)\s*(?:กระป๋อง|can)" + ThaiBoundary,                      "$1can"),
        (@"(\d+(?:\.\d+)?)\s*(?:ถุง|bag)" + ThaiBoundary,                          "$1bag"),
        (@"(\d+(?:\.\d+)?)\s*(?:ลัง|crate|carton|ctn)" + ThaiBoundary,             "$1ctn"),
        (@"(\d+(?:\.\d+)?)\s*(?:ห่อ|wrap)" + ThaiBoundary,                         "$1wrap"),
        // Length
        (@"(\d+(?:\.\d+)?)\s*(?:เซนติเมตร|ซม\.?|cm)" + ThaiBoundary,              "$1cm"),
        (@"(\d+(?:\.\d+)?)\s*(?:เมตร|m)" + ThaiBoundary,                           "$1m"),
        (@"(\d+(?:\.\d+)?)\s*(?:นิ้ว|inch|in|\"")",                                "$1in"),
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
            (@"\d+(?:\.\d+)?\s*(ลิตร|ltr|liter|l)"  + ThaiBoundary, "ลิตร"),
            (@"\d+(?:\.\d+)?\s*(มล\.?|ml|cc|ซีซี)"  + ThaiBoundary, "มล."),
            (@"\d+(?:\.\d+)?\s*(กก\.?|kg)"          + ThaiBoundary, "กก."),
            (@"\d+(?:\.\d+)?\s*(กรัม|g)"            + ThaiBoundary, "กรัม"),
            (@"\d+(?:\.\d+)?\s*(โหล|dozen|dz)"      + ThaiBoundary, "โหล"),
            (@"\d+(?:\.\d+)?\s*(แพ็ค|แพ็ก|pack|pk)" + ThaiBoundary, "แพ็ค"),
            (@"\d+(?:\.\d+)?\s*(กล่อง|box)"         + ThaiBoundary, "กล่อง"),
            (@"\d+(?:\.\d+)?\s*(ขวด|bottle|btl)"    + ThaiBoundary, "ขวด"),
            (@"\d+(?:\.\d+)?\s*(กระป๋อง|can)"       + ThaiBoundary, "กระป๋อง"),
            (@"\d+(?:\.\d+)?\s*(ถุง|bag)"           + ThaiBoundary, "ถุง"),
            (@"\d+(?:\.\d+)?\s*(ลัง|crate|carton|ctn)" + ThaiBoundary, "ลัง"),
            (@"\d+(?:\.\d+)?\s*(ห่อ|wrap)"          + ThaiBoundary, "ห่อ"),
            (@"\d+(?:\.\d+)?\s*(ม\.?|m)"            + ThaiBoundary, "เมตร"),
            (@"\d+(?:\.\d+)?\s*(นิ้ว|inch|in)"      + ThaiBoundary, "นิ้ว"),
            (@"\d+(?:\.\d+)?\s*(ชิ้น|piece|pcs?)"   + ThaiBoundary, "ชิ้น"),
        };
        foreach (var (pat, unit) in patterns)
            if (Regex.IsMatch(s, pat)) return unit;
        return null;
    }

    // ── Brand-token extraction ────────────────────────────────────────
    // Distinguishes "Pepsi 325ml" from "Coke 325ml" so trigram similarity
    // on the shared "325ml" doesn't accidentally fuse them. A brand token
    // here is any standalone uppercase ASCII alpha run of length ≥ 2 OR
    // a capitalized first-letter word inside the description. We bias
    // matches downward when the description names a brand the candidate
    // doesn't share, so "Pepsi" never matches "Coke" by accident.
    private static readonly Regex BrandTokenRegex = new(@"\b([A-Z]{2,}|[A-Z][a-z]{2,})\b", RegexOptions.Compiled);

    public static HashSet<string> ExtractBrandTokens(string? raw)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return tokens;
        foreach (Match m in BrandTokenRegex.Matches(raw))
        {
            var t = m.Value.ToUpperInvariant();
            // Filter out common units / generic words that look like brands
            if (t.Length <= 1) continue;
            if (t is "ML" or "KG" or "CM" or "MM" or "PCS" or "PC" or "BOX" or "PK"
                or "PACK" or "CAN" or "BAG" or "BOTTLE" or "BTL" or "LTR" or "OZ"
                or "FT" or "VAT" or "PHP" or "USD" or "THB" or "VND" or "EUR" or "JPY") continue;
            tokens.Add(t);
        }
        return tokens;
    }

    // ── Vendor purchase history bias ──────────────────────────────────
    // Promote products this same supplier has previously sold us. Linked
    // through DocumentLine.ProductCode → Product.Code on past purchase-
    // side documents (PurchaseInvoice / Expense / PaymentVoucher /
    // PurchaseOrder). Boost is capped so a deterministic stage-1 alias
    // still wins.
    private const double VendorHistoryBoost = 0.10;
    private const double MaxBoostedScore = 0.94;

    private async Task<HashSet<Guid>> GetVendorHistoryProductIdsAsync(Guid companyId, Guid? vendorContactId)
    {
        if (!vendorContactId.HasValue) return new();
        try
        {
            var sql = @"
                SELECT DISTINCT p.""Id""
                FROM ""DocumentLines"" dl
                JOIN ""Documents"" d ON d.""Id"" = dl.""DocumentId"" AND d.""IsDeleted"" = false
                JOIN ""Products"" p ON p.""Code"" = dl.""ProductCode""
                                   AND p.""CompanyId"" = d.""CompanyId""
                                   AND p.""IsDeleted"" = false
                WHERE d.""CompanyId"" = {0}
                  AND d.""ContactId"" = {1}
                  AND d.""DocumentType"" IN (7, 8, 9, 13)
                  AND dl.""ProductCode"" IS NOT NULL
                  AND dl.""IsDeleted"" = false";
            var ids = await _db.Database
                .SqlQueryRaw<VendorHistoryRow>(sql, companyId, vendorContactId.Value)
                .ToListAsync();
            return ids.Select(x => x.Id).ToHashSet();
        }
        catch
        {
            return new();
        }
    }

    public class VendorHistoryRow { public Guid Id { get; set; } }

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
        var descBrands = ExtractBrandTokens(description);
        var vendorHistoryIds = await GetVendorHistoryProductIdsAsync(companyId, vendorContactId);

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

        // ── Brand-protection + vendor-history rescore ──────────────────
        // Apply AFTER all sourcing stages so it works against the union
        // of candidates. Stage-1 exact-alias hits (confidence ≥ 1.0) are
        // never demoted — the user already taught us this mapping.
        var rescored = new List<ProductMatchCandidate>();
        foreach (var c in byProductId.Values)
        {
            var score = c.Confidence;
            var reason = c.Reason;

            // Vendor purchase-history bias — small boost for products
            // this vendor has actually sold us before.
            if (vendorHistoryIds.Contains(c.ProductId) && score < VendorAliasScore)
            {
                score = Math.Min(MaxBoostedScore, score + VendorHistoryBoost);
                reason = reason + "+vendor-hist";
            }

            // Brand-token protection — when the OCR'd description names
            // a brand (e.g. "Pepsi", "Toyota", "A"), the candidate's
            // name MUST contain at least one of those tokens. Otherwise
            // we likely have two unrelated products that just share a
            // size like "325ml". Stage-1 alias hits AND deterministic
            // code/SKU hits are exempt — those are explicit signals from
            // the user / the receipt and shouldn't be demoted by fuzzy
            // brand heuristics.
            var isDeterministic = reason.StartsWith("exact-alias") || reason.StartsWith("code-hit");
            if (descBrands.Count > 0 && !isDeterministic)
            {
                var candBrands = ExtractBrandTokens(c.Name);
                // Only demote when BOTH sides expose English brand tokens.
                // Pure-Thai candidates (e.g. "เป๊ปซี่ 325ml") would otherwise
                // be wrongly demoted against a desc that says "Pepsi 325ml"
                // — same brand, different script.
                if (candBrands.Count > 0 && !candBrands.Overlaps(descBrands))
                {
                    score *= 0.6;
                    reason = reason + "-brand-mismatch";
                }
            }

            rescored.Add(c with { Confidence = score, Reason = reason });
        }

        return rescored
            .OrderByDescending(c => c.Confidence)
            .Take(maxAlternatives + 1)
            .ToList();
    }

    // ====================================================================
    // History backfill — turn previously-entered DocumentLines into seed
    // aliases so the matcher works from day-one without forcing the user
    // to "teach" every wording one at a time.
    //
    // Idempotent: alias insert uses RecordAliasAsync (TimesUsed += 1 on
    // dup). Cheap by default (caps at 5000 rows) — caller passes a higher
    // limit for one-off migration runs. Designed to be triggered lazily
    // on the first stock-preview call from the OCR review modal.
    // ====================================================================
    public async Task<int> BackfillAliasesFromHistoryAsync(
        Guid companyId, string userId, int maxLines = 5000)
    {
        // Pull purchase-side DocumentLines with both a Description and a
        // ProductCode → resolvable product, joined to the document so we
        // know the vendor (ContactId).
        var rows = await _db.Database.SqlQueryRaw<HistoryRow>(@"
            SELECT p.""Id"" AS ProductId, d.""ContactId"" AS ContactId, dl.""Description"" AS Description
            FROM ""DocumentLines"" dl
            JOIN ""Documents"" d ON d.""Id"" = dl.""DocumentId"" AND d.""IsDeleted"" = false
            JOIN ""Products"" p ON p.""Code"" = dl.""ProductCode""
                               AND p.""CompanyId"" = d.""CompanyId""
                               AND p.""IsDeleted"" = false
            WHERE d.""CompanyId"" = {0}
              AND d.""DocumentType"" IN (7, 8, 9, 13)
              AND dl.""ProductCode"" IS NOT NULL
              AND dl.""Description"" IS NOT NULL
              AND dl.""IsDeleted"" = false
            LIMIT {1}", companyId, maxLines).ToListAsync();

        var seen = new HashSet<string>();
        var count = 0;
        foreach (var r in rows)
        {
            var key = $"{r.ProductId}|{r.ContactId}|{Normalize(r.Description)}";
            if (!seen.Add(key)) continue;  // dedupe within this run
            await RecordAliasAsync(companyId, r.ProductId, r.Description, r.ContactId, userId, source: "import");
            count++;
        }
        if (count > 0) await _db.SaveChangesAsync();
        return count;
    }

    public class HistoryRow
    {
        public Guid ProductId { get; set; }
        public Guid? ContactId { get; set; }
        public string Description { get; set; } = "";
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
