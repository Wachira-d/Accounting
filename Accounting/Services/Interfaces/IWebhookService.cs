using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IWebhookService
{
    // Registration
    Task<WebhookRegistrationResponse> RegisterAsync(Guid companyId, CreateWebhookRequest request);
    Task<List<WebhookRegistrationResponse>> GetRegistrationsAsync(Guid companyId);
    Task<WebhookRegistrationResponse> UpdateAsync(Guid companyId, Guid webhookId, UpdateWebhookRequest request);
    Task DeleteAsync(Guid companyId, Guid webhookId);
    Task<WebhookRegistrationResponse> TestAsync(Guid companyId, Guid webhookId);

    // Delivery
    Task TriggerAsync(Guid companyId, string eventType, object payload);
    Task<PagedResponse<WebhookDeliveryResponse>> GetDeliveriesAsync(Guid companyId, Guid webhookId, PagedRequest request);
    Task RetryAsync(Guid companyId, Guid deliveryId);

    // Available events
    Task<List<WebhookEventTypeResponse>> GetEventTypesAsync();
}

public record CreateWebhookRequest(string Name, string Url, string? Secret, string EventTypes, int MaxRetries, int TimeoutSeconds, string? HeadersJson);
public record UpdateWebhookRequest(string? Name, string? Url, string? Secret, string? EventTypes, bool? IsActive, int? MaxRetries, int? TimeoutSeconds);
public record WebhookRegistrationResponse(Guid Id, string Name, string Url, string EventTypes, bool IsActive, int MaxRetries, int FailureCount, DateTime? LastTriggeredAt, DateTime? LastSuccessAt, string? LastError);

public record WebhookDeliveryResponse(Guid Id, string EventType, int HttpStatusCode, bool IsSuccess, int AttemptNumber, decimal DurationMs, DateTime DeliveredAt, string? ErrorMessage);
public record WebhookEventTypeResponse(string EventType, string Description, string? SamplePayloadJson);
