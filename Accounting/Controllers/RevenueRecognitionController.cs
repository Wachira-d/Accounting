using Accounting.Models.DTOs;
using Accounting.Models.DTOs.RevenueRecognition;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/revenue-recognition")]
[Authorize]
public class RevenueRecognitionController : ControllerBase
{
    private readonly IRevenueRecognitionService _service;
    public RevenueRecognitionController(IRevenueRecognitionService service) => _service = service;

    [HttpPost("contracts")]
    public async Task<ActionResult<ApiResponse<RevenueContractResponse>>> CreateContract(Guid companyId, [FromBody] CreateRevenueContractRequest request)
        => StatusCode(201, new ApiResponse<RevenueContractResponse>(true, await _service.CreateContractAsync(companyId, request)));

    [HttpGet("contracts/{contractId:guid}")]
    public async Task<ActionResult<ApiResponse<RevenueContractResponse>>> GetContract(Guid companyId, Guid contractId)
        => Ok(new ApiResponse<RevenueContractResponse>(true, await _service.GetContractAsync(companyId, contractId)));

    [HttpGet("contracts")]
    public async Task<ActionResult<ApiResponse<PagedResponse<RevenueContractResponse>>>> GetContracts(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<RevenueContractResponse>>(true, await _service.GetContractsAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPut("contracts/{contractId:guid}")]
    public async Task<ActionResult<ApiResponse<RevenueContractResponse>>> UpdateContract(Guid companyId, Guid contractId, [FromBody] UpdateRevenueContractRequest request)
        => Ok(new ApiResponse<RevenueContractResponse>(true, await _service.UpdateContractAsync(companyId, contractId, request)));

    [HttpPost("contracts/{contractId:guid}/obligations")]
    public async Task<ActionResult<ApiResponse<PerformanceObligationResponse>>> AddObligation(Guid companyId, Guid contractId, [FromBody] CreateObligationRequest request)
        => Ok(new ApiResponse<PerformanceObligationResponse>(true, await _service.AddObligationAsync(companyId, contractId, request)));

    [HttpPut("obligations/{obligationId:guid}/progress")]
    public async Task<ActionResult<ApiResponse<PerformanceObligationResponse>>> UpdateProgress(Guid companyId, Guid obligationId, [FromQuery] decimal percent)
        => Ok(new ApiResponse<PerformanceObligationResponse>(true, await _service.UpdateProgressAsync(companyId, obligationId, percent)));

    [HttpPost("obligations/{obligationId:guid}/satisfy")]
    public async Task<ActionResult<ApiResponse<PerformanceObligationResponse>>> SatisfyObligation(Guid companyId, Guid obligationId)
        => Ok(new ApiResponse<PerformanceObligationResponse>(true, await _service.SatisfyObligationAsync(companyId, obligationId)));

    [HttpPost("contracts/{contractId:guid}/generate-schedule")]
    public async Task<ActionResult<ApiResponse<List<RevenueScheduleResponse>>>> GenerateSchedule(Guid companyId, Guid contractId)
        => Ok(new ApiResponse<List<RevenueScheduleResponse>>(true, await _service.GenerateScheduleAsync(companyId, contractId)));

    [HttpPost("schedules/{scheduleId:guid}/recognize")]
    public async Task<ActionResult<ApiResponse<RevenueScheduleResponse>>> Recognize(Guid companyId, Guid scheduleId)
        => Ok(new ApiResponse<RevenueScheduleResponse>(true, await _service.RecognizeRevenueAsync(companyId, scheduleId)));

    [HttpPost("process-due-recognitions")]
    public async Task<ActionResult<ApiResponse<List<RevenueScheduleResponse>>>> ProcessDueRecognitions(Guid companyId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<List<RevenueScheduleResponse>>(true, await _service.ProcessDueRecognitionsAsync(companyId, asOfDate)));

    [HttpGet("deferred-revenue")]
    public async Task<ActionResult<ApiResponse<DeferredRevenueReportResponse>>> GetDeferredRevenue(Guid companyId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<DeferredRevenueReportResponse>(true, await _service.GetDeferredRevenueReportAsync(companyId, asOfDate)));
}
