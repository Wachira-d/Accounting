using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Treasury;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Cash forecast — day-by-day projected balance combining bank
/// balance, open AR/AP due dates, and approved payroll runs. Two
/// flavours:
///   • Company-wide (no projectId) — full inflow/outflow + payroll
///   • Project-scoped (?projectId=...) — AR/AP tagged to the project
///     only; payroll is excluded since labour already lives in
///     ProjectCostEntry (double-count avoidance)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cash-forecast")]
[Authorize]
public class CashForecastController : ControllerBase
{
    private readonly ICashForecastService _svc;
    public CashForecastController(ICashForecastService svc) { _svc = svc; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<CashForecastResponse>>> Get(
        Guid companyId,
        [FromQuery] int days = 30,
        [FromQuery] Guid? projectId = null,
        [FromQuery] bool includePayroll = true,
        [FromQuery] decimal? minimumCashFloor = null,
        CancellationToken ct = default)
    {
        var r = await _svc.ForecastAsync(companyId,
            new CashForecastRequest(days, projectId, includePayroll, minimumCashFloor), ct);
        return Ok(new ApiResponse<CashForecastResponse>(true, r));
    }
}
