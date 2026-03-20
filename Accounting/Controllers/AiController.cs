using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/ai")]
[Authorize]
public class AiController : ControllerBase
{
    private readonly IAiService _service;
    public AiController(IAiService service) => _service = service;

    // Auto-categorization
    [HttpPost("categorize/{entityType}/{entityId:guid}")]
    public async Task<ActionResult<ApiResponse<CategorizationResultResponse>>> Categorize(Guid companyId, string entityType, Guid entityId)
        => Ok(new ApiResponse<CategorizationResultResponse>(true, await _service.AutoCategorizeAsync(companyId, entityType, entityId)));

    [HttpPost("categorize/batch")]
    public async Task<ActionResult<ApiResponse<List<CategorizationResultResponse>>>> BatchCategorize(Guid companyId, [FromQuery] string entityType, [FromBody] List<Guid> entityIds)
        => Ok(new ApiResponse<List<CategorizationResultResponse>>(true, await _service.BatchCategorizeAsync(companyId, entityType, entityIds)));

    [HttpPost("categorize/{resultId:guid}/accept")]
    public async Task<ActionResult<ApiResponse<bool>>> Accept(Guid companyId, Guid resultId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _service.AcceptCategorizationAsync(companyId, resultId, userId);
        return Ok(new ApiResponse<bool>(true, true));
    }

    // Rules
    [HttpPost("rules")]
    public async Task<ActionResult<ApiResponse<AutoCatRuleResponse>>> CreateRule(Guid companyId, [FromBody] CreateAutoCatRuleRequest request)
        => Ok(new ApiResponse<AutoCatRuleResponse>(true, await _service.CreateRuleAsync(companyId, request)));

    [HttpGet("rules")]
    public async Task<ActionResult<ApiResponse<List<AutoCatRuleResponse>>>> GetRules(Guid companyId)
        => Ok(new ApiResponse<List<AutoCatRuleResponse>>(true, await _service.GetRulesAsync(companyId)));

    [HttpPost("rules/learn")]
    public async Task<ActionResult<ApiResponse<List<AutoCatRuleResponse>>>> LearnRules(Guid companyId)
        => Ok(new ApiResponse<List<AutoCatRuleResponse>>(true, await _service.LearnRulesFromHistoryAsync(companyId)));

    // Anomaly detection
    [HttpPost("anomalies/detect")]
    public async Task<ActionResult<ApiResponse<List<AnomalyResponse>>>> Detect(Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
        => Ok(new ApiResponse<List<AnomalyResponse>>(true, await _service.DetectAnomaliesAsync(companyId, fromDate, toDate)));

    [HttpGet("anomalies")]
    public async Task<ActionResult<ApiResponse<List<AnomalyResponse>>>> GetAnomalies(Guid companyId, [FromQuery] string? status)
        => Ok(new ApiResponse<List<AnomalyResponse>>(true, await _service.GetAnomaliesAsync(companyId, status)));

    [HttpPost("anomalies/{anomalyId:guid}/resolve")]
    public async Task<ActionResult<ApiResponse<bool>>> Resolve(Guid companyId, Guid anomalyId, [FromQuery] string notes)
    { await _service.ResolveAnomalyAsync(companyId, anomalyId, notes, User.Identity?.Name ?? ""); return Ok(new ApiResponse<bool>(true, true)); }

    // Forecasting
    [HttpPost("forecast")]
    public async Task<ActionResult<ApiResponse<CashFlowForecastResponse>>> CreateForecast(Guid companyId, [FromBody] CreateForecastRequest request)
        => Ok(new ApiResponse<CashFlowForecastResponse>(true, await _service.GenerateForecastAsync(companyId, request)));

    [HttpGet("forecasts")]
    public async Task<ActionResult<ApiResponse<List<CashFlowForecastResponse>>>> GetForecasts(Guid companyId)
        => Ok(new ApiResponse<List<CashFlowForecastResponse>>(true, await _service.GetForecastsAsync(companyId)));
}
