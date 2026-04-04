using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Integration;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Integration Management — ตั้งค่าและจัดการการเชื่อมต่อระบบภายนอก (per-company)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/integrations")]
[Authorize]
public class IntegrationController : ControllerBase
{
    private readonly IIntegrationService _service;

    public IntegrationController(IIntegrationService service)
    {
        _service = service;
    }

    // ===== Integration Config =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<IntegrationResponse>>>> GetIntegrations(Guid companyId)
    {
        var result = await _service.GetIntegrationsAsync(companyId);
        return Ok(new ApiResponse<List<IntegrationResponse>>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<IntegrationCreatedResponse>>> CreateIntegration(Guid companyId, [FromBody] CreateIntegrationRequest request)
    {
        var result = await _service.CreateIntegrationAsync(companyId, request);
        return StatusCode(201, new ApiResponse<IntegrationCreatedResponse>(true, result, "สร้าง Integration สำเร็จ (เก็บ API Key ไว้ จะแสดงครั้งเดียว)"));
    }

    [HttpPut("{integrationId:guid}")]
    public async Task<ActionResult<ApiResponse<IntegrationResponse>>> UpdateIntegration(Guid companyId, Guid integrationId, [FromBody] UpdateIntegrationRequest request)
    {
        var result = await _service.UpdateIntegrationAsync(companyId, integrationId, request);
        return Ok(new ApiResponse<IntegrationResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}")]
    public async Task<IActionResult> DeleteIntegration(Guid companyId, Guid integrationId)
    {
        await _service.DeleteIntegrationAsync(companyId, integrationId);
        return NoContent();
    }

    [HttpPost("{integrationId:guid}/regenerate-key")]
    public async Task<ActionResult<ApiResponse<IntegrationResponse>>> RegenerateKey(Guid companyId, Guid integrationId)
    {
        var result = await _service.RegenerateApiKeyAsync(companyId, integrationId);
        return Ok(new ApiResponse<IntegrationResponse>(true, result, "สร้าง API Key ใหม่สำเร็จ"));
    }

    // ===== Account Mapping =====

    [HttpGet("{integrationId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<List<AccountMappingResponse>>>> GetMappings(Guid companyId, Guid integrationId)
    {
        var result = await _service.GetMappingsAsync(companyId, integrationId);
        return Ok(new ApiResponse<List<AccountMappingResponse>>(true, result));
    }

    [HttpPost("{integrationId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<AccountMappingResponse>>> CreateMapping(Guid companyId, Guid integrationId, [FromBody] CreateAccountMappingRequest request)
    {
        var result = await _service.CreateMappingAsync(companyId, integrationId, request);
        return StatusCode(201, new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpPut("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<ActionResult<ApiResponse<AccountMappingResponse>>> UpdateMapping(Guid companyId, Guid integrationId, Guid mappingId, [FromBody] UpdateAccountMappingRequest request)
    {
        var result = await _service.UpdateMappingAsync(companyId, integrationId, mappingId, request);
        return Ok(new ApiResponse<AccountMappingResponse>(true, result));
    }

    [HttpDelete("{integrationId:guid}/mappings/{mappingId:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid companyId, Guid integrationId, Guid mappingId)
    {
        await _service.DeleteMappingAsync(companyId, integrationId, mappingId);
        return NoContent();
    }

    // ===== Mapping Templates =====

    [HttpGet("mapping-templates")]
    public ActionResult<ApiResponse<List<MappingTemplateResponse>>> GetMappingTemplates()
    {
        var result = _service.GetMappingTemplates();
        return Ok(new ApiResponse<List<MappingTemplateResponse>>(true, result));
    }

    // ===== Sync Logs =====

    [HttpGet("sync-logs")]
    public async Task<ActionResult<ApiResponse<List<SyncLogResponse>>>> GetSyncLogs(Guid companyId, [FromQuery] Guid? integrationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _service.GetSyncLogsAsync(companyId, integrationId, page, pageSize);
        return Ok(new ApiResponse<List<SyncLogResponse>>(true, result));
    }

    // ===== Dashboard =====

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<IntegrationDashboardResponse>>> GetDashboard(Guid companyId)
    {
        var result = await _service.GetDashboardAsync(companyId);
        return Ok(new ApiResponse<IntegrationDashboardResponse>(true, result));
    }

    // ===== Revenue Reports =====

    [HttpGet("reports/revenue-by-category")]
    public async Task<ActionResult<ApiResponse<List<RevenueByCategoryItem>>>> GetRevenueByCategory(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetRevenueByCategoryAsync(companyId, from, to);
        return Ok(new ApiResponse<List<RevenueByCategoryItem>>(true, result));
    }

    [HttpGet("reports/revenue-by-source")]
    public async Task<ActionResult<ApiResponse<List<RevenueBySourceItem>>>> GetRevenueBySource(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetRevenueBySourceAsync(companyId, from, to);
        return Ok(new ApiResponse<List<RevenueBySourceItem>>(true, result));
    }

    [HttpGet("reports/deposit-summary")]
    public async Task<ActionResult<ApiResponse<DepositSummaryResponse>>> GetDepositSummary(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetDepositSummaryAsync(companyId, from, to);
        return Ok(new ApiResponse<DepositSummaryResponse>(true, result));
    }

    [HttpGet("reports/daily-revenue")]
    public async Task<ActionResult<ApiResponse<List<DailyRevenueItem>>>> GetDailyRevenue(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var result = await _service.GetDailyRevenueAsync(companyId, from, to);
        return Ok(new ApiResponse<List<DailyRevenueItem>>(true, result));
    }
}

/// <summary>
/// External Integration API — Universal endpoints for ANY external system
/// Authenticated by Integration API Key via X-Integration-Key header
/// </summary>
[ApiController]
[Route("api/integration")]
[AllowAnonymous]
public class ExternalIntegrationController : ControllerBase
{
    private readonly IIntegrationService _service;

    public ExternalIntegrationController(IIntegrationService service)
    {
        _service = service;
    }

    // ===== Inbound endpoints (external systems push data TO Next Acc) =====

    [HttpPost("customers")]
    public async Task<ActionResult<InboundSyncResponse>> SyncCustomer([FromBody] InboundCustomerRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessCustomerAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("invoices")]
    public async Task<ActionResult<InboundSyncResponse>> CreateInvoice([FromBody] InboundInvoiceRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessInvoiceAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("payments")]
    public async Task<ActionResult<InboundSyncResponse>> RecordPayment([FromBody] InboundPaymentRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessPaymentAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("credit-notes")]
    public async Task<ActionResult<InboundSyncResponse>> CreateCreditNote([FromBody] InboundCreditNoteRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessCreditNoteAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("debit-notes")]
    public async Task<ActionResult<InboundSyncResponse>> CreateDebitNote([FromBody] InboundDebitNoteRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessDebitNoteAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("expenses")]
    public async Task<ActionResult<InboundSyncResponse>> CreateExpense([FromBody] InboundExpenseRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessExpenseAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("products")]
    public async Task<ActionResult<InboundSyncResponse>> SyncProduct([FromBody] InboundProductRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessProductAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("journals")]
    public async Task<ActionResult<InboundSyncResponse>> CreateJournal([FromBody] InboundJournalRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessJournalAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("daily-summary")]
    public async Task<ActionResult<InboundSyncResponse>> DailySummary([FromBody] InboundDailySummaryRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessDailySummaryAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("batch")]
    public async Task<ActionResult<InboundBatchResponse>> BatchImport([FromBody] InboundBatchRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundBatchResponse(0, 0, 0, new List<BatchResultItem>()));

        var result = await _service.ProcessBatchAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return Ok(result);
    }

    // ===== Outbound endpoints (external systems read data FROM Next Acc) =====

    [HttpGet("documents")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundDocumentResponse>>> GetDocuments([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetDocumentsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("contacts")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundContactResponse>>> GetContacts([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetContactsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("payments-list")]
    public async Task<ActionResult<OutboundPagedResponse<OutboundPaymentResponse>>> GetPayments([FromQuery] OutboundQueryParams query)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetPaymentsForExternalAsync(auth.Value.CompanyId, query);
        return Ok(result);
    }

    [HttpGet("account-balances")]
    public async Task<ActionResult<List<OutboundAccountBalanceResponse>>> GetAccountBalances()
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized();

        var result = await _service.GetAccountBalancesForExternalAsync(auth.Value.CompanyId);
        return Ok(result);
    }

    private async Task<(Guid CompanyId, Guid IntegrationId)?> AuthenticateIntegration()
    {
        var apiKey = Request.Headers["X-Integration-Key"].FirstOrDefault()
            ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        if (string.IsNullOrEmpty(apiKey)) return null;
        return await _service.ValidateApiKeyAsync(apiKey);
    }
}
