namespace Accounting.Models.DTOs.Webhook;

/// <summary>รอบ 193 · A08: <c>Secret</c> เป็น <c>string?</c> — ฟอร์มถือว่าไม่บังคับ (ไม่มีดอกจัน) และ
/// <c>ISecretProtector.Protect</c> รับ null อยู่แล้ว ⇒ เดิมสองชั้นขัดกัน (เว้นว่าง = 400 อังกฤษ).
/// <c>EventTypes</c> = รายชื่อ event คั่นด้วย "," (ไม่ใช่ array) — ตัวจับคู่ <c>MatchesEventType</c> split ด้วย ","</summary>
public record CreateWebhookRequest(
    string Name, string Url, string? Secret, string EventTypes,
    int MaxRetries = 3, int TimeoutSeconds = 30, string? HeadersJson = null,
    // ช่อง "เปิดใช้งาน" บนฟอร์มสร้าง — เดิมไม่มีที่ลง (สร้างแล้ว Active เสมอ = silent no-op)
    bool IsActive = true);

public record UpdateWebhookRequest(
    string? Name = null, string? Url = null, string? Secret = null,
    string? EventTypes = null, int? MaxRetries = null,
    int? TimeoutSeconds = null, bool? IsActive = null);

public record WebhookResponse(
    Guid Id, string Name, string Url, string EventTypes,
    int MaxRetries, int TimeoutSeconds, bool IsActive,
    int SuccessCount, int FailureCount, DateTime? LastTriggeredAt, DateTime CreatedAt);

public record WebhookDeliveryResponse(
    Guid Id, string EventType, int HttpStatusCode,
    bool IsSuccess, int AttemptNumber, int DurationMs,
    DateTime DeliveredAt, string? ErrorMessage);

public record WebhookRegistrationResponse(Guid Id, string Name, string Url, string EventTypes, bool IsActive, int MaxRetries, int FailureCount, DateTime? LastTriggeredAt, DateTime? LastSuccessAt, string? LastError);

public record WebhookEventTypeResponse(string EventType, string Description, string? SamplePayloadJson);
