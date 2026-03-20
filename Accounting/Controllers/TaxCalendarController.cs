using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/tax-calendar")]
[Authorize]
public class TaxCalendarController : ControllerBase
{
    private readonly ITaxCalendarService _service;
    public TaxCalendarController(ITaxCalendarService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<TaxCalendarEventResponse>>>> GetEvents(Guid companyId, [FromQuery] int year, [FromQuery] int? month)
        => Ok(new ApiResponse<List<TaxCalendarEventResponse>>(true, await _service.GetEventsAsync(companyId, year, month)));

    [HttpGet("{eventId:guid}")]
    public async Task<ActionResult<ApiResponse<TaxCalendarEventResponse>>> GetEvent(Guid companyId, Guid eventId)
        => Ok(new ApiResponse<TaxCalendarEventResponse>(true, await _service.GetEventAsync(companyId, eventId)));

    [HttpPost("initialize/{year:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Initialize(Guid companyId, int year)
    { await _service.InitializeYearAsync(companyId, year); return Ok(new ApiResponse<bool>(true, true, "สร้างปฏิทินภาษีสำเร็จ")); }

    [HttpPut("{eventId:guid}")]
    public async Task<ActionResult<ApiResponse<TaxCalendarEventResponse>>> Update(Guid companyId, Guid eventId, [FromBody] UpdateTaxCalendarEventRequest request)
        => Ok(new ApiResponse<TaxCalendarEventResponse>(true, await _service.UpdateEventAsync(companyId, eventId, request)));

    [HttpGet("upcoming")]
    public async Task<ActionResult<ApiResponse<List<TaxCalendarEventResponse>>>> GetUpcoming(Guid companyId, [FromQuery] int daysAhead = 30)
        => Ok(new ApiResponse<List<TaxCalendarEventResponse>>(true, await _service.GetUpcomingAsync(companyId, daysAhead)));

    [HttpGet("overdue")]
    public async Task<ActionResult<ApiResponse<List<TaxCalendarEventResponse>>>> GetOverdue(Guid companyId)
        => Ok(new ApiResponse<List<TaxCalendarEventResponse>>(true, await _service.GetOverdueAsync(companyId)));
}
