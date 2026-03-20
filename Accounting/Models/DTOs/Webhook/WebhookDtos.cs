namespace Accounting.Models.DTOs.Webhook;

public record CreateWebhookRequest(
    string Name, string Url, string Secret, string EventTypes,
    int MaxRetries = 3, int TimeoutSeconds = 30, string? HeadersJson = null);

public record UpdateWebhookRequest(
    string? Name = null, string? Url = null, string? EventTypes = null,
    int? MaxRetries = null, bool? IsActive = null);

public record WebhookResponse(
    Guid Id, string Name, string Url, string EventTypes,
    int MaxRetries, int TimeoutSeconds, bool IsActive,
    int SuccessCount, int FailureCount, DateTime? LastTriggeredAt, DateTime CreatedAt);

public record WebhookDeliveryResponse(
    Guid Id, Guid WebhookId, string EventType, string PayloadJson,
    int HttpStatusCode, string? ResponseBody, int AttemptNumber,
    bool IsSuccess, DateTime DeliveredAt);
