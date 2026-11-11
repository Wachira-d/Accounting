using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" (DECISIONS 258-1-x) — เงินที่รับจากลูกค้าแล้วแต่ยังไม่ผูกกับใบ: รายการ · จัดสรรให้ใบที่ออกทีหลัง · ยกเลิกการจัดสรร ·
/// แผนจัดการเงินเกินของหน้ารับชำระ
/// <para><b>สิทธิ์ (ระดับเดียวกับเส้นรับชำระของ <c>DocumentController</c>):</b> อ่าน = เห็นเอกสารฝั่งขาย · จัดสรร = อนุมัติ (Approve) ของชนิดใบปลายทาง
/// (ลง JE + ลดยอดค้าง เท่ากับบันทึกรับชำระ) · ยกเลิกการจัดสรร = ยกเลิก (Void) ของชนิดใบปลายทาง — เฝ้าโดย <c>tools/write_permission_gate_check.py</c></para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/customer-credits")]
[Authorize]
public class CustomerCreditController : ControllerBase
{
    private readonly ICustomerCreditService _credits;
    private readonly IPermissionService _permissions;
    private readonly AccountingDbContext _db;

    public CustomerCreditController(ICustomerCreditService credits, IPermissionService permissions, AccountingDbContext db)
    {
        _credits = credits;
        _permissions = permissions;
        _db = db;
    }

    private ActionResult<ApiResponse<T>> Forbid403<T>(string th) => StatusCode(403, new ApiResponse<T>(false, default, th));

    /// <summary>ผู้ใช้เห็นเอกสารฝั่งขายไหม (เงินรอจัดสรรเป็นข้อมูลลูกหนี้) — null = ผ่าน</summary>
    private async Task<string?> DenyViewAsync(Guid companyId)
        => (await DocumentPermissionHelper.VisibleDirectionsAsync(_permissions, companyId, JwtHelper.GetUserIdFromClaims(User))).Revenue
            ? null
            : "ไม่มีสิทธิ์ดูเงินรอจัดสรรของลูกค้า (ต้องเห็นเอกสารฝั่งขาย — Document.Revenue.View)";

    private Task<DocumentType?> DocumentTypeAsync(Guid companyId, Guid documentId)
        => _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync();

    [HttpGet]
    public async Task<ActionResult<ApiResponse<CustomerCreditListResponse>>> List(Guid companyId,
        [FromQuery] Guid? contactId = null, [FromQuery] bool includeResolved = false, CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<CustomerCreditListResponse>(deny);
        return Ok(new ApiResponse<CustomerCreditListResponse>(true, await _credits.ListAsync(companyId, contactId, includeResolved, ct)));
    }

    [HttpGet("{paymentId:guid}/targets")]
    public async Task<ActionResult<ApiResponse<List<CustomerCreditTargetRow>>>> Targets(Guid companyId, Guid paymentId, CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<List<CustomerCreditTargetRow>>(deny);
        return Ok(new ApiResponse<List<CustomerCreditTargetRow>>(true, await _credits.TargetsAsync(companyId, paymentId, ct)));
    }

