using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>
/// CRUD for the NotificationSettings matrix (per-company admin) and
/// NotificationPreferences (per-user opt-outs). Decoupled from the
/// dispatcher so the admin UI can edit settings without going through
/// the engine.
/// </summary>
public interface INotificationConfigService
{
    NotificationCatalogResponse GetCatalog();

    Task<List<NotificationSettingDto>> GetCompanySettingsAsync(Guid companyId);
    Task BulkUpsertCompanySettingsAsync(Guid companyId, BulkUpdateNotificationSettingsRequest request, string updatedBy);

    Task<List<NotificationPreferenceDto>> GetUserPreferencesAsync(Guid companyId, Guid userId);
    Task BulkUpsertUserPreferencesAsync(Guid companyId, Guid userId, BulkUpdateNotificationPreferencesRequest request);

    Task<LineBindingResponse> GetLineBindingAsync(Guid userId);
    /// <summary>รอบ 258 (A-H2): ถอดการผูก LINE ของตัวเอง + บันทึก audit (การผูกทำผ่านรหัสที่บอทเท่านั้น — ไม่มีทางตั้ง userId ดิบ)</summary>
    Task<LineBindingResponse> ClearLineBindingAsync(Guid userId, Guid? companyId = null, string? ipAddress = null);
}
