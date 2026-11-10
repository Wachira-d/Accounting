using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Autopilot;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 252 WP-12A — หน้า Autopilot (AUTOPILOT §11.3–§11.4): ภาพรวม · เรื่องที่ระบบทำต่อไม่ได้ · ค่าตั้ง · ใบเสนอราคาแปลงอัตโนมัติ · คิวเงินโอนเข้าที่รอยืนยัน ·
/// ผลจำลองปิดหนี้อัตโนมัติ 30 วัน · ทุก endpoint ส่ง <c>companyId</c> ลง service (ทุก query กรองบริษัท) · ด่านสิทธิ์ทุก write (write_permission_gate_check)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/autopilot")]
[Authorize]
public class AutopilotController : ControllerBase
{
    private readonly IAutopilotService _autopilot;
    private readonly IBankReceiptSettleService _bank;
    private readonly IPermissionService _permissions;

    public AutopilotController(IAutopilotService autopilot, IBankReceiptSettleService bank, IPermissionService permissions)
    {
        _autopilot = autopilot;
        _bank = bank;
        _permissions = permissions;
    }

    private Guid UserId => JwtHelper.GetUserIdFromClaims(User);

    /// <summary>คืนผลปฏิเสธ (403 ข้อความไทย) เมื่อไม่มีคีย์ · null = ผ่าน</summary>
    private async Task<ActionResult?> DenyKeyAsync<T>(Guid companyId, string key, string what)
        => await _permissions.HasPermissionAsync(companyId, UserId, key)
            ? null
            : StatusCode(403, new ApiResponse<T>(false, default, $"ไม่มีสิทธิ์{what} — ต้องมีสิทธิ์ {key.Replace("perm:", "")}"));

    /// <summary>ภาพรวม (อ่านอย่างเดียว) — ต้องเห็นบัญชีของบริษัท (Accounting.View)</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<AutopilotOverviewResponse>>> Overview(Guid companyId, [FromQuery] int days = 30)
    {
        if (await DenyKeyAsync<AutopilotOverviewResponse>(companyId, PermissionKeys.AccountingView, "ดูหน้า Autopilot") is { } deny) return deny;
        return Ok(new ApiResponse<AutopilotOverviewResponse>(true, await _autopilot.OverviewAsync(companyId, days)));
    }

    /// <summary>ค่าตั้ง Autopilot ของบริษัท (ปิดหนี้อัตโนมัติ · ค่าตั้งต้นแปลงใบเสนอราคา · เวลาสรุป) — CompanySettings.Edit · ปิดหนี้อัตโนมัติต้องมี Bank.Reconcile ด้วย</summary>
    [HttpPut("settings")]
    [Accounting.Filters.RejectApiKey("ตั้งค่า Autopilot")]
    public async Task<ActionResult<ApiResponse<AutopilotSettingsDto>>> UpdateSettings(Guid companyId, [FromBody] UpdateAutopilotSettingsRequest request)
    {
        if (await DenyKeyAsync<AutopilotSettingsDto>(companyId, PermissionKeys.CompanySettingsEdit, "ตั้งค่า Autopilot") is { } deny) return deny;
        // รอบ 257 A-M7: ตรวจ Bank.Reconcile เฉพาะเมื่อธงเปลี่ยนจริง (ส่งค่าเดิมมา = ไม่ใช่การเปิด/ปิด) — ผู้ตั้งเวลาสรุป/ใบเสนอราคาไม่ติด 403
        // ค่าเดิม ⇒ ถอดออกจากคำขอ (ไม่เขียนทับ — กันคำขอค้างพลิกค่าที่ผู้มีสิทธิ์เพิ่งเปลี่ยน)
        if (request.BankAutoSettleEnabled != null)
        {
            if (!AutopilotBankSettleToggle.IsChange(request.BankAutoSettleEnabled, (await _autopilot.SettingsAsync(companyId)).BankAutoSettleEnabled))
                request = request with { BankAutoSettleEnabled = null };
            else if (await DenyKeyAsync<AutopilotSettingsDto>(companyId, PermissionKeys.BankReconcile, "เปิด/ปิดการปิดหนี้อัตโนมัติจากเงินโอน") is { } denyBank)
                return denyBank;
        }
        var result = await _autopilot.UpdateSettingsAsync(companyId, request, UserId);
        return Ok(new ApiResponse<AutopilotSettingsDto>(true, result, "บันทึกค่าตั้ง Autopilot แล้ว"));
    }

