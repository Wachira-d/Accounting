using System.Security.Cryptography;
using System.Text;
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
    private readonly string _encryptionKey;
    private readonly IErrorLogService _errorLogService;

    public OpenBankingService(AccountingDbContext db, IConfiguration configuration, IErrorLogService errorLogService)
    {
        _db = db;
        // ★ H-A6: เดิมตกไปใช้คีย์ที่เขียนไว้ใน source **เงียบ ๆ** ⇒ credential
        // ของธนาคาร (ClientId/ClientSecret/token) ถูกเข้ารหัสด้วยกุญแจสาธารณะ
        // = เท่ากับ plaintext ถ้าฐานข้อมูลรั่ว · JWT_SECRET / ENCRYPTION_KEY
        // fail-fast ใน production มาตั้งแต่ต้น (Program.cs) — ตัวนี้ตกหล่นตัวเดียว
        _encryptionKey = Environment.GetEnvironmentVariable("OPENBANKING_ENCRYPTION_KEY")
            ?? configuration["OpenBanking:EncryptionKey"]
            ?? (IsProductionEnvironment()
                ? throw new InvalidOperationException(
                    "OPENBANKING_ENCRYPTION_KEY environment variable is required in production — " +
                    "ห้ามเข้ารหัส credential ของธนาคารด้วยคีย์ที่อยู่ใน source")
                : "DefaultKeyForDev-Change-In-Production!");
        _errorLogService = errorLogService;
    }

    /// <summary>อ่านจาก ASPNETCORE_ENVIRONMENT ตรง ๆ — service นี้ไม่ได้รับ
    /// <c>IWebHostEnvironment</c> และการเพิ่ม dependency เพื่อเช็คค่าเดียวไม่คุ้ม</summary>
    private static bool IsProductionEnvironment()
        => string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Production", StringComparison.OrdinalIgnoreCase);

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
            EncryptedCredentials = EncryptString(request.Credentials ?? ""),
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
        if (request.Credentials != null) connection.EncryptedCredentials = EncryptString(request.Credentials);

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
                await _errorLogService.LogErrorAsync(ex, $"OpenBanking.SyncTransactions/{connection.Id}");
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

        int totalTransactions = 0;
        int newTransactions = 0;
        int duplicateSkipped = 0;
        int autoMatched = 0;
        var periodStart = DateTime.UtcNow;
        var periodEnd = DateTime.UtcNow;
        var fileContent = ""; // for non-CSV formats

        if (fileFormat.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            // Use the robust BankCsvParser shared with BankService.ImportBankStatement
            var (parsedRows, _) = Helpers.BankCsvParser.ParseStatement(fileBytes);
            totalTransactions = parsedRows.Count;

            if (parsedRows.Count > 0)
            {
                periodStart = parsedRows.Min(r => r.Date);
                periodEnd = parsedRows.Max(r => r.Date);
            }

            // Get current running balance once (rather than re-querying for each row)
            // tenant isolation (กฎเหล็ก #2 M) — คิวรีเดิมไม่มี CompanyId
            var runningBalance = await _db.BankTransactions
                .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId)
                .OrderByDescending(t => t.TransactionDate)
                .ThenByDescending(t => t.CreatedAt)
                .Select(t => t.BalanceAfter)
                .FirstOrDefaultAsync();

            foreach (var r in parsedRows)
            {
                var isDeposit = r.Deposit > 0;
                var amount = isDeposit ? r.Deposit : r.Withdrawal;
                if (amount <= 0) continue;

                var signedAmount = isDeposit ? amount : -amount;

                // Duplicate detection: same reference + same date, OR same date + amount + description
                var txDayStart = r.Date.Date;
                var txDayEnd = txDayStart.AddDays(1);
                bool isDuplicate;
                if (!string.IsNullOrEmpty(r.Reference))
                {
                    isDuplicate = await _db.BankTransactions
                        .AnyAsync(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId
                                    && t.Reference == r.Reference
                                    && t.TransactionDate >= txDayStart && t.TransactionDate < txDayEnd);
                }
                else
                {
                    isDuplicate = await _db.BankTransactions
                        .AnyAsync(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId
                                    && t.Amount == amount
                                    && t.TransactionType == (isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal)
                                    && t.TransactionDate >= txDayStart && t.TransactionDate < txDayEnd
                                    && t.Description == r.Description);
                }

                if (isDuplicate) { duplicateSkipped++; continue; }

                // Use balance from CSV if available (null = ช่องว่าง), else compute running
                var balanceAfter = r.Balance ?? (runningBalance + signedAmount);
                runningBalance = balanceAfter;

                var desc = r.Description;
                if (!string.IsNullOrWhiteSpace(r.Channel)) desc = $"{desc} | {r.Channel}";
                if (desc.Length > 500) desc = desc[..500];

                _db.BankTransactions.Add(new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = bankAccountId,
                    TransactionDate = r.Date,
                    TransactionType = isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal,
                    Amount = amount,
                    BalanceAfter = balanceAfter,
                    Description = desc,
                    Reference = r.Reference,
                    ReconciliationStatus = ReconciliationStatus.Unmatched
                });

                newTransactions++;
            }
        }
        else if (fileFormat.Equals("OFX", StringComparison.OrdinalIgnoreCase) || fileFormat.Equals("QIF", StringComparison.OrdinalIgnoreCase))
        {
            fileContent = System.Text.Encoding.UTF8.GetString(fileBytes);
            var ofxLines = fileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Simplified OFX/QIF parsing: count transactions from content structure
            totalTransactions = ofxLines.Count(l => l.Contains("<STMTTRN>") || l.StartsWith("D", StringComparison.OrdinalIgnoreCase));
            newTransactions = totalTransactions; // Simplified: treat all as new
        }
        else
        {
            throw new InvalidOperationException($"รูปแบบไฟล์ '{fileFormat}' ยังไม่รองรับ");
        }

        // ═══ จับคู่อัตโนมัติ — เดินด่านเดียวกับทุกทางเข้า ═══
        // ของเดิม (`DECISION_AUDIT_2026-09-18.md` §3 D4-1, P0) มี 3 บั๊ก:
        //  1. คิวรี `BankTransactions` **ไม่มี `CompanyId`** ⇒ ละเมิด tenant
        //     isolation (กฎเหล็ก #2 M) — ไฟล์ statement ของบริษัทหนึ่งไป
        //     ประทับสถานะให้บรรทัดของอีกบริษัทที่บังเอิญใช้ bankAccountId เดียวกัน
        //  2. โหลด `Payments` **ทั้งบริษัทโดยไม่กันตัวที่ถูกจับไปแล้ว** ⇒
        //     รายการชำระใบเดียวถูกจับคู่กับหลายบรรทัดธนาคาร (double-match)
        //  3. `FirstOrDefault(ยอดตรง + ≤3 วัน)` = **ใครมาก่อนชนะ** ไม่ดูชื่อ
        //     ผู้โอน แล้วประทับ `Matched` เอง
        var alreadyPairedPaymentIds = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && t.MatchedPaymentId != null)
            .Select(t => t.MatchedPaymentId!.Value)
            .ToListAsync();
        var takenPaymentIds = new HashSet<Guid>(alreadyPairedPaymentIds);

        var candidatePayments = await _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.Document.Status != DocumentStatus.Voided)
            .ToListAsync();
        await Accounting.Helpers.ContactHydration.HydratePaymentContactsAsync(
            _db, companyId, candidatePayments);

        var newUnmatched = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId
                      && t.BankAccountId == bankAccountId
                      && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ToListAsync();

        foreach (var tx in newUnmatched)
        {
            var options = new List<Accounting.Helpers.BankMatchArbiter.Option>();
            foreach (var p in candidatePayments)
            {
                if (takenPaymentIds.Contains(p.Id)) continue;
                if (Math.Abs((p.PaymentDate.Date - tx.TransactionDate.Date).TotalDays) > 3) continue;

                var sc = Accounting.Helpers.BankMatchScorer.Score(
                    new Accounting.Helpers.BankMatchScorer.Input(
                        CandidateAmount: p.Amount,
                        BankAmount: Math.Abs(tx.Amount),
                        CandidateDate: p.PaymentDate,
                        BankDate: tx.TransactionDate,
                        CandidateRef: p.Reference,
                        CandidateNotes: p.Notes,
                        CandidateDocNumber: p.Document?.DocumentNumber,
                        CandidateName: p.Document?.Contact?.Name,
                        BankDescription: tx.Description,
                        BankReference: tx.Reference,
                        BankPayee: tx.Payee));
                options.Add(new Accounting.Helpers.BankMatchArbiter.Option(
                    p.Id, "Payment", sc.Score, sc.HasIdentitySignal, sc.Reason));
            }

            var decision = Accounting.Helpers.BankMatchArbiter.Decide(options);
            if (decision.Verdict == Accounting.Helpers.BankMatchVerdict.None) continue;

            // ทุกสถานะที่เขียนต้องมี id ของคู่เสมอ — ห้ามประทับกับความว่าง
            tx.MatchedPaymentId = decision.Chosen!.Id;
            takenPaymentIds.Add(decision.Chosen.Id);

            if (decision.Verdict == Accounting.Helpers.BankMatchVerdict.Apply)
            {
                tx.ReconciliationStatus = ReconciliationStatus.Matched;
                tx.ReconciledAt = DateTime.UtcNow;
                tx.ReconciledBy = "OpenBanking";
                autoMatched++;
            }
            else
            {
                tx.ReconciliationStatus = ReconciliationStatus.Suggested;
                tx.ReconciledBy = "OpenBanking (เสนอ รอยืนยัน)";
            }
        }

        var import = new BankFeedImport
        {
            CompanyId = companyId,
            BankConnectionId = null, // File import — no connection (FK is nullable)
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
            .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId)
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

    private string EncryptString(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(_encryptionKey));
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }
}
