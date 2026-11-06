using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class RecurringController : ControllerBase
{
    private readonly IRecurringTransactionService _recurringService;
    private readonly IRecurringAutopilotService _autopilot;
    private readonly IPermissionService _permissions;

    public RecurringController(IRecurringTransactionService recurringService, IRecurringAutopilotService autopilot, IPermissionService permissions)
    {
        _recurringService = recurringService;
        _autopilot = autopilot;
        _permissions = permissions;
    }

    private ActionResult<ApiResponse<T>> Forbid403<T>(string message)
        => StatusCode(403, new ApiResponse<T>(false, default, message));

    // ═══════════════ รอบ 248 — Autopilot (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1) ═══════════════

    /// <summary>ตัวเลือกของ wizard (โหมดรับเงิน · ช่องทางที่บริษัทเปิด · คำเตือนที่ปล่อยได้/ไม่ได้) — เซิร์ฟเวอร์คำนวณ หน้าแสดงอย่างเดียว</summary>
    [HttpGet("autopilot/options")]
    public async Task<ActionResult<ApiResponse<AutopilotOptionsResponse>>> AutopilotOptions(Guid companyId)
        => Ok(new ApiResponse<AutopilotOptionsResponse>(true, await _autopilot.OptionsAsync(companyId)));

    /// <summary>ความพร้อมของรายการที่บันทึกแล้ว (อ่านอย่างเดียว)</summary>
    [HttpGet("{id:guid}/readiness")]
    public async Task<ActionResult<ApiResponse<AutopilotReadinessResponse>>> Readiness(Guid companyId, Guid id)
        => Ok(new ApiResponse<AutopilotReadinessResponse>(true, await _autopilot.ReadinessAsync(companyId, id)));

    /// <summary>ความพร้อมของร่างในฟอร์ม — POST เพราะรับร่างทั้งชุด · <b>อ่านอย่างเดียว ไม่เขียนอะไร</b></summary>
    [HttpPost("readiness")]
    public async Task<ActionResult<ApiResponse<AutopilotReadinessResponse>>> ReadinessDraft(Guid companyId,
        [FromBody] CreateRecurringTransactionRequest draft, [FromQuery] Guid? recurringId = null)
        => Ok(new ApiResponse<AutopilotReadinessResponse>(true, await _autopilot.ReadinessForDraftAsync(companyId, draft, recurringId)));

    /// <summary>ทดลองรันรอบถัดไป — <b>ไม่เขียนอะไรเลย</b> (ไม่ออกเลข · ไม่สร้างลิงก์ · ไม่ส่ง)</summary>
    [HttpPost("{id:guid}/dry-run")]
    public async Task<ActionResult<ApiResponse<RecurringDryRunResponse>>> DryRun(Guid companyId, Guid id)
        => Ok(new ApiResponse<RecurringDryRunResponse>(true, await _autopilot.DryRunAsync(companyId, id)));

    /// <summary>ทดลองรันร่างในฟอร์ม — ไม่เขียนอะไรเลย</summary>
    [HttpPost("dry-run")]
    public async Task<ActionResult<ApiResponse<RecurringDryRunResponse>>> DryRunDraft(Guid companyId,
        [FromBody] CreateRecurringTransactionRequest draft, [FromQuery] Guid? recurringId = null)
        => Ok(new ApiResponse<RecurringDryRunResponse>(true, await _autopilot.DryRunDraftAsync(companyId, draft, recurringId)));

    /// <summary>ตั้ง/ล้างนโยบายอนุมัติข้ามคำเตือนของรายการ — <b>ต้องมีสิทธิ์อนุมัติเอกสารชนิดนั้น</b> (ผู้ไม่มีสิทธิ์อนุมัติห้ามตั้งให้ระบบอนุมัติแทน)</summary>
    [HttpPut("{id:guid}/approve-policy")]
    public async Task<ActionResult<ApiResponse<AutoApprovePolicyView?>>> SetApprovePolicy(Guid companyId, Guid id,
        [FromBody] SetAutoApprovePolicyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var item = await _recurringService.GetByIdAsync(companyId, id);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, item.DocumentType ?? DocumentType.Invoice))
            return Forbid403<AutoApprovePolicyView?>("ตั้งนโยบายอนุมัติได้เฉพาะผู้มีสิทธิ์อนุมัติเอกสารชนิดนี้ (Document.Approve) — ขอผู้ดูแลบริษัทเพิ่มสิทธิ์ หรือให้ผู้มีสิทธิ์ตั้งแทน");
        var result = await _autopilot.SetRecurringPolicyAsync(companyId, id, request, userId);
        return Ok(new ApiResponse<AutoApprovePolicyView?>(true, result, request.Inherit ? "ล้างนโยบายแล้ว — ตามค่าของผู้ติดต่อ" : "บันทึกนโยบายอนุมัติแล้ว"));
    }

    /// <summary>ค่าตั้ง Autopilot ของผู้ติดต่อ (ช่องทางรับเงิน + นโยบาย)</summary>
    [HttpGet("contacts/{contactId:guid}/autopilot")]
    public async Task<ActionResult<ApiResponse<ContactAutopilotSettingsResponse>>> ContactAutopilot(Guid companyId, Guid contactId)
        => Ok(new ApiResponse<ContactAutopilotSettingsResponse>(true, await _autopilot.GetContactSettingsAsync(companyId, contactId)));

    /// <summary>แก้ช่องทางรับเงินของผู้ติดต่อ — ต้องมีสิทธิ์แก้ผู้ติดต่อ</summary>
    [HttpPut("contacts/{contactId:guid}/autopilot")]
    public async Task<ActionResult<ApiResponse<ContactAutopilotSettingsResponse>>> UpdateContactAutopilot(Guid companyId, Guid contactId,
        [FromBody] UpdateContactAutopilotRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.ContactEdit))
            return Forbid403<ContactAutopilotSettingsResponse>("แก้ช่องทางรับเงินของผู้ติดต่อได้เฉพาะผู้มีสิทธิ์แก้ผู้ติดต่อ (Contact.Edit)");
        var result = await _autopilot.UpdateContactPaymentAsync(companyId, contactId, request, userId);
        return Ok(new ApiResponse<ContactAutopilotSettingsResponse>(true, result, "บันทึกช่องทางรับเงินของผู้ติดต่อแล้ว"));
    }

    /// <summary>ตั้ง/ล้างนโยบายอนุมัติระดับผู้ติดต่อ — ต้องมีสิทธิ์อนุมัติเอกสารขาย</summary>
    [HttpPut("contacts/{contactId:guid}/approve-policy")]
    public async Task<ActionResult<ApiResponse<ContactAutopilotSettingsResponse>>> SetContactApprovePolicy(Guid companyId, Guid contactId,
        [FromBody] SetAutoApprovePolicyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Invoice))
            return Forbid403<ContactAutopilotSettingsResponse>("ตั้งนโยบายอนุมัติของผู้ติดต่อได้เฉพาะผู้มีสิทธิ์อนุมัติเอกสารขาย (Document.Approve)");
        var result = await _autopilot.SetContactPolicyAsync(companyId, contactId, request, userId);
        return Ok(new ApiResponse<ContactAutopilotSettingsResponse>(true, result, request.Inherit ? "ล้างนโยบายของผู้ติดต่อแล้ว" : "บันทึกนโยบายของผู้ติดต่อแล้ว"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<RecurringTransactionResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] string? status = null)
    {
        var result = await _recurringService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search), status);
        return Ok(new ApiResponse<PagedResponse<RecurringTransactionResponse>>(true, result));
    }

    /// <summary>รอบ 247: ฟอร์มรายการประจำถามตอนเลือกผู้ติดต่อ — ไม่มีอีเมล ⇒ ปิดช่อง "ส่งอีเมล" พร้อมเหตุผล + ลิงก์หน้าผู้ติดต่อ</summary>
    [HttpGet("email-readiness")]
    public async Task<ActionResult<ApiResponse<RecurringEmailReadinessResponse>>> EmailReadiness(Guid companyId, [FromQuery] Guid contactId)
    {
        var result = await _recurringService.GetEmailReadinessAsync(companyId, contactId);
        return Ok(new ApiResponse<RecurringEmailReadinessResponse>(true, result));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> GetById(Guid companyId, Guid id)
    {
        var result = await _recurringService.GetByIdAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Create(
        Guid companyId, [FromBody] CreateRecurringTransactionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _recurringService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<RecurringTransactionResponse>(true, result, "สร้างรายการที่เกิดซ้ำสำเร็จ"));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Update(
        Guid companyId, Guid id, [FromBody] UpdateRecurringTransactionRequest request)
    {
        var result = await _recurringService.UpdateAsync(companyId, id, request);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid id)
    {
        await _recurringService.DeleteAsync(companyId, id);
        return NoContent();
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Pause(Guid companyId, Guid id)
    {
        var result = await _recurringService.PauseAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "หยุดชั่วคราวสำเร็จ"));
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> Resume(Guid companyId, Guid id)
    {
        var result = await _recurringService.ResumeAsync(companyId, id);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "เปิดใช้งานอีกครั้งสำเร็จ"));
    }

    [HttpPost("{id:guid}/run-now")]
    public async Task<ActionResult<ApiResponse<RecurringTransactionResponse>>> RunNow(Guid companyId, Guid id)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _recurringService.RunNowAsync(companyId, id, userId);
        return Ok(new ApiResponse<RecurringTransactionResponse>(true, result, "ดำเนินการสำเร็จ"));
    }
}
