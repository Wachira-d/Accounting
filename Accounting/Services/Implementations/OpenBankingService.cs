using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.OpenBanking;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OpenBankingService : IOpenBankingService
{
    private readonly AccountingDbContext _db;

    public OpenBankingService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Connections =====

    public async Task<BankConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankConnectionRequest request)
    {
        var connection = new BankConnection
        {
            CompanyId = companyId,
            BankCode = request.BankCode,
            BankName = request.BankName,
            ConnectionType = request.ConnectionType,
            ApiEndpoint = request.ApiEndpoint,
            ClientId = request.ClientId,
            EncryptedCredentials = request.Credentials, // In production, encrypt before storing
            AutoSync = request.AutoSync,
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            LinkedBankAccountId = request.LinkedBankAccountId,
            Status = "Active"
        };

        _db.Set<BankConnection>().Add(connection);
        await _db.SaveChangesAsync();

        return MapToConnectionResponse(connection);
    }

    public async Task<List<BankConnectionResponse>> GetConnectionsAsync(Guid companyId)
    {
        return await _db.Set<BankConnection>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => MapToConnectionResponse(c))
            .ToListAsync();
    }

    public async Task<BankConnectionResponse> UpdateConnectionAsync(Guid companyId, Guid connectionId, UpdateBankConnectionRequest request)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId && !c.IsDeleted)
            ?? throw new InvalidOperationException("Bank connection not found.");

        if (request.AutoSync.HasValue) connection.AutoSync = request.AutoSync.Value;
        if (request.SyncIntervalMinutes.HasValue) connection.SyncIntervalMinutes = request.SyncIntervalMinutes.Value;
        if (request.Credentials != null) connection.EncryptedCredentials = request.Credentials;

        await _db.SaveChangesAsync();

        return MapToConnectionResponse(connection);
    }

    public async Task DeleteConnectionAsync(Guid companyId, Guid connectionId)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new InvalidOperationException("Bank connection not found.");

        connection.IsDeleted = true;
        connection.Status = "Disconnected";
        await _db.SaveChangesAsync();
    }

    // ===== Sync =====

    public async Task<BankFeedImportResponse> SyncTransactionsAsync(Guid companyId, Guid connectionId, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId && !c.IsDeleted)
            ?? throw new InvalidOperationException("Bank connection not found.");

        var syncFrom = fromDate ?? connection.LastSyncAt ?? DateTime.UtcNow.AddDays(-30);
        var syncTo = toDate ?? DateTime.UtcNow;

        // In production, this would call the bank's API to fetch transactions.
        // Here we simulate by checking for existing bank transactions in that period
        // and creating an import record.

        var linkedBankAccountId = connection.LinkedBankAccountId;
        int totalTransactions = 0;
        int newTransactions = 0;
        int duplicateSkipped = 0;
        int autoMatched = 0;

        if (linkedBankAccountId.HasValue)
        {
            // Count existing transactions in the period as "fetched"
            var existingTransactions = await _db.BankTransactions
                .Where(t => t.BankAccountId == linkedBankAccountId.Value
                          && t.TransactionDate >= syncFrom
                          && t.TransactionDate <= syncTo)
                .ToListAsync();

            totalTransactions = existingTransactions.Count;

            // Simulate: some are new, some are duplicates
            var existingReferences = existingTransactions
                .Where(t => t.Reference != null)
                .Select(t => t.Reference!)
                .ToHashSet();

            newTransactions = existingTransactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Unmatched);
            duplicateSkipped = totalTransactions - newTransactions;
            autoMatched = existingTransactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Matched);
        }

        var import = new BankFeedImport
        {
            CompanyId = companyId,
            BankConnectionId = connectionId,
            ImportDate = DateTime.UtcNow,
            PeriodStart = syncFrom,
            PeriodEnd = syncTo,
            TotalTransactions = totalTransactions,
            NewTransactions = newTransactions,
            DuplicateSkipped = duplicateSkipped,
            AutoMatched = autoMatched,
            Status = "Completed"
        };

        _db.Set<BankFeedImport>().Add(import);

        // Update connection sync status
        connection.LastSyncAt = DateTime.UtcNow;
        connection.LastSyncStatus = "Success";

        await _db.SaveChangesAsync();

        return MapToImportResponse(import);
    }

    public async Task<List<BankFeedImportResponse>> GetImportHistoryAsync(Guid companyId, Guid connectionId)
    {
        return await _db.Set<BankFeedImport>()
            .Where(i => i.BankConnectionId == connectionId && i.CompanyId == companyId)
            .OrderByDescending(i => i.ImportDate)
            .Select(i => MapToImportResponse(i))
            .ToListAsync();
    }

    public async Task ProcessAutoSyncAsync()
    {
        // Find all connections that need auto-sync
        var connectionsToSync = await _db.Set<BankConnection>()
            .Where(c => c.AutoSync
                      && !c.IsDeleted
                      && c.Status == "Active"
                      && (c.LastSyncAt == null
                          || c.LastSyncAt.Value.AddMinutes(c.SyncIntervalMinutes) <= DateTime.UtcNow))
            .ToListAsync();

        foreach (var connection in connectionsToSync)
        {
            try
            {
                await SyncTransactionsAsync(connection.CompanyId, connection.Id);
            }
            catch (Exception ex)
            {
                connection.LastSyncStatus = "Failed";
                connection.LastError = ex.Message;
                await _db.SaveChangesAsync();
            }
        }
    }

    // ===== File Import =====

    public async Task<BankFeedImportResponse> ImportFileAsync(Guid companyId, Guid bankAccountId, string fileFormat, string base64Content)
    {
        var bankAccount = await _db.BankAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == bankAccountId)
            ?? throw new InvalidOperationException("Bank account not found.");

        // Decode the file content
        var fileBytes = Convert.FromBase64String(base64Content);
        var fileContent = System.Text.Encoding.UTF8.GetString(fileBytes);

        int totalTransactions = 0;
        int newTransactions = 0;
        int duplicateSkipped = 0;
        int autoMatched = 0;
        var periodStart = DateTime.UtcNow;
        var periodEnd = DateTime.UtcNow;

        // Parse based on file format
        var lines = fileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (fileFormat.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            // Skip header row
            var dataLines = lines.Skip(1).ToList();
            totalTransactions = dataLines.Count;

            foreach (var line in dataLines)
            {
                var fields = line.Split(',');
                if (fields.Length < 4) continue;

                if (!DateTime.TryParse(fields[0].Trim().Trim('"'), out var txDate)) continue;
                var description = fields[1].Trim().Trim('"');
                if (!decimal.TryParse(fields[2].Trim().Trim('"'), out var amount)) continue;
                var reference = fields.Length > 3 ? fields[3].Trim().Trim('"') : null;

                // Check for duplicate by reference and date
                var isDuplicate = !string.IsNullOrEmpty(reference) && await _db.BankTransactions
                    .AnyAsync(t => t.BankAccountId == bankAccountId
                                && t.Reference == reference
                                && t.TransactionDate.Date == txDate.Date);

                if (isDuplicate)
                {
                    duplicateSkipped++;
                    continue;
                }

                var txType = amount >= 0 ? BankTransactionType.Deposit : BankTransactionType.Withdrawal;

                // Get running balance
                var lastBalance = await _db.BankTransactions
                    .Where(t => t.BankAccountId == bankAccountId)
                    .OrderByDescending(t => t.TransactionDate)
                    .ThenByDescending(t => t.CreatedAt)
                    .Select(t => t.BalanceAfter)
                    .FirstOrDefaultAsync();

                var newBalance = lastBalance + amount;

                _db.BankTransactions.Add(new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = bankAccountId,
                    TransactionDate = txDate,
                    TransactionType = txType,
                    Amount = amount,
                    BalanceAfter = newBalance,
                    Description = description,
                    Reference = reference,
                    ReconciliationStatus = ReconciliationStatus.Unmatched
                });

                newTransactions++;

                if (txDate < periodStart) periodStart = txDate;
                if (txDate > periodEnd) periodEnd = txDate;
            }
        }
        else if (fileFormat.Equals("OFX", StringComparison.OrdinalIgnoreCase) || fileFormat.Equals("QIF", StringComparison.OrdinalIgnoreCase))
        {
            // Simplified OFX/QIF parsing: count transactions from content structure
            totalTransactions = lines.Count(l => l.Contains("<STMTTRN>") || l.StartsWith("D", StringComparison.OrdinalIgnoreCase));
            newTransactions = totalTransactions; // Simplified: treat all as new
        }

        // Try auto-matching new transactions with existing payments
        var unmatchedPayments = await _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId)
            .ToListAsync();

        var newUnmatched = await _db.BankTransactions
            .Where(t => t.BankAccountId == bankAccountId
                      && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ToListAsync();

        foreach (var tx in newUnmatched)
        {
            var matchingPayment = unmatchedPayments
                .FirstOrDefault(p => p.Amount == Math.Abs(tx.Amount)
                                  && Math.Abs((p.PaymentDate - tx.TransactionDate).TotalDays) <= 3);

            if (matchingPayment != null)
            {
                tx.ReconciliationStatus = ReconciliationStatus.Matched;
                tx.MatchedPaymentId = matchingPayment.Id;
                tx.ReconciledAt = DateTime.UtcNow;
                tx.ReconciledBy = "AutoMatch";
                autoMatched++;
            }
        }

        var import = new BankFeedImport
        {
            CompanyId = companyId,
            BankConnectionId = Guid.Empty, // File import - no connection
            ImportDate = DateTime.UtcNow,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            TotalTransactions = totalTransactions,
            NewTransactions = newTransactions,
            DuplicateSkipped = duplicateSkipped,
            AutoMatched = autoMatched,
            Status = "Completed"
        };

        _db.Set<BankFeedImport>().Add(import);

        // Update bank account balance
        var latestTransaction = await _db.BankTransactions
            .Where(t => t.BankAccountId == bankAccountId)
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (latestTransaction != null)
        {
            bankAccount.CurrentBalance = latestTransaction.BalanceAfter;
        }

        await _db.SaveChangesAsync();

        return MapToImportResponse(import);
    }

    // ===== Mapping =====

    private static BankConnectionResponse MapToConnectionResponse(BankConnection c) => new(
        c.Id, c.BankCode, c.BankName, c.ConnectionType, c.Status,
        c.AutoSync, c.SyncIntervalMinutes, c.LastSyncAt, c.LastSyncStatus, c.LinkedBankAccountId);

    private static BankFeedImportResponse MapToImportResponse(BankFeedImport i) => new(
        i.Id, i.ImportDate, i.PeriodStart, i.PeriodEnd,
        i.TotalTransactions, i.NewTransactions, i.DuplicateSkipped, i.AutoMatched,
        i.Status, i.ErrorMessage);
}
