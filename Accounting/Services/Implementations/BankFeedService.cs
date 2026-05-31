using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class BankFeedService : IBankFeedService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BankFeedService> _logger;
    private readonly ISecretProtector _secrets;
    private readonly Accounting.Services.Ai.IBankAiAugmenter? _aiAugmenter;

    private static readonly Dictionary<string, string> BankApiEndpoints = new()
    {
        { "SCB", "https://api-sandbox.partners.scb/partners/sandbox" },
        { "KBANK", "https://openapi.kasikornbank.com" },
        { "BBL", "https://api.bangkokbank.com" },
        { "BAY", "https://api.krungsri.com" },
        { "KTB", "https://api.krungthai.com" },
        { "TTB", "https://api.ttbbank.com" }
    };

    public BankFeedService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        ILogger<BankFeedService> logger, ISecretProtector secrets,
        Accounting.Services.Ai.IBankAiAugmenter? aiAugmenter = null)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _secrets = secrets;
        _aiAugmenter = aiAugmenter;
    }

    public async Task<BankFeedConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankFeedConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BankCode))
            throw new InvalidOperationException("กรุณาระบุรหัสธนาคาร");

        var conn = new BankConnection
        {
            CompanyId = companyId,
            BankCode = request.BankCode.ToUpper(),
            BankName = request.BankName,
            ConnectionType = request.ConnectionType ?? "API",
            ApiEndpoint = request.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(request.BankCode.ToUpper()),
            ClientId = request.ClientId,
            EncryptedCredentials = _secrets.Protect(request.ClientSecret),
            AccessToken = _secrets.Protect(request.AccessToken),
            AutoSync = request.AutoSync,
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            LinkedBankAccountId = request.LinkedBankAccountId,
            Status = "Pending"
        };

        _db.BankConnections.Add(conn);
        await _db.SaveChangesAsync();

        return MapToResponse(conn);
    }

    public async Task<List<BankFeedConnectionResponse>> GetConnectionsAsync(Guid companyId)
    {
        return await _db.BankConnections
            .Where(c => c.CompanyId == companyId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => MapToResponse(c))
            .ToListAsync();
    }

    public async Task<BankFeedConnectionResponse> GetConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");
        return MapToResponse(conn);
    }

    public async Task<BankFeedSyncResult> SyncAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        if (conn.Status == "Disabled")
            throw new InvalidOperationException("การเชื่อมต่อถูกปิดใช้งาน");

        if (!conn.LinkedBankAccountId.HasValue)
            throw new InvalidOperationException("กรุณาเชื่อมโยงบัญชีธนาคารก่อนทำ Sync");

        try
        {
            var transactions = await FetchTransactionsFromBankAsync(conn);

            var existingRefs = await _db.BankTransactions
                .Where(t => t.CompanyId == companyId && t.BankAccountId == conn.LinkedBankAccountId)
                .Select(t => t.Reference)
                .Where(r => r != null)
                .ToListAsync();

            int newCount = 0, duplicateCount = 0, autoMatched = 0;

            foreach (var txn in transactions)
            {
                if (!string.IsNullOrEmpty(txn.Reference) && existingRefs.Contains(txn.Reference))
                {
                    duplicateCount++;
                    continue;
                }

                var bankTxn = new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = conn.LinkedBankAccountId!.Value,
                    TransactionDate = txn.Date,
                    Amount = Math.Abs(txn.Amount),
                    TransactionType = txn.Amount >= 0
                        ? Models.Enums.BankTransactionType.Deposit
                        : Models.Enums.BankTransactionType.Withdrawal,
                    Description = txn.Description,
                    Reference = txn.Reference,
                    ReconciliationStatus = Models.Enums.ReconciliationStatus.Unmatched
                };

                _db.BankTransactions.Add(bankTxn);
                newCount++;

                if (await TryAutoMatchAsync(companyId, bankTxn))
                    autoMatched++;
            }

            var feedImport = new BankFeedImport
            {
                CompanyId = companyId,
                BankConnectionId = connectionId,
                ImportDate = DateTime.UtcNow,
                PeriodStart = transactions.Any() ? transactions.Min(t => t.Date) : DateTime.UtcNow,
                PeriodEnd = transactions.Any() ? transactions.Max(t => t.Date) : DateTime.UtcNow,
                TotalTransactions = transactions.Count,
                NewTransactions = newCount,
                DuplicateSkipped = duplicateCount,
                AutoMatched = autoMatched,
                Status = "Completed"
            };

            _db.BankFeedImports.Add(feedImport);

            conn.LastSyncAt = DateTime.UtcNow;
            conn.LastSyncStatus = "Success";
            conn.LastError = null;
            conn.Status = "Active";

            await _db.SaveChangesAsync();

            return new BankFeedSyncResult(connectionId, conn.BankName,
                transactions.Count, newCount, duplicateCount, autoMatched, "Success", null);
        }
        catch (Exception ex)
        {
            conn.LastSyncAt = DateTime.UtcNow;
            conn.LastSyncStatus = "Failed";
            conn.LastError = ex.Message;
            await _db.SaveChangesAsync();

            _logger.LogError(ex, "Bank feed sync failed for {BankCode} connection {ConnectionId}", conn.BankCode, connectionId);
            return new BankFeedSyncResult(connectionId, conn.BankName, 0, 0, 0, 0, "Failed", ex.Message);
        }
    }

    public async Task<BankFeedSyncResult> SyncAllAsync(Guid companyId)
    {
        var connections = await _db.BankConnections
            .Where(c => c.CompanyId == companyId && c.AutoSync && c.Status == "Active")
            .ToListAsync();

        int totalNew = 0, totalDuplicates = 0, totalMatched = 0;

        foreach (var conn in connections)
        {
            var result = await SyncAsync(companyId, conn.Id);
            totalNew += result.NewTransactions;
            totalDuplicates += result.DuplicateSkipped;
            totalMatched += result.AutoMatched;
        }

        return new BankFeedSyncResult(Guid.Empty, "All Banks",
            totalNew + totalDuplicates, totalNew, totalDuplicates, totalMatched, "Success", null);
    }

    public async Task DeleteConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        conn.Status = "Disabled";
        conn.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<BankFeedConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = _secrets.Unprotect(conn.AccessToken);
            if (!string.IsNullOrEmpty(token))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var endpoint = conn.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(conn.BankCode, "");
            if (!string.IsNullOrEmpty(endpoint))
            {
                var response = await client.GetAsync($"{endpoint}/health");
                conn.Status = response.IsSuccessStatusCode ? "Active" : "Error";
                conn.LastSyncStatus = response.IsSuccessStatusCode ? "Connected" : $"HTTP {response.StatusCode}";
            }
            else
            {
                conn.Status = "Active";
                conn.LastSyncStatus = "Connected (no endpoint test)";
            }

            conn.LastError = null;
        }
        catch (Exception ex)
        {
            conn.Status = "Error";
            conn.LastSyncStatus = "Failed";
            conn.LastError = ex.Message;
        }

        await _db.SaveChangesAsync();
        return MapToResponse(conn);
    }

    private async Task<List<BankFeedTransaction>> FetchTransactionsFromBankAsync(BankConnection conn)
    {
        var client = _httpClientFactory.CreateClient();

        var token = _secrets.Unprotect(conn.AccessToken);
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var fromDate = conn.LastSyncAt?.AddDays(-1) ?? DateTime.UtcNow.AddDays(-30);
        var toDate = DateTime.UtcNow;

        var endpoint = conn.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(conn.BankCode, "");
        if (string.IsNullOrEmpty(endpoint))
            return new List<BankFeedTransaction>();

        try
        {
            var url = $"{endpoint}/v1/accounts/transactions?from={fromDate:yyyy-MM-dd}&to={toDate:yyyy-MM-dd}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bank API returned {StatusCode} for {BankCode}", response.StatusCode, conn.BankCode);
                return new List<BankFeedTransaction>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var transactions = System.Text.Json.JsonSerializer.Deserialize<List<BankFeedTransaction>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return transactions ?? new List<BankFeedTransaction>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch transactions from {BankCode}", conn.BankCode);
            throw new InvalidOperationException($"ไม่สามารถเชื่อมต่อ {conn.BankName} ได้: {ex.Message}");
        }
    }

    private async Task<bool> TryAutoMatchAsync(Guid companyId, BankTransaction txn)
    {
        if (string.IsNullOrWhiteSpace(txn.Description) && string.IsNullOrWhiteSpace(txn.Reference))
            return false;

        // Deposits match revenue docs (Invoice/TaxInvoice), withdrawals match expense docs (PurchaseInvoice)
        var revenueTypes = new[] { Models.Enums.DocumentType.Invoice, Models.Enums.DocumentType.TaxInvoice };
        var expenseTypes = new[] { Models.Enums.DocumentType.PurchaseInvoice, Models.Enums.DocumentType.CertificateInLieu };
        var matchTypes = txn.TransactionType == Models.Enums.BankTransactionType.Deposit ? revenueTypes : expenseTypes;

        var matchedDoc = await _db.Documents
            .FirstOrDefaultAsync(d => d.CompanyId == companyId
                && d.TotalAmount == txn.Amount
                && matchTypes.Contains(d.DocumentType)
                && d.Status != Models.Enums.DocumentStatus.Paid
                && d.Status != Models.Enums.DocumentStatus.Voided
                && d.Status != Models.Enums.DocumentStatus.Draft
                && (d.DocumentNumber == txn.Reference
                    || (txn.Description != null && d.Reference != null && txn.Description.Contains(d.Reference))));

        if (matchedDoc != null)
        {
            txn.ReconciliationStatus = Models.Enums.ReconciliationStatus.Matched;
            return true;
        }

        // ── AI fallback when exact heuristic missed ──
        // Local matcher is strict (exact amount + same matchTypes +
        // memo contains DocNumber/Reference). When it misses, fire AI
        // augmenter against open docs in a ±15% / ±14d window. AI may
        // see a match the strict heuristic couldn't (memo says
        // "settlement for INV2025-0312" even though we strip-matched
        // wrong, or amount differs by bank fee). Reports the match as
        // a "suggested" status — user still has to confirm in the
        // bank reconciliation UI before it's auto-applied.
        if (_aiAugmenter == null) return false;
        try
        {
            using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var memo = string.IsNullOrEmpty(txn.Reference) ? txn.Description : $"{txn.Reference} {txn.Description}";
            var aiResult = await _aiAugmenter.SuggestStatementMatchAsync(
                companyId, txn.Id, memo, txn.TransactionDate, txn.Amount, "THB",
                txn.TransactionType, localBestDocumentId: null, localConfidence: 0m,
                aiCts.Token);
            if (aiResult.UsedAi
                && !string.IsNullOrEmpty(aiResult.Answer) && aiResult.Answer != "__NEW__"
                && (aiResult.Confidence ?? 0m) >= 0.75m
                && Guid.TryParse(aiResult.Answer, out var aiMatchedId))
            {
                var verified = await _db.Documents.AnyAsync(d => d.Id == aiMatchedId
                    && d.CompanyId == companyId && !d.IsDeleted
                    && d.Status != Models.Enums.DocumentStatus.Voided);
                if (verified)
                {
                    txn.ReconciliationStatus = Models.Enums.ReconciliationStatus.Suggested;
                    _logger.LogInformation("Bank txn {Txn} → AI-suggested match {Doc} ({Conf:P0})",
                        txn.Id, aiMatchedId, aiResult.Confidence ?? 0m);
                    return true;
                }
            }
        }
        catch (Exception aiEx)
        {
            _logger.LogWarning(aiEx, "AI bank-match fallback failed for txn {Id} — leaving Unmatched", txn.Id);
        }

        return false;
    }

    private static BankFeedConnectionResponse MapToResponse(BankConnection c) => new(
        c.Id, c.BankCode, c.BankName, c.ConnectionType, c.Status,
        c.LastSyncAt, c.LastSyncStatus, c.LastError, c.AutoSync,
        c.SyncIntervalMinutes, c.LinkedBankAccountId);
}

internal record BankFeedTransaction(
    DateTime Date,
    decimal Amount,
    string? Description,
    string? Reference,
    string? CounterParty);
