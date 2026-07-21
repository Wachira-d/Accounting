namespace Accounting.Models.DTOs.Mobile;

public record RegisterDeviceRequest(
    string DeviceToken, string Platform, string? DeviceName, string? AppVersion);

public record DeviceRegistrationResponse(Guid Id, string DeviceToken, string Platform, bool IsActive);

public record SyncRequest(DateTime? LastSyncAt, List<string>? EntityTypes, List<SyncQueueItem>? LocalChanges = null);

public record SyncResponse(
    DateTime ServerTime, List<SyncQueueResponse> Changes, List<SyncConflict> Conflicts);

public record MobileDashboardResponse(
    decimal CashBalance, decimal TotalReceivables, decimal TotalPayables,
    int PendingApprovals, int OverdueInvoices, decimal TodayRevenue,
    decimal MonthRevenue, List<MobileAlertResponse> Alerts);

public record MobileChartDataPoint(string Label, decimal Value);

public record MobileQuickActionsResponse(
    bool CanApproveDocuments, bool CanApproveExpenses, bool CanApprovePayroll,
    int PendingDocuments, int PendingExpenses, int PendingPayroll);

public record MobileQuickAction(string ActionType, string Title, string? EntityType, Guid? EntityId);

public record MobileApprovalResponse(
    bool Success, string? Message, string EntityType, Guid EntityId);

public record SyncQueueItem(string EntityType, Guid EntityId, string OperationType, string PayloadJson);
public record SyncQueueResponse(string EntityType, Guid EntityId, string OperationType, string PayloadJson, DateTime ChangedAt);
public record SyncConflict(string EntityType, Guid EntityId, string ServerVersion, string ClientVersion, string Resolution);

public record MobileAlertResponse(string Type, string Title, string Message, string? ActionUrl, DateTime CreatedAt);
