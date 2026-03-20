namespace Accounting.Models.DTOs.Mobile;

public record RegisterDeviceRequest(
    string DeviceToken, string Platform, string? DeviceName, string? AppVersion);

public record DeviceRegistrationResponse(Guid Id, string DeviceToken, string Platform, DateTime RegisteredAt);

public record SyncRequest(DateTime? LastSyncAt, List<string>? EntityTypes);

public record SyncResponse(
    DateTime ServerTime, Dictionary<string, int> ChangeCounts,
    bool HasMore, DateTime? NextSyncFrom);

public record MobileDashboardResponse(
    decimal TotalRevenue, decimal TotalExpense, decimal NetProfit,
    decimal CashBalance, int PendingApprovals, int OverdueInvoices,
    List<MobileChartDataPoint> RevenueChart);

public record MobileChartDataPoint(string Label, decimal Value);

public record MobileQuickActionsResponse(
    int PendingApprovals, int DraftDocuments, int UnreconciledTransactions,
    List<MobileQuickAction> Actions);

public record MobileQuickAction(string ActionType, string Title, string? EntityType, Guid? EntityId);

public record MobileApprovalResponse(
    Guid EntityId, string EntityType, string Action, bool Success, string? Message);
