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

        // ===== Build "already matched" exclusion sets =====
        // Walk every Matched bank transaction (other than the current one) and collect
        // every Payment / JournalEntry ID it references — via the direct columns
        // (MatchedPaymentId / MatchedJournalEntryId) AND the JSON array column for
        // many-to-one matches (MatchedEntryIdsJson). Bulletproof against:
        //   • single matches
        //   • multi-matches (JSON array)
        //   • stale match IDs on Unmatched/Excluded txns (we filter Status=Matched)
        var dateMin = bankDate.AddDays(-60);
        var dateMax = bankDate.AddDays(60);

        // IMPORTANT: include EVERY sibling bank transaction that has ANY non-null
        // match reference, regardless of ReconciliationStatus. This is intentionally
        // more aggressive than just `Status == Matched` because:
        //   • A txn may have stale match IDs after partial unmatch flows
        //   • Legacy data from before the JSON column existed only stored the first
        //     ID — we still want to exclude that first ID via direct columns
        //   • Better to be over-cautious (hide a possibly-available candidate) than
        //     show duplicates that lead to double-matching.
        var siblingMatched = await _db.Set<BankTransaction>()
            .AsNoTracking()
            .Where(t => t.CompanyId == companyId
                     && t.Id != bankTransactionId
                     && (t.MatchedPaymentId != null
                         || t.MatchedJournalEntryId != null
                         || t.MatchedEntryIdsJson != null
                         || t.MatchGroupId != null))
            .Select(t => new { t.MatchedPaymentId, t.MatchedJournalEntryId, t.MatchedEntryIdsJson, t.MatchGroupId })
            .ToListAsync();

        var usedPaymentIds = new HashSet<Guid>();
        var usedJeIds = new HashSet<Guid>();

        foreach (var t in siblingMatched)
        {
            if (t.MatchedPaymentId.HasValue) usedPaymentIds.Add(t.MatchedPaymentId.Value);
            if (t.MatchedJournalEntryId.HasValue) usedJeIds.Add(t.MatchedJournalEntryId.Value);

            if (string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson)) continue;
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson!);
                if (ids == null) continue;
                // The JSON list is type-agnostic (could be Payment or JE IDs from a multi-match).
                // Adding to both sets is safe because Payment IDs and JE IDs come from different
                // tables and never collide.
                foreach (var id in ids) { usedPaymentIds.Add(id); usedJeIds.Add(id); }
            }
            catch { /* malformed JSON — ignore */ }
        }

        // ===== Cross-reference: hide "twin" entries =====
        // Every Payment auto-creates a Journal Entry with Reference = PaymentNumber
        // (see IntegrationService — the "RV-xxxx" entries). If we matched the Payment,
        // the twin JE looks unmatched and would still show up in the JE tab — letting
        // the user accidentally double-match the same money. Mirror the exclusion in
        // both directions so the picker only ever shows truly unmatched candidates.
        if (usedPaymentIds.Count > 0)
        {
            var paidPaymentIds = usedPaymentIds.ToList();
            var matchedPaymentNumbers = await _db.Set<Payment>()
                .AsNoTracking()
                .Where(p => p.CompanyId == companyId && paidPaymentIds.Contains(p.Id))
                .Select(p => p.PaymentNumber)
                .ToListAsync();

            if (matchedPaymentNumbers.Count > 0)
            {
                var twinJeIds = await _db.Set<JournalEntry>()
                    .AsNoTracking()
                    .Where(j => j.CompanyId == companyId
                             && j.IsAutoGenerated
                             && j.Reference != null
                             && matchedPaymentNumbers.Contains(j.Reference))
                    .Select(j => j.Id)
                    .ToListAsync();
                foreach (var id in twinJeIds) usedJeIds.Add(id);
            }
        }

        if (usedJeIds.Count > 0)
        {
            var matchedJeIds = usedJeIds.ToList();
            var matchedJeRefs = await _db.Set<JournalEntry>()
                .AsNoTracking()
                .Where(j => j.CompanyId == companyId
                         && matchedJeIds.Contains(j.Id)
                         && j.IsAutoGenerated
                         && j.Reference != null)
                .Select(j => j.Reference!)
                .ToListAsync();

            if (matchedJeRefs.Count > 0)
            {
                var twinPaymentIds = await _db.Set<Payment>()
                    .AsNoTracking()
                    .Where(p => p.CompanyId == companyId
                             && matchedJeRefs.Contains(p.PaymentNumber))
                    .Select(p => p.Id)
                    .ToListAsync();
                foreach (var id in twinPaymentIds) usedPaymentIds.Add(id);
            }
        }

        // ===== Candidate Payments =====
        // Filter: same company + date window + not soft-deleted + parent document not voided +
        // not already matched on another bank transaction.
        var paymentExclude = usedPaymentIds.ToList();
        var paymentCandidates = await _db.Set<Payment>()
            .AsNoTracking()
            .Include(p => p.Document).ThenInclude(d => d.Contact)
            .Where(p => p.CompanyId == companyId
                     && !p.IsDeleted
                     && p.PaymentDate >= dateMin && p.PaymentDate <= dateMax
                     && p.Document.Status != DocumentStatus.Voided
                     && !paymentExclude.Contains(p.Id))
            .ToListAsync();

        // Pre-fetch all JE lines (with ChartOfAccount) for candidates to derive
        // "deposit destination" per JE without N+1. We pull lines for ALL JEs in
        // the date window, not just candidates, since candidates aren't decided yet.
        var jeLinesLookup = await _db.Set<JournalEntryLine>()
            .AsNoTracking()
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                     && l.JournalEntry.EntryDate >= dateMin && l.JournalEntry.EntryDate <= dateMax
                     && !l.JournalEntry.IsDeleted
                     && (l.DebitAmount > 0 || l.CreditAmount > 0)
                     && l.Account != null
                     && l.Account.AccountCode.StartsWith("11")) // 11xx = current assets (cash + bank)
            .Select(l => new JeLineSummary(
                l.JournalEntryId, l.DebitAmount, l.CreditAmount,
                l.Account!.AccountCode, l.Account.AccountName))
            .ToListAsync();
        var jeLinesByEntry = jeLinesLookup
            .GroupBy(l => l.JournalEntryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rankedPaymentsAll = paymentCandidates
            .Select(p =>
            {
                var amountDiff = Math.Abs(p.Amount - bankAmount);
                var dateDiff = (int)Math.Abs((p.PaymentDate.Date - bankDate).TotalDays);
                var (score, reason) = ScoreCandidate(p.Amount, bankAmount, p.PaymentDate.Date, bankDate,
                    p.Reference, p.Notes, p.Document?.DocumentNumber, p.Document?.Contact?.Name,
                    bankDescLower, bankRefLower);

                // Deposit info: derive from PaymentMethod + BankAccount field on Payment.
                var (depLabel, depCat) = DerivePaymentDeposit(p);

                // Bonus: bank-deposit txns prefer Bank candidates; if cash-only payment, slight penalty
                var bankTxnIsDeposit = bankTxn.TransactionType == BankTransactionType.Deposit
                    || bankTxn.TransactionType == BankTransactionType.Interest;
                if (bankTxnIsDeposit && depCat == "Bank") score = Math.Min(100, score + 5);
                if (depCat == "Cash" && bankTxnIsDeposit && p.Amount < 5000) score = Math.Max(0, score - 5);

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
                    ScoreReason: reason,
                    DepositLabel: depLabel,
                    DepositCategory: depCat);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DateDiffDays)
            .ToList();

        var totalPaymentsInWindow = rankedPaymentsAll.Count;
        var rankedPayments = rankedPaymentsAll.Take(DisplayCap).ToList();

        // ===== Candidate Journal Entries =====
        var jeExclude = usedJeIds.ToList();
        var jeCandidates = await _db.Set<JournalEntry>()
            .AsNoTracking()
            .Where(j => j.CompanyId == companyId
                     && !j.IsDeleted
                     && j.EntryDate >= dateMin && j.EntryDate <= dateMax
                     && j.Status != JournalEntryStatus.Voided
                     && !jeExclude.Contains(j.Id))
            .ToListAsync();

        var bankTxnIsDeposit2 = bankTxn.TransactionType == BankTransactionType.Deposit
            || bankTxn.TransactionType == BankTransactionType.Interest;

        var rankedJournalsAll = jeCandidates
            .Select(j =>
            {
                // Use TotalDebit (= TotalCredit) as the JE amount
                var jeAmount = j.TotalDebit;
                var amountDiff = Math.Abs(jeAmount - bankAmount);
                var dateDiff = (int)Math.Abs((j.EntryDate.Date - bankDate).TotalDays);
                var (score, reason) = ScoreCandidate(jeAmount, bankAmount, j.EntryDate.Date, bankDate,
                    j.Reference, j.Description, null, null,
                    bankDescLower, bankRefLower);

                // Deposit info from JE lines: which 11xx asset account(s) received the money
                jeLinesByEntry.TryGetValue(j.Id, out var lines);
                var (depLabel, depCat) = DeriveJeDeposit(lines, bankTxnIsDeposit2);

                // Score adjustment: prefer bank-posting JEs for bank deposits
                if (bankTxnIsDeposit2 && depCat == "Bank") score = Math.Min(100, score + 8);
                else if (bankTxnIsDeposit2 && depCat == "Mixed") score = Math.Min(100, score + 4);
                else if (bankTxnIsDeposit2 && depCat == "Cash") score = Math.Max(0, score - 8);

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
                    ScoreReason: reason,
                    DepositLabel: depLabel,
                    DepositCategory: depCat);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.DateDiffDays)
            .ToList();

        var totalJournalEntriesInWindow = rankedJournalsAll.Count;
        var rankedJournals = rankedJournalsAll.Take(DisplayCap).ToList();

        return new MatchCandidatesResponse(
            BankTransactionId: bankTxn.Id,
            BankTransactionDate: bankTxn.TransactionDate,
            BankTransactionAmount: bankTxn.Amount,
            BankTransactionDescription: bankTxn.Description ?? "",
            Payments: rankedPayments,
            JournalEntries: rankedJournals,
            ExcludedAlreadyMatchedCount: usedPaymentIds.Count + usedJeIds.Count,
            TotalPaymentsInWindow: totalPaymentsInWindow,
            TotalJournalEntriesInWindow: totalJournalEntriesInWindow);
    }

    /// <summary>
    /// Max rows displayed per tab in the picker. Anything beyond is reachable via
    /// the search box / amount filter. We expose a "totalInWindow" diagnostic so
    /// the UI can show "showing 200 of 480" — making the cap visible.
    /// </summary>
    private const int DisplayCap = 200;

    /// <summary>
    /// AI auto-suggest: pick a subset of match candidates whose amounts sum to
    /// the bank transaction's amount. Tries single match first (1-to-1 exact),
    /// then many-to-one (via subset-sum backtracking). Searches Payments first,
    /// then JournalEntries.
    /// </summary>
    public async Task<AiMatchSuggestionResponse> SuggestMatchAsync(Guid companyId, Guid bankTransactionId)
    {
        var data = await GetMatchCandidatesAsync(companyId, bankTransactionId);
        var target = data.BankTransactionAmount;
        const decimal tolerance = 0.01m; // 1 satang tolerance

        // Helper: try to find a subset whose amounts sum to `target`.
        // Returns selected IDs + total + exact flag.
        static (List<Guid> ids, decimal sum, bool exact) PickSubset(List<MatchCandidate> items, decimal target, decimal tol)
        {
            // 1) Single exact match wins
            var single = items.FirstOrDefault(i => Math.Abs(i.Amount - target) <= tol);
            if (single != null) return (new List<Guid> { single.Id }, single.Amount, true);

            // 2) Many-to-one subset-sum (limit to 20 to keep backtracking tractable)
            var pool = items.Where(i => i.Amount > 0 && i.Amount <= target + tol)
                .OrderByDescending(i => i.Score) // bias toward high-score candidates first
                .Take(20)
                .ToList();

            var picks = new List<MatchCandidate>();
            if (BacktrackGeneric(pool, 0, target, tol, picks, new List<MatchCandidate>()))
                return (picks.Select(p => p.Id).ToList(), picks.Sum(p => p.Amount), true);

            return (new List<Guid>(), 0m, false);
        }

        // Try Payments first (most common case for bank deposits = customer payments)
        var (payIds, paySum, payExact) = PickSubset(data.Payments, target, tolerance);
        if (payExact)
        {
            return new AiMatchSuggestionResponse(
                Found: true, Type: "Payment",
                SuggestedIds: payIds, SuggestedTotal: paySum,
                BankTransactionAmount: target,
                Difference: Math.Abs(target - paySum),
                Message: payIds.Count == 1
                    ? $"พบการชำระเงินที่ตรงเป๊ะ 1 รายการ"
                    : $"AI วิเคราะห์: รวม {payIds.Count} การชำระเงิน = ฿{paySum:N2} (ตรงกับยอดธนาคาร)");
        }

        // Try JournalEntries
        var (jeIds, jeSum, jeExact) = PickSubset(data.JournalEntries, target, tolerance);
        if (jeExact)
        {
            return new AiMatchSuggestionResponse(
                Found: true, Type: "JournalEntry",
                SuggestedIds: jeIds, SuggestedTotal: jeSum,
                BankTransactionAmount: target,
                Difference: Math.Abs(target - jeSum),
                Message: jeIds.Count == 1
                    ? $"พบ Journal Entry ที่ตรงเป๊ะ 1 รายการ"
                    : $"AI วิเคราะห์: รวม {jeIds.Count} Journal Entry = ฿{jeSum:N2} (ตรงกับยอดธนาคาร)");
        }

        return new AiMatchSuggestionResponse(
            Found: false, Type: null,
            SuggestedIds: new List<Guid>(),
            SuggestedTotal: 0m,
            BankTransactionAmount: target,
            Difference: target,
            Message: "AI ไม่พบรายการเดี่ยวหรือชุดที่รวมแล้วได้ยอดตรงเป๊ะ — กรุณาเลือกด้วยตนเอง");
    }

    private static bool BacktrackGeneric(
        List<MatchCandidate> items, int idx, decimal target, decimal tolerance,
        List<MatchCandidate> result, List<MatchCandidate> current)
    {
        if (Math.Abs(target) <= tolerance && current.Count >= 1)
        {
            result.AddRange(current);
            return true;
        }
        if (idx >= items.Count || target < -tolerance) return false;

        // Include
        current.Add(items[idx]);
        if (BacktrackGeneric(items, idx + 1, target - items[idx].Amount, tolerance, result, current))
            return true;
        current.RemoveAt(current.Count - 1);

        // Skip
        return BacktrackGeneric(items, idx + 1, target, tolerance, result, current);
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

    /// <summary>
    /// Derive a "deposit destination" label + category from a Payment record.
    /// The Payment entity stores PaymentMethod (enum) and BankAccount (free text).
    /// </summary>
    private static (string? label, string? category) DerivePaymentDeposit(Payment p)
    {
        var method = p.PaymentMethod;
        var bankAcct = (p.BankAccount ?? "").Trim();

        switch (method)
        {
            case PaymentMethod.Cash:
                return ("💵 เงินสด", "Cash");
            case PaymentMethod.BankTransfer:
            case PaymentMethod.PromptPay:
            case PaymentMethod.DirectDebit:
                return (string.IsNullOrEmpty(bankAcct) ? "🏦 โอนผ่านธนาคาร" : $"🏦 {bankAcct}", "Bank");
            case PaymentMethod.CreditCard:
                return ("💳 บัตรเครดิต", "Bank");
            case PaymentMethod.Cheque:
                return (string.IsNullOrEmpty(bankAcct) ? "📄 เช็ค" : $"📄 เช็ค ({bankAcct})", "Bank");
            case PaymentMethod.EWallet:
                return ("📱 e-Wallet", "Bank");
            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Inspect the JE's lines to determine where the money was booked. Looks at
    /// lines posting to 11xx (current asset) accounts:
    ///   • 1110-1111: Cash (เงินสด) — "💵"
    ///   • 1112-1119: Bank deposits (เงินฝากธนาคาร) — "🏦"
    /// For a deposit-direction bank txn, we look at DEBIT lines (money coming in).
    /// For a withdrawal direction, we look at CREDIT lines (money going out of bank).
    /// </summary>
    /// <summary>JE-line projection used to compute deposit destination.</summary>
    private record JeLineSummary(
        Guid JournalEntryId,
        decimal DebitAmount,
        decimal CreditAmount,
        string AccountCode,
        string AccountName);

    private static (string? label, string? category) DeriveJeDeposit(
        List<JeLineSummary>? lines, bool isDepositTxn)
    {
        if (lines == null || lines.Count == 0) return (null, null);

        // For a deposit, the asset account is debited (money in).
        // For a withdrawal, the asset account is credited (money out).
        decimal cashAmount = 0;
        decimal bankAmount = 0;
        var bankAccountNames = new HashSet<string>();
        var cashAccountNames = new HashSet<string>();

        foreach (var l in lines)
        {
            var code = l.AccountCode;
            var name = l.AccountName;
            var amt = isDepositTxn ? l.DebitAmount : l.CreditAmount;
            if (amt <= 0) continue;

            if (code.StartsWith("1110") || code.StartsWith("1111"))
            {
                cashAmount += amt;
                cashAccountNames.Add(name);
            }
            else if (code.StartsWith("1112") || code.StartsWith("1113") ||
                     code.StartsWith("1114") || code.StartsWith("1115") ||
                     code.StartsWith("1116") || code.StartsWith("1117") ||
                     code.StartsWith("1118") || code.StartsWith("1119"))
            {
                bankAmount += amt;
                bankAccountNames.Add(name);
            }
        }

        if (cashAmount == 0 && bankAmount == 0) return (null, null);

        if (bankAmount > 0 && cashAmount > 0)
        {
            // Both — likely a "deposit cash to bank" transfer JE
            var bankPart = bankAccountNames.FirstOrDefault() ?? "ธนาคาร";
            return ($"💵 → 🏦 {bankPart}", "Mixed");
        }
        if (bankAmount > 0)
        {
            var name = bankAccountNames.FirstOrDefault() ?? "บัญชีธนาคาร";
            return ($"🏦 {name}", "Bank");
        }
        // cashAmount > 0
        return ("💵 เงินสด", "Cash");
    }
}
