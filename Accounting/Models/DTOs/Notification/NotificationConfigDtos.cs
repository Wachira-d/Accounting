namespace Accounting.Models.DTOs.Notification;

public record NotificationEventCatalogItem(
    string Domain,
    string Key,
    string Label);

public record NotificationCatalogResponse(
    List<NotificationEventCatalogItem> Events,
    List<string> RecipientRoles);

public record NotificationSettingDto(
    string EventKey,
    string RecipientRole,
    bool EnableSystem,
    bool EnableEmail,
    bool EnableLine);

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
