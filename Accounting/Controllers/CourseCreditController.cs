using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.CourseCredit;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// คอร์ส / Class Credits / Hour Pass (ทีม L · <c>docs/COURSE_CREDIT_DESIGN.md</c>) —
/// อ่าน: สมาชิกบริษัท · ตั้งแพ็กเกจ/รอบเรียน/ปิดบัตรหมดอายุ/เปิดบัตรจากใบเดิม: <c>POS.Manager</c> ·
/// ขาย/จอง/เช็คอิน/ยกเลิก: <c>POS.Cashier</c> (หน้าร้านชุดเดียวกับ POS — การขายออกใบกำกับเหมือนบิล POS)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/course-credits")]
[Authorize]
public class CourseCreditController : ControllerBase
{
    private readonly ICourseCreditService _service;

    public CourseCreditController(ICourseCreditService service) { _service = service; }

    private string UserId => JwtHelper.GetUserIdFromClaims(User).ToString();

    // ---------- แพ็กเกจ ----------
    [HttpGet("packages")]
    public async Task<ActionResult<ApiResponse<List<CreditPackageResponse>>>> Packages(Guid companyId, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
        => Ok(new ApiResponse<List<CreditPackageResponse>>(true, await _service.GetPackagesAsync(companyId, includeInactive, ct)));

    [HttpPost("packages")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditPackageResponse>>> CreatePackage(Guid companyId, [FromBody] SaveCreditPackageRequest request, CancellationToken ct)
        => Ok(new ApiResponse<CreditPackageResponse>(true, await _service.CreatePackageAsync(companyId, request, UserId, ct), "เพิ่มแพ็กเกจแล้ว"));

    [HttpPut("packages/{packageId:guid}")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditPackageResponse>>> UpdatePackage(Guid companyId, Guid packageId, [FromBody] SaveCreditPackageRequest request, CancellationToken ct)
        => Ok(new ApiResponse<CreditPackageResponse>(true, await _service.UpdatePackageAsync(companyId, packageId, request, UserId, ct), "บันทึกแพ็กเกจแล้ว"));

    // ---------- บัตรของลูกค้า ----------
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<CreditSummaryResponse>>> Summary(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<CreditSummaryResponse>(true, await _service.GetSummaryAsync(companyId, ct)));

    [HttpGet("passes")]
    public async Task<ActionResult<ApiResponse<PagedResponse<CreditPassResponse>>>> Passes(Guid companyId, [FromQuery] Guid? contactId,
        [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(new ApiResponse<PagedResponse<CreditPassResponse>>(true, await _service.GetPassesAsync(companyId, contactId, status, search, page, pageSize, ct)));

    [HttpGet("passes/{passId:guid}")]
    public async Task<ActionResult<ApiResponse<CreditPassDetailResponse>>> Pass(Guid companyId, Guid passId, CancellationToken ct)
        => Ok(new ApiResponse<CreditPassDetailResponse>(true, await _service.GetPassAsync(companyId, passId, ct)));

    [HttpPost("passes/sell")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<CreditPassResponse>>> Sell(Guid companyId, [FromBody] SellCreditPassRequest request, CancellationToken ct)
    {
        var r = await _service.SellAsync(companyId, request, UserId, ct);
        return Ok(new ApiResponse<CreditPassResponse>(true, r, $"ขายคอร์สแล้ว — บัตร {r.PassNumber} · เอกสาร {r.SaleDocumentNumber}"));
    }

    [HttpPost("passes/activate-from-document")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditPassResponse>>> Activate(Guid companyId, [FromBody] ActivateCreditPassRequest request, CancellationToken ct)
    {
        var r = await _service.ActivateFromDocumentAsync(companyId, request, UserId, ct);
        return Ok(new ApiResponse<CreditPassResponse>(true, r, $"เปิดบัตร {r.PassNumber} จากเอกสาร {r.SaleDocumentNumber} แล้ว"));
    }

    /// <summary>R-38 ตัวอย่างผลการคืนเงินจากบัตร (อ่าน) — ส่วนที่เหลือ · ราคาต่อเครดิต · คืนบางส่วนได้สูงสุด</summary>
    [HttpGet("passes/{passId:guid}/refund-preview")]
    public async Task<ActionResult<ApiResponse<CreditRefundPreviewResponse>>> RefundPreview(Guid companyId, Guid passId, CancellationToken ct)
        => Ok(new ApiResponse<CreditRefundPreviewResponse>(true, await _service.PreviewRefundAsync(companyId, passId, ct)));

    /// <summary>R-38 สร้าง<b>ใบร่าง</b>ใบลดหนี้คืนเงินจากบัตร — ผู้มีสิทธิ์อนุมัติที่หน้าเอกสาร (ด่าน CreditPassDocumentHook ลดเครดิต/ปิดบัตร)</summary>
    [HttpPost("passes/{passId:guid}/refund-draft")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditRefundDraftResponse>>> RefundDraft(Guid companyId, Guid passId, [FromBody] DraftCreditRefundRequest request, CancellationToken ct)
    {
        var r = await _service.DraftRefundCreditNoteAsync(companyId, passId, request, UserId, ct);
        return Ok(new ApiResponse<CreditRefundDraftResponse>(true, r, r.Message));
    }

    [HttpPost("passes/{passId:guid}/expire")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditPassResponse>>> Expire(Guid companyId, Guid passId, CancellationToken ct)
        => Ok(new ApiResponse<CreditPassResponse>(true, await _service.ExpirePassAsync(companyId, passId, UserId, ct), "ปิดบัตรหมดอายุแล้ว — รับรู้ส่วนคงเหลือเป็นรายได้"));

    [HttpPost("passes/expire-due")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditExpiryRunResult>>> ExpireDue(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<CreditExpiryRunResult>(true, await _service.ExpireDueAsync(companyId, UserId, ct)));

    // ---------- รอบเรียน ----------
    [HttpGet("trainers")]
    public async Task<ActionResult<ApiResponse<List<CreditTrainerOption>>>> Trainers(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<List<CreditTrainerOption>>(true, await _service.GetTrainersAsync(companyId, ct)));

    [HttpGet("services")]
    public async Task<ActionResult<ApiResponse<List<CreditTrainerOption>>>> Services(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<List<CreditTrainerOption>>(true, await _service.GetBookableServicesAsync(companyId, ct)));

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<CreditSessionResponse>>>> Sessions(Guid companyId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => Ok(new ApiResponse<List<CreditSessionResponse>>(true, await _service.GetSessionsAsync(companyId, from, to, ct)));

    [HttpPost("sessions")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditSessionResponse>>> CreateSession(Guid companyId, [FromBody] SaveCreditSessionRequest request, CancellationToken ct)
        => Ok(new ApiResponse<CreditSessionResponse>(true, await _service.CreateSessionAsync(companyId, request, UserId, ct), "เพิ่มรอบเรียนแล้ว"));

    [HttpPost("sessions/{sessionId:guid}/cancel")]
    [RequirePermission(PermissionKeys.PosManager)]
    public async Task<ActionResult<ApiResponse<CreditSessionResponse>>> CancelSession(Guid companyId, Guid sessionId, CancellationToken ct)
        => Ok(new ApiResponse<CreditSessionResponse>(true, await _service.CancelSessionAsync(companyId, sessionId, UserId, ct), "ยกเลิกรอบแล้ว — คืนเครดิตผู้จองทุกคน"));

    [HttpGet("sessions/{sessionId:guid}/bookings")]
    public async Task<ActionResult<ApiResponse<List<CreditBookingResponse>>>> SessionBookings(Guid companyId, Guid sessionId, CancellationToken ct)
        => Ok(new ApiResponse<List<CreditBookingResponse>>(true, await _service.GetSessionBookingsAsync(companyId, sessionId, ct)));

    // ---------- จอง ----------
    [HttpPost("bookings")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<CreditBookingResponse>>> Book(Guid companyId, [FromBody] BookWithCreditRequest request, CancellationToken ct)
        => Ok(new ApiResponse<CreditBookingResponse>(true, await _service.BookAsync(companyId, request, UserId, ct), "จองแล้ว — กันเครดิตไว้"));

    [HttpPost("bookings/{bookingId:guid}/settle")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<CreditBookingResponse>>> Settle(Guid companyId, Guid bookingId, [FromBody] SettleCreditBookingRequest request, CancellationToken ct)
        => Ok(new ApiResponse<CreditBookingResponse>(true, await _service.SettleBookingAsync(companyId, bookingId, request, UserId, ct), "ตัดเครดิตแล้ว"));

    [HttpPost("bookings/{bookingId:guid}/cancel")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<ActionResult<ApiResponse<CreditBookingResponse>>> Cancel(Guid companyId, Guid bookingId, [FromBody] CancelCreditBookingRequest request, CancellationToken ct)
    {
        var r = await _service.CancelBookingAsync(companyId, bookingId, request, UserId, ct);
        return Ok(new ApiResponse<CreditBookingResponse>(true, r, r.StatusLabel));
    }
}
