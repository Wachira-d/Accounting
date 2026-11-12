using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Line;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 260 — คิวพนักงานของ "สลิปจากแชต LINE ที่ยังไม่ผูกเอกสาร" (ไม่ได้ผูก LINE · อ่านยอดไม่ได้ · ยอดไม่ตรง · ลูกค้าไม่ได้เลือก/บอกว่าไม่ใช่) ·
/// แสดงบนหน้ารายการรับชำระออนไลน์ (คิวเดียวกับสลิปพอร์ทัล — <c>AutopilotService.SlipQueueHref</c>)
/// <para><b>สิทธิ์:</b> ดู = ดูรายการรับชำระ (<see cref="PaymentGatewayPermissionScope.ViewPayments"/>) · ผูก/ปิด = สิทธิ์เดียวกับยืนยัน/ปฏิเสธสลิป
/// (<see cref="PaymentGatewayPermissionScope.ConfirmManually"/>) · ผูกแล้ว = รายการรอยืนยัน (ไม่ใช่รับชำระ — ยืนยันที่แถวรายการตามเดิม)</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/companies/{companyId:guid}/line-chat-slips")]
public class LineChatSlipController : ControllerBase
{
    private readonly ILineChatSlipService _slips;

    public LineChatSlipController(ILineChatSlipService slips) => _slips = slips;

    private string Actor() => $"user:{JwtHelper.GetUserIdFromClaims(User)}";

    [HttpGet]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ViewPayments)]
    public async Task<ActionResult<ApiResponse<LineChatSlipListResponse>>> List(Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<LineChatSlipListResponse>(true, await _slips.ListOpenAsync(companyId, ct)));

    [HttpGet("{slipId:guid}/file")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ViewPayments)]
    public async Task<IActionResult> SlipFile(Guid companyId, Guid slipId, CancellationToken ct)
    {
        var f = await _slips.ReadFileAsync(companyId, slipId, ct);
        if (f == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบไฟล์สลิป"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return File(f.Value.Bytes, f.Value.ContentType);
    }

    [HttpPost("{slipId:guid}/assign")]
    [Accounting.Filters.RejectApiKey("ผูกสลิปจากแชตกับเอกสาร")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ConfirmManually)]
    public async Task<ActionResult<ApiResponse<LineChatSlipAssignResult>>> Assign(Guid companyId, Guid slipId,
        [FromBody] AssignLineChatSlipRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _slips.AssignAsync(companyId, slipId, request, Actor(), ct);
            return Ok(new ApiResponse<LineChatSlipAssignResult>(true, r,
                $"ผูกสลิปกับ {r.DocumentNumber} แล้ว — อยู่ในรายการรอยืนยันด้านล่าง (ยังไม่ใช่การรับชำระ)"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<LineChatSlipAssignResult>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<LineChatSlipAssignResult>(false, null, ex.Message)); }
    }

    [HttpPost("{slipId:guid}/dismiss")]
    [Accounting.Filters.RejectApiKey("ปิดสลิปจากแชต")]
    [Accounting.Filters.RequirePermission(PaymentGatewayPermissionScope.ConfirmManually)]
    public async Task<ActionResult<ApiResponse<object>>> Dismiss(Guid companyId, Guid slipId,
        [FromBody] DismissLineChatSlipRequest request, CancellationToken ct)
    {
        try
        {
            await _slips.DismissAsync(companyId, slipId, request?.Reason, Actor(), ct);
            return Ok(new ApiResponse<object>(true, null, "ปิดรายการแล้ว"));
        }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
        catch (BusinessRuleException ex) { return StatusCode(ex.StatusCode, new ApiResponse<object>(false, null, ex.Message)); }
    }
}
