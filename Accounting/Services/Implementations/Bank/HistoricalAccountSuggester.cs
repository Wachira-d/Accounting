using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// "What account did we usually post this memo to?" — for any unmatched
/// bank txn, finds the most common OFFSETTING GL account from this company's
/// historical reconciled JEs that share the same memo signature + amount
/// bucket. Avoids reading the full JournalEntryLines table per txn by
/// preloading a small dictionary keyed by (sig, bucket).
///
/// Lightweight C: no new schema. Mines `JournalEntryLines` for confirmed
/// (Posted, not reversed) entries that were matched to a bank txn whose
/// description shares this txn's signature. The offsetting line = the line
/// that ISN'T the bank's GL account. Top-3 account codes returned.
///
/// Surfaces hints in `UnmatchedTxn.SuggestedAction` so the operator sees
/// "ปกติ post: 5511 - ค่าไฟ (12 ครั้ง), 5512 - ค่าน้ำ (3 ครั้ง)" instead of
/// just "no candidate".
/// </summary>
public sealed class HistoricalAccountSuggester
{
    public sealed record SuggestionHit(string Code, string Name, int Hits);

    private readonly Dictionary<(string Sig, string Bucket), List<SuggestionHit>> _hits;

    private HistoricalAccountSuggester(
        Dictionary<(string, string), List<SuggestionHit>> hits)
    {
        _hits = hits;
    }

    /// <summary>Mine the company's confirmed-reconciled bank txns for the
    /// offsetting GL account pattern per (memo signature, amount bucket).
    /// Looks back 365 days by default — newer postings are reweighted by recency.</summary>
    public static async Task<HistoricalAccountSuggester> BuildAsync(
        AccountingDbContext db, Guid companyId, Guid bankLinkedAccountId,
        DateTime sinceDate, CancellationToken ct)
    {
        if (bankLinkedAccountId == Guid.Empty)
            return new HistoricalAccountSuggester(new());

        // Pull bank-matched JE ids + their bank txn descriptions in one query.
        // A "matched" bank txn has MatchedJournalEntryId set.
        var rows = await (
            from t in db.BankTransactions.AsNoTracking()
            where t.CompanyId == companyId && !t.IsDeleted
                && t.MatchedJournalEntryId != null
                && t.TransactionDate >= sinceDate
            join je in db.JournalEntries.AsNoTracking()
                on t.MatchedJournalEntryId!.Value equals je.Id
            where je.Status == JournalEntryStatus.Posted && je.ReversedByEntryId == null
            from line in db.JournalEntryLines.AsNoTracking()
                .Where(l => l.JournalEntryId == je.Id && !l.IsDeleted
                            && l.AccountId != bankLinkedAccountId)
            join coa in db.ChartOfAccounts.AsNoTracking()
                on line.AccountId equals coa.Id
            where coa.IsActive && !coa.IsDeleted
            select new
            {
                t.Description, t.Reference, t.Payee, t.Amount,
                coa.AccountCode, coa.AccountName,
            }
        ).Take(5000).ToListAsync(ct);

        var grouped = new Dictionary<(string, string), Dictionary<(string, string), int>>();
        foreach (var r in rows)
        {
            var sig = BankService.ComputeDescriptionSignature(r.Description, r.Reference, r.Payee);
            var bucket = BankService.ComputeAmountBucket(r.Amount);
            var key = (sig, bucket);
            if (!grouped.TryGetValue(key, out var inner))
            {
                inner = new();
                grouped[key] = inner;
            }
            var accountKey = (r.AccountCode, r.AccountName);
            inner[accountKey] = inner.GetValueOrDefault(accountKey) + 1;
        }

        var final = new Dictionary<(string, string), List<SuggestionHit>>();
        foreach (var (k, v) in grouped)
        {
            final[k] = v
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => new SuggestionHit(kv.Key.Item1, kv.Key.Item2, kv.Value))
                .ToList();
        }
        return new HistoricalAccountSuggester(final);
    }

    /// <summary>Top suggestions for this bank txn (top-3, by past usage).
    /// Returns empty when there's no learned history yet.</summary>
    public IReadOnlyList<SuggestionHit> Suggest(string? description, string? reference, string? payee, decimal amount)
    {
        var sig = BankService.ComputeDescriptionSignature(description, reference, payee);
        var bucket = BankService.ComputeAmountBucket(amount);
        return _hits.TryGetValue((sig, bucket), out var v)
            ? v
            : Array.Empty<SuggestionHit>();
    }

    /// <summary>One-line Thai summary of top suggestions, suitable for the
    /// SuggestedAction of an UnmatchedTxn.</summary>
    public string? FormatHint(string? description, string? reference, string? payee, decimal amount)
    {
        var hits = Suggest(description, reference, payee, amount);
        if (hits.Count == 0) return null;
        var parts = hits.Select(h => $"{h.Code} - {h.Name} ({h.Hits} ครั้ง)");
        return "📒 ปกติ post: " + string.Join(", ", parts);
    }
}
