using System.Text.Json;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ONE shared resolver for "what amount did a bank transaction match to".
/// Previously six places computed this independently and drifted apart
/// (a 5,800 M:1 match showed mismatched in some views, fine in others).
/// Every view + the save-time guard now routes through here.
///
/// Resolution rules (identical for all callers):
///   • Counterpart ids come from MatchedPaymentId + MatchedJournalEntryId +
///     MatchedEntryIdsJson (legacy M:1 — the single field holds only the FIRST
///     id) for non-group matches, or from ReconciliationGroupItems for M:N.
///   • Payment  → Payment.Amount.
///   • JournalEntry → NET movement on the bank's own linked GL account
///     (Σ Debit − Credit on that account, abs), so compound entries are valued
///     by what actually hit the bank; falls back to TotalDebit if the JE posts
///     nothing to the bank account.
///   • Document → Document.TotalAmount.
///   • In an M:N group the item's signed AllocatedAmount is shown (the group is
///     balanced as a whole, so individual rows are judged by group.IsBalanced,
///     never by a naive full-amount sum).
///   • A counterpart id that no longer resolves (deleted after matching) is
///     flagged via HasMissingCounterpart rather than silently scored as 0.
/// </summary>
public partial class BankService
{
    public async Task<ResolvedMatchDto?> ResolveMatchAsync(Guid companyId, Guid txnId)
    {
        var txn = await _db.Set<BankTransaction>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == txnId && t.CompanyId == companyId && !t.IsDeleted);
        if (txn == null) return null;
        var bankCoaId = await GetBankCoaIdAsync(companyId, txn.BankAccountId);
        var list = await ResolveCoreAsync(companyId, bankCoaId, new List<BankTransaction> { txn });
        return list.FirstOrDefault();
    }

    public async Task<List<ResolvedMatchDto>> ResolveMatchesAsync(Guid companyId, Guid bankAccountId)
    {
        var bankCoaId = await GetBankCoaIdAsync(companyId, bankAccountId);
        var txns = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId && t.CompanyId == companyId && !t.IsDeleted
                && t.ReconciliationStatus == ReconciliationStatus.Matched)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();
        return await ResolveCoreAsync(companyId, bankCoaId, txns);
    }

    private async Task<Guid?> GetBankCoaIdAsync(Guid companyId, Guid bankAccountId) =>
        await _db.Set<BankAccount>().AsNoTracking()
            .Where(a => a.Id == bankAccountId && a.CompanyId == companyId)
            .Select(a => a.LinkedAccountId).FirstOrDefaultAsync();

    private static readonly Dictionary<BankTransactionType, string> _txnTypeTh = new()
    {
        [BankTransactionType.Deposit] = "ฝาก", [BankTransactionType.Withdrawal] = "ถอน",
        [BankTransactionType.Transfer] = "โอน", [BankTransactionType.Fee] = "ค่าธรรมเนียม",
        [BankTransactionType.Interest] = "ดอกเบี้ย",
    };

    private async Task<List<ResolvedMatchDto>> ResolveCoreAsync(
        Guid companyId, Guid? bankCoaId, List<BankTransaction> txns)
    {
        // ── Group membership (M:N) ──────────────────────────────────────
        var groupIds = txns.Where(t => t.ReconciliationGroupId.HasValue)
            .Select(t => t.ReconciliationGroupId!.Value).Distinct().ToList();
        var groups = await _db.Set<ReconciliationGroup>().AsNoTracking()
            .Where(g => groupIds.Contains(g.Id) && g.CompanyId == companyId)
            .Select(g => new { g.Id, g.GroupNumber, g.IsBalanced })
            .ToListAsync();
        var groupItems = await _db.ReconciliationGroupItems.AsNoTracking()
            .Where(i => groupIds.Contains(i.GroupId) && i.ItemType != ReconciliationItemType.BankTransaction)
            .Select(i => new { i.GroupId, i.ItemType, i.ItemId, i.AllocatedAmount })
            .ToListAsync();

        // ── Per-txn counterpart id sets for NON-group matches ───────────
        var idsByTxn = new Dictionary<Guid, List<Guid>>();
        foreach (var t in txns)
        {
            if (t.ReconciliationGroupId.HasValue) { idsByTxn[t.Id] = new(); continue; }
            var ids = new List<Guid>();
            if (t.MatchedPaymentId.HasValue) ids.Add(t.MatchedPaymentId.Value);
            if (t.MatchedJournalEntryId.HasValue) ids.Add(t.MatchedJournalEntryId.Value);
            if (!string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson))
                try { ids.AddRange(JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson!) ?? new()); }
                catch { /* malformed JSON — ignore */ }
            idsByTxn[t.Id] = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        }

        // ── Batch-load every referenced entity once ─────────────────────
        var allIds = idsByTxn.Values.SelectMany(x => x)
            .Concat(groupItems.Select(g => g.ItemId)).Distinct().ToList();

        var pays = await _db.Payments.AsNoTracking()
            .Where(p => allIds.Contains(p.Id) && p.CompanyId == companyId)
            .Select(p => new { p.Id, p.Amount, p.PaymentNumber, p.PaymentDate,
                DocNo = p.Document.DocumentNumber, DocType = p.Document.DocumentType })
            .ToListAsync();
        var payMap = pays.ToDictionary(p => p.Id);
        var payIdSet = payMap.Keys.ToHashSet();

        var jeCandidateIds = allIds.Where(i => !payIdSet.Contains(i)).ToList();
        var jeHeaders = await _db.JournalEntries.AsNoTracking()
            .Where(j => jeCandidateIds.Contains(j.Id))
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.TotalDebit, j.Description })
            .ToListAsync();
        var jeMap = jeHeaders.ToDictionary(j => j.Id);

        // jeNet = magnitude on the bank account; jeSignedNet preserves direction
        // so the resolver can tell which side of a net-settlement each JE is on.
        var jeNet = new Dictionary<Guid, decimal>();
        var jeSignedNet = new Dictionary<Guid, decimal>();
        if (bankCoaId.HasValue && jeCandidateIds.Count > 0)
        {
            var lines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => jeCandidateIds.Contains(l.JournalEntryId) && l.AccountId == bankCoaId.Value)
                .Select(l => new { l.JournalEntryId, Net = l.DebitAmount - l.CreditAmount })
                .ToListAsync();
            jeSignedNet = lines.GroupBy(l => l.JournalEntryId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
            jeNet = jeSignedNet.ToDictionary(kv => kv.Key, kv => Math.Abs(kv.Value));
        }

        // Ids that are neither Payment nor JournalEntry → try Document.
        var docCandidateIds = jeCandidateIds.Where(i => !jeMap.ContainsKey(i))
            .Concat(groupItems.Where(g => g.ItemType == ReconciliationItemType.Document).Select(g => g.ItemId))
            .Distinct().ToList();
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => docCandidateIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount })
            .ToListAsync();
        var docMap = docs.ToDictionary(d => d.Id);

        decimal JeAmount(Guid id) =>
            jeNet.TryGetValue(id, out var n) ? n : (jeMap.TryGetValue(id, out var h) ? h.TotalDebit : 0m);

        // ── Build a counterpart for one id; null if it no longer resolves ─
        ResolvedCounterpartDto? Describe(Guid id, decimal? overrideAmount)
        {
            if (payMap.TryGetValue(id, out var p))
                return new("Payment", id, p.DocNo ?? p.PaymentNumber, p.PaymentDate, overrideAmount ?? p.Amount);
            if (jeMap.TryGetValue(id, out var j))
                return new("JournalEntry", id,
                    j.EntryNumber + (j.Description != null ? $" — {j.Description}" : ""),
                    j.EntryDate, overrideAmount ?? JeAmount(id));
            if (docMap.TryGetValue(id, out var d))
                return new("Document", id, d.DocumentNumber, d.DocumentDate, overrideAmount ?? d.TotalAmount);
            return null;
        }

        // ── Assemble per-txn results ────────────────────────────────────
        var result = new List<ResolvedMatchDto>();
        foreach (var t in txns)
        {
            var bankAmt = Math.Abs(t.Amount);
            var type = _txnTypeTh.GetValueOrDefault(t.TransactionType, t.TransactionType.ToString());
            var cps = new List<ResolvedCounterpartDto>();
            bool missing = false;
            decimal total = 0m;
            bool agree;
            string? groupNo = null;
            var isGroup = t.ReconciliationGroupId.HasValue;

            if (isGroup)
            {
                var g = groups.FirstOrDefault(x => x.Id == t.ReconciliationGroupId!.Value);
                groupNo = g?.GroupNumber;
                foreach (var gi in groupItems.Where(i => i.GroupId == t.ReconciliationGroupId!.Value))
                {
                    // Show the item's signed allocation (its contribution to the
                    // group), not the full entity amount.
                    var cp = Describe(gi.ItemId, gi.AllocatedAmount);
                    if (cp == null) { missing = true; continue; }
                    cps.Add(cp);
                    total += gi.AllocatedAmount;
                }
                // A group is built balanced; judge the row by the group, never
                // by a naive sum (items may be partially allocated).
                agree = g?.IsBalanced ?? false;
            }
            else
            {
                // Same-side items add (+); opposite-side items subtract (−).
                // Direction is read from the document type (Payment) or the
                // signed net on the bank GL (JE). Unknown direction → additive.
                bool txnIsIn = t.TransactionType is BankTransactionType.Deposit
                    or BankTransactionType.Interest;
                var ids = idsByTxn[t.Id];
                decimal grossTotal = 0m;          // WHT/fee alternative interpretation
                var grossCps = new List<ResolvedCounterpartDto>();
                foreach (var id in ids)
                {
                    var cp = Describe(id, null);
                    if (cp == null) { missing = true; continue; }
                    int sign = +1;
                    if (cp.ItemType == "Payment" && payMap.TryGetValue(id, out var pp))
                    {
                        bool payIn = pp.DocType is Models.Enums.DocumentType.Receipt
                            or Models.Enums.DocumentType.ReceiptVoucher
                            or Models.Enums.DocumentType.Invoice
                            or Models.Enums.DocumentType.TaxInvoice
                            or Models.Enums.DocumentType.BillingNote
                            or Models.Enums.DocumentType.DebitNote;
                        bool payOut = pp.DocType is Models.Enums.DocumentType.PaymentVoucher
                            or Models.Enums.DocumentType.Expense
                            or Models.Enums.DocumentType.PurchaseInvoice
                            or Models.Enums.DocumentType.CertificateInLieu;
                        if ((payIn && !txnIsIn) || (payOut && txnIsIn)) sign = -1;
                    }
                    else if (cp.ItemType == "JournalEntry" && jeSignedNet.TryGetValue(id, out var sn) && Math.Abs(sn) > 0.01m)
                    {
                        bool jeIn = sn > 0;
                        if (jeIn != txnIsIn) sign = -1;
                    }
                    cps.Add(cp with { Amount = sign * cp.Amount });
                    total += sign * cp.Amount;
                    // Gross variant: a JE uses its TotalDebit (gross) instead of
                    // the bank-line net, so a WHT/fee payment (bank shows gross,
                    // JE bank-line is net) still reconciles.
                    var grossAmt = cp.ItemType == "JournalEntry" && jeMap.TryGetValue(id, out var jh)
                        ? jh.TotalDebit : Math.Abs(cp.Amount);
                    grossCps.Add(cp with { Amount = sign * grossAmt });
                    grossTotal += sign * grossAmt;
                }
                // No counterpart link at all (legacy matched row) → can't assess.
                // Accept when EITHER the net or the gross interpretation matches;
                // when only gross matches (WHT case), display the gross amounts.
                bool netAgree = !missing && cps.Count > 0 && Math.Abs(total - bankAmt) <= 0.01m;
                bool grossAgree = !missing && grossCps.Count > 0 && Math.Abs(grossTotal - bankAmt) <= 0.01m;
                if (!netAgree && grossAgree) { cps = grossCps; total = grossTotal; }
                agree = ids.Count == 0 || netAgree || grossAgree;
            }

            result.Add(new ResolvedMatchDto(t.Id, t.TransactionDate, type, t.Description ?? t.Payee,
                bankAmt, total, agree, missing, isGroup, groupNo, cps));
        }
        return result;
    }
}
