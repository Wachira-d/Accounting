using Accounting.Models.DTOs.Integration;

namespace Accounting.Services.Interfaces;

public interface IIntegrationService
{
    // Integration config
    Task<List<IntegrationResponse>> GetIntegrationsAsync(Guid companyId);
    Task<IntegrationCreatedResponse> CreateIntegrationAsync(Guid companyId, CreateIntegrationRequest request);
    Task<IntegrationResponse> UpdateIntegrationAsync(Guid companyId, Guid integrationId, UpdateIntegrationRequest request);
    Task DeleteIntegrationAsync(Guid companyId, Guid integrationId);
    Task<IntegrationResponse> RegenerateApiKeyAsync(Guid companyId, Guid integrationId);

    // Account mapping
    Task<List<AccountMappingResponse>> GetMappingsAsync(Guid companyId, Guid integrationId);
    Task<AccountMappingResponse> CreateMappingAsync(Guid companyId, Guid integrationId, CreateAccountMappingRequest request);
    Task<AccountMappingResponse> UpdateMappingAsync(Guid companyId, Guid integrationId, Guid mappingId, UpdateAccountMappingRequest request);
    Task DeleteMappingAsync(Guid companyId, Guid integrationId, Guid mappingId);

    // Sync logs
    Task<List<SyncLogResponse>> GetSyncLogsAsync(Guid companyId, Guid? integrationId, int page = 1, int pageSize = 50);

    // Dashboard
    Task<IntegrationDashboardResponse> GetDashboardAsync(Guid companyId);

    // Inbound data processing
    Task<InboundSyncResponse> ProcessCustomerAsync(Guid companyId, Guid integrationId, InboundCustomerRequest request);
    Task<InboundSyncResponse> ProcessInvoiceAsync(Guid companyId, Guid integrationId, InboundInvoiceRequest request);
    Task<InboundSyncResponse> ProcessPaymentAsync(Guid companyId, Guid integrationId, InboundPaymentRequest request);
    Task<InboundSyncResponse> ProcessCreditNoteAsync(Guid companyId, Guid integrationId, InboundCreditNoteRequest request);
    Task<InboundSyncResponse> ProcessDebitNoteAsync(Guid companyId, Guid integrationId, InboundDebitNoteRequest request);
    Task<InboundSyncResponse> ProcessDailySummaryAsync(Guid companyId, Guid integrationId, InboundDailySummaryRequest request);

    // Authentication
    Task<(Guid CompanyId, Guid IntegrationId)?> ValidateApiKeyAsync(string apiKey);

    // Revenue Reports
    Task<List<RevenueByCategoryItem>> GetRevenueByCategoryAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<List<RevenueBySourceItem>> GetRevenueBySourceAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<DepositSummaryResponse> GetDepositSummaryAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<List<DailyRevenueItem>> GetDailyRevenueAsync(Guid companyId, DateTime? from, DateTime? to);
}