    /// <summary>ตั้งโหมดแปลงของใบเสนอราคาหนึ่งใบ — สิทธิ์สร้างเอกสารขาย (ระบบจะสร้างร่างแทนผู้ตั้ง)</summary>
    [HttpPut("quotations/{quotationId:guid}/mode")]
    public async Task<ActionResult<ApiResponse<QuotationAutopilotRowDto>>> SetQuotationMode(Guid companyId, Guid quotationId,
        [FromBody] SetQuotationAutopilotModeRequest request)
    {
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, UserId, DocumentType.Invoice))
            return StatusCode(403, new ApiResponse<QuotationAutopilotRowDto>(false, null, "ตั้งการแปลงอัตโนมัติได้เฉพาะผู้มีสิทธิ์สร้างใบแจ้งหนี้ (Document.Create)"));
        var row = await _autopilot.SetQuotationModeAsync(companyId, quotationId, request.Mode, UserId);
        return Ok(new ApiResponse<QuotationAutopilotRowDto>(true, row, "บันทึกแล้ว — " + row.EffectiveLabel));
    }

    /// <summary>ลองแปลงใบเสนอราคาที่หยุด/ล้มอีกครั้ง (รอบงานถัดไป ~5 นาที)</summary>
    [HttpPost("quotations/{quotationId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<object>>> RetryQuotation(Guid companyId, Guid quotationId)
    {
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, UserId, DocumentType.Invoice))
            return StatusCode(403, new ApiResponse<object>(false, null, "สั่งแปลงใหม่ได้เฉพาะผู้มีสิทธิ์สร้างใบแจ้งหนี้ (Document.Create)"));
        await _autopilot.RetryQuotationAsync(companyId, quotationId, UserId);
        return Ok(new ApiResponse<object>(true, null, "ตั้งให้ลองใหม่แล้ว — ระบบจะตัดสินอีกครั้งภายใน ~5 นาที"));
    }

    /// <summary>คนยืนยันข้อเสนอเงินโอนเข้า ⇒ บันทึกรับชำระ (ทางเดียวกับระบบ) — Bank.Reconcile</summary>
    [HttpPost("bank-queue/{lineId:guid}/confirm")]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmBank(Guid companyId, Guid lineId, [FromBody] BankAutoSettleConfirmRequest request)
    {
        if (await DenyKeyAsync<object>(companyId, PermissionKeys.BankReconcile, "ยืนยันเงินโอนเข้า") is { } deny) return deny;
        // ฝ่ายค้าน M4: ยืนยัน = บันทึกรับชำระของใบนั้น ⇒ ต้องมีสิทธิ์อนุมัติเอกสารชนิดนั้นด้วย (ระดับเดียวกับบันทึกรับชำระที่หน้าเอกสาร)
        var docType = await _bank.DocumentTypeAsync(companyId, request.DocumentId);
        if (docType == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, UserId, docType.Value))
            return StatusCode(403, new ApiResponse<object>(false, null, "ยืนยันเงินโอนเข้าได้เฉพาะผู้มีสิทธิ์อนุมัติเอกสารชนิดนี้ (Document.Approve)"));
        var msg = await _bank.ConfirmAsync(companyId, lineId, request.DocumentId, UserId);
        return Ok(new ApiResponse<object>(true, null, msg));
    }

    /// <summary>คนปฏิเสธข้อเสนอเงินโอนเข้า — Bank.Reconcile</summary>
    [HttpPost("bank-queue/{lineId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> RejectBank(Guid companyId, Guid lineId)
    {
        if (await DenyKeyAsync<object>(companyId, PermissionKeys.BankReconcile, "ปฏิเสธข้อเสนอเงินโอนเข้า") is { } deny) return deny;
        await _bank.RejectAsync(companyId, lineId, UserId);
        return Ok(new ApiResponse<object>(true, null, "ปฏิเสธข้อเสนอแล้ว — จับคู่เองได้ที่หน้ากระทบยอดธนาคาร"));
    }

    /// <summary>ผู้ติดต่อรายนี้ไม่ปิดหนี้อัตโนมัติจากเงินโอน (ยังเสนอให้คนยืนยัน) — Contact.Edit</summary>
    [HttpPut("contacts/{contactId:guid}/bank-auto-settle")]
    public async Task<ActionResult<ApiResponse<object>>> SetContactBankAutoSettle(Guid companyId, Guid contactId, [FromBody] SetContactBankAutoSettleRequest request)
    {
        if (await DenyKeyAsync<object>(companyId, PermissionKeys.ContactEdit, "ตั้งการปิดหนี้อัตโนมัติของผู้ติดต่อ") is { } deny) return deny;
        await _autopilot.SetContactBankAutoSettleAsync(companyId, contactId, request.Disabled, UserId);
        return Ok(new ApiResponse<object>(true, null, request.Disabled
            ? "ผู้ติดต่อนี้จะไม่ถูกปิดหนี้อัตโนมัติ — ระบบเสนอให้ยืนยันเท่านั้น"
            : "ผู้ติดต่อนี้กลับมาใช้การปิดหนี้อัตโนมัติตามค่าบริษัท"));
    }

    /// <summary>ผลจำลองปิดหนี้อัตโนมัติย้อนหลัง (อ่านอย่างเดียว · ไม่เขียนอะไร) — Bank.View</summary>
    [HttpGet("bank-simulate")]
    public async Task<ActionResult<ApiResponse<BankAutoSettleSimulationDto>>> SimulateBank(Guid companyId, [FromQuery] int days = 30)
    {
        if (await DenyKeyAsync<BankAutoSettleSimulationDto>(companyId, PermissionKeys.BankView, "ดูผลจำลองการปิดหนี้อัตโนมัติ") is { } deny) return deny;
        return Ok(new ApiResponse<BankAutoSettleSimulationDto>(true, await _bank.SimulateAsync(companyId, days)));
    }
}
