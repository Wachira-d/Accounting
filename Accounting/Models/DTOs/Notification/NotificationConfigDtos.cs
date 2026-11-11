namespace Accounting.Models.DTOs.Notification;

public record NotificationEventCatalogItem(
    string Domain,
    string Key,
    string Label);

/// <param name="RoleLabels">รอบ 258: ป้ายไทยของบทบาทผู้รับ (เซิร์ฟเวอร์เป็นตัวตั้ง — หน้าเว็บแสดงอย่างเดียว)</param>
public record NotificationCatalogResponse(
    List<NotificationEventCatalogItem> Events,
    List<string> RecipientRoles,
    Dictionary<string, string>? RoleLabels = null);

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

/// <summary>รอบ 258 (A-H3 · E-H1): แถวที่ผู้เรียกส่งมาบันทึก — <c>EnableLineGroup</c> เป็น <c>bool?</c>:
/// <b>null (ไม่ได้ส่ง) = คงค่าที่ระบบใช้อยู่</b> ไม่ใช่ "ปิด" (ตัวตัดสิน <c>Helpers/NotificationSettingMerge</c>) —
/// เดิมผู้เรียกที่ไม่รู้จักช่องนี้ทำให้ประกาศเข้ากลุ่ม LINE ของทีมถูกปิดเงียบทุกเหตุการณ์</summary>
public record NotificationSettingUpdateDto(
    string EventKey,
    string RecipientRole,
    bool EnableSystem,
    bool EnableEmail,
    bool EnableLine,
    bool? EnableLineGroup = null);

public record BulkUpdateNotificationSettingsRequest(
    List<NotificationSettingUpdateDto> Settings);

public record NotificationPreferenceDto(
    string EventKey,
    bool SuppressSystem,
    bool SuppressEmail,
    bool SuppressLine);

public record BulkUpdateNotificationPreferencesRequest(
    List<NotificationPreferenceDto> Preferences);

/// <summary>รอบ 258 (A-H2): สถานะการผูก LINE ของผู้ใช้ — <b>ไม่ส่ง LINE userId เต็ม</b> (เป็นกุญแจยืนยันตัวของบอท) ส่งแค่ท้าย 6 ตัว ·
/// การผูกทำได้ทางเดียวคือรหัส 6 หลักที่หน้า <c>line-bind.html</c> (จำกัดความถี่ · audit · อีเมลแจ้งเจ้าของบัญชี) — ทางพิมพ์ userId ดิบถูกถอดแล้ว</summary>
public record LineBindingResponse(bool IsBound, string? LineUserIdTail);
