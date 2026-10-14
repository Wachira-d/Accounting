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
    /// <summary>นับเฉพาะคำขอที่รอผู้ใช้คนนี้อนุมัติ (ผู้อนุมัติของขั้นปัจจุบัน — ด่านเดียวกับ SubmitActionAsync)</summary>
    Task<MobileQuickActionsResponse> GetQuickActionsAsync(Guid companyId, Guid userId);
    /// <param name="acknowledgeWarnings">รอบ 193: ผู้ใช้กด "รับทราบ" คำเตือนยอดจากสแกนแล้ว — false + มีคำเตือน ⇒ คืน
    /// <c>RequiresAcknowledgement = true</c> พร้อมรายการ (ไม่อนุมัติ) ให้แอปแสดงแล้วเรียกซ้ำด้วย true</param>
    Task<MobileApprovalResponse> QuickApproveAsync(Guid companyId, Guid entityId, string entityType, string action, Guid userId,
        bool acknowledgeWarnings = false);
}
