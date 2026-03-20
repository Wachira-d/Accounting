namespace Accounting.Models.DTOs.OpenBanking;

public record CreateBankConnectionRequest(
    string BankCode, string BankName, string ConnectionType,
    string? ApiEndpoint, Guid BankAccountId, bool AutoSync = false,
    int SyncIntervalMinutes = 60);

public record UpdateBankConnectionRequest(
    bool? AutoSync = null, int? SyncIntervalMinutes = null, bool? IsActive = null);

public record BankConnectionResponse(
    Guid Id, string BankCode, string BankName, string ConnectionType,
    Guid BankAccountId, string BankAccountName,
    bool AutoSync, int SyncIntervalMinutes, bool IsActive,
    string ConnectionStatus, DateTime? LastSyncAt, DateTime CreatedAt);

public record BankFeedImportResponse(
    Guid Id, Guid BankConnectionId, DateTime ImportDate,
    int TotalRecords, int ImportedRecords, int SkippedRecords,
    int ErrorRecords, string Status, string? ErrorMessage, DateTime CreatedAt);

public record SyncBankFeedRequest(DateTime? FromDate, DateTime? ToDate);
