using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 209 · P4 — ตารางคลาสของสตูดิโอ (เทอม → รอบเรียน · ครูแทน · ยกเลิกทั้งคลาส · roster/เช็คอิน) บนเครื่องจอง CMS ตัวเดิม ·
/// ทุก endpoint เป็นของพนักงาน (หน้าร้านเห็นรอบผ่าน <c>available-slots</c> เดิม — บริการที่มีเทอม server คืนรอบเรียนเป็นรอบให้เอง) ·
/// เขียน = บทบาทเว็บ (Admin/OrderManager) <b>และ</b> คีย์ <see cref="PermissionKeys.CmsOrderManage"/> (write_permission_gate_check)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/booking/classes")]
[Authorize]
public class CmsClassScheduleController : ControllerBase
{
    private readonly ICmsBookingService _bookingService;

    public CmsClassScheduleController(ICmsBookingService bookingService) => _bookingService = bookingService;

    private string UserId => JwtHelper.GetUserIdFromClaims(User).ToString();

    // ===== เทอม =====

    [HttpGet("terms")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager, SiteStaffRole.Viewer)]
    public async Task<ActionResult<ApiResponse<List<ClassTermResponse>>>> GetTerms(Guid companyId, Guid siteId, [FromQuery] Guid? serviceId = null)
        => Ok(new ApiResponse<List<ClassTermResponse>>(true, await _bookingService.GetClassTermsAsync(companyId, siteId, serviceId)));

    [HttpPost("terms")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassTermResponse>>> CreateTerm(Guid companyId, Guid siteId, [FromBody] SaveClassTermRequest request)
        => StatusCode(201, new ApiResponse<ClassTermResponse>(true, await _bookingService.SaveClassTermAsync(companyId, siteId, null, request, UserId), "บันทึกเทอมคลาสแล้ว"));

    [HttpPut("terms/{termId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassTermResponse>>> UpdateTerm(Guid companyId, Guid siteId, Guid termId, [FromBody] SaveClassTermRequest request)
        => Ok(new ApiResponse<ClassTermResponse>(true, await _bookingService.SaveClassTermAsync(companyId, siteId, termId, request, UserId), "บันทึกเทอมคลาสแล้ว"));

    [HttpDelete("terms/{termId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<List<string>>>> DeleteTerm(Guid companyId, Guid siteId, Guid termId)
        => Ok(new ApiResponse<List<string>>(true, await _bookingService.DeleteClassTermAsync(companyId, siteId, termId, UserId), "ลบเทอมแล้ว"));

    /// <summary>สร้างรอบ N สัปดาห์ (สร้างซ้ำได้ — ไม่ซ้ำ ไม่ลบรอบที่มีการจอง)</summary>
    [HttpPost("terms/{termId:guid}/generate")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassGenerateResponse>>> Generate(Guid companyId, Guid siteId, Guid termId, [FromBody] GenerateClassSessionsRequest? request)
        => Ok(new ApiResponse<ClassGenerateResponse>(true,
            await _bookingService.GenerateClassSessionsAsync(companyId, siteId, termId, request ?? new GenerateClassSessionsRequest(), UserId)));

    // ===== รอบเรียน =====

    /// <summary>ปฏิทิน: รอบเรียนของช่วงวัน (สูงสุด 63 วัน) พร้อมที่นั่ง/คิวสำรอง</summary>
    [HttpGet("sessions")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager, SiteStaffRole.Viewer)]
    public async Task<ActionResult<ApiResponse<List<ClassSessionResponse>>>> GetSessions(Guid companyId, Guid siteId,
        [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? serviceId = null)
        => Ok(new ApiResponse<List<ClassSessionResponse>>(true,
            await _bookingService.GetClassSessionsAsync(companyId, siteId, DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), serviceId)));

    /// <summary>รอบเดียว + roster หน้าเคาน์เตอร์ (มีชื่อ/เบอร์ลูกค้า ⇒ บทบาทจัดการการจองเท่านั้น)</summary>
    [HttpGet("sessions/{sessionId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    public async Task<ActionResult<ApiResponse<ClassSessionDetailResponse>>> GetSession(Guid companyId, Guid siteId, Guid sessionId)
        => Ok(new ApiResponse<ClassSessionDetailResponse>(true, await _bookingService.GetClassSessionAsync(companyId, siteId, sessionId)));

    [HttpPut("sessions/{sessionId:guid}")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassActionResponse>>> UpdateSession(Guid companyId, Guid siteId, Guid sessionId, [FromBody] UpdateClassSessionRequest request)
        => Ok(new ApiResponse<ClassActionResponse>(true, await _bookingService.UpdateClassSessionAsync(companyId, siteId, sessionId, request, UserId)));

    /// <summary>ครูแทนทั้งรอบ (null = กลับเป็นครูประจำ) — การจองที่ยังไม่เริ่มตามทุกใบ + แจ้งลูกค้า</summary>
    [HttpPut("sessions/{sessionId:guid}/substitute")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassActionResponse>>> SetSubstitute(Guid companyId, Guid siteId, Guid sessionId, [FromBody] ClassSubstituteRequest? request)
        => Ok(new ApiResponse<ClassActionResponse>(true,
            await _bookingService.SetClassSubstituteAsync(companyId, siteId, sessionId, request ?? new ClassSubstituteRequest(), UserId)));

    /// <summary>ร้านยกเลิกทั้งคลาส — ทุกการจองยกเลิกผ่านเส้นสถานะเดิม (คืนเครดิตเต็ม · มัดจำตามกติกายกเลิก) + แจ้งลูกค้า</summary>
    [HttpPost("sessions/{sessionId:guid}/cancel")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassActionResponse>>> Cancel(Guid companyId, Guid siteId, Guid sessionId, [FromBody] ClassCancelRequest? request)
        => Ok(new ApiResponse<ClassActionResponse>(true,
            await _bookingService.CancelClassSessionAsync(companyId, siteId, sessionId, request ?? new ClassCancelRequest(), UserId)));

    [HttpPost("sessions/{sessionId:guid}/reopen")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassActionResponse>>> Reopen(Guid companyId, Guid siteId, Guid sessionId)
        => Ok(new ApiResponse<ClassActionResponse>(true, await _bookingService.ReopenClassSessionAsync(companyId, siteId, sessionId, UserId)));

    /// <summary>เช็คอินหน้าเคาน์เตอร์: มาเรียน (เสร็จสิ้น) / ไม่มา — ผ่านเส้นสถานะเดิม (บัตรคอร์สตัดเครดิต + รับรู้รายได้)</summary>
    [HttpPost("sessions/{sessionId:guid}/check-in")]
    [RequireSiteRole(SiteStaffRole.Admin, SiteStaffRole.OrderManager)]
    [RequirePermission(PermissionKeys.CmsOrderManage)]
    public async Task<ActionResult<ApiResponse<ClassActionResponse>>> CheckIn(Guid companyId, Guid siteId, Guid sessionId, [FromBody] ClassCheckInRequest request)
        => Ok(new ApiResponse<ClassActionResponse>(true, await _bookingService.CheckInClassAsync(companyId, siteId, sessionId, request, UserId)));
}
