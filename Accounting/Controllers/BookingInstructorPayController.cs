using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 209 · P5 (R-209-C6 · คำตัดสินข้อ 168 ฏ) — ค่าตอบแทนผู้ให้บริการของระบบจอง: ผูกพนักงาน (ทะเบียน HR) + กติกาต่อคลาส/ต่อหัว/% ·
/// อ่าน = <c>Payroll.View</c> (อัตราค่าตอบแทนเป็นข้อมูลเงินเดือน — ไม่เปิดให้ทุกคนที่ดูรายงานผู้ให้บริการได้) · แก้ = <c>HR.Admin</c> ·
/// ตัวคิดเดียว <see cref="InstructorPayRule"/> (ตัวเดียวกับฐานคอมมิชชัน InstructorPay)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/booking")]
[Authorize]
public class BookingInstructorPayController : ControllerBase
{
    private readonly ICommissionService _commission;

    public BookingInstructorPayController(ICommissionService commission) { _commission = commission; }

    [HttpGet("resource-pay")]
    [RequirePermission(PermissionKeys.PayrollView)]
    public async Task<ActionResult<ApiResponse<ResourcePaySettingsResponse>>> Settings(Guid companyId, Guid siteId)
        => Ok(new ApiResponse<ResourcePaySettingsResponse>(true, await _commission.GetResourcePayAsync(companyId, siteId)));

    [HttpPut("resource-pay/{resourceId:guid}")]
    [RequirePermission(PermissionKeys.HrAdmin)]
    public async Task<ActionResult<ApiResponse<ResourcePayResponse>>> Save(Guid companyId, Guid siteId, Guid resourceId, [FromBody] SaveResourcePayRequest request)
        => Ok(new ApiResponse<ResourcePayResponse>(true,
            await _commission.SaveResourcePayAsync(companyId, siteId, resourceId, request, JwtHelper.GetUserIdFromClaims(User).ToString()),
            "บันทึกค่าตอบแทนผู้ให้บริการแล้ว"));

    [HttpGet("instructor-pay")]
    [RequirePermission(PermissionKeys.PayrollView)]
    public async Task<ActionResult<ApiResponse<List<InstructorPayLine>>>> Pay(Guid companyId, Guid siteId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<List<InstructorPayLine>>(true, await _commission.GetInstructorPayAsync(companyId, siteId, fromDate, toDate)));
}
