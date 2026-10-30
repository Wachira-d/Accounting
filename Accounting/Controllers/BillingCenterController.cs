using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §3.2 · คำตัดสินข้อ 180) — <b>ศูนย์บิลของผู้เช่า</b> (หน้า <c>/pages/billing.html</c>)
/// <para>route อยู่ใต้ <c>api/subscription/{companyId}</c> โดยตั้งใจ: (1) <c>TenantAccessMiddleware</c> ตรวจสมาชิกจาก route <c>{companyId}</c> ·
/// (2) <c>SubscriptionCheckMiddleware</c> ยกเว้น <c>/api/subscription</c> ⇒ บริษัทที่ถูกระงับเพราะค้างชำระ<b>ยังเข้ามาจ่ายได้</b> (ทางไปต่อของผู้ถูกระงับ)</para>
/// <para>สิทธิ์: ดู/จ่าย = <c>Billing.Manage</c> (เจ้าของผ่านอัตโนมัติ) · ยอด/สถานะ/ป้าย/จ่ายได้ไหม คำนวณที่เซิร์ฟเวอร์ทั้งหมด</para>
/// </summary>
[ApiController]
[Route("api/subscription/{companyId:guid}/billing-center")]
[Authorize]
public class BillingCenterController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IPlatformBillingCenterService _center;
    private readonly ISubscriptionService _subs;

    public BillingCenterController(AccountingDbContext db, IPermissionService permissions,
        IPlatformBillingCenterService center, ISubscriptionService subs)
    { _db = db; _permissions = permissions; _center = center; _subs = subs; }

    /// <summary>ด่านสิทธิ์ของทุก action — ปฏิเสธเป็น 403 + ข้อความไทยบอกสิทธิ์ที่ต้องขอ (ทางไปต่อ)</summary>
    private async Task<ActionResult?> RequireBillingAsync(Guid companyId, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, PermissionKeys.BillingManage)) return null;
        return StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = PermissionKeys.BillingManage.Replace("perm:", "") },
            $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \"จัดการค่าใช้จ่าย/ส่วนเสริม\" จากเจ้าของบริษัทก่อน"));
    }

    private bool IsOwnerOrAdmin(Guid companyId)
    {
        if (User.IsInRole("SystemAdmin")) return true;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return _db.CompanyUsers.Any(cu => cu.CompanyId == companyId && cu.UserId == userId && cu.Role == UserRole.Owner);
    }

    /// <summary>ด่านติ๊กยอมรับนโยบาย (part B — ตัวเดียวของทุกหน้าชำระค่าบริการ · ช่อง platform-invoice · ต้นทาง = ใบแจ้งหนี้) — ไม่ผ่าน ⇒ BusinessRuleException ·
    /// ผ่าน ⇒ แถวหลักฐานอยู่ใน context (บันทึกพร้อมรายการชำระ)</summary>
    private async Task EnforcePolicyAsync(ILegalDocumentService legal, Guid companyId, Guid documentId, bool accepted, string? versionShown, CancellationToken ct)
    {
        await PurchasePolicyAcceptance.EnforcePlatformAsync(_db, legal, companyId, JwtHelper.GetUserIdFromClaims(User),
            PurchasePolicyAcceptance.ChannelPlatformInvoice, documentId, accepted, versionShown,
            HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<BillingCenterSummary>>> Get(Guid companyId, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ดูศูนย์บิล") is { } deny) return deny;
        var summary = await _center.GetSummaryAsync(companyId, canPay: true, canManagePlan: IsOwnerOrAdmin(companyId), ct);
        return Ok(new ApiResponse<BillingCenterSummary>(true, summary));
    }

    /// <summary>ดาวน์โหลดใบแจ้งหนี้ค่าบริการ — เรนเดอร์สดจากเอกสารจริงใน tenant ผู้ให้บริการ (ตัวเรนเดอร์เดิมของเอกสาร) · ใบที่ไม่ใช่ของบริษัทนี้ = 404</summary>
    [HttpGet("invoices/{documentId:guid}/pdf")]
    public async Task<IActionResult> InvoicePdf(Guid companyId, Guid documentId,
        [FromServices] IPdfGenerationService pdf, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ดาวน์โหลดใบแจ้งหนี้") is { } deny) return deny;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (platformId == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบใบแจ้งหนี้"));
        var mine = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            PlatformInvoiceQueries.ForBuyer(_db, platformId.Value, companyId), d => d.Id == documentId, ct);
        if (!mine) return NotFound(new ApiResponse<object>(false, null, "ไม่พบใบแจ้งหนี้"));
        var gen = await pdf.GenerateDocumentPdfAsync(platformId.Value,
            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(documentId, null, null, null, null));
        return File(gen.PdfData, "application/pdf", gen.FileName);
    }

    /// <summary>แจ้งชำระใบแจ้งหนี้ด้วยการโอน + แนบสลิปในคำขอเดียว (ยอด = ยอดค้างของใบ · ติ๊กยอมรับเงื่อนไขบังคับ)</summary>
    [HttpPost("invoices/{documentId:guid}/slip")]
    [RequestSizeLimit(SubscriptionSlipUpload.MaxBytes + 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> PayBySlip(Guid companyId, Guid documentId,
        IFormFile? file, [FromForm] DateTime? paymentDate, [FromForm] string? transferReference, [FromForm] string? fromBankName,
        [FromForm] string? customerNotes, [FromForm] bool policyAccepted, [FromForm] string? policyVersionShown,
        [FromServices] ILegalDocumentService legal, [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env,
        [FromServices] ISlipOcrAssistService slipOcr, [FromServices] ILogger<BillingCenterController> logger, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "แจ้งชำระค่าบริการ") is { } deny) return deny;
        if (SubscriptionSlipUpload.Reject(file) is string bad)
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null!, bad));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        await EnforcePolicyAsync(legal, companyId, documentId, policyAccepted, policyVersionShown, ct);
        var payment = await _subs.SubmitInvoicePaymentAsync(companyId, documentId,
            new SubmitInvoicePaymentRequest(paymentDate, transferReference, fromBankName, customerNotes), actor);
        var result = await SubscriptionSlipUpload.SaveAsync(file!, payment.Id, actor, images, env, _subs, slipOcr, logger);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result,
            $"แจ้งชำระ {result.PaymentNumber} แล้ว — ผู้ดูแลระบบจะตรวจสอบและยืนยันให้"));
    }

    public record PrepareOnlineRequest(bool PolicyAccepted, string? PolicyVersionShown);

    /// <summary>เตรียมชำระออนไลน์ผ่านช่องทางของ NextAcc (part B) — สร้าง/ใช้รายการชำระที่ผูกใบ (ยอด = ยอดค้างของใบ) แล้วคืนเส้นทางของ part B
    /// (<c>api/companies/{cid}/platform-pay/subscription-payments/{paymentId}</c>) ให้ <c>PayWidget</c> เริ่ม intent ใต้บริษัทผู้ให้บริการ ·
    /// เงินเข้า ⇒ <c>SubscriptionPaymentHandler</c> อนุมัติ ⇒ ตัดชำระใบเดิม · ช่องทางยังไม่เปิด ⇒ 409 + วิธีโอน (ทางไปต่อ = แนบสลิป)</summary>
    [HttpPost("invoices/{documentId:guid}/online-payment")]
    public async Task<ActionResult<ApiResponse<object>>> PrepareOnline(Guid companyId, Guid documentId, [FromBody] PrepareOnlineRequest req,
        [FromServices] Accounting.Services.Payments.IPlatformPaymentService platform, [FromServices] ILegalDocumentService legal, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ชำระค่าบริการ") is { } deny) return deny;
        var status = await platform.StatusAsync(ct);
        if (!status.Online)
            return StatusCode(409, new ApiResponse<object>(false, await PlatformPayInfo.BuildAsync(_db, platform, legal, ct),
                status.OfflineReason ?? "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป"));
        await EnforcePolicyAsync(legal, companyId, documentId, req.PolicyAccepted, req.PolicyVersionShown, ct);
        var payment = await _subs.SubmitInvoicePaymentAsync(companyId, documentId,
            new SubmitInvoicePaymentRequest(null, null, null, "ชำระออนไลน์"), JwtHelper.GetUserIdFromClaims(User).ToString());
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            paymentId = payment.Id, amount = payment.Amount, methods = status.Methods,
            publicBase = $"/api/companies/{companyId}/platform-pay/subscription-payments/{payment.Id}",
        }));
    }
}
