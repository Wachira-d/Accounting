using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IBankFeedService
{
    Task<BankFeedConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankFeedConnectionRequest request);
    Task<List<BankFeedConnectionResponse>> GetConnectionsAsync(Guid companyId);
    Task<BankFeedConnectionResponse> GetConnectionAsync(Guid companyId, Guid connectionId);
    Task<BankFeedSyncResult> SyncAsync(Guid companyId, Guid connectionId);
    Task<BankFeedSyncResult> SyncAllAsync(Guid companyId);
    Task DeleteConnectionAsync(Guid companyId, Guid connectionId);
    Task<BankFeedConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId);
}

public record CreateBankFeedConnectionRequest(
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

public record BankFeedConnectionResponse(
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
