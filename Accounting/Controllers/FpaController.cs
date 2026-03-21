using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Fpa;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/fpa")]
[Authorize]
public class FpaController : ControllerBase
{
    private readonly IFpaService _service;
    public FpaController(IFpaService service) => _service = service;

    // Scenarios
    [HttpPost("scenarios")]
    public async Task<ActionResult<ApiResponse<ScenarioResponse>>> CreateScenario(Guid companyId, [FromBody] CreateScenarioRequest request)
        => Ok(new ApiResponse<ScenarioResponse>(true, await _service.CreateScenarioAsync(companyId, request)));

    [HttpGet("scenarios")]
    public async Task<ActionResult<ApiResponse<List<ScenarioResponse>>>> GetScenarios(Guid companyId, [FromQuery] int? fiscalYear)
        => Ok(new ApiResponse<List<ScenarioResponse>>(true, await _service.GetScenariosAsync(companyId, fiscalYear)));

    [HttpGet("scenarios/{scenarioId:guid}")]
    public async Task<ActionResult<ApiResponse<ScenarioResponse>>> GetScenario(Guid companyId, Guid scenarioId)
        => Ok(new ApiResponse<ScenarioResponse>(true, await _service.GetScenarioAsync(companyId, scenarioId)));

    [HttpPut("scenarios/{scenarioId:guid}")]
    public async Task<ActionResult<ApiResponse<ScenarioResponse>>> UpdateScenario(Guid companyId, Guid scenarioId, [FromBody] UpdateScenarioRequest request)
        => Ok(new ApiResponse<ScenarioResponse>(true, await _service.UpdateScenarioAsync(companyId, scenarioId, request)));

    [HttpDelete("scenarios/{scenarioId:guid}")]
    public async Task<IActionResult> DeleteScenario(Guid companyId, Guid scenarioId)
    {
        await _service.DeleteScenarioAsync(companyId, scenarioId);
        return NoContent();
    }

    [HttpPost("scenarios/{scenarioId:guid}/assumptions")]
    public async Task<ActionResult<ApiResponse<ScenarioResponse>>> AddAssumption(Guid companyId, Guid scenarioId, [FromBody] CreateAssumptionRequest request)
        => Ok(new ApiResponse<ScenarioResponse>(true, await _service.AddAssumptionAsync(companyId, scenarioId, request)));

    [HttpDelete("assumptions/{assumptionId:guid}")]
    public async Task<IActionResult> RemoveAssumption(Guid companyId, Guid assumptionId)
    {
        await _service.RemoveAssumptionAsync(companyId, assumptionId);
        return NoContent();
    }

    [HttpPost("scenarios/{scenarioId:guid}/calculate")]
    public async Task<ActionResult<ApiResponse<ScenarioResultsResponse>>> Calculate(Guid companyId, Guid scenarioId)
        => Ok(new ApiResponse<ScenarioResultsResponse>(true, await _service.CalculateScenarioAsync(companyId, scenarioId)));

    [HttpPost("scenarios/compare")]
    public async Task<ActionResult<ApiResponse<ScenarioComparisonResponse>>> Compare(Guid companyId, [FromBody] List<Guid> scenarioIds)
        => Ok(new ApiResponse<ScenarioComparisonResponse>(true, await _service.CompareAsync(companyId, scenarioIds)));

    // KPIs
    [HttpPost("kpis")]
    public async Task<ActionResult<ApiResponse<FinancialKpiResponse>>> CreateKpi(Guid companyId, [FromBody] CreateFinancialKpiRequest request)
        => Ok(new ApiResponse<FinancialKpiResponse>(true, await _service.CreateKpiAsync(companyId, request)));

    [HttpGet("kpis")]
    public async Task<ActionResult<ApiResponse<List<FinancialKpiResponse>>>> GetKpis(Guid companyId)
        => Ok(new ApiResponse<List<FinancialKpiResponse>>(true, await _service.GetKpisAsync(companyId)));

    [HttpGet("kpis/{kpiId:guid}/history")]
    public async Task<ActionResult<ApiResponse<List<KpiSnapshotResponse>>>> GetKpiHistory(Guid companyId, Guid kpiId, [FromQuery] int months = 12)
        => Ok(new ApiResponse<List<KpiSnapshotResponse>>(true, await _service.GetKpiHistoryAsync(companyId, kpiId, months)));

    [HttpPost("kpis/snapshots/{year:int}/{month:int}")]
    public async Task<IActionResult> CalculateKpiSnapshots(Guid companyId, int year, int month)
    {
        await _service.CalculateKpiSnapshotsAsync(companyId, year, month);
        return NoContent();
    }

    // Ratios
    [HttpGet("ratios")]
    public async Task<ActionResult<ApiResponse<FinancialRatiosResponse>>> GetRatios(Guid companyId, [FromQuery] DateTime asOfDate)
        => Ok(new ApiResponse<FinancialRatiosResponse>(true, await _service.CalculateRatiosAsync(companyId, asOfDate)));

    [HttpGet("break-even/{fiscalYear:int}")]
    public async Task<ActionResult<ApiResponse<BreakEvenResponse>>> GetBreakEven(Guid companyId, int fiscalYear)
        => Ok(new ApiResponse<BreakEvenResponse>(true, await _service.CalculateBreakEvenAsync(companyId, fiscalYear)));
}
