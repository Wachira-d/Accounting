using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Procurement endpoints — 3-way match (PO ↔ GRN ↔ Invoice) is the
/// flagship; future endpoints (PR approval, supplier comparison)
/// belong here too.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/procurement")]
[Authorize]
public class ProcurementController : ControllerBase
{
    private readonly IGrnMatchService _match;

    public ProcurementController(IGrnMatchService match) { _match = match; }

    /// <summary>3-way match check before approving an AP invoice for
    /// payment. Returns findings (Error blocks payment; Warning is
    /// flagged; Info is observation) + recommended pay amount capped
    /// at received-and-priced.</summary>
    [HttpGet("invoices/{invoiceId:guid}/three-way-match")]
    public async Task<ActionResult<ApiResponse<GrnMatchReport>>> ThreeWayMatch(
        Guid companyId, Guid invoiceId, CancellationToken ct)
    {
        try
        {
            var report = await _match.CheckAsync(companyId, invoiceId, ct: ct);
            return Ok(new ApiResponse<GrnMatchReport>(true, report,
                report.CanPay ? "ผ่าน 3-way match — จ่ายได้"
                    : $"ติดปัญหา {report.Findings.Count(f => f.Severity == "Error")} ข้อ — ดูรายละเอียดก่อนจ่าย"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message));
        }
    }
}
