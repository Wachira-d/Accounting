using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Integration;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Integration Management — ตั้งค่าและจัดการการเชื่อมต่อระบบภายนอก
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

    // ===== Revenue Reports (Hotel / PMS integration) =====

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
/// External Integration API — endpoints สำหรับระบบภายนอกเรียกเข้ามา (authenticated by Integration API Key)
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

    // ===== Inbound endpoints (called by external systems) =====

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

    [HttpPost("daily-summary")]
    public async Task<ActionResult<InboundSyncResponse>> DailySummary([FromBody] InboundDailySummaryRequest request)
    {
        var auth = await AuthenticateIntegration();
        if (auth == null) return Unauthorized(new InboundSyncResponse(false, "Invalid API Key", null, null, null, null, null));

        var result = await _service.ProcessDailySummaryAsync(auth.Value.CompanyId, auth.Value.IntegrationId, request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    private async Task<(Guid CompanyId, Guid IntegrationId)?> AuthenticateIntegration()
    {
        var apiKey = Request.Headers["X-Integration-Key"].FirstOrDefault()
            ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        if (string.IsNullOrEmpty(apiKey)) return null;
        return await _service.ValidateApiKeyAsync(apiKey);
    }
}
