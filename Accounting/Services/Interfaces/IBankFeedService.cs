using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IBankFeedService
{
    Task<BankConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankConnectionRequest request);
    Task<List<BankConnectionResponse>> GetConnectionsAsync(Guid companyId);
    Task<BankConnectionResponse> GetConnectionAsync(Guid companyId, Guid connectionId);
    Task<BankFeedSyncResult> SyncAsync(Guid companyId, Guid connectionId);
    Task<BankFeedSyncResult> SyncAllAsync(Guid companyId);
    Task DeleteConnectionAsync(Guid companyId, Guid connectionId);
    Task<BankConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId);
}

public record CreateBankConnectionRequest(
    string BankCode,
    string BankName,
    string ConnectionType,
    string? ApiEndpoint,
    string? ClientId,
    string? ClientSecret,
    string? AccessToken,
    bool AutoSync = false,
    int SyncIntervalMinutes = 60,
    Guid? LinkedBankAccountId = null);

public record BankConnectionResponse(
    Guid Id,
    string BankCode,
    string BankName,
    string ConnectionType,
    string Status,
    DateTime? LastSyncAt,
    string? LastSyncStatus,
    string? LastError,
    bool AutoSync,
    int SyncIntervalMinutes,
    Guid? LinkedBankAccountId);

public record BankFeedSyncResult(
    Guid ConnectionId,
    string BankName,
    int TotalTransactions,
    int NewTransactions,
    int DuplicateSkipped,
    int AutoMatched,
    string Status,
    string? ErrorMessage);
