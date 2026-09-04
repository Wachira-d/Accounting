using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>หลังบ้านโมดูลที่พัก — ตั้งค่า (LodgingSettings) + front desk (LodgingManage)
/// ทุก route มี {companyId} ⇒ TenantGuardFilter ตรวจสมาชิกบริษัทให้ (กฎ M)</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/lodging")]
[Authorize]
public class LodgingController : ControllerBase
{
    private readonly ILodgingService _svc;
    public LodgingController(ILodgingService svc) { _svc = svc; }

    private string Uid => JwtHelper.GetUserIdFromClaims(User).ToString();
    private static ActionResult<ApiResponse<T>> Wrap<T>(T data, string? msg = null) => new OkObjectResult(new ApiResponse<T>(true, data, msg));
    private static ActionResult<ApiResponse<T>> Missing<T>(string msg) => new NotFoundObjectResult(new ApiResponse<T>(false, default, msg));

    /// <summary>
    /// อัปโหลดรูปที่พัก/ประเภทห้อง (LDG-P1-05)
    ///
    /// <para>เดิมหน้าตั้งค่าให้ <b>พิมพ์ URL เอง</b> ซึ่งลูกค้า SME ทำไม่ได้จริง
    /// (ต้องไปหาที่ฝากรูปเองก่อน) ⇒ ช่องรูปว่างเปล่าทุกราย แล้วหน้าเว็บที่พักไม่มีรูป</para>
    ///
    /// <para>⚠️ prefix <c>/uploads/lodging</c> ต้องอยู่ใน <c>publicUploadPrefixes</c>
    /// ของ <c>Program.cs</c> ด้วย ไม่งั้นเขียนไฟล์สำเร็จแต่เบราว์เซอร์ได้ 404 —
    /// บังคับด้วย <c>tools/upload_route_check.py</c></para>
    /// </summary>
    [HttpPost("images")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<string>>> UploadImage(
        Guid companyId, IFormFile? file,
        [FromServices] IImageProcessingService images,
        [FromServices] IWebHostEnvironment env)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาเลือกไฟล์รูป"));
        if (!file.ContentType.StartsWith("image/"))
            return BadRequest(new ApiResponse<string>(false, null, "รองรับเฉพาะไฟล์รูปภาพ"));

        // แยกโฟลเดอร์ต่อบริษัท — รูปของที่พักเป็นของสาธารณะ แต่ไม่ควรปนกันข้ามผู้เช่า
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "lodging", companyId.ToString("N"));
        await using var s = file.OpenReadStream();
        var saved = await images.ProcessAndSaveAsync(
            s, file.ContentType, file.FileName, dir,
            $"/uploads/lodging/{companyId:N}", ImageProfile.Banner);
        return Wrap(saved.RelativeUrl, "อัปโหลดรูปแล้ว");
    }

    // ═══════════ ตั้งค่า: ที่พัก ═══════════

    [HttpGet("properties")]
    public async Task<ActionResult<ApiResponse<List<LodgingPropertyDto>>>> GetProperties(Guid companyId)
        => Wrap(await _svc.GetPropertiesAsync(companyId));

    [HttpGet("properties/{propertyId:guid}")]
    public async Task<ActionResult<ApiResponse<LodgingPropertyDto>>> GetProperty(Guid companyId, Guid propertyId)
    {
        var p = await _svc.GetPropertyAsync(companyId, propertyId);
        return p == null ? Missing<LodgingPropertyDto>("ไม่พบที่พัก") : Wrap(p);
    }

    [HttpPost("properties")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingPropertyDto>>> CreateProperty(Guid companyId, [FromBody] LodgingPropertyDto dto)
        => Wrap(await _svc.CreatePropertyAsync(companyId, dto, Uid), "สร้างที่พักแล้ว");

    [HttpPut("properties/{propertyId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingPropertyDto>>> UpdateProperty(Guid companyId, Guid propertyId, [FromBody] LodgingPropertyDto dto)
        => Wrap(await _svc.UpdatePropertyAsync(companyId, propertyId, dto, Uid), "บันทึกการตั้งค่าแล้ว");

    // ═══════════ ตั้งค่า: ห้อง ═══════════

    [HttpGet("properties/{propertyId:guid}/room-types")]
    public async Task<ActionResult<ApiResponse<List<LodgingRoomTypeDto>>>> GetRoomTypes(Guid companyId, Guid propertyId, [FromQuery] bool includeInactive = true)
        => Wrap(await _svc.GetRoomTypesAsync(companyId, propertyId, includeInactive));

    [HttpPost("room-types")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingRoomTypeDto>>> SaveRoomType(Guid companyId, [FromBody] LodgingRoomTypeDto dto)
        => Wrap(await _svc.SaveRoomTypeAsync(companyId, dto, Uid), "บันทึกประเภทห้องแล้ว");

    [HttpDelete("room-types/{roomTypeId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteRoomType(Guid companyId, Guid roomTypeId)
        => await _svc.DeleteRoomTypeAsync(companyId, roomTypeId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบประเภทห้อง");

    [HttpGet("properties/{propertyId:guid}/units")]
    public async Task<ActionResult<ApiResponse<List<LodgingUnitDto>>>> GetUnits(Guid companyId, Guid propertyId)
        => Wrap(await _svc.GetUnitsAsync(companyId, propertyId));

    [HttpPost("units")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingUnitDto>>> SaveUnit(Guid companyId, [FromBody] LodgingUnitDto dto)
        => Wrap(await _svc.SaveUnitAsync(companyId, dto, Uid), "บันทึกห้องแล้ว");

    [HttpDelete("units/{unitId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteUnit(Guid companyId, Guid unitId)
        => await _svc.DeleteUnitAsync(companyId, unitId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบห้อง");

    /// <summary>เปลี่ยนสถานะแม่บ้าน/ปิดซ่อม — front desk ทำได้ (ไม่ต้องสิทธิ์ตั้งค่า)</summary>
    [HttpPut("units/{unitId:guid}/status")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingUnitDto>>> SetUnitStatus(Guid companyId, Guid unitId, [FromBody] LodgingUnitStatusRequest req)
        => Wrap(await _svc.SetUnitStatusAsync(companyId, unitId, req, Uid), "อัปเดตสถานะห้องแล้ว");

    // ═══════════ ตั้งค่า: ราคา/นโยบาย/บริการเสริม ═══════════

    [HttpGet("properties/{propertyId:guid}/rate-plans")]
    public async Task<ActionResult<ApiResponse<List<LodgingRatePlanDto>>>> GetRatePlans(Guid companyId, Guid propertyId)
        => Wrap(await _svc.GetRatePlansAsync(companyId, propertyId));

    [HttpPost("rate-plans")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingRatePlanDto>>> SaveRatePlan(Guid companyId, [FromBody] LodgingRatePlanDto dto)
        => Wrap(await _svc.SaveRatePlanAsync(companyId, dto, Uid), "บันทึกแผนราคาแล้ว");

    [HttpDelete("rate-plans/{ratePlanId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteRatePlan(Guid companyId, Guid ratePlanId)
        => await _svc.DeleteRatePlanAsync(companyId, ratePlanId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบแผนราคา");

    [HttpGet("properties/{propertyId:guid}/seasons")]
    public async Task<ActionResult<ApiResponse<List<LodgingSeasonDto>>>> GetSeasons(Guid companyId, Guid propertyId)
        => Wrap(await _svc.GetSeasonsAsync(companyId, propertyId));

    [HttpPost("seasons")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingSeasonDto>>> SaveSeason(Guid companyId, [FromBody] LodgingSeasonDto dto)
        => Wrap(await _svc.SaveSeasonAsync(companyId, dto, Uid), "บันทึกฤดูกาลแล้ว");

    [HttpDelete("seasons/{seasonId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteSeason(Guid companyId, Guid seasonId)
        => await _svc.DeleteSeasonAsync(companyId, seasonId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบฤดูกาล");

    [HttpGet("properties/{propertyId:guid}/rate-overrides")]
    public async Task<ActionResult<ApiResponse<List<LodgingRateOverrideDto>>>> GetRateOverrides(Guid companyId, Guid propertyId, [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? roomTypeId = null)
        => Wrap(await _svc.GetRateOverridesAsync(companyId, propertyId, from, to, roomTypeId));

    [HttpPost("rate-overrides")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<int>>> SaveRateOverrides(Guid companyId, [FromBody] LodgingRateOverrideBulkRequest req)
    {
        var n = await _svc.SaveRateOverridesAsync(companyId, req, Uid);
        return Wrap(n, req.Clear ? $"ล้างราคาพิเศษ {n} วัน" : $"ตั้งราคา/การขาย {n} วัน");
    }

    [HttpGet("properties/{propertyId:guid}/policies")]
    public async Task<ActionResult<ApiResponse<List<LodgingCancellationPolicyDto>>>> GetPolicies(Guid companyId, Guid propertyId)
        => Wrap(await _svc.GetPoliciesAsync(companyId, propertyId));

    [HttpPost("policies")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingCancellationPolicyDto>>> SavePolicy(Guid companyId, [FromBody] LodgingCancellationPolicyDto dto)
        => Wrap(await _svc.SavePolicyAsync(companyId, dto, Uid), "บันทึกนโยบายแล้ว");

    [HttpDelete("policies/{policyId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeletePolicy(Guid companyId, Guid policyId)
        => await _svc.DeletePolicyAsync(companyId, policyId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบนโยบาย");

    [HttpGet("properties/{propertyId:guid}/extras")]
    public async Task<ActionResult<ApiResponse<List<LodgingExtraDto>>>> GetExtras(Guid companyId, Guid propertyId)
        => Wrap(await _svc.GetExtrasAsync(companyId, propertyId, includeInactive: true));

    [HttpPost("extras")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<LodgingExtraDto>>> SaveExtra(Guid companyId, [FromBody] LodgingExtraDto dto)
        => Wrap(await _svc.SaveExtraAsync(companyId, dto, Uid), "บันทึกบริการเสริมแล้ว");

    [HttpDelete("extras/{extraId:guid}")]
    [RequirePermission(PermissionKeys.LodgingSettings)]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteExtra(Guid companyId, Guid extraId)
        => await _svc.DeleteExtraAsync(companyId, extraId) ? Wrap(true, "ลบแล้ว") : Missing<bool>("ไม่พบบริการเสริม");

    // ═══════════ Front desk: ค้นหา/quote/จอง ═══════════

    [HttpPost("properties/{propertyId:guid}/search")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<List<LodgingSearchResult>>>> Search(Guid companyId, Guid propertyId, [FromBody] LodgingSearchRequest req)
        => Wrap(await _svc.SearchAsync(companyId, propertyId, req));

    [HttpPost("properties/{propertyId:guid}/quote")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingQuoteResponse>>> Quote(Guid companyId, Guid propertyId, [FromBody] LodgingQuoteRequest req)
        => Wrap(await _svc.QuoteAsync(companyId, propertyId, req));

    [HttpPost("properties/{propertyId:guid}/reservations")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> CreateReservation(Guid companyId, Guid propertyId, [FromBody] LodgingCreateReservationRequest req)
    {
        var source = req.Source is null or LodgingReservationSource.Web ? LodgingReservationSource.WalkIn : req.Source.Value;
        var r = await _svc.CreateReservationAsync(companyId, propertyId, req, source, Uid);
        return new ObjectResult(new ApiResponse<LodgingReservationResponse>(true, r, $"สร้างการจอง {r.ReservationNumber} แล้ว")) { StatusCode = 201 };
    }

    [HttpGet("reservations")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<PagedResponse<LodgingReservationListItem>>>> ListReservations(Guid companyId,
        [FromQuery] Guid? propertyId, [FromQuery] string? status, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? search, [FromQuery] string? view, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Wrap(await _svc.ListReservationsAsync(companyId, propertyId, status, from, to, search, view, page, pageSize));

    [HttpGet("reservations/{id:guid}")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> GetReservation(Guid companyId, Guid id)
    {
        var r = await _svc.GetReservationAsync(companyId, id);
        return r == null ? Missing<LodgingReservationResponse>("ไม่พบการจอง") : Wrap(r);
    }

    [HttpPut("reservations/{id:guid}")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> UpdateReservation(Guid companyId, Guid id, [FromBody] LodgingUpdateReservationRequest req)
        => Wrap(await _svc.UpdateReservationAsync(companyId, id, req, Uid), "บันทึกแล้ว");

    [HttpPost("reservations/{id:guid}/confirm")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Confirm(Guid companyId, Guid id, [FromBody] LodgingConfirmRequest req)
    {
        var r = await _svc.ConfirmAsync(companyId, id, req, Uid);
        var msg = r.DepositDocumentNumber != null ? $"ยืนยันการจองแล้ว · ใบเสร็จมัดจำ {r.DepositDocumentNumber}" : "ยืนยันการจองแล้ว";
        return Wrap(r, msg);
    }

    /// <summary>ปฏิเสธสลิปที่แขกส่งมา — ทางออกที่หายไปเมื่อสลิปไม่ตรง/ปลอม
    /// (เดิมมีแค่ "ยืนยัน" กับ "ยกเลิกทั้งใบ" ⇒ พนักงานได้แต่เงียบ)</summary>
    [HttpPost("reservations/{id:guid}/reject-slip")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> RejectSlip(
        Guid companyId, Guid id, [FromBody] LodgingRejectSlipRequest req)
    {
        var r = await _svc.RejectSlipAsync(companyId, id, req, Uid);
        if (r == null) return NotFound(new ApiResponse<LodgingReservationResponse>(false, null, "ไม่พบการจอง"));
        var msg = r.SlipUploadBlocked
            ? "ปฏิเสธสลิปและปิดรับสลิปของการจองนี้แล้ว — แจ้งแขกทางอีเมลแล้ว"
            : "ปฏิเสธสลิปแล้ว — แจ้งแขกพร้อมเหตุผล และต่อเวลาถือห้องให้อีก 24 ชม.";
        return Wrap(r, msg);
    }

    [HttpPost("reservations/{id:guid}/assign")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Assign(Guid companyId, Guid id, [FromBody] LodgingAssignUnitRequest req)
        => Wrap(await _svc.AssignUnitAsync(companyId, id, req, Uid), "จัดห้องแล้ว");

    [HttpPost("reservations/{id:guid}/check-in")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> CheckIn(Guid companyId, Guid id, [FromBody] LodgingCheckInRequest req)
        => Wrap(await _svc.CheckInAsync(companyId, id, req, Uid), "เช็คอินแล้ว");

    [HttpPost("reservations/{id:guid}/charges")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> AddCharge(Guid companyId, Guid id, [FromBody] LodgingAddChargeRequest req)
        => Wrap(await _svc.AddChargeAsync(companyId, id, req, Uid), "เพิ่มรายการแล้ว");

    [HttpDelete("reservations/{id:guid}/charges/{chargeId:guid}")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> CancelCharge(Guid companyId, Guid id, Guid chargeId)
        => Wrap(await _svc.CancelChargeAsync(companyId, id, chargeId, Uid), "ยกเลิกรายการแล้ว");

    [HttpPost("reservations/{id:guid}/check-out")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> CheckOut(Guid companyId, Guid id, [FromBody] LodgingCheckOutRequest req)
    {
        var r = await _svc.CheckOutAsync(companyId, id, req, Uid);
        // บอกเลขเอกสารปลายทางเสมอ — ผลของการกระทำไปโผล่ที่เอกสารคนละใบ (กติกา "ห้ามให้ผู้ใช้เดาว่าไปดูผลที่ไหน")
        var msg = $"เช็คเอาต์แล้ว · ออก {r.FinalDocumentNumber}" + (r.BalanceDue > 0 ? $" (ค้างชำระ {r.BalanceDue:N2})" : " · รับชำระครบ");
        return Wrap(r, msg);
    }

    [HttpPost("reservations/{id:guid}/cancel")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Cancel(Guid companyId, Guid id, [FromBody] LodgingCancelRequest? req)
    {
        var r = await _svc.CancelAsync(companyId, id, req ?? new LodgingCancelRequest(), Uid);
        return Wrap(r, $"ยกเลิกแล้ว · ค่าปรับ {r.CancellationFee:N2} · คืนเงิน {r.RefundAmount:N2}");
    }

    [HttpPost("reservations/{id:guid}/no-show")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> NoShow(Guid companyId, Guid id, [FromBody] LodgingCancelRequest? req)
    {
        var r = await _svc.CancelAsync(companyId, id, req ?? new LodgingCancelRequest(), Uid, noShow: true);
        return Wrap(r, $"บันทึก no-show แล้ว · ริบ {r.CancellationFee:N2} · คืนเงิน {r.RefundAmount:N2}");
    }

    [HttpPost("reservations/{id:guid}/reschedule")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingReservationResponse>>> Reschedule(Guid companyId, Guid id, [FromBody] LodgingRescheduleRequest req)
        => Wrap(await _svc.RescheduleAsync(companyId, id, req, Uid), "เลื่อนวันแล้ว — กรุณาจัดห้องใหม่");

    // ═══════════ แม่บ้าน / คำขอแขก ═══════════

    [HttpGet("properties/{propertyId:guid}/housekeeping")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<List<LodgingHousekeepingTaskDto>>>> GetTasks(Guid companyId, Guid propertyId, [FromQuery] string? status, [FromQuery] DateTime? date)
        => Wrap(await _svc.GetTasksAsync(companyId, propertyId, status, date));

    [HttpPost("housekeeping")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingHousekeepingTaskDto>>> CreateTask(Guid companyId, [FromBody] LodgingHousekeepingTaskDto dto)
        => Wrap(await _svc.CreateTaskAsync(companyId, dto, Uid), "สร้างงานแล้ว");

    [HttpPut("housekeeping/{taskId:guid}/status")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingHousekeepingTaskDto>>> UpdateTask(Guid companyId, Guid taskId, [FromBody] LodgingTaskStatusRequest req)
        => Wrap(await _svc.UpdateTaskStatusAsync(companyId, taskId, req, Uid), "อัปเดตงานแล้ว");

    [HttpGet("properties/{propertyId:guid}/guest-requests")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<List<LodgingGuestRequestDto>>>> GetGuestRequests(Guid companyId, Guid propertyId, [FromQuery] bool openOnly = true)
        => Wrap(await _svc.GetGuestRequestsAsync(companyId, propertyId, openOnly));

    [HttpPut("guest-requests/{requestId:guid}")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingGuestRequestDto>>> ResolveGuestRequest(Guid companyId, Guid requestId, [FromBody] LodgingGuestRequestResolve req)
        => Wrap(await _svc.ResolveGuestRequestAsync(companyId, requestId, req, Uid), "อัปเดตคำขอแล้ว");

    // ═══════════ แดชบอร์ด / ปฏิทิน ═══════════

    [HttpGet("properties/{propertyId:guid}/dashboard")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingDashboard>>> Dashboard(Guid companyId, Guid propertyId, [FromQuery] DateTime? date)
        => Wrap(await _svc.GetDashboardAsync(companyId, propertyId, date));

    [HttpGet("properties/{propertyId:guid}/calendar")]
    [RequirePermission(PermissionKeys.LodgingManage)]
    public async Task<ActionResult<ApiResponse<LodgingCalendar>>> Calendar(Guid companyId, Guid propertyId, [FromQuery] DateTime from, [FromQuery] DateTime? to)
        => Wrap(await _svc.GetCalendarAsync(companyId, propertyId, from, to ?? from.AddDays(14)));
}
