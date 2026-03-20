using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class BankService : IBankService
{
    private readonly AccountingDbContext _db;

    public BankService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<BankAccountResponse> CreateBankAccountAsync(Guid companyId, CreateBankAccountRequest request)
    {
        var account = new BankAccount
        {
            CompanyId = companyId,
            AccountName = request.AccountName,
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            BranchName = request.BranchName,
            AccountType = request.AccountType,
            Currency = request.Currency,
            CurrentBalance = request.OpeningBalance,
            LinkedAccountId = request.LinkedAccountId
        };

        _db.Set<BankAccount>().Add(account);
        await _db.SaveChangesAsync();

        return MapToResponse(account);
    }

    public async Task<List<BankAccountResponse>> GetBankAccountsAsync(Guid companyId)
    {
        var accounts = await _db.Set<BankAccount>()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .OrderBy(a => a.AccountName)
            .ToListAsync();

        return accounts.Select(MapToResponse).ToList();
    }

    public async Task<BankAccountResponse> UpdateBankAccountAsync(Guid companyId, Guid accountId, UpdateBankAccountRequest request)
    {
        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        if (request.AccountName != null) account.AccountName = request.AccountName;
        if (request.BranchName != null) account.BranchName = request.BranchName;
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;
        if (request.LinkedAccountId.HasValue) account.LinkedAccountId = request.LinkedAccountId.Value;

        await _db.SaveChangesAsync();
        return MapToResponse(account);
    }

    public async Task<BankTransactionResponse> CreateTransactionAsync(Guid companyId, CreateBankTransactionRequest request)
    {
        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var amount = request.TransactionType == BankTransactionType.Withdrawal || request.TransactionType == BankTransactionType.Fee
            ? -Math.Abs(request.Amount) : Math.Abs(request.Amount);

        account.CurrentBalance += amount;

        var transaction = new BankTransaction
        {
            CompanyId = companyId,
            BankAccountId = request.BankAccountId,
            TransactionDate = request.TransactionDate,
            TransactionType = request.TransactionType,
            Amount = request.Amount,
            BalanceAfter = account.CurrentBalance,
            Description = request.Description,
            Reference = request.Reference,
            Payee = request.Payee
        };

        _db.Set<BankTransaction>().Add(transaction);
        await _db.SaveChangesAsync();

        return MapTransactionToResponse(transaction);
    }

    public async Task<PagedResponse<BankTransactionResponse>> GetTransactionsAsync(Guid companyId, Guid bankAccountId, PagedRequest request)
    {
        var query = _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(t => (t.Description != null && t.Description.Contains(request.Search))
                || (t.Reference != null && t.Reference.Contains(request.Search))
                || (t.Payee != null && t.Payee.Contains(request.Search)));

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(t => t.TransactionDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<BankTransactionResponse>(
            items.Select(MapTransactionToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<BankTransactionResponse> ReconcileAsync(Guid companyId, ReconcileRequest request)
    {
        var transaction = await _db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == request.BankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        transaction.ReconciliationStatus = ReconciliationStatus.Matched;
        transaction.MatchedPaymentId = request.MatchedPaymentId;
        transaction.MatchedJournalEntryId = request.MatchedJournalEntryId;
        transaction.ReconciledAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return MapTransactionToResponse(transaction);
    }

    public async Task<List<BankTransactionResponse>> GetUnreconciledAsync(Guid companyId, Guid bankAccountId)
    {
        var transactions = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == bankAccountId
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();

        return transactions.Select(MapTransactionToResponse).ToList();
    }

    public async Task<List<BankTransactionResponse>> AutoMatchAsync(Guid companyId, Guid bankAccountId)
    {
        var unmatched = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == bankAccountId
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ToListAsync();

        var payments = await _db.Payments
            .Where(p => p.CompanyId == companyId && p.PaymentMethod == PaymentMethod.BankTransfer)
            .ToListAsync();

        // Get already matched payment IDs to prevent double-matching
        var alreadyMatchedPaymentIds = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.MatchedPaymentId.HasValue)
            .Select(t => t.MatchedPaymentId!.Value)
            .ToListAsync();

        var matched = new List<BankTransactionResponse>();

        foreach (var txn in unmatched)
        {
            // Smart matching: amount + date proximity + reference similarity
            var candidates = payments
                .Where(p => !alreadyMatchedPaymentIds.Contains(p.Id)
                    && !matched.Any(m => m.MatchedPaymentId == p.Id))
                .ToList();

            Payment? bestMatch = null;
            decimal bestScore = 0;

            foreach (var payment in candidates)
            {
                decimal score = 0;

                // Exact amount match = 50 points
                if (payment.Amount == txn.Amount)
                    score += 50;
                // Close amount (within 1%) = 30 points
                else if (Math.Abs(payment.Amount - txn.Amount) / Math.Max(txn.Amount, 1) < 0.01m)
                    score += 30;
                else
                    continue; // Amount must be close

                // Date proximity: same day = 30 points, within 3 days = 20, within 7 days = 10
                var daysDiff = Math.Abs((payment.PaymentDate - txn.TransactionDate).TotalDays);
                if (daysDiff <= 0) score += 30;
                else if (daysDiff <= 3) score += 20;
                else if (daysDiff <= 7) score += 10;
                else continue; // Too far apart

                // Reference match = 20 points
                if (!string.IsNullOrEmpty(txn.Reference) && !string.IsNullOrEmpty(payment.Reference)
                    && txn.Reference.Contains(payment.Reference, StringComparison.OrdinalIgnoreCase))
                    score += 20;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = payment;
                }
            }

            // Only match if confidence is high enough (at least amount match + date proximity)
            if (bestMatch != null && bestScore >= 60)
            {
                txn.ReconciliationStatus = ReconciliationStatus.Matched;
                txn.MatchedPaymentId = bestMatch.Id;
                txn.ReconciledAt = DateTime.UtcNow;
                txn.ReconciledBy = "AutoMatch";
                matched.Add(MapTransactionToResponse(txn));
                alreadyMatchedPaymentIds.Add(bestMatch.Id);
            }
        }

        await _db.SaveChangesAsync();
        return matched;
    }

    private static BankAccountResponse MapToResponse(BankAccount a) => new(
        a.Id, a.AccountName, a.BankName, a.AccountNumber,
        a.BranchName, a.AccountType, a.Currency, a.CurrentBalance,
        a.LinkedAccountId, a.IsActive);

    private static BankTransactionResponse MapTransactionToResponse(BankTransaction t) => new(
        t.Id, t.BankAccountId, t.TransactionDate, t.TransactionType,
        t.Amount, t.BalanceAfter, t.Description, t.Reference, t.Payee,
        t.ReconciliationStatus, t.MatchedPaymentId);
}
