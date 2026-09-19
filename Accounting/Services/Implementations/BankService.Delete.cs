using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class BankService
{
    /// <summary>
    /// Soft-delete a single bank transaction (and unmatch any reconciled payment/journal entry).
    /// Recalculates the parent BankAccount.CurrentBalance from the latest remaining row.
    /// </summary>
    public async Task<int> DeleteTransactionAsync(Guid companyId, Guid transactionId)
    {
        var txn = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        UnmatchIfReconciled(txn);
        txn.IsDeleted = true;
        txn.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await RefreshAccountBalance(txn.BankAccountId);
        await _db.SaveChangesAsync();

        return 1;
    }

    /// <summary>
    /// Bulk delete transactions by ID list, or by date-range within a bank account.
    /// By default, reconciled transactions are skipped (set DeleteReconciled=true to include them).
    /// </summary>
    public async Task<int> DeleteTransactionsAsync(Guid companyId, DeleteTransactionsRequest request)
    {
        var query = _db.Set<BankTransaction>().Where(t => t.CompanyId == companyId);

        var hasIds = request.TransactionIds?.Count > 0;
        var hasDateRange = request.BankAccountId.HasValue
            && (request.DateFrom.HasValue || request.DateTo.HasValue);

        if (!hasIds && !hasDateRange)
            throw new ArgumentException(
                "กรุณาระบุ TransactionIds หรือ BankAccountId พร้อม DateFrom/DateTo อย่างน้อย 1 ตัวกรอง");

        if (hasIds)
        {
            query = query.Where(t => request.TransactionIds!.Contains(t.Id));
        }

        if (request.BankAccountId.HasValue)
            query = query.Where(t => t.BankAccountId == request.BankAccountId.Value);

        if (request.DateFrom.HasValue)
        {
            var from = request.DateFrom.Value.Date;
            query = query.Where(t => t.TransactionDate >= from);
        }
        if (request.DateTo.HasValue)
        {
            var to = request.DateTo.Value.Date.AddDays(1);
            query = query.Where(t => t.TransactionDate < to);
        }

        if (!request.DeleteReconciled)
            query = query.Where(t => t.ReconciliationStatus != ReconciliationStatus.Matched);

        var transactions = await query.ToListAsync();
        if (transactions.Count == 0) return 0;

        var affectedAccountIds = transactions.Select(t => t.BankAccountId).Distinct().ToList();

        foreach (var t in transactions)
        {
            if (request.DeleteReconciled) UnmatchIfReconciled(t);
            t.IsDeleted = true;
            t.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        foreach (var accountId in affectedAccountIds)
            await RefreshAccountBalance(accountId);

        await _db.SaveChangesAsync();
        return transactions.Count;
    }

    /// <summary>
    /// Recalculate BankAccount.CurrentBalance from the latest non-deleted transaction's BalanceAfter.
    /// </summary>
    private async Task RefreshAccountBalance(Guid bankAccountId)
    {
        var account = await _db.Set<BankAccount>().FirstOrDefaultAsync(a => a.Id == bankAccountId);
        if (account == null) return;

        var latest = await _db.Set<BankTransaction>()
            .Where(t => t.BankAccountId == bankAccountId)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        account.CurrentBalance = latest?.BalanceAfter ?? 0m;
        account.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear match references on the transaction itself so we don't leave dangling links.
    /// (Payment / JournalEntry don't track reconciliation state directly — only BankTransaction does.)
    /// </summary>
    private static void UnmatchIfReconciled(BankTransaction txn)
    {
        // `Suggested` ก็ถือคู่ไว้เหมือนกัน (`MatchedPaymentId` / `SuggestedDocumentId`)
        // ⇒ ถ้าไม่ล้างด้วย แถวจะชี้ไปยังของที่เพิ่งถูกลบ
        if (txn.ReconciliationStatus != ReconciliationStatus.Matched
            && txn.ReconciliationStatus != ReconciliationStatus.Suggested) return;

        txn.MatchedPaymentId = null;
        txn.MatchedJournalEntryId = null;
        txn.SuggestedDocumentId = null;
        txn.ReconciliationStatus = ReconciliationStatus.Unmatched;
        txn.ReconciledAt = null;
        txn.ReconciledBy = null;
        txn.MatchRuleCode = null;
        txn.MatchReason = null;
        txn.MatchGroupId = null;
        txn.MatchedEntryIdsJson = null;
    }
}
