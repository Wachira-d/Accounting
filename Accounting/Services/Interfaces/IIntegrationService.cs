using Accounting.Models.DTOs.Integration;

namespace Accounting.Services.Interfaces;

public interface IIntegrationService
{
    // Integration config
    Task<List<IntegrationResponse>> GetIntegrationsAsync(Guid companyId);
    Task<IntegrationCreatedResponse> CreateIntegrationAsync(Guid companyId, CreateIntegrationRequest request);
    Task<IntegrationResponse> UpdateIntegrationAsync(Guid companyId, Guid integrationId, UpdateIntegrationRequest request);
    Task DeleteIntegrationAsync(Guid companyId, Guid integrationId);
    Task<IntegrationCreatedResponse> RegenerateApiKeyAsync(Guid companyId, Guid integrationId);

    // Account mapping
    Task<List<AccountMappingResponse>> GetMappingsAsync(Guid companyId, Guid integrationId);
    Task<AccountMappingResponse> CreateMappingAsync(Guid companyId, Guid integrationId, CreateAccountMappingRequest request);
    Task<AccountMappingResponse> UpdateMappingAsync(Guid companyId, Guid integrationId, Guid mappingId, UpdateAccountMappingRequest request);
    Task DeleteMappingAsync(Guid companyId, Guid integrationId, Guid mappingId);

    // Mapping templates
    List<MappingTemplateResponse> GetMappingTemplates();

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
    Task<InboundSyncResponse> ProcessExpenseAsync(Guid companyId, Guid integrationId, InboundExpenseRequest request);
    /// <summary>สร้างใบสำคัญจ่าย (จ่ายเงินจริงแล้ว) จาก partner — เอกสารเดียวจบ:
    /// Dr ค่าใช้จ่าย+ภาษีซื้อ / Cr เงินสด (+Cr WHT ค้างจ่าย) ไม่ผ่านการตั้งหนี้</summary>
    Task<InboundSyncResponse> ProcessPaymentVoucherAsync(Guid companyId, Guid integrationId, InboundPaymentVoucherRequest request);
    Task<InboundSyncResponse> ProcessCertificateInLieuAsync(Guid companyId, Guid integrationId, InboundCertificateInLieuRequest request);
    Task<InboundSyncResponse> ProcessProductAsync(Guid companyId, Guid integrationId, InboundProductRequest request);
    Task<InboundSyncResponse> ProcessJournalAsync(Guid companyId, Guid integrationId, InboundJournalRequest request);
    Task<InboundSyncResponse> ProcessJournalReverseAsync(Guid companyId, Guid integrationId, InboundReverseJournalRequest request);
    Task<InboundBatchResponse> ProcessBatchAsync(Guid companyId, Guid integrationId, InboundBatchRequest request);
    Task<InboundSyncResponse> VoidDocumentByExternalRefAsync(Guid companyId, Guid integrationId, InboundVoidDocumentRequest request);

    // Outbound data (external systems read FROM Next Acc)
    Task<OutboundPagedResponse<OutboundDocumentResponse>> GetDocumentsForExternalAsync(Guid companyId, OutboundQueryParams query);
    Task<OutboundPagedResponse<OutboundContactResponse>> GetContactsForExternalAsync(Guid companyId, OutboundQueryParams query);
    Task<OutboundPagedResponse<OutboundPaymentResponse>> GetPaymentsForExternalAsync(Guid companyId, OutboundQueryParams query);
    Task<List<OutboundAccountBalanceResponse>> GetAccountBalancesForExternalAsync(Guid companyId);

    // Authentication
    Task<(Guid CompanyId, Guid IntegrationId)?> ValidateApiKeyAsync(string apiKey);

    // Revenue Reports
    Task<List<RevenueByCategoryItem>> GetRevenueByCategoryAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<List<RevenueBySourceItem>> GetRevenueBySourceAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<DepositSummaryResponse> GetDepositSummaryAsync(Guid companyId, DateTime? from, DateTime? to);
    Task<List<DailyRevenueItem>> GetDailyRevenueAsync(Guid companyId, DateTime? from, DateTime? to);
}
