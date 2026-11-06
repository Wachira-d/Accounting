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

    /// <summary>ฝ่ายค้าน LOW-6: ความพร้อม/ทดลองรันเปิดเผยยอด · คำเตือนค้างชำระ · อีเมลลูกค้า ⇒ ต้องมีสิทธิ์สร้างเอกสารชนิดนั้น (คนตั้งรายการประจำ) · null = ผ่าน</summary>
    private async Task<string?> DenyAutopilotReadAsync(Guid companyId, DocumentType? type)
        => await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, JwtHelper.GetUserIdFromClaims(User), type ?? DocumentType.Invoice)
            ? null
            : "ดูความพร้อม/ทดลองรันของรายการประจำได้เฉพาะผู้มีสิทธิ์สร้างเอกสารชนิดนี้ (Document.Create)";

    // ═══════════════ รอบ 248 — Autopilot (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1) ═══════════════

    /// <summary>ตัวเลือกของ wizard (โหมดรับเงิน · ช่องทางที่บริษัทเปิด · คำเตือนที่ปล่อยได้/ไม่ได้) — เซิร์ฟเวอร์คำนวณ หน้าแสดงอย่างเดียว</summary>
    [HttpGet("autopilot/options")]
    public async Task<ActionResult<ApiResponse<AutopilotOptionsResponse>>> AutopilotOptions(Guid companyId)
        => Ok(new ApiResponse<AutopilotOptionsResponse>(true, await _autopilot.OptionsAsync(companyId)));

    /// <summary>ความพร้อมของรายการที่บันทึกแล้ว (อ่านอย่างเดียว)</summary>
    [HttpGet("{id:guid}/readiness")]
    public async Task<ActionResult<ApiResponse<AutopilotReadinessResponse>>> Readiness(Guid companyId, Guid id)
    {
        var item = await _recurringService.GetByIdAsync(companyId, id);
        if (await DenyAutopilotReadAsync(companyId, item.DocumentType) is { } deny) return Forbid403<AutopilotReadinessResponse>(deny);
        return Ok(new ApiResponse<AutopilotReadinessResponse>(true, await _autopilot.ReadinessAsync(companyId, id)));
    }

    /// <summary>ความพร้อมของร่างในฟอร์ม — POST เพราะรับร่างทั้งชุด · <b>อ่านอย่างเดียว ไม่เขียนอะไร</b></summary>
    [HttpPost("readiness")]
    public async Task<ActionResult<ApiResponse<AutopilotReadinessResponse>>> ReadinessDraft(Guid companyId,
        [FromBody] CreateRecurringTransactionRequest draft, [FromQuery] Guid? recurringId = null)
    {
        if (await DenyAutopilotReadAsync(companyId, draft.DocumentType) is { } deny) return Forbid403<AutopilotReadinessResponse>(deny);
        return Ok(new ApiResponse<AutopilotReadinessResponse>(true, await _autopilot.ReadinessForDraftAsync(companyId, draft, recurringId)));
    }

    /// <summary>ทดลองรันรอบถัดไป — <b>ไม่เขียนอะไรเลย</b> (ไม่ออกเลข · ไม่สร้างลิงก์ · ไม่ส่ง)</summary>
    [HttpPost("{id:guid}/dry-run")]
    public async Task<ActionResult<ApiResponse<RecurringDryRunResponse>>> DryRun(Guid companyId, Guid id)
    {
        var item = await _recurringService.GetByIdAsync(companyId, id);
        if (await DenyAutopilotReadAsync(companyId, item.DocumentType) is { } deny) return Forbid403<RecurringDryRunResponse>(deny);
        return Ok(new ApiResponse<RecurringDryRunResponse>(true, await _autopilot.DryRunAsync(companyId, id)));
    }

    /// <summary>ทดลองรันร่างในฟอร์ม — ไม่เขียนอะไรเลย</summary>
    [HttpPost("dry-run")]
    public async Task<ActionResult<ApiResponse<RecurringDryRunResponse>>> DryRunDraft(Guid companyId,
        [FromBody] CreateRecurringTransactionRequest draft, [FromQuery] Guid? recurringId = null)
    {
        if (await DenyAutopilotReadAsync(companyId, draft.DocumentType) is { } deny) return Forbid403<RecurringDryRunResponse>(deny);
        return Ok(new ApiResponse<RecurringDryRunResponse>(true, await _autopilot.DryRunDraftAsync(companyId, draft, recurringId)));
    }

    /// <summary>รอบ 251 (WP-07 ข้อ 1 · AUTOPILOT §4.6): ระดับ Autopilot ของแถวในตาราง — เซิร์ฟเวอร์คำนวณด้วยตัวตัดสินเดียวกับแผงความพร้อม ·
    /// อ่านอย่างเดียว · ส่งเฉพาะแถวที่ผู้เรียกมีสิทธิ์สร้างเอกสารชนิดนั้น (ด่านเดียวกับความพร้อม LOW-6) · <paramref name="ids"/> คั่นด้วยจุลภาค</summary>
    [HttpGet("autopilot/levels")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RecurringAutopilotLevelRow>>>> AutopilotLevels(Guid companyId, [FromQuery] string? ids)
    {
        var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        // ฝ่ายค้านข้อ 6: ด่านสิทธิ์ต่อชนิดถูกถามก่อนรวบรวม (service จำผลต่อชนิด) — แถวที่ไม่มีสิทธิ์ไม่ถูกรวบรวมและไม่ส่งกลับ
        var rows = await _autopilot.LevelsAsync(companyId, list, async t => await DenyAutopilotReadAsync(companyId, t) == null);
        return Ok(new ApiResponse<IReadOnlyList<RecurringAutopilotLevelRow>>(true, rows));
    }

    /// <summary>รอบ 251 (WP-07 ข้อ 4 · §4.5): ส่งอีเมลทดสอบของรอบถัดไปถึง<b>ผู้ใช้ที่กดเท่านั้น</b> — ไม่นับเพดานอีเมลเอกสาร · ไม่บันทึกเป็นการส่งเอกสาร ·
    /// ต้องมีสิทธิ์สร้างเอกสารชนิดนั้น · คีย์ API ห้าม (ไม่มี "ผู้ใช้" ให้ส่งถึง)</summary>
    [HttpPost("{id:guid}/send-test")]
    [Accounting.Filters.RejectApiKey("ส่งอีเมลทดสอบของรายการประจำ")]
    public async Task<ActionResult<ApiResponse<RecurringSendTestResponse>>> SendTest(Guid companyId, Guid id)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var item = await _recurringService.GetByIdAsync(companyId, id);
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, item.DocumentType ?? DocumentType.Invoice))
            return Forbid403<RecurringSendTestResponse>("ส่งทดสอบได้เฉพาะผู้มีสิทธิ์สร้างเอกสารชนิดนี้ (Document.Create)");
        var result = await _autopilot.SendTestAsync(companyId, id, userId);
        return Ok(new ApiResponse<RecurringSendTestResponse>(true, result, "ส่งอีเมลทดสอบถึง " + result.To + " แล้ว"));
    }

    /// <summary>ตั้ง/ล้างนโยบายอนุมัติข้ามคำเตือนของรายการ — <b>ต้องมีสิทธิ์อนุมัติเอกสารชนิดนั้น</b> (ผู้ไม่มีสิทธิ์อนุมัติห้ามตั้งให้ระบบอนุมัติแทน)</summary>
    [HttpPut("{id:guid}/approve-policy")]
    [Accounting.Filters.RejectApiKey("ตั้งนโยบายอนุมัติอัตโนมัติของรายการประจำ")]   // ฝ่ายค้าน HIGH-1: คีย์ API ห้ามประทับว่าเจ้าของตั้งนโยบาย
    public async Task<ActionResult<ApiResponse<AutoApprovePolicyView?>>> SetApprovePolicy(Guid companyId, Guid id,
        [FromBody] SetAutoApprovePolicyRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var item = await _recurringService.GetByIdAsync(companyId, id);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, item.DocumentType ?? DocumentType.Invoice))
            return Forbid403<AutoApprovePolicyView?>("ตั้งนโยบายอนุมัติได้เฉพาะผู้มีสิทธิ์อนุมัติเอกสารชนิดนี้ (Document.Approve) — ขอผู้ดูแลบริษัทเพิ่มสิทธิ์ หรือให้ผู้มีสิทธิ์ตั้งแทน");
        var result = await _autopilot.SetRecurringPolicyAsync(companyId, id, request, userId);
        return Ok(new ApiResponse<AutoApprovePolicyView?>(true, result, request.Clear ? "ล้างนโยบายแล้ว — รายการนี้ไม่ปล่อยคำเตือนใด ๆ"
            : request.Inherit ? "รับรองแล้ว — รายการนี้ใช้นโยบายของผู้ติดต่อ (แก้ยอด/ผู้ติดต่อ/ธงอัตโนมัติภายหลัง ต้องรับรองใหม่)" : "บันทึกนโยบายอนุมัติแล้ว"));
    }

    /// <summary>ค่าตั้ง Autopilot ของผู้ติดต่อ (ช่องทางรับเงิน + นโยบาย)</summary>
    [HttpGet("contacts/{contactId:guid}/autopilot")]
    public async Task<ActionResult<ApiResponse<ContactAutopilotSettingsResponse>>> ContactAutopilot(Guid companyId, Guid contactId)
    {
        if (await DenyAutopilotReadAsync(companyId, DocumentType.Invoice) is { } deny) return Forbid403<ContactAutopilotSettingsResponse>(deny);
        return Ok(new ApiResponse<ContactAutopilotSettingsResponse>(true, await _autopilot.GetContactSettingsAsync(companyId, contactId)));
    }

    /// <summary>แก้ช่องทางรับเงินของผู้ติดต่อ — ต้องมีสิทธิ์แก้ผู้ติดต่อ</summary>
    [HttpPut("contacts/{contactId:guid}/autopilot")]
    [Accounting.Filters.RejectApiKey("ตั้งช่องทางรับเงินของผู้ติดต่อ")]   // ฝ่ายค้าน HIGH-1
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
    [Accounting.Filters.RejectApiKey("ตั้งนโยบายอนุมัติอัตโนมัติของผู้ติดต่อ")]   // ฝ่ายค้าน HIGH-1
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
