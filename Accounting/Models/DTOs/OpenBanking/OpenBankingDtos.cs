namespace Accounting.Models.DTOs.OpenBanking;

public record CreateBankConnectionRequest(
    string BankCode, string BankName, string ConnectionType,
    string? ApiEndpoint, string? ClientId, string? Credentials,
    Guid? LinkedBankAccountId = null, bool AutoSync = false,
    int SyncIntervalMinutes = 60);

public record UpdateBankConnectionRequest(
    bool? AutoSync = null, int? SyncIntervalMinutes = null,
    string? Credentials = null, bool? IsActive = null);

public record BankConnectionResponse(
    Guid Id, string BankCode, string BankName, string ConnectionType,
    string Status, bool AutoSync, int SyncIntervalMinutes,
    DateTime? LastSyncAt, string? LastSyncStatus, Guid? LinkedBankAccountId);

public record BankFeedImportResponse(
    Guid Id, DateTime ImportDate, DateTime? PeriodStart, DateTime? PeriodEnd,
    int TotalTransactions, int NewTransactions, int DuplicateSkipped, int AutoMatched,
    string Status, string? ErrorMessage);

public record SyncBankFeedRequest(DateTime? FromDate, DateTime? ToDate);
