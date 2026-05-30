using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Pdpa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/pdpa")]
[Authorize]
public class PdpaController : ControllerBase
{
    private readonly IPdpaService _svc;

    public PdpaController(IPdpaService svc) { _svc = svc; }

    public sealed record SubmitRequest(string RequesterContact, string? RequesterName,
        string RequestType, string? Description,
        Guid? LinkedUserId, Guid? LinkedContactId);

    [HttpPost("requests")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Submit(
        Guid companyId, [FromBody] SubmitRequest req, CancellationToken ct)
    {
        var r = await _svc.SubmitAsync(companyId, req.RequesterContact, req.RequesterName,
            req.RequestType, req.Description, req.LinkedUserId, req.LinkedContactId, ct);
        return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r,
            $"รับคำขอ PDPA {r.RequestNumber} — ต้องตอบกลับภายใน {r.DueBy:yyyy-MM-dd}"));
    }

    public sealed record AssignRequest(Guid DpoUserId);

    [HttpPost("requests/{requestId:guid}/assign")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Assign(
        Guid companyId, Guid requestId, [FromBody] AssignRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _svc.AssignAsync(companyId, requestId, req.DpoUserId, ct);
            return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r, "มอบหมาย DPO แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record CompleteRequest(string CompletionNote);

    [HttpPost("requests/{requestId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Complete(
        Guid companyId, Guid requestId, [FromBody] CompleteRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _svc.CompleteAsync(companyId, requestId, req.CompletionNote, ct);
            return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r, "ปิดงาน PDPA แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("overdue")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PdpaDataSubjectRequest>>>> Overdue(
        Guid companyId, CancellationToken ct)
    {
        var rows = await _svc.ListOverdueAsync(companyId, ct);
        return Ok(new ApiResponse<IReadOnlyList<PdpaDataSubjectRequest>>(true, rows,
            $"คำขอ PDPA เกินกำหนด {rows.Count} รายการ"));
    }

    [HttpGet("erasure-impact")]
    public async Task<ActionResult<ApiResponse<ErasureImpactReport>>> ErasureImpact(
        Guid companyId, [FromQuery] Guid? userId, [FromQuery] Guid? contactId,
        CancellationToken ct)
    {
        var r = await _svc.ProposeErasureImpactAsync(companyId, userId, contactId, ct);
        return Ok(new ApiResponse<ErasureImpactReport>(true, r));
    }
}
