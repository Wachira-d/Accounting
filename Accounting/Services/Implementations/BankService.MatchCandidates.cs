using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class BankService
{
    /// <summary>
    /// Find ranked match candidates (Payments + JournalEntries) for a bank transaction.
    /// Used by the manual reconciliation picker UI so the user doesn't have to type a UUID.
    ///
    /// Scoring (0–100):
    ///  - Amount equality:  exact = 60, ±0.5% = 50, ±1% = 40, ±2% = 25, else 0
    ///  - Date proximity:   same day = 30, ±1d = 25, ±3d = 20, ±7d = 15, ±14d = 8, ±30d = 3
    ///  - Reference / desc: keyword overlap = +10
    /// </summary>
    public async Task<MatchCandidatesResponse> GetMatchCandidatesAsync(Guid companyId, Guid bankTransactionId)
    {
        var bankTxn = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        var bankAmount = bankTxn.Amount;
        var bankDate = bankTxn.TransactionDate.Date;
        var bankDescLower = (bankTxn.Description ?? "").ToLowerInvariant();
        var bankRefLower = (bankTxn.Reference ?? "").ToLowerInvariant();

        // ===== Candidate Payments =====
        // Pull unmatched payments within ±60 days, sorted by best amount match first.
        var dateMin = bankDate.AddDays(-60);
        var dateMax = bankDate.AddDays(60);

        // Match-direction filter: deposit txn → likely receiving customer payment;
        // withdrawal txn → likely supplier payment / refund.
        // We don't enforce direction strictly because it can vary.
        // Collect all "already matched" IDs from sibling bank transactions:
        //   1. Single matches (MatchedPaymentId / MatchedJournalEntryId)
        //   2. Multi-matches (MatchedEntryIdsJson contains a list of IDs)
        // We must exclude both so users don't see candidates that were already used
        // as part of an aggregated match on another bank transaction.
        var siblingTxns = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.Id != bankTransactionId)
            .Select(t => new { t.MatchedPaymentId, t.MatchedJournalEntryId, t.MatchedEntryIdsJson })
            .ToListAsync();

        var matchedPaymentIds = new HashSet<Guid>(
            siblingTxns.Where(t => t.MatchedPaymentId.HasValue).Select(t => t.MatchedPaymentId!.Value));
        var matchedJeIdsHash = new HashSet<Guid>(
            siblingTxns.Where(t => t.MatchedJournalEntryId.HasValue).Select(t => t.MatchedJournalEntryId!.Value));

        // Pull IDs from all multi-match JSON arrays. Each is either a payment or JE ID;
        // we don't know which type without checking, so add to both sets — the candidate
        // queries below will only filter the relevant set.
        foreach (var t in siblingTxns)
        {
            if (string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson)) continue;
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson);
                if (ids == null) continue;
                foreach (var id in ids)
                {
                    matchedPaymentIds.Add(id);
                    matchedJeIdsHash.Add(id);
                }
            }
            catch { /* malformed JSON — ignore */ }
        }

        var paymentIdSet = matchedPaymentIds.ToList();
        var paymentCandidates = await _db.Set<Payment>()
            .Include(p => p.Document).ThenInclude(d => d.Contact)
            .Where(p => p.CompanyId == companyId
                     && p.PaymentDate >= dateMin && p.PaymentDate <= dateMax
                     && !paymentIdSet.Contains(p.Id))
            .ToListAsync();

        var rankedPayments = paymentCandidates
            .Select(p =>
            {
                var amountDiff = Math.Abs(p.Amount - bankAmount);
                var dateDiff = (int)Math.Abs((p.PaymentDate.Date - bankDate).TotalDays);
                var (score, reason) = ScoreCandidate(p.Amount, bankAmount, p.PaymentDate.Date, bankDate,
                    p.Reference, p.Notes, p.Document?.DocumentNumber, p.Document?.Contact?.Name,
                    bankDescLower, bankRefLower);

                return new MatchCandidate(
                    Type: "Payment",
                    Id: p.Id,
                    Number: p.PaymentNumber,
                    Date: p.PaymentDate,
                    Amount: p.Amount,
                    Description: $"{p.Document?.DocumentNumber ?? ""} {p.Document?.Contact?.Name ?? ""}".Trim(),
                    CounterpartyName: p.Document?.Contact?.Name,
                    Reference: p.Reference,
                    DateDiffDays: dateDiff,
                    AmountDiff: amountDiff,
                    Score: score,
                    ScoreReason: reason);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DateDiffDays)
            .Take(50)
            .ToList();

        // ===== Candidate Journal Entries =====
        // Use the same matchedJeIdsHash assembled above (includes single-matched IDs +
        // every ID in any sibling transaction's MatchedEntryIdsJson list).
        var jeIdSet = matchedJeIdsHash.ToList();
        var jeCandidates = await _db.Set<JournalEntry>()
            .Where(j => j.CompanyId == companyId
                     && j.EntryDate >= dateMin && j.EntryDate <= dateMax
                     && j.Status != JournalEntryStatus.Voided
                     && !jeIdSet.Contains(j.Id))
            .ToListAsync();

        var rankedJournals = jeCandidates
            .Select(j =>
            {
                // Use TotalDebit (= TotalCredit) as the JE amount
                var jeAmount = j.TotalDebit;
                var amountDiff = Math.Abs(jeAmount - bankAmount);
                var dateDiff = (int)Math.Abs((j.EntryDate.Date - bankDate).TotalDays);
                var (score, reason) = ScoreCandidate(jeAmount, bankAmount, j.EntryDate.Date, bankDate,
                    j.Reference, j.Description, null, null,
                    bankDescLower, bankRefLower);

                return new MatchCandidate(
                    Type: "JournalEntry",
                    Id: j.Id,
                    Number: j.EntryNumber,
                    Date: j.EntryDate,
                    Amount: jeAmount,
                    Description: j.Description ?? "",
                    CounterpartyName: null,
                    Reference: j.Reference,
                    DateDiffDays: dateDiff,
                    AmountDiff: amountDiff,
                    Score: score,
                    ScoreReason: reason);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DateDiffDays)
            .Take(50)
            .ToList();

        return new MatchCandidatesResponse(
            BankTransactionId: bankTxn.Id,
            BankTransactionDate: bankTxn.TransactionDate,
            BankTransactionAmount: bankTxn.Amount,
            BankTransactionDescription: bankTxn.Description ?? "",
            Payments: rankedPayments,
            JournalEntries: rankedJournals);
    }

    private static (int score, string reason) ScoreCandidate(
        decimal candidateAmount, decimal bankAmount,
        DateTime candidateDate, DateTime bankDate,
        string? candidateRef, string? candidateNotes,
        string? candidateDocNumber, string? candidateName,
        string bankDescLower, string bankRefLower)
    {
        int score = 0;
        var reasons = new List<string>();

        // === Amount (max 60) ===
        var amountDiff = Math.Abs(candidateAmount - bankAmount);
        if (bankAmount > 0)
        {
            var pctDiff = amountDiff / bankAmount;
            if (amountDiff < 0.005m) { score += 60; reasons.Add("ยอดตรงเป๊ะ"); }
            else if (pctDiff <= 0.005m) { score += 50; reasons.Add("ยอดต่างน้อยมาก"); }
            else if (pctDiff <= 0.01m) { score += 40; reasons.Add("ยอดต่าง ≤1%"); }
            else if (pctDiff <= 0.02m) { score += 25; reasons.Add("ยอดต่าง ≤2%"); }
        }

        // === Date proximity (max 30) ===
        var dateDiff = Math.Abs((candidateDate - bankDate).TotalDays);
        if (dateDiff == 0) { score += 30; reasons.Add("วันเดียวกัน"); }
        else if (dateDiff <= 1) { score += 25; reasons.Add("ห่างกัน 1 วัน"); }
        else if (dateDiff <= 3) { score += 20; reasons.Add("ห่างกัน ≤3 วัน"); }
        else if (dateDiff <= 7) { score += 15; reasons.Add("ห่างกัน ≤7 วัน"); }
        else if (dateDiff <= 14) { score += 8; }
        else if (dateDiff <= 30) { score += 3; }

        // === Text match (max 10) ===
        bool textMatch = false;
        var allCandidateText = ($"{candidateRef} {candidateNotes} {candidateDocNumber} {candidateName}").ToLowerInvariant();

        // Reference exact match
        if (!string.IsNullOrWhiteSpace(candidateRef) && !string.IsNullOrWhiteSpace(bankRefLower)
            && allCandidateText.Contains(bankRefLower)) textMatch = true;

        // Document number appears in bank desc
        if (!string.IsNullOrWhiteSpace(candidateDocNumber)
            && bankDescLower.Contains(candidateDocNumber.ToLowerInvariant())) textMatch = true;

        // Counterparty name appears in bank desc (split words, look for >= 2 char tokens)
        if (!string.IsNullOrWhiteSpace(candidateName))
        {
            var tokens = candidateName.ToLowerInvariant().Split(new[] { ' ', '-', '_', ',', '.' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(t => t.Length >= 2 && bankDescLower.Contains(t))) textMatch = true;
        }

        if (textMatch) { score += 10; reasons.Add("คำอธิบาย/ชื่อตรงกัน"); }

        return (Math.Min(score, 100), reasons.Count > 0 ? string.Join(", ", reasons) : "");
    }
}
