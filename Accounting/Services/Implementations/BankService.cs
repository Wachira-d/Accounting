using System.Globalization;
using System.Text;
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
        // Input validation
        if (string.IsNullOrWhiteSpace(request.AccountName))
            throw new ArgumentException("กรุณาระบุชื่อบัญชี");

        if (string.IsNullOrWhiteSpace(request.BankName))
            throw new ArgumentException("กรุณาระบุชื่อธนาคาร");

        if (string.IsNullOrWhiteSpace(request.AccountNumber) || request.AccountNumber.Length < 5 || request.AccountNumber.Length > 20)
            throw new ArgumentException("เลขที่บัญชีต้องมีความยาว 5-20 ตัวอักษร");

        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3 || request.Currency != request.Currency.ToUpperInvariant())
            throw new ArgumentException("สกุลเงินต้องเป็นรหัส 3 ตัวอักษรพิมพ์ใหญ่ (เช่น THB, USD)");

        if (request.OpeningBalance < 0)
            throw new ArgumentException("ยอดเปิดบัญชีต้องไม่ติดลบ");

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
        // Input validation
        if (request.Amount <= 0)
            throw new ArgumentException("จำนวนเงินต้องมากกว่า 0");

        if (request.TransactionDate > DateTime.UtcNow.Date.AddDays(1))
            throw new ArgumentException("วันที่ทำรายการต้องไม่เป็นวันในอนาคต");

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var account = await _db.Set<BankAccount>()
                .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

            var amount = request.TransactionType == BankTransactionType.Withdrawal || request.TransactionType == BankTransactionType.Fee
                ? -Math.Abs(request.Amount) : Math.Abs(request.Amount);

            // Check if balance would go negative for withdrawals/fees
            var description = request.Description;
            if ((request.TransactionType == BankTransactionType.Withdrawal || request.TransactionType == BankTransactionType.Fee)
                && account.CurrentBalance + amount < 0)
            {
                description = $"[คำเตือน: ยอดคงเหลือติดลบ] {description}";
            }

            account.CurrentBalance += amount;

            var transaction = new BankTransaction
            {
                CompanyId = companyId,
                BankAccountId = request.BankAccountId,
                TransactionDate = request.TransactionDate,
                TransactionType = request.TransactionType,
                Amount = request.Amount,
                BalanceAfter = account.CurrentBalance,
                Description = description,
                Reference = request.Reference,
                Payee = request.Payee
            };

            _db.Set<BankTransaction>().Add(transaction);
            await _db.SaveChangesAsync();

            await dbTransaction.CommitAsync();

            return MapTransactionToResponse(transaction);
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
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
        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
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

            // Duplicate detection: get already processed transaction keys (amount+date+reference)
            var processedTransactionKeys = await _db.Set<BankTransaction>()
                .Where(t => t.CompanyId == companyId
                    && t.BankAccountId == bankAccountId
                    && t.ReconciliationStatus == ReconciliationStatus.Matched)
                .Select(t => new { t.Amount, t.TransactionDate, t.Reference })
                .ToListAsync();

            var processedKeySet = new HashSet<string>(
                processedTransactionKeys.Select(t => $"{t.Amount}|{t.TransactionDate:yyyyMMdd}|{t.Reference ?? ""}"));

            var matched = new List<BankTransactionResponse>();

            foreach (var txn in unmatched)
            {
                // Duplicate detection: skip if same amount+date+reference already processed
                var txnKey = $"{txn.Amount}|{txn.TransactionDate:yyyyMMdd}|{txn.Reference ?? ""}";
                if (processedKeySet.Contains(txnKey))
                    continue;

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
                    processedKeySet.Add(txnKey);
                }
            }

            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return matched;
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
    }

    public async Task<int> ImportBankStatementAsync(Guid companyId, ImportBankStatementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Base64Content))
            throw new ArgumentException("กรุณาระบุเนื้อหาไฟล์");

        if (!string.Equals(request.FileFormat, "CSV", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("รองรับเฉพาะรูปแบบ CSV เท่านั้น");

        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var csvBytes = Convert.FromBase64String(request.Base64Content);
        var csvContent = Encoding.UTF8.GetString(csvBytes);
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length < 2)
            throw new ArgumentException("ไฟล์ CSV ต้องมีหัวตารางและข้อมูลอย่างน้อย 1 รายการ");

        // Expected columns: Date, Description, Deposit, Withdrawal, Balance
        var importedCount = 0;

        await using var dbTransaction = await _db.Database.BeginTransactionAsync();
        try
        {
            for (var i = 1; i < lines.Length; i++)
            {
                var columns = lines[i].Split(',');
                if (columns.Length < 5)
                    continue;

                var dateStr = columns[0].Trim().Trim('"');
                var description = columns[1].Trim().Trim('"');
                var depositStr = columns[2].Trim().Trim('"');
                var withdrawalStr = columns[3].Trim().Trim('"');
                var balanceStr = columns[4].Trim().Trim('"');

                if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var transactionDate))
                    continue;

                var deposit = decimal.TryParse(depositStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var dep) ? dep : 0;
                var withdrawal = decimal.TryParse(withdrawalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var wth) ? wth : 0;
                var balance = decimal.TryParse(balanceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var bal) ? bal : 0;

                var isDeposit = deposit > 0;
                var amount = isDeposit ? deposit : withdrawal;

                if (amount <= 0)
                    continue;

                var bankTxn = new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = request.BankAccountId,
                    TransactionDate = transactionDate,
                    TransactionType = isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal,
                    Amount = amount,
                    BalanceAfter = balance,
                    Description = description
                };

                _db.Set<BankTransaction>().Add(bankTxn);
                importedCount++;
            }

            // Update account balance to the last row's balance
            if (importedCount > 0)
            {
                var lastBalanceStr = lines[^1].Split(',');
                if (lastBalanceStr.Length >= 5)
                {
                    var lastBal = lastBalanceStr[4].Trim().Trim('"');
                    if (decimal.TryParse(lastBal, NumberStyles.Any, CultureInfo.InvariantCulture, out var finalBalance))
                    {
                        account.CurrentBalance = finalBalance;
                    }
                }
            }

            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return importedCount;
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }
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
