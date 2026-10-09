using Accounting.Filters;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รายงานตรวจข้อมูลที่อาจผิดจากบั๊กที่แก้แล้ว (คำตัดสินเจ้าของ 2026-10-08: "รายงานอ่านอย่างเดียวก่อน" — ผู้ทำบัญชีตรวจแล้วซ่อมรายตัว) ·
/// อ่านอย่างเดียว ไม่แก้ข้อมูล · สิทธิ์ระดับรายงานการเงิน
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/data-integrity")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsFinancial)]
public class DataIntegrityController : ControllerBase
{
    private readonly DataIntegrityReportService _service;
    public DataIntegrityController(DataIntegrityReportService service) => _service = service;

    [HttpGet("suspects")]
    public async Task<ActionResult<ApiResponse<DataIntegrityReport>>> Suspects(Guid companyId)
    {
        var r = await _service.GetAsync(companyId);
        var n = r.PosOrderTotals.Count + r.RollupBillingNoteChildren.Count + r.PurchaseInvoicesBilledFromPoWithGrn.Count + r.PayrollRunTotals.Count;
        return Ok(new ApiResponse<DataIntegrityReport>(true, r, n == 0 ? "ไม่พบรายการที่ต้องตรวจ" : $"พบ {n} รายการที่ควรให้ผู้ทำบัญชีตรวจ"));
    }
}
