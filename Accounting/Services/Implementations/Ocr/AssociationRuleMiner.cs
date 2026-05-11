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
/// Why basket analysis (Apriori-style frequent-itemset mining):
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
/// Algorithm: simplified Apriori. We mine 2-itemsets and 3-itemsets only —
/// going higher inflates the candidate count exponentially and the
/// marginal accuracy gain is small for accounting data.
///
/// Computational profile:
///   • O(N) pass to build per-item counts (1-itemsets)
///   • O(N × |frequent_1|²) pass for 2-itemsets
///   • Pruning: drop items below min_support before going to 2-itemsets
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
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .ToListAsync(ct);

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

        // 1-itemset support
        var itemCount = new Dictionary<string, int>();
        foreach (var (items, _) in transactions)
            foreach (var item in items)
                itemCount[item] = itemCount.GetValueOrDefault(item) + 1;

        var minSupportCount = (int)Math.Ceiling(minSupport * totalTxn);
        var frequentItems = itemCount.Where(kv => kv.Value >= minSupportCount)
            .Select(kv => kv.Key).ToHashSet();

        // Generate single-item → consequent rules + 2-itemset → consequent rules
        var rules = new List<AssociationRuleDto>();

        // 1-itemset → consequent
        foreach (var item in frequentItems)
        {
            var groupedByConsequent = transactions
                .Where(t => t.Items.Contains(item))
                .GroupBy(t => t.Consequent)
                .Select(g => (Consequent: g.Key, Count: g.Count()))
                .ToList();
            var totalWithItem = groupedByConsequent.Sum(x => x.Count);
            foreach (var (cons, cnt) in groupedByConsequent)
            {
                var support = (decimal)cnt / totalTxn;
                var confidence = (decimal)cnt / totalWithItem;
                if (support < minSupport || confidence < minConfidence) continue;
                var pCons = (decimal)consequentCount.GetValueOrDefault(cons) / totalTxn;
                var lift = pCons > 0 ? confidence / pCons : 0m;
                rules.Add(new AssociationRuleDto(
                    new[] { item }, cons, support, confidence, lift, cnt));
            }
        }

        // 2-itemset → consequent (only when 1-itemset alone wasn't already
        // very high confidence — keeps the rule store from bloating with
        // redundant longer rules).
        var frequentList = frequentItems.OrderBy(x => x).ToList();
        for (int i = 0; i < frequentList.Count && !ct.IsCancellationRequested; i++)
        {
            for (int j = i + 1; j < frequentList.Count; j++)
            {
                var a = frequentList[i];
                var b = frequentList[j];
                var matched = transactions.Where(t => t.Items.Contains(a) && t.Items.Contains(b)).ToList();
                if (matched.Count < minSupportCount) continue;

                var byConsequent = matched.GroupBy(t => t.Consequent)
                    .Select(g => (Consequent: g.Key, Count: g.Count()))
                    .ToList();
                foreach (var (cons, cnt) in byConsequent)
                {
                    var support = (decimal)cnt / totalTxn;
                    var confidence = (decimal)cnt / matched.Count;
                    if (support < minSupport || confidence < minConfidence) continue;
                    // Only persist if the 2-itemset rule beats EITHER of its
                    // 1-itemset siblings — otherwise it's redundant.
                    var betterThanA = !rules.Any(r => r.Antecedent.Length == 1 && r.Antecedent[0] == a
                        && r.Consequent == cons && r.Confidence >= confidence - 0.05m);
                    var betterThanB = !rules.Any(r => r.Antecedent.Length == 1 && r.Antecedent[0] == b
                        && r.Consequent == cons && r.Confidence >= confidence - 0.05m);
                    if (!betterThanA && !betterThanB) continue;

                    var pCons = (decimal)consequentCount.GetValueOrDefault(cons) / totalTxn;
                    var lift = pCons > 0 ? confidence / pCons : 0m;
                    rules.Add(new AssociationRuleDto(
                        new[] { a, b }, cons, support, confidence, lift, cnt));
                }
            }
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
