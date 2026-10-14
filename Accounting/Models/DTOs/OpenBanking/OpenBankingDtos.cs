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
    string Status, string? ErrorMessage,
    // รอบ 205 ทีม C: คำเตือนของการนำเข้าไฟล์ (ยอดไม่ต่อเนื่อง · ไม่มียอดคงเหลือ · เลขบัญชีในไฟล์ไม่ตรง · แถวที่ข้าม) — ผู้ใช้ต้องเห็น
    List<string>? Warnings = null);

public record SyncBankFeedRequest(DateTime? FromDate, DateTime? ToDate);
