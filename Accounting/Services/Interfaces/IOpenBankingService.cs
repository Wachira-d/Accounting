using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IOpenBankingService
{
    // Connections
    Task<BankConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankConnectionRequest request);
    Task<List<BankConnectionResponse>> GetConnectionsAsync(Guid companyId);
    Task<BankConnectionResponse> UpdateConnectionAsync(Guid companyId, Guid connectionId, UpdateBankConnectionRequest request);
    Task DeleteConnectionAsync(Guid companyId, Guid connectionId);

    // Sync
    Task<BankFeedImportResponse> SyncTransactionsAsync(Guid companyId, Guid connectionId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<BankFeedImportResponse>> GetImportHistoryAsync(Guid companyId, Guid connectionId);
    Task ProcessAutoSyncAsync();

    // File import (OFX, QIF, CSV)
    Task<BankFeedImportResponse> ImportFileAsync(Guid companyId, Guid bankAccountId, string fileFormat, string base64Content);
}

public record CreateBankConnectionRequest(string BankCode, string BankName, string ConnectionType, string? ApiEndpoint, string? ClientId, string? Credentials, bool AutoSync, int SyncIntervalMinutes, Guid? LinkedBankAccountId);
public record UpdateBankConnectionRequest(bool? AutoSync, int? SyncIntervalMinutes, string? Credentials);
public record BankConnectionResponse(Guid Id, string BankCode, string BankName, string ConnectionType, string Status, bool AutoSync, int SyncIntervalMinutes, DateTime? LastSyncAt, string? LastSyncStatus, Guid? LinkedBankAccountId);

public record BankFeedImportResponse(Guid Id, DateTime ImportDate, DateTime PeriodStart, DateTime PeriodEnd, int TotalTransactions, int NewTransactions, int DuplicateSkipped, int AutoMatched, string Status, string? ErrorMessage);
