namespace Accounting.Models.DTOs.Webhook;

public record CreateWebhookRequest(
    string Name, string Url, string Secret, string EventTypes,
    int MaxRetries = 3, int TimeoutSeconds = 30, string? HeadersJson = null);

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
