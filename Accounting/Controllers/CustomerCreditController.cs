using Accounting.Data;
using Accounting.Helpers;
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
}
