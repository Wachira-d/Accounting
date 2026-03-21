using Accounting.Models.DTOs;
using Accounting.Models.DTOs.TimeBilling;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/time-billing")]
[Authorize]
public class TimeBillingController : ControllerBase
{
    private readonly ITimeBillingService _service;
    public TimeBillingController(ITimeBillingService service) => _service = service;

    // Time entries
    [HttpPost("entries")]
    public async Task<ActionResult<ApiResponse<TimeEntryResponse>>> CreateEntry(Guid companyId, [FromBody] CreateTimeEntryRequest request)
        => Ok(new ApiResponse<TimeEntryResponse>(true, await _service.CreateTimeEntryAsync(companyId, request, User.Identity?.Name ?? "")));

    [HttpGet("entries/{entryId:guid}")]
    public async Task<ActionResult<ApiResponse<TimeEntryResponse>>> GetEntry(Guid companyId, Guid entryId)
        => Ok(new ApiResponse<TimeEntryResponse>(true, await _service.GetTimeEntryAsync(companyId, entryId)));

    [HttpGet("entries")]
    public async Task<ActionResult<ApiResponse<PagedResponse<TimeEntryResponse>>>> GetEntries(Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] Guid? projectId, [FromQuery] Guid? contactId, [FromQuery] string? category, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<TimeEntryResponse>>(true, await _service.GetTimeEntriesAsync(companyId, new TimeEntryFilterRequest(employeeId, projectId, contactId, category, fromDate, toDate), new PagedRequest(page, pageSize))));

    [HttpPut("entries/{entryId:guid}")]
    public async Task<ActionResult<ApiResponse<TimeEntryResponse>>> UpdateEntry(Guid companyId, Guid entryId, [FromBody] UpdateTimeEntryRequest request)
        => Ok(new ApiResponse<TimeEntryResponse>(true, await _service.UpdateTimeEntryAsync(companyId, entryId, request)));

    [HttpPost("entries/{entryId:guid}/submit")]
    public async Task<ActionResult<ApiResponse<TimeEntryResponse>>> Submit(Guid companyId, Guid entryId)
        => Ok(new ApiResponse<TimeEntryResponse>(true, await _service.SubmitAsync(companyId, entryId)));

    [HttpPost("entries/{entryId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<TimeEntryResponse>>> Approve(Guid companyId, Guid entryId)
        => Ok(new ApiResponse<TimeEntryResponse>(true, await _service.ApproveAsync(companyId, entryId, User.Identity?.Name ?? "")));

    // Billing rates
    [HttpPost("rates")]
    public async Task<ActionResult<ApiResponse<BillingRateResponse>>> CreateRate(Guid companyId, [FromBody] CreateBillingRateRequest request)
        => Ok(new ApiResponse<BillingRateResponse>(true, await _service.CreateRateAsync(companyId, request)));

    [HttpGet("rates")]
    public async Task<ActionResult<ApiResponse<List<BillingRateResponse>>>> GetRates(Guid companyId)
        => Ok(new ApiResponse<List<BillingRateResponse>>(true, await _service.GetRatesAsync(companyId)));

    // Invoice
    [HttpPost("generate-invoice")]
    public async Task<ActionResult<ApiResponse<Guid>>> GenerateInvoice(Guid companyId, [FromBody] GenerateTimeInvoiceRequest request)
        => Ok(new ApiResponse<Guid>(true, await _service.GenerateInvoiceAsync(companyId, request, User.Identity?.Name ?? "")));

    // Reports
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<TimeSummaryResponse>>> GetSummary(Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<TimeSummaryResponse>(true, await _service.GetTimeSummaryAsync(companyId, fromDate, toDate)));

    [HttpGet("utilization")]
    public async Task<ActionResult<ApiResponse<List<UtilizationResponse>>>> GetUtilization(Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<List<UtilizationResponse>>(true, await _service.GetUtilizationAsync(companyId, fromDate, toDate)));
}
