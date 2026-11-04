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
using Microsoft.EntityFrameworkCore;

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
        await PurchasePolicyAcceptance.EnforcePlatformForUserAsync(_db, legal, companyId, User,
            PurchasePolicyAcceptance.ChannelPlatformInvoice, documentId, accepted, versionShown,
            HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<BillingCenterSummary>>> Get(Guid companyId, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ดูศูนย์บิล") is { } deny) return deny;
        var summary = await _center.GetSummaryAsync(companyId, canPay: true, canManagePlan: IsOwnerOrAdmin(companyId), ct,
            Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var viewerId) ? viewerId : null);   // รอบ 236 B1
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

    /// <summary>รอบ 228 (PLATFORM_OMISE_AUDIT §5.7): ดาวน์โหลดใบเสร็จ/ใบกำกับภาษี/ใบลดหนี้ค่าบริการ (เช่น ค่าส่วนเสริมที่จ่ายล่วงหน้า) — เรนเดอร์สดจากเอกสารจริงใน
    /// tenant ผู้ให้บริการ · ต้องเป็นเอกสารที่บริษัทนี้เป็นผู้ซื้อ (<see cref="PlatformInvoiceQueries.PaidDocumentsForBuyer"/>) · ของบริษัทอื่น/ไม่มี = 404 (ข้อความเดียว)</summary>
    [HttpGet("receipts/{documentId:guid}/pdf")]
    public async Task<IActionResult> ReceiptPdf(Guid companyId, Guid documentId,
        [FromServices] IPdfGenerationService pdf, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ดาวน์โหลดใบเสร็จค่าบริการ") is { } deny) return deny;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (platformId == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        var mine = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            PlatformInvoiceQueries.PaidDocumentsForBuyer(_db, platformId.Value, companyId), d => d.Id == documentId, ct);
        if (!mine) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        var gen = await pdf.GenerateDocumentPdfAsync(platformId.Value,
            new Models.DTOs.DocumentTemplate.GeneratePdfRequest(documentId, null, null, null, null));
        Response.Headers["Cache-Control"] = "private, no-store";
        return File(gen.PdfData, "application/pdf", gen.FileName);
    }

    /// <summary>แจ้งชำระใบแจ้งหนี้ด้วยการโอน + แนบสลิปในคำขอเดียว (ยอด = ยอดค้างของใบ · ติ๊กยอมรับเงื่อนไขบังคับ)</summary>
    [HttpPost("invoices/{documentId:guid}/slip")]
    [RequestSizeLimit(SubscriptionSlipUpload.MaxBytes + 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> PayBySlip(Guid companyId, Guid documentId,
        IFormFile? file, [FromForm] DateTime? paymentDate, [FromForm] string? transferReference, [FromForm] string? fromBankName,
        [FromForm] string? customerNotes, [FromForm] bool policyAccepted, [FromForm] string? policyVersionShown,
        [FromServices] ILegalDocumentService legal, [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env,
        [FromServices] ISlipOcrAssistService slipOcr, [FromServices] ILogger<BillingCenterController> logger, CancellationToken ct,
        [FromForm] decimal? withholdingTaxAmount = null)
    {
        if (await RequireBillingAsync(companyId, "แจ้งชำระค่าบริการ") is { } deny) return deny;
        if (SubscriptionSlipUpload.Reject(file) is string bad)
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null!, bad));
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        await EnforcePolicyAsync(legal, companyId, documentId, policyAccepted, policyVersionShown, ct);
        // ฝ่ายค้าน N3 + รอบสุดท้าย 5: ตรวจว่าแจ้งได้ก่อน (ไม่เก็บไฟล์ที่จะถูกปฏิเสธ) → เก็บ/ตรวจไฟล์ → สร้างรายการ · สร้างไม่สำเร็จ ⇒ ลบไฟล์ที่เก็บ
        await _subs.EnsureInvoicePayableAsync(companyId, documentId);
        var stored = await SubscriptionSlipUpload.StoreAsync(file!, images, env);
        SubscriptionPaymentResponse payment;
        try
        {
            payment = await _subs.SubmitInvoicePaymentAsync(companyId, documentId,
                new SubmitInvoicePaymentRequest(paymentDate, transferReference, fromBankName, customerNotes, withholdingTaxAmount), actor);
        }
        catch { SubscriptionSlipUpload.DeleteStored(stored); throw; }
        var result = await SubscriptionSlipUpload.AttachAsync(stored, payment.Id, actor, _subs, slipOcr, logger);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result,
            $"แจ้งชำระ {result.PaymentNumber} แล้ว — ผู้ดูแลระบบจะตรวจสอบและยืนยันให้"));
    }

    /// <summary>ฝ่ายค้าน S9: สลิปของรายการชำระค่าบริการของบริษัทนี้ — สิทธิ์ Billing.Manage · รายการของบริษัทอื่น = 404 · ไม่เสิร์ฟเป็นไฟล์สาธารณะแล้ว</summary>
    [HttpGet("payments/{paymentId:guid}/slip")]
    public async Task<IActionResult> PaymentSlip(Guid companyId, Guid paymentId, [FromServices] IWebHostEnvironment env, [FromServices] Accounting.Services.Storage.IFileStorage storage, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ดูสลิป") is { } deny) return deny;
        var p = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            _db.SubscriptionPayments.Where(x => x.Id == paymentId && x.Subscription.CompanyId == companyId)
                .Select(x => new { x.SlipFileName, x.SlipOriginalFileName }), ct);
        if (p == null || SubscriptionSlipUpload.OpenStored(env, p.SlipFileName) is not { } f)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
        Response.Headers["Cache-Control"] = "private, no-store";
        return await SubscriptionSlipUpload.ServeAsync(storage, f, p.SlipOriginalFileName ?? p.SlipFileName)
            ?? NotFound(new ApiResponse<object>(false, null, "ไม่พบสลิป"));
    }

    /// <summary>ฝ่ายค้านรอบสุดท้าย 4a: ส่งหนังสือรับรอง 50 ทวิ ของรายการที่หักภาษี ณ ที่จ่าย (ชำระออนไลน์ไม่มีสลิปให้แนบ) — Billing.Manage · ไฟล์ไม่สาธารณะ</summary>
    [HttpPost("payments/{paymentId:guid}/wht-certificate")]
    [RequestSizeLimit(SubscriptionSlipUpload.MaxBytes + 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> WhtCertificate(Guid companyId, Guid paymentId, IFormFile? file,
        [FromServices] IImageProcessingService images, [FromServices] IWebHostEnvironment env)
    {
        if (await RequireBillingAsync(companyId, "ส่งหนังสือรับรองภาษีหัก ณ ที่จ่าย") is { } deny) return deny;
        if (SubscriptionSlipUpload.Reject(file) is string bad)
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null!, bad));
        var stored = await SubscriptionSlipUpload.StoreAsync(file!, images, env);
        try
        {
            var r = await _subs.AttachWhtCertificateAsync(companyId, paymentId, stored.FileName, JwtHelper.GetUserIdFromClaims(User).ToString());
            return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, r, "ส่งหนังสือรับรองแล้ว — ผู้ดูแลระบบจะตรวจสอบ"));
        }
        catch { SubscriptionSlipUpload.DeleteStored(stored); throw; }
    }

    public record PayerProfileRequest(string? BuyerType, string? Name, string? Address, string? TaxId, string? BranchCode);

    /// <summary>ฝ่ายค้าน 234 N2: การ์ด "ข้อมูลบริษัทสำหรับใบกำกับภาษี" — ประเภทผู้ซื้อมีผลจริง (ตัวตัดสินเดียว <c>PlatformPayerProfileGate.Normalize</c>) ·
    /// บันทึกผ่านเส้นแก้ข้อมูลบริษัทเดิม (<c>ICompanyService.UpdateAsync</c> — เจ้าของเท่านั้น · audit ชื่อ/เลข/สาขา/ที่อยู่) · บุคคลธรรมดาเว้นเลข = ล้างเลขเดิม</summary>
    [HttpPut("payer-profile")]
    public async Task<ActionResult<ApiResponse<object>>> SavePayerProfile(Guid companyId, [FromBody] PayerProfileRequest req,
        [FromServices] ICompanyService companies, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "แก้ข้อมูลบริษัทสำหรับใบกำกับภาษี") is { } deny) return deny;
        var (v, problem) = PlatformPayerProfileGate.Normalize(new PlatformPayerProfileGate.Input(req?.BuyerType, req?.Name, req?.Address, req?.TaxId, req?.BranchCode));
        if (problem != null) throw new BusinessRuleException(problem, PlatformPayerProfileGate.RuleCode, 400);
        await companies.UpdateAsync(companyId, JwtHelper.GetUserIdFromClaims(User), new Accounting.Models.DTOs.Company.UpdateCompanyRequest(
            Name: v!.Name, NameEn: null, TaxId: v.TaxId, BranchCode: v.BranchCode, BranchName: null, BusinessType: null, IndustryType: null, JuristicId: null,
            IsVatRegistered: null, VatRate: null, IsWhtRegistered: null, IsSocialSecurityRegistered: null, SocialSecurityAccountNo: null,
            Address: v.Address ?? "", BuildingNumber: null, BuildingName: null, Moo: null, StreetName: null, SubDistrict: null, District: null, Province: null,
            PostalCode: null, Phone: null, Fax: null, Email: null, Website: null, FiscalYearStartMonth: null, IsSetupComplete: null));
        var payer = await Accounting.Services.Platform.PlatformWithholdingReader.PayerAsync(_db, companyId, ct);
        return Ok(new ApiResponse<object>(true, payer, payer.Warning ?? "บันทึกข้อมูลบริษัทแล้ว"));
    }

    /// <summary>รอบ 234 (คำตัดสิน 234-3): ค่าตั้ง "หักภาษี ณ ที่จ่ายทุกครั้งที่ชำระค่าบริการ NextAcc" (ค่าตั้งต้นของช่องติ๊ก + ตัดบัตรอัตโนมัติ) ·
    /// เปิด (true) ได้เฉพาะนิติบุคคลที่ข้อมูลบริษัทครบ (<c>PlatformPayerProfileGate</c> — ไม่ครบ = 400 + สิ่งที่ขาด) · ปิดได้เสมอ · audit</summary>
    [HttpPut("withholding-preference")]
    public async Task<ActionResult<ApiResponse<object>>> SetWithholdingPreference(Guid companyId, [FromBody] WithholdingPreferenceRequest req, CancellationToken ct)
    {
        if (await RequireBillingAsync(companyId, "ตั้งค่าการหักภาษี ณ ที่จ่าย") is { } deny) return deny;
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        var payer = await Accounting.Services.Platform.PlatformWithholdingReader.PayerAsync(_db, companyId, ct);
        if (req.WithholdTax)
        {
            if (!payer.IsJuristic)
                throw new BusinessRuleException("หักภาษี ณ ที่จ่ายได้เฉพาะผู้ซื้อที่เป็นนิติบุคคล — กรอกเลขผู้เสียภาษีของบริษัท (ขึ้นต้นด้วย 0) ในการ์ดข้อมูลบริษัทก่อน",
                    PlatformPayerProfileGate.RuleCode, 400);
            if (PlatformPayerProfileGate.WithholdingRefusal(payer) is string problem)
                throw new BusinessRuleException(problem, PlatformPayerProfileGate.RuleCode, 400);
        }
        var before = sub.WithholdTaxPreference;
        sub.WithholdTaxPreference = req.WithholdTax;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        _db.AddChainedAuditLog(new Accounting.Models.Entities.AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Subscription", EntityId = sub.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { withholdTaxPreference = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { withholdTaxPreference = req.WithholdTax, ruleCode = PlatformPayerProfileGate.RuleCode,
                legalReference = "ท.ป.4/2528 · ม.40(2)" }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new { withholdTaxPreference = sub.WithholdTaxPreference },
            req.WithholdTax ? "ตั้งค่าแล้ว — หักภาษี ณ ที่จ่ายทุกครั้งที่ชำระค่าบริการ · ส่งหนังสือรับรอง 50 ทวิ ที่ประวัติการชำระ · ตัดบัตรอัตโนมัติจะตัดยอดสุทธิเมื่อยืนยันความยินยอมตัดบัตรฉบับปัจจุบันแล้ว (การ์ดบัตรที่บันทึกไว้)"
                : "ตั้งค่าแล้ว — ชำระค่าบริการเต็มจำนวน (ไม่หักภาษี ณ ที่จ่าย)"));
    }

    public record PrepareOnlineRequest(bool PolicyAccepted, string? PolicyVersionShown, decimal? WithholdingTaxAmount = null);

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
            new SubmitInvoicePaymentRequest(null, null, null, "ชำระออนไลน์", req.WithholdingTaxAmount), JwtHelper.GetUserIdFromClaims(User).ToString());
        await _db.SaveChangesAsync(ct);
        return Ok(new ApiResponse<object>(true, new
        {
            paymentId = payment.Id, amount = payment.Amount, methods = status.Methods,
            publicBase = $"/api/companies/{companyId}/platform-pay/subscription-payments/{payment.Id}",
        }));
    }
}
