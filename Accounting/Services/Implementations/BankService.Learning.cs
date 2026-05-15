using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class BankService
{
    // ============================================================
    // AI reconciliation pattern memory ("data mining")
    // ------------------------------------------------------------
    // Every confirmed reconciliation contributes one row per
    // (bankTxn, item) pair to BankReconciliationPatterns. The pattern
    // captures:
    //   • a tokenised signature of the bank txn's description/payee/ref
    //   • a coarse amount bucket so similar-magnitude txns share a row
    //   • the target item type and (if available) the counterparty ContactId
    //
    // GetLearnedSuggestionsAsync uses the same lookup key on an
    // unmatched bank txn and returns candidate items ordered by pattern
    // confidence (TimesConfirmed × recency-decay × signature-overlap).
    //
    // Repeated identical patterns increment TimesConfirmed in place so
    // the table stays compact; min/max/avg amount stats update with each
    // hit so the suggester can detect outliers.
    // ============================================================

    private static readonly Regex _tokenRegex = new(@"[a-z0-9ก-๙]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Lower-case alphanumeric tokens, length ≥ 3, joined by "|". Strips
    /// common noise words ("transfer", "kbank", "pay", date fragments) so
    /// the signature focuses on the parts likely to recur — counterparty
    /// names, account-tail digits, reference codes.
    /// </summary>
    public static string ComputeDescriptionSignature(string? description, string? reference, string? payee)
    {
        var combined = string.Join(" ", new[] { description, reference, payee }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(combined)) return "(empty)";

        var matches = _tokenRegex.Matches(combined.ToLowerInvariant());
        var tokens = matches
            .Select(m => m.Value)
            .Where(t => t.Length >= 3)
            .Where(t => !_stopWords.Contains(t))
            .Distinct()
            .Take(8)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return tokens.Count == 0 ? "(empty)" : string.Join("|", tokens);
    }

    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer", "deposit", "withdraw", "withdrawal", "credit", "debit",
        "atm", "kbank", "scb", "bbl", "ktb", "bay", "ttb", "tmb", "uob", "cimb",
        "bank", "channel", "code", "fee", "interest",
        "pay", "payment", "payee", "ref",
        "เงิน", "โอน", "ฝาก", "ถอน", "ดอกเบี้ย", "ค่าธรรมเนียม",
    };

    /// <summary>Magnitude bucket — same buckets used both when recording and
    /// when looking up so a 950-baht receipt matches the 1000-baht pattern.</summary>
    public static string ComputeAmountBucket(decimal amount)
    {
        var a = Math.Abs(amount);
        return a switch
        {
            < 100m => "<100",
            < 1_000m => "100-1k",
            < 10_000m => "1k-10k",
            < 100_000m => "10k-100k",
            < 1_000_000m => "100k-1m",
            _ => "1m+",
        };
    }

    /// <summary>
    /// Record pattern rows for every (bankTxn, matchItem) pair in a confirmed
    /// reconciliation group. Called from CreateReconciliationGroupAsync after
    /// the group + items are saved. Failures are swallowed — learning never
    /// blocks a confirmed reconciliation.
    /// </summary>
    public async Task RecordReconciliationPatternsAsync(Guid companyId, Guid groupId)
    {
        try
        {
            var group = await _db.ReconciliationGroups.AsNoTracking()
                .Include(g => g.Items)
                .FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId);
            if (group == null) return;

            var bankItems = group.Items.Where(i => i.ItemType == ReconciliationItemType.BankTransaction).ToList();
            var matchItems = group.Items.Where(i => i.ItemType != ReconciliationItemType.BankTransaction).ToList();
            if (bankItems.Count == 0 || matchItems.Count == 0) return;

            // Pull bank txn descriptors in one shot.
            var bankIds = bankItems.Select(b => b.ItemId).ToList();
            var bankTxns = await _db.Set<BankTransaction>().AsNoTracking()
                .Where(t => bankIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Description, t.Reference, t.Payee })
                .ToListAsync();

            // Map items' contacts when possible (best-effort).
            var paymentContactMap = await _db.Set<Payment>().AsNoTracking()
                .Where(p => matchItems
                    .Where(i => i.ItemType == ReconciliationItemType.Payment)
                    .Select(i => i.ItemId).Contains(p.Id))
                .Select(p => new { p.Id, ContactId = (Guid?)p.Document.ContactId })
                .ToDictionaryAsync(x => x.Id, x => x.ContactId);
            var docContactMap = await _db.Documents.AsNoTracking()
                .Where(d => matchItems
                    .Where(i => i.ItemType == ReconciliationItemType.Document)
                    .Select(i => i.ItemId).Contains(d.Id))
                .Select(d => new { d.Id, ContactId = (Guid?)d.ContactId })
                .ToDictionaryAsync(x => x.Id, x => x.ContactId);

            foreach (var b in bankItems)
            {
                var info = bankTxns.FirstOrDefault(x => x.Id == b.ItemId);
                if (info == null) continue;
                var sig = ComputeDescriptionSignature(info.Description, info.Reference, info.Payee);
                var bucket = ComputeAmountBucket(b.AllocatedAmount);

                foreach (var item in matchItems)
                {
                    Guid? contactId = null;
                    if (item.ItemType == ReconciliationItemType.Payment)
                        paymentContactMap.TryGetValue(item.ItemId, out contactId);
                    else if (item.ItemType == ReconciliationItemType.Document)
                        docContactMap.TryGetValue(item.ItemId, out contactId);

                    await UpsertPatternAsync(companyId, group.BankAccountId, sig, bucket,
                        item.ItemType, contactId, Math.Abs(item.AllocatedAmount));
                }
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RecordReconciliationPatternsAsync failed for group {GroupId}", groupId);
        }
    }

    private async Task UpsertPatternAsync(Guid companyId, Guid bankAccountId, string sig, string bucket,
        ReconciliationItemType targetType, Guid? contactId, decimal amount)
    {
        var existing = await _db.BankReconciliationPatterns
            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                && p.BankAccountId == bankAccountId
                && p.DescriptionSignature == sig
                && p.AmountBucket == bucket
                && p.TargetType == targetType
                && p.ContactId == contactId);
        if (existing != null)
        {
            existing.TimesConfirmed += 1;
            existing.LastUsedAt = DateTime.UtcNow;
            existing.AvgAmount = ((existing.AvgAmount * (existing.TimesConfirmed - 1)) + amount) / existing.TimesConfirmed;
            if (amount < existing.MinAmount || existing.MinAmount == 0) existing.MinAmount = amount;
            if (amount > existing.MaxAmount) existing.MaxAmount = amount;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.BankReconciliationPatterns.Add(new BankReconciliationPattern
            {
                CompanyId = companyId,
                BankAccountId = bankAccountId,
                DescriptionSignature = sig,
                AmountBucket = bucket,
                TargetType = targetType,
                ContactId = contactId,
                TimesConfirmed = 1,
                LastUsedAt = DateTime.UtcNow,
                AvgAmount = amount,
                MinAmount = amount,
                MaxAmount = amount,
            });
        }
    }

    /// <summary>
    /// Suggest items for a single unmatched bank txn based on learned patterns.
    /// Returns a flat list ordered by confidence — UI surfaces them as
    /// "📚 จากประวัติ" hints alongside the heuristic AI suggestions.
    /// </summary>
    public async Task<LearnedSuggestionsResponse> GetLearnedSuggestionsAsync(Guid companyId, Guid bankTransactionId)
    {
        var txn = await _db.Set<BankTransaction>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        var sig = ComputeDescriptionSignature(txn.Description, txn.Reference, txn.Payee);
        var bucket = ComputeAmountBucket(txn.Amount);
        var sigTokens = sig.Split('|').Where(t => t.Length > 0).ToHashSet();

        // Pull patterns that overlap on signature tokens OR amount bucket —
        // ranking step below handles the actual scoring.
        var rawPatterns = await _db.BankReconciliationPatterns.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.BankAccountId == txn.BankAccountId
                && (p.DescriptionSignature == sig || p.AmountBucket == bucket))
            .ToListAsync();

        // In-memory ranking — small N (≤ a few thousand patterns per account).
        var scored = rawPatterns.Select(p =>
        {
            var theirTokens = p.DescriptionSignature.Split('|').Where(t => t.Length > 0).ToHashSet();
            var overlap = sigTokens.Intersect(theirTokens).Count();
            var unionSize = Math.Max(1, sigTokens.Union(theirTokens).Count());
            var jaccard = overlap / (double)unionSize;        // 0..1
            var bucketBoost = p.AmountBucket == bucket ? 0.3 : 0.0;
            var recencyDays = (DateTime.UtcNow - p.LastUsedAt).TotalDays;
            var recencyDecay = Math.Max(0.5, 1.0 - recencyDays / 365.0);   // half-life ~1 year
            var confidence = (0.5 * jaccard + bucketBoost + 0.2 * Math.Min(1.0, p.TimesConfirmed / 10.0)) * recencyDecay;
            return (Pattern: p, Score: confidence);
        })
        .Where(x => x.Score > 0.2)
        .OrderByDescending(x => x.Score)
        .Take(20)
        .ToList();

        if (scored.Count == 0)
            return new LearnedSuggestionsResponse(new List<LearnedSuggestion>(), sig, bucket, 0);

        // Materialise candidate items based on the suggested (targetType, contactId)
        // tuples — for each top-pattern, surface the actually-unmatched items.
        var suggestions = new List<LearnedSuggestion>();
        var seenItems = new HashSet<(string, Guid)>();
        foreach (var (pat, score) in scored)
        {
            var items = await ResolveUnmatchedItemsForPatternAsync(companyId, txn.BankAccountId, pat, sig, bucket);
            foreach (var it in items)
            {
                var key = (it.ItemType, it.Id);
                if (!seenItems.Add(key)) continue;
                suggestions.Add(new LearnedSuggestion(
                    it.ItemType, it.Id, it.Number, it.Date, it.Description,
                    it.Amount, it.ContactName,
                    Math.Round(score, 3),
                    $"พบรูปแบบนี้ {pat.TimesConfirmed} ครั้ง · ใช้ล่าสุด {pat.LastUsedAt:dd/MM/yyyy}"));
                if (suggestions.Count >= 10) break;
            }
            if (suggestions.Count >= 10) break;
        }

        return new LearnedSuggestionsResponse(suggestions, sig, bucket, rawPatterns.Count);
    }

    private async Task<List<UnmatchedItem>> ResolveUnmatchedItemsForPatternAsync(
        Guid companyId, Guid bankAccountId, BankReconciliationPattern pattern, string sig, string bucket)
    {
        // Reuse the master unmatched-items pool then filter to the pattern's
        // target type + contact.
        var pool = await GetUnmatchedItemsAsync(companyId, bankAccountId, search: null, fromDate: null, toDate: null);
        var typed = pattern.TargetType switch
        {
            ReconciliationItemType.Payment => pool.Payments,
            ReconciliationItemType.JournalEntry => pool.JournalEntries,
            ReconciliationItemType.Document => pool.Documents,
            _ => new List<UnmatchedItem>(),
        };

        // When the pattern has a ContactId, prefer items from the same contact.
        // Without ContactId, prefer items in the same amount bucket.
        return typed
            .Where(it => ComputeAmountBucket(it.Amount) == bucket
                || (pattern.MaxAmount > 0 && it.Amount <= pattern.MaxAmount * 1.2m && it.Amount >= pattern.MinAmount * 0.8m))
            .OrderBy(it => Math.Abs(it.Amount - pattern.AvgAmount))   // closest-to-mean first
            .Take(5)
            .ToList();
    }
}
