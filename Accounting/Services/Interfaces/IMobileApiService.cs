using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IMobileApiService
{
    // Device management
    Task<DeviceRegistrationResponse> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request);
    Task UnregisterDeviceAsync(Guid userId, string deviceToken);
    Task SendPushNotificationAsync(Guid userId, string title, string body, string? actionUrl = null);

    // Offline sync
    Task<SyncResponse> SyncAsync(Guid companyId, Guid userId, SyncRequest request);
    Task<List<SyncQueueResponse>> GetPendingChangesAsync(Guid companyId, Guid userId, DateTime? since = null);

    // Mobile-optimized endpoints
    Task<MobileDashboardResponse> GetMobileDashboardAsync(Guid companyId);
    Task<MobileQuickActionsResponse> GetQuickActionsAsync(Guid companyId);
    Task<MobileApprovalResponse> QuickApproveAsync(Guid companyId, Guid entityId, string entityType, string action, Guid userId);
}

public record RegisterDeviceRequest(string DeviceToken, string Platform, string? DeviceName, string? AppVersion);
public record DeviceRegistrationResponse(Guid Id, string DeviceToken, string Platform, bool IsActive);

public record SyncRequest(DateTime? LastSyncAt, List<SyncQueueItem>? LocalChanges);
public record SyncQueueItem(string EntityType, Guid EntityId, string OperationType, string PayloadJson);
public record SyncResponse(DateTime SyncTimestamp, List<SyncQueueResponse> ServerChanges, List<SyncConflict> Conflicts);
public record SyncQueueResponse(string EntityType, Guid EntityId, string OperationType, string PayloadJson, DateTime ChangedAt);
public record SyncConflict(string EntityType, Guid EntityId, string ServerVersion, string ClientVersion, string Resolution);

public record MobileDashboardResponse(decimal CashBalance, decimal TotalReceivables, decimal TotalPayables, int PendingApprovals, int OverdueInvoices, decimal TodayRevenue, decimal MonthRevenue, List<MobileAlertResponse> Alerts);
public record MobileAlertResponse(string Type, string Title, string Message, string? ActionUrl, DateTime CreatedAt);
public record MobileQuickActionsResponse(bool CanApproveDocuments, bool CanApproveExpenses, bool CanApprovePayroll, int PendingDocuments, int PendingExpenses, int PendingPayroll);
public record MobileApprovalResponse(bool Success, string Message, string EntityType, Guid EntityId);
