using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Tax;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/stamp-duty")]
[Authorize]
public class StampDutyController : ControllerBase
{
    private readonly IStampDutyService _svc;

    public StampDutyController(IStampDutyService svc) { _svc = svc; }

    [HttpGet("compute")]
    public ActionResult<ApiResponse<object>> Compute(Guid companyId,
        [FromQuery] int rdScheduleNumber, [FromQuery] decimal instrumentValue,
        [FromQuery] int? leaseYears)
    {
        var duty = _svc.Compute(rdScheduleNumber, instrumentValue, leaseYears);
        return Ok(new ApiResponse<object>(true, new
        {
            schedule = rdScheduleNumber,
            instrumentValue,
            leaseYears,
            duty,
            method = duty == 0 ? "Unknown" : "Computed",
        }));
    }

    public sealed record CreateRequest(string Reference, Guid? ContactId,
        int RdScheduleNumber, string InstrumentType, decimal InstrumentValue,
        DateTime InstrumentDate, int? LeaseYears, string PaymentMethod);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<StampDutyRecord>>> Create(
        Guid companyId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        var rec = await _svc.CreateAsync(companyId, req.Reference, req.ContactId,
            req.RdScheduleNumber, req.InstrumentType, req.InstrumentValue,
            req.InstrumentDate, req.LeaseYears, req.PaymentMethod, ct);
        return Ok(new ApiResponse<StampDutyRecord>(true, rec,
            $"บันทึกอากรแสตมป์ {rec.DutyAmount:N0} บาท"));
    }

    public sealed record MarkPaidRequest(string RdReceiptNumber, DateTime PaidAt);

    [HttpPost("{id:guid}/paid")]
    public async Task<ActionResult<ApiResponse<StampDutyRecord>>> MarkPaid(
        Guid companyId, Guid id, [FromBody] MarkPaidRequest req, CancellationToken ct)
    {
        try
        {
            var rec = await _svc.MarkPaidAsync(companyId, id, req.RdReceiptNumber, req.PaidAt, ct);
            return Ok(new ApiResponse<StampDutyRecord>(true, rec, "บันทึกการจ่ายอากรแสตมป์แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("unpaid")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StampDutyRecord>>>> Unpaid(
        Guid companyId, CancellationToken ct)
    {
        var rows = await _svc.ListUnpaidAsync(companyId, ct);
        return Ok(new ApiResponse<IReadOnlyList<StampDutyRecord>>(true, rows,
            $"อากรค้างชำระ {rows.Count} รายการ"));
    }
}
