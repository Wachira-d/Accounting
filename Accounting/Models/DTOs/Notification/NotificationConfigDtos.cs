namespace Accounting.Models.DTOs.Notification;

public record NotificationEventCatalogItem(
    string Domain,
    string Key,
    string Label);

public record NotificationCatalogResponse(
    List<NotificationEventCatalogItem> Events,
    List<string> RecipientRoles);

/// <param name="EnableLineGroup">รอบ 232: ประกาศเข้ากลุ่ม LINE ของบริษัท (OA ของบริษัท) หนึ่งครั้งต่อเหตุการณ์ — ไม่ขึ้นกับบทบาทผู้รับ</param>
/// <param name="IsDefault">รอบ 232: แถวนี้เป็นค่าตั้งต้นที่ระบบใช้อยู่ (บริษัทยังไม่ได้บันทึกเอง) — หน้าเว็บแสดงเป็นค่าที่ติ๊กไว้แล้ว</param>
public record NotificationSettingDto(
    string EventKey,
    string RecipientRole,
    bool EnableSystem,
    bool EnableEmail,
    bool EnableLine,
    bool EnableLineGroup = false,
    bool IsDefault = false);

public record BulkUpdateNotificationSettingsRequest(
    List<NotificationSettingDto> Settings);

public record NotificationPreferenceDto(
    string EventKey,
    bool SuppressSystem,
    bool SuppressEmail,
    bool SuppressLine);

public record BulkUpdateNotificationPreferencesRequest(
    List<NotificationPreferenceDto> Preferences);

public record LineBindingRequest(string LineUserId);
public record LineBindingResponse(string? LineUserId, bool IsBound);
