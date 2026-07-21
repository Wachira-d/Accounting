using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Webhook;

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