    [HttpGet("for-document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<CustomerCreditForDocumentResponse>>> ForDocument(Guid companyId, Guid documentId,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<CustomerCreditForDocumentResponse>(deny);
        return Ok(new ApiResponse<CustomerCreditForDocumentResponse>(true, await _credits.ForDocumentAsync(companyId, documentId, ct)));
    }

    /// <summary>แผนจัดการเงินเกินของหน้ารับชำระ (อ่านอย่างเดียว) — เซิร์ฟเวอร์คิดเงินเกิน/เหตุผล/ใบค้างอื่น หน้าเว็บแสดงตาม</summary>
    [HttpGet("excess-plan")]
    public async Task<ActionResult<ApiResponse<PaymentExcessPlanResponse>>> ExcessPlan(Guid companyId,
        [FromQuery] Guid documentId, [FromQuery] decimal amount, [FromQuery] decimal fee = 0m, [FromQuery] bool hasAdjustments = false,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<PaymentExcessPlanResponse>(deny);
        return Ok(new ApiResponse<PaymentExcessPlanResponse>(true,
            await _credits.ExcessPlanAsync(companyId, documentId, amount, fee, hasAdjustments, ct)));
    }

    /// <summary>ฝ่ายค้านรอบ 258 M4: ยืนยันว่าเงินส่วนเกินก่อนรอบ 258 ยังค้างจริง (เปิดให้จัดสรร/นับเป็นเงินรอจัดสรร) — ระดับเดียวกับบันทึกรับชำระฝั่งขาย</summary>
    [HttpPost("{paymentId:guid}/legacy/confirm")]
    public async Task<ActionResult<ApiResponse<CustomerCreditRow>>> ConfirmLegacy(Guid companyId, Guid paymentId, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Invoice))
            return Forbid403<CustomerCreditRow>("ไม่มีสิทธิ์ยืนยันรายการเงินรอจัดสรร (ต้องการ Document.Approve หรือ Document.Revenue.Approve)");
        var row = await _credits.ConfirmLegacyAsync(companyId, paymentId, userId.ToString(), ct);
        return Ok(new ApiResponse<CustomerCreditRow>(true, row, $"ยืนยันแล้ว — {row.PaymentNumber} ใช้เป็นเงินรอจัดสรรได้"));
    }

    /// <summary>ฝ่ายค้านรอบ 258 M4: ปิดรายการเงินส่วนเกินก่อนรอบ 258 ที่ถูกจัดการในบัญชีไปแล้ว (เหตุผลบังคับ · ไม่ลงบัญชี) — ระดับ Void ฝั่งขาย</summary>
    [HttpPost("{paymentId:guid}/legacy/close")]
    public async Task<ActionResult<ApiResponse<CustomerCreditRow>>> CloseLegacy(Guid companyId, Guid paymentId,
        [FromBody] CloseLegacyCreditRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, DocumentType.Invoice))
            return Forbid403<CustomerCreditRow>("ไม่มีสิทธิ์ปิดรายการเงินรอจัดสรร (ต้องการ Document.Void หรือ Document.Revenue.Void)");
        var row = await _credits.CloseLegacyAsync(companyId, paymentId, request.Reason, userId.ToString(), ct);
        return Ok(new ApiResponse<CustomerCreditRow>(true, row, $"ปิดรายการ {row.PaymentNumber} แล้ว"));
    }

    /// <summary>จัดสรรเงินรอจัดสรรให้ใบขาย — ระดับเดียวกับบันทึกรับชำระ (Approve ของชนิดใบปลายทาง)</summary>
    [HttpPost("{paymentId:guid}/allocate")]
    public async Task<ActionResult<ApiResponse<CustomerCreditAllocationResult>>> Allocate(Guid companyId, Guid paymentId,
        [FromBody] AllocateCustomerCreditRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var type = await DocumentTypeAsync(companyId, request.DocumentId);
        if (type == null) return NotFound(new ApiResponse<CustomerCreditAllocationResult>(false, null, "ไม่พบเอกสารปลายทาง"));
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, type.Value))
            return Forbid403<CustomerCreditAllocationResult>(
                $"ไม่มีสิทธิ์จัดสรรเงินรอจัดสรรให้เอกสาร {type.Value} (ต้องการ Document.Approve หรือ Document.Revenue.Approve)");
        var result = await _credits.AllocateAsync(companyId, paymentId, request, userId.ToString(), ct);
        var message = result.Notices.Count == 0
            ? $"จัดสรรให้ {result.DocumentNumber} แล้ว"
            : $"จัดสรรให้ {result.DocumentNumber} แล้ว — " + string.Join(" · ", result.Notices);
        return Ok(new ApiResponse<CustomerCreditAllocationResult>(true, result, message));
    }

    /// <summary>ยกเลิกการจัดสรรทีหลัง — กลับ JE + คืนยอดค้าง ⇒ ระดับ Void ของชนิดใบปลายทาง</summary>
    [HttpPost("{paymentId:guid}/allocations/{allocationId:guid}/undo")]
    public async Task<ActionResult<ApiResponse<CustomerCreditAllocationResult>>> UndoAllocation(Guid companyId, Guid paymentId,
        Guid allocationId, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var type = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.Id == allocationId && a.PaymentId == paymentId && a.CompanyId == companyId)
            .Select(a => (DocumentType?)a.Document.DocumentType).FirstOrDefaultAsync(ct);
        if (type == null) return NotFound(new ApiResponse<CustomerCreditAllocationResult>(false, null, "ไม่พบการจัดสรร"));
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, type.Value))
            return Forbid403<CustomerCreditAllocationResult>(
                $"ไม่มีสิทธิ์ยกเลิกการจัดสรรของเอกสาร {type.Value} (ต้องการ Document.Void หรือ Document.Revenue.Void)");
        var result = await _credits.UndoAllocationAsync(companyId, paymentId, allocationId, userId.ToString(), ct);
        var message = result.Notices.Count == 0
            ? $"ยกเลิกการจัดสรรของ {result.DocumentNumber} แล้ว"
            : $"ยกเลิกการจัดสรรของ {result.DocumentNumber} แล้ว — " + string.Join(" · ", result.Notices);
        return Ok(new ApiResponse<CustomerCreditAllocationResult>(true, result, message));
    }

    // ───────── รอบ 259 P4/P5/P6 (DECISIONS 259-1-x) — สิทธิ์ระดับเดียวกับเส้นรับ/จ่ายชำระเดิม ─────────

    private ActionResult<ApiResponse<CustomerCreditMovementResult>> MovementOk(CustomerCreditMovementResult r, string head)
        => Ok(new ApiResponse<CustomerCreditMovementResult>(true, r, r.Notices.Count == 0 ? head : head + " — " + string.Join(" · ", r.Notices)));

    /// <summary>คืนเงินลูกค้า (เงินออกจากธนาคาร) — อนุมัติฝั่งขาย + กระทบยอดธนาคาร (Bank.Reconcile)</summary>
    [HttpPost("{paymentId:guid}/refund")]
    public async Task<ActionResult<ApiResponse<CustomerCreditMovementResult>>> Refund(Guid companyId, Guid paymentId,
        [FromBody] CustomerCreditRefundRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Invoice)
            || !await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BankReconcile))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์คืนเงินลูกค้า (ต้องการ Document.Revenue.Approve และ Bank.Reconcile)");
        var r = await _credits.RefundAsync(companyId, paymentId, request, userId.ToString(), ct);
        return MovementOk(r, $"คืนเงิน {r.Amount:N2} บาทจาก {r.PaymentNumber} แล้ว");
    }

    /// <summary>ตัดเศษคงเหลือ (≤ เพดานของบริษัท) — ระดับเดียวกับบันทึกรับชำระฝั่งขาย</summary>
    [HttpPost("{paymentId:guid}/write-off")]
    public async Task<ActionResult<ApiResponse<CustomerCreditMovementResult>>> WriteOff(Guid companyId, Guid paymentId,
        [FromBody] CustomerCreditWriteOffRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Invoice))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์ตัดเศษเงินรอจัดสรร (ต้องการ Document.Approve หรือ Document.Revenue.Approve)");
        var r = await _credits.WriteOffAsync(companyId, paymentId, request, userId.ToString(), ct);
        return MovementOk(r, $"ตัดเศษ {r.Amount:N2} บาทของ {r.PaymentNumber} แล้ว");
    }

    /// <summary>ย้ายเป็นเงินมัดจำ — ออกและอนุมัติใบมัดจำ ⇒ ต้องสร้าง + อนุมัติใบเสร็จรับเงินได้</summary>
    [HttpPost("{paymentId:guid}/to-deposit")]
    public async Task<ActionResult<ApiResponse<CustomerCreditMovementResult>>> ToDeposit(Guid companyId, Guid paymentId,
        [FromBody] CustomerCreditToDepositRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, DocumentType.Receipt)
            || !await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Receipt))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์ออกใบมัดจำ (ต้องการสร้างและอนุมัติเอกสารฝั่งขาย)");
        var r = await _credits.ToDepositAsync(companyId, paymentId, request, userId.ToString(), ct);
        return MovementOk(r, $"ออกใบมัดจำ {r.TargetDocumentNumber} ยอด {r.Amount:N2} บาทแล้ว");
    }

    /// <summary>ออกใบแจ้งหนี้ส่วนที่ขาด (ร่าง) — สิทธิ์สร้างเอกสารชนิดนั้น · จัดสรรเกิดตอนอนุมัติ (ต้องมีสิทธิ์อนุมัติอยู่แล้ว)</summary>
    [HttpPost("{paymentId:guid}/issue-invoice")]
    public async Task<ActionResult<ApiResponse<CustomerCreditMovementResult>>> IssueInvoice(Guid companyId, Guid paymentId,
        [FromBody] CustomerCreditIssueInvoiceRequest request, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var type = request.DocumentType == DocumentType.TaxInvoice ? DocumentType.TaxInvoice : DocumentType.Invoice;
        if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, type))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์สร้างใบแจ้งหนี้ (ต้องการ Document.Create หรือ Document.Revenue.Create)");
        var r = await _credits.IssueInvoiceAsync(companyId, paymentId, request, userId.ToString(), ct);
        return MovementOk(r, $"สร้างใบแจ้งหนี้ร่าง {r.TargetDocumentNumber} แล้ว");
    }

    /// <summary>ยกเลิกขาออก (คืนเงิน/ตัดเศษ/การจอง) — ระดับ Void ฝั่งขาย</summary>
    [HttpPost("{paymentId:guid}/movements/{movementId:guid}/undo")]
    public async Task<ActionResult<ApiResponse<CustomerCreditMovementResult>>> UndoMovement(Guid companyId, Guid paymentId, Guid movementId,
        [FromBody] UndoCustomerCreditMovementRequest? request = null, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, DocumentType.Invoice))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์ยกเลิกรายการเงินรอจัดสรร (ต้องการ Document.Void หรือ Document.Revenue.Void)");
        // ฝ่ายค้านรอบ 259 L1: ยกเลิกคืนเงิน = เงินกลับเข้าธนาคาร/กลับมาใช้ได้ ⇒ สิทธิ์ไม่ต่ำกว่าคืนเงิน (อนุมัติฝั่งขาย + Bank.Reconcile)
        var kind = await _db.PaymentCreditMovements.AsNoTracking()
            .Where(m => m.Id == movementId && m.PaymentId == paymentId && m.CompanyId == companyId).Select(m => m.Kind).FirstOrDefaultAsync(ct);
        if (kind == null) return NotFound(new ApiResponse<CustomerCreditMovementResult>(false, null, "ไม่พบรายการ"));
        if (kind == nameof(PaymentCreditMovementKind.Refund)
            && (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, DocumentType.Invoice)
                || !await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BankReconcile)))
            return Forbid403<CustomerCreditMovementResult>("ไม่มีสิทธิ์ยกเลิกการคืนเงิน (ต้องการสิทธิ์เดียวกับคืนเงิน: Document.Revenue.Approve และ Bank.Reconcile)");
        var r = await _credits.UndoMovementAsync(companyId, paymentId, movementId, request?.Reason, userId.ToString(), ct);
        return MovementOk(r, $"ยกเลิกรายการของ {r.PaymentNumber} แล้ว (คงเหลือรอจัดสรร {r.RemainingUnapplied:N2})");
    }

    /// <summary>ตั้งเพดานตัดเศษของบริษัท — สิทธิ์แก้การตั้งค่าบริษัท</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<decimal>>> UpdateSettings(Guid companyId, [FromBody] CustomerCreditSettingsRequest request,
        CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.CompanySettingsEdit))
            return Forbid403<decimal>("ไม่มีสิทธิ์แก้การตั้งค่าบริษัท (CompanySettings.Edit)");
        var cap = await _credits.SetWriteOffCapAsync(companyId, request.WriteOffCap, userId.ToString(), ct);
        return Ok(new ApiResponse<decimal>(true, cap, $"ตั้งเพดานตัดเศษเป็น {cap:N2} บาทแล้ว"));
    }

    /// <summary>รอบ 259 P5: ข้อเสนอของบรรทัดเงินเข้าที่ยังไม่จับคู่ (อ่านอย่างเดียว · ไม่ลงบัญชี) — เห็นฝั่งขาย + ดูบัญชีธนาคาร</summary>
    [HttpGet("bank-line/{lineId:guid}/suggestions")]
    public async Task<ActionResult<ApiResponse<BankLineCreditSuggestionResponse>>> BankLineSuggestions(Guid companyId, Guid lineId,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return Forbid403<BankLineCreditSuggestionResponse>(deny);
        if (!await _permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User), PermissionKeys.BankView))
            return Forbid403<BankLineCreditSuggestionResponse>("ไม่มีสิทธิ์ดูบัญชีธนาคาร (Bank.View)");
        return Ok(new ApiResponse<BankLineCreditSuggestionResponse>(true, await _credits.BankLineSuggestionsAsync(companyId, lineId, ct)));
    }

    /// <summary>รอบ 259 P6: ส่งออกรายการเงินรอจัดสรร (xlsx/csv · ตัวเขียนกลาง TabularExport) — ตัวเลขเดียวกับหน้า</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(Guid companyId, [FromQuery] string? format = "xlsx", [FromQuery] bool includeResolved = false,
        CancellationToken ct = default)
    {
        if (await DenyViewAsync(companyId) is { } deny) return StatusCode(403, new ApiResponse<object>(false, null, deny));
        if (!TabularExport.TryParseFormat(format, out var fmt))
            return BadRequest(new ApiResponse<object>(false, null, "รูปแบบไฟล์ต้องเป็น csv หรือ xlsx"));
        var list = await _credits.ListAsync(companyId, null, includeResolved, ct);
        var f = CustomerCreditExport.Render(fmt, list, ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date);
        return File(f.Content, f.ContentType, f.FileName);
    }
}
