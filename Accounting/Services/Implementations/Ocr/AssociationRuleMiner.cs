using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Market-basket analysis over approved documents to surface frequent
/// (vendor + line-keyword) → account-code association rules. The output
/// powers a system-wide knowledge base that bootstraps new tenants AND
/// catches patterns that single-vendor history alone would miss.
///
/// Why basket analysis (FP-Growth frequent-itemset mining):
///   • Per-vendor learning (OcrVendorIntelligence + OcrCategoryMapping)
///     captures "this vendor → this account" — but it can't generalize.
///   • A new tenant scanning their first PTT receipt would have zero
///     vendor history. Brand-keyword rules in ExpenseCategoryResolver
///     bridge that gap but require manual rule curation.
///   • This miner discovers rules AUTOMATICALLY from approved-document
///     data across all tenants. Example output:
///       {keyword:น้ำมัน} → account:5402   (sup 8%, conf 96%)
///       {brand:HomePro,keyword:วัสดุ} → account:5305 (sup 1%, conf 92%)
///
/// Algorithm: FP-Growth (Han 2000) mines all itemsets at or above the
/// support threshold — O(n log n) on the candidate space vs Apriori's
/// O(n²). We cap itemset size at 2 since marginal accuracy from 3+
/// items is small on accounting baskets but candidate count explodes.
///
/// Computational profile:
///   • O(N) pass to build per-item counts
///   • Single FP-tree build + recursive pattern growth
///   • Designed to run as a background job (admin-triggered), not at
///     scan time. Mining 100k documents finishes in a few seconds.
/// </summary>
public class AssociationRuleMiner
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AssociationRuleMiner> _logger;

    public AssociationRuleMiner(AccountingDbContext db, ILogger<AssociationRuleMiner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record MineResult(
        int TransactionsScanned,
        int RulesDiscovered,
        int RulesPersisted,
        TimeSpan Duration);

    public record AssociationRuleDto(
        string[] Antecedent,        // ["brand:PTT", "kw:น้ำมัน"]
        string Consequent,          // "acct:5402"
        decimal Support,            // 0.0 – 1.0
        decimal Confidence,         // 0.0 – 1.0
        decimal Lift,               // confidence / P(consequent)
        int TransactionCount);

    /// <summary>Mine rules across ALL approved documents in the system.
    /// Stores results in SystemOcrAssociationRules (replacing previous
    /// rules — this is a full re-mine, not incremental).</summary>
    public async Task<MineResult> MineSystemWideAsync(
        decimal minSupport = 0.005m,
        decimal minConfidence = 0.5m,
        int sinceMonths = 24,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var since = DateTime.UtcNow.AddMonths(-sinceMonths);

        // Build the transaction set: each approved document is one "basket"
        // containing the vendor brand tokens + line-keyword tokens, with the
        // consequent being the dominant debit account code.
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted
                && d.CreatedAt >= since
                && (d.Status == Models.Enums.DocumentStatus.Approved
                    || d.Status == Models.Enums.DocumentStatus.Paid))
            // ไม่ Include Contact (required nav + !IsDeleted → INNER JOIN ตัดใบที่
            // contact ถูกลบ = mining set หด). reattach เอง. คิวรีนี้ cross-tenant
            // (system-wide) จึง match ด้วย ContactId ล้วน (GUID ไม่ชนข้าม tenant).
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .ToListAsync(ct);

        var minerContactIds = docs.Where(d => d.ContactId != Guid.Empty)
            .Select(d => d.ContactId).Distinct().ToList();
        var minerContactMap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => minerContactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
        foreach (var d in docs)
            if (minerContactMap.TryGetValue(d.ContactId, out var c)) d.Contact = c;

        var transactions = new List<(HashSet<string> Items, string Consequent)>(docs.Count);
        foreach (var d in docs)
        {
            var dominantAcct = d.Lines
                .Where(l => l.Account != null && !string.IsNullOrEmpty(l.Account.AccountCode))
                .GroupBy(l => l.Account!.AccountCode)
                .OrderByDescending(g => g.Sum(l => l.Amount))
                .Select(g => g.Key)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(dominantAcct)) continue;
            var items = new HashSet<string>();
            // Vendor brand tokens (lowercased)
            if (!string.IsNullOrEmpty(d.Contact?.Name))
                foreach (var t in Tokenize(d.Contact.Name)) items.Add($"brand:{t}");
            // Line keyword tokens
            foreach (var line in d.Lines)
                foreach (var t in Tokenize(line.Description)) items.Add($"kw:{t}");
            if (items.Count == 0) continue;
            transactions.Add((items, $"acct:{dominantAcct}"));
        }

        if (transactions.Count == 0)
        {
            return new MineResult(0, 0, 0, sw.Elapsed);
        }

        // Frequency of each consequent (used for lift)
        var totalTxn = transactions.Count;
        var consequentCount = transactions
            .GroupBy(t => t.Consequent)
            .ToDictionary(g => g.Key, g => g.Count());

        var minSupportCount = (int)Math.Ceiling(minSupport * totalTxn);

        // FP-Growth mines every frequent itemset up to size 2 in a
        // single tree-walk. Replaces the previous O(F²) candidate
        // double-loop. The Count field is the itemset's marginal
        // support — we still need to scan transactions to break it
        // down per consequent (FP-Growth doesn't track that axis).
        var itemsets = FpGrowth.Mine(
            transactions.Select(t => t.Items).ToList(),
            minSupportCount,
            maxItemsetSize: 2);

        var rules = new List<AssociationRuleDto>();
        // Index 1-itemset confidences so we can dedup redundant 2-itemset
        // rules (a 2-itemset rule must beat the BETTER of its 1-itemset
        // siblings — otherwise it's noise on top of the simpler rule).
        var oneItemConf = new Dictionary<(string Item, string Cons), decimal>();

        foreach (var (itemset, _) in itemsets.OrderBy(x => x.Itemset.Count))
        {
            if (itemset.Count > 2) continue;   // enforce cap defensively
            // Walk transactions matching ALL items in the itemset; group
            // by their consequent (account code) to compute per-rule stats.
            var matched = transactions.Where(t => itemset.All(i => t.Items.Contains(i))).ToList();
            if (matched.Count < minSupportCount) continue;

            var byConsequent = matched.GroupBy(t => t.Consequent)
                .Select(g => (Consequent: g.Key, Count: g.Count()))
                .ToList();
            foreach (var (cons, cnt) in byConsequent)
            {
                var support = (decimal)cnt / totalTxn;
                var confidence = (decimal)cnt / matched.Count;
                if (support < minSupport || confidence < minConfidence) continue;

                if (itemset.Count == 2)
                {
                    // Dedup: skip if a 1-itemset child already explains
                    // this consequent at within-5pp confidence.
                    var skip = itemset.Any(item =>
                        oneItemConf.TryGetValue((item, cons), out var single)
                        && single >= confidence - 0.05m);
                    if (skip) continue;
                }

                var pCons = (decimal)consequentCount.GetValueOrDefault(cons) / totalTxn;
                var lift = pCons > 0 ? confidence / pCons : 0m;
                var ante = itemset.OrderBy(x => x).ToArray();
                rules.Add(new AssociationRuleDto(ante, cons, support, confidence, lift, cnt));

                if (itemset.Count == 1)
                    oneItemConf[(ante[0], cons)] = confidence;
            }
            if (ct.IsCancellationRequested) break;
        }

        // Persist — wipe + replace (we re-mined from scratch)
        var existing = await _db.SystemOcrAssociationRules.IgnoreQueryFilters().ToListAsync(ct);
        _db.SystemOcrAssociationRules.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);

        foreach (var r in rules.OrderByDescending(x => x.Lift).Take(2000))
        {
            _db.SystemOcrAssociationRules.Add(new SystemOcrAssociationRule
            {
                AntecedentJson = System.Text.Json.JsonSerializer.Serialize(r.Antecedent),
                Consequent = r.Consequent,
                Support = r.Support,
                Confidence = r.Confidence,
                Lift = r.Lift,
                TransactionCount = r.TransactionCount,
                MinedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync(ct);
        sw.Stop();

        _logger.LogInformation("Association mining: {N} txn → {R} rules in {T}",
            totalTxn, rules.Count, sw.Elapsed);
        return new MineResult(totalTxn, rules.Count, Math.Min(rules.Count, 2000), sw.Elapsed);
    }

    /// <summary>At scan time, given a list of tokens from the current
    /// document (vendor name + line keywords), return the best matching
    /// association rule. Higher Lift = more discriminative.</summary>
    public async Task<AssociationRuleDto?> FindBestMatchAsync(string? vendorName, IEnumerable<string?>? lineDescriptions)
    {
        var tokens = new HashSet<string>();
        if (!string.IsNullOrEmpty(vendorName))
            foreach (var t in Tokenize(vendorName)) tokens.Add($"brand:{t}");
        if (lineDescriptions != null)
            foreach (var desc in lineDescriptions)
                foreach (var t in Tokenize(desc)) tokens.Add($"kw:{t}");

        if (tokens.Count == 0) return null;

        // Pull all rules — system-wide table is bounded (≤2000), so an
        // in-memory match is cheaper than building a SQL-side any() clause.
        var rules = await _db.SystemOcrAssociationRules.AsNoTracking().ToListAsync();
        AssociationRuleDto? best = null;
        decimal bestScore = 0;
        foreach (var r in rules)
        {
            var ante = ParseAntecedent(r.AntecedentJson);
            if (ante == null || ante.Length == 0) continue;
            if (!ante.All(a => tokens.Contains(a))) continue;
            // Score: lift × log(transaction count) — strong + well-supported wins
            var score = r.Lift * (decimal)Math.Log(Math.Max(r.TransactionCount, 2));
            if (score > bestScore)
            {
                bestScore = score;
                best = new AssociationRuleDto(
                    ante, r.Consequent, r.Support, r.Confidence, r.Lift, r.TransactionCount);
            }
        }
        return best;
    }

    private static string[]? ParseAntecedent(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json); }
        catch { return null; }
    }

    // Lower-cases, strips punctuation, drops short/digit-only tokens.
    private static IEnumerable<string> Tokenize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var raw = s.ToLowerInvariant()
            .Replace(",", " ").Replace(".", " ").Replace("-", " ")
            .Replace("(", " ").Replace(")", " ").Replace("/", " ")
            .Replace(":", " ").Replace(";", " ");
        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2) continue;
            if (part.All(char.IsDigit)) continue;
            yield return part;
        }
    }
}
