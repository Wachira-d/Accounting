using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Mobile;

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
