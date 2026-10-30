using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>ตั้งค่าสรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C · หน้า <c>settings.html</c> แท็บการแจ้งเตือน) —
/// อ่าน/เขียน/ส่งทดสอบ ต้องมีสิทธิ์ตั้งค่าบริษัท (<see cref="PermissionKeys.CompanySettingsEdit"/>): หน้านี้เห็นรายชื่อสมาชิก + สถานะผูก LINE และ
/// ตัดสินว่าใครได้รับข้อมูลธุรกิจทาง LINE · คีย์ API ใช้ไม่ได้ (งานของคน) · write_permission_gate_check เฝ้าไฟล์นี้</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/notifications/daily-summary")]
[Authorize]
public class StaffDailySummaryController : ControllerBase
{
    private readonly IStaffDailySummaryService _service;

    public StaffDailySummaryController(IStaffDailySummaryService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<StaffDailySummaryOverviewDto>>> Get(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<StaffDailySummaryOverviewDto>(true, await _service.GetOverviewAsync(companyId, ct)));

    [HttpPost]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าสรุปประจำวันทาง LINE")]
    public async Task<ActionResult<ApiResponse<StaffDailySummaryConfigDto>>> Create(Guid companyId, [FromBody] UpsertStaffDailySummaryRequest request, CancellationToken ct)
        => Ok(new ApiResponse<StaffDailySummaryConfigDto>(true,
            await _service.UpsertAsync(companyId, null, request, Actor(), ct), "บันทึกชุดสรุปประจำวันแล้ว"));

    [HttpPut("{configId:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ตั้งค่าสรุปประจำวันทาง LINE")]
    public async Task<ActionResult<ApiResponse<StaffDailySummaryConfigDto>>> Update(Guid companyId, Guid configId, [FromBody] UpsertStaffDailySummaryRequest request, CancellationToken ct)
        => Ok(new ApiResponse<StaffDailySummaryConfigDto>(true,
            await _service.UpsertAsync(companyId, configId, request, Actor(), ct), "บันทึกชุดสรุปประจำวันแล้ว"));

    [HttpDelete("{configId:guid}")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ลบชุดสรุปประจำวันทาง LINE")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid configId, CancellationToken ct)
    {
        await _service.DeleteAsync(companyId, configId, Actor(), ct);
        return Ok(new ApiResponse<string>(true, null, "ลบชุดสรุปประจำวันแล้ว"));
    }

    /// <summary>ส่งสรุปของชุดนี้ถึง LINE ของผู้กดตอนนี้ (กรองตามสิทธิ์ของผู้กด · นับโควตาข้อความ 1)</summary>
    [HttpPost("{configId:guid}/test")]
    [RequirePermission(PermissionKeys.CompanySettingsEdit)]
    [RejectApiKey("ส่งทดสอบสรุปประจำวัน")]
    public async Task<ActionResult<ApiResponse<StaffDailySummaryTestResult>>> SendTest(Guid companyId, Guid configId, CancellationToken ct)
    {
        var r = await _service.SendTestAsync(companyId, configId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<StaffDailySummaryTestResult>(r.Sent, r, r.Message));
    }

    private string Actor() => JwtHelper.GetUserIdFromClaims(User).ToString();
}
