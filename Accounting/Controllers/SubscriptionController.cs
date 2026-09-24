using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IImageProcessingService _images;
    private readonly IWebHostEnvironment _env;
    private readonly ISaasBillingDocumentService _billing;
    private readonly ISlipOcrAssistService _slipOcr;
    private readonly Accounting.Data.AccountingDbContext _db;

    public SubscriptionController(ISubscriptionService subscriptionService, IImageProcessingService images,
        IWebHostEnvironment env, ISaasBillingDocumentService billing, ISlipOcrAssistService slipOcr,
        Accounting.Data.AccountingDbContext db)
    {
        _subscriptionService = subscriptionService;
        _images = images;
        _env = env;
        _billing = billing;
        _slipOcr = slipOcr;
        _db = db;
    }

    /// <summary>
    /// **ด่าน tenant ของรายการชำระค่าบริการ — คืน `null` = ผ่าน**
    ///
    /// <para>═══ ที่มา (บั๊กจริง · ผลตรวจ F-02) ═══ route ของคอนโทรลเลอร์นี้เป็น
    /// <c>api/[controller]</c> ซึ่ง<b>ไม่มี <c>{companyId}</c></b> ⇒
    /// <c>TenantAccessMiddleware</c> ที่คุมทั้งระบบ **ข้ามไปเลย** (มันหา companyId
    /// จาก route) และ service ก็ค้นด้วย <c>FindAsync(paymentId)</c> เปล่า ๆ ⇒
    /// สมาชิกบริษัทใดก็ได้ที่ถือ GUID ของ payment **อ่านรายการชำระค่าบริการของ
    /// บริษัทอื่น** (ยอด · เลขอ้างอิงการโอน · ชื่อผู้ชำระ · ไฟล์สลิป) และ
    /// **เขียนทับสลิป** ของรายการที่ยังเป็น Pending ได้</para>
    ///
    /// <para>นี่คือ defect class เดียวกับ PDPA DSR: entity ที่<b>ไม่มี
    /// <c>CompanyId</c> ของตัวเอง</b> (payment ผูกบริษัทผ่าน Subscription)
    /// คือจุดที่ global query filter ช่วยไม่ได้เลย — ต้องมีด่านสมาชิกคั่นเอง</para>
    ///
    /// <para>ข้อความปฏิเสธเป็น <b>404 เหมือนกันหมด</b> ทั้งกรณี "ไม่มี id นี้" และ
    /// "มีแต่ไม่ใช่ของคุณ" — กัน enumeration ข้ามบริษัท</para>
    /// </summary>
    /// <summary>ด่านสมาชิกของบริษัทที่ **ส่งมาใน body** (route ไม่มี companyId
    /// ⇒ TenantAccessMiddleware ไม่ได้ตรวจให้)</summary>
    private async Task<ActionResult?> DenyForeignCompanyAsync(Guid companyId)
    {
        if (User.IsInRole("SystemAdmin")) return null;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isMember = await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        return isMember ? null : NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));
    }

    /// <summary>**ด่านเจ้าของของการเงิน subscription** (ฝ่ายค้านรอบ 193 W-C3) — ยกเลิก/เปลี่ยนแพ็กเกจ/แปลง trial/ขยาย trial
    /// เดิมมีแค่ <c>[Authorize]</c> ⇒ พนักงานบทบาท "ดูอย่างเดียว" หรือคีย์ใดก็ได้ยกเลิก subscription ของบริษัทได้ ·
    /// คีย์ถูกปฏิเสธด้วย <c>OwnerActionGuard</c> ตัวเดียว · ผู้ดูแลแพลตฟอร์มผ่าน · ปฏิเสธเป็น 403 + ข้อความไทย (ไม่ใช่ 401 ที่พาไปหน้า login)</summary>
    private async Task<ActionResult?> RequireOwnerAsync(Guid companyId, string verb)
    {
        if (OwnerActionGuard.DenyResult(HttpContext, verb) is { } key) return key;
        if (User.IsInRole("SystemAdmin")) return null;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var role = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (Models.Enums.UserRole?)cu.Role)
            .FirstOrDefaultAsync();
        if (role is Models.Enums.UserRole.Owner or Models.Enums.UserRole.SystemAdmin) return null;
        return StatusCode(403, new ApiResponse<object>(false, new { requiredRole = "Owner" },
            $"ไม่มีสิทธิ์{verb} — เรื่องแพ็กเกจและการชำระค่าบริการทำได้เฉพาะเจ้าของบริษัท กรุณาติดต่อเจ้าของบริษัท"));
    }

    private async Task<ActionResult?> DenyForeignPaymentAsync(Guid paymentId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var ownerCompanyId = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => (Guid?)p.Subscription.CompanyId)
            .FirstOrDefaultAsync();
        if (ownerCompanyId == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงิน"));

        // SystemAdmin ดูได้ทุกบริษัท (หน้าตรวจสลิปฝั่งแพลตฟอร์ม)
        if (User.IsInRole("SystemAdmin")) return null;

        var isMember = await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == ownerCompanyId.Value && cu.UserId == userId);
        return isMember
            ? null
            : NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงิน"));
    }

    // ===== Trial =====

    /// <summary>
    /// เริ่มทดลองใช้งาน
    /// </summary>
    [HttpPost("trial/start")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> StartTrial([FromBody] StartTrialRequest request)
    {
        // request.CompanyId มาจาก body — ไม่มีอะไรตรวจให้ (F-02 คลาสเดียวกัน)
        var denyTrial = await DenyForeignCompanyAsync(request.CompanyId);
        if (denyTrial != null) return denyTrial;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.StartTrialAsync(request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "เริ่มทดลองใช้งานสำเร็จ"));
    }

    /// <summary>
    /// ดูสถานะ Trial
    /// </summary>
    [HttpGet("{companyId:guid}/trial")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> GetTrialStatus(Guid companyId)
    {
        var result = await _subscriptionService.GetTrialStatusAsync(companyId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result));
    }

    /// <summary>
    /// ขยายเวลา Trial
    /// </summary>
    [HttpPost("{companyId:guid}/trial/extend")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> ExtendTrial(Guid companyId, [FromBody] ExtendTrialRequest request)
    {
        if (await RequireOwnerAsync(companyId, "ขยายเวลาทดลองใช้") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ExtendTrialAsync(companyId, request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "ขยายเวลาทดลองใช้สำเร็จ"));
    }

    // ===== Subscription =====

    /// <summary>
    /// ดูสถานะ Subscription
    /// </summary>
    [HttpGet("{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionResponse>>> GetSubscription(Guid companyId)
    {
        var result = await _subscriptionService.GetSubscriptionAsync(companyId);
        return Ok(new ApiResponse<SubscriptionResponse>(true, result));
    }

    /// <summary>
    /// แปลง Trial → Paid
    /// </summary>
    [HttpPost("{companyId:guid}/convert")]
    public async Task<ActionResult<ApiResponse<SubscriptionResponse>>> ConvertTrial(Guid companyId, [FromBody] ConvertTrialRequest request)
    {
        if (await RequireOwnerAsync(companyId, "อัปเกรดแพ็กเกจ") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ConvertTrialAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionResponse>(true, result, "อัพเกรดสำเร็จ"));
    }

    /// <summary>
    /// เปลี่ยน Plan
    /// </summary>
    [HttpPut("{companyId:guid}/plan")]
    public async Task<ActionResult<ApiResponse<SubscriptionResponse>>> ChangePlan(Guid companyId, [FromBody] ChangeSubscriptionRequest request)
    {
        if (await RequireOwnerAsync(companyId, "เปลี่ยนแพ็กเกจ") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ChangeSubscriptionAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionResponse>(true, result, "เปลี่ยน plan สำเร็จ"));
    }

    /// <summary>
    /// ยกเลิก Subscription
    /// </summary>
    [HttpPost("{companyId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<string>>> Cancel(Guid companyId)
    {
        if (await RequireOwnerAsync(companyId, "ยกเลิก subscription") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _subscriptionService.CancelSubscriptionAsync(companyId, userId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิก subscription สำเร็จ"));
    }

    // ===== Plan Templates (Public) =====

    /// <summary>
    /// ดู Plan ที่เปิดให้ใช้งาน
    /// </summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<PlanTemplateResponse>>>> GetPlans()
    {
        var result = await _subscriptionService.GetPlanTemplatesAsync(false);
        return Ok(new ApiResponse<List<PlanTemplateResponse>>(true, result));
    }

    /// <summary>แคตตาล็อกฟีเจอร์ (ป้าย/หมวด/ชุดสำเร็จรูป) — สาธารณะเพราะหน้าแรกใช้วาดตาราง
    /// เปรียบเทียบแพ็กเกจ; ค่า "แพ็กเกจไหนเปิดอะไร" มาจาก `plans` ไม่ใช่ที่นี่</summary>
    [HttpGet("feature-catalog")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<FeatureCatalogResponse>> GetFeatureCatalog()
        => Ok(new ApiResponse<FeatureCatalogResponse>(true, FeatureFlagsHelper.Catalog()));

    // ===== Notification Settings =====

    /// <summary>
    /// ดูการตั้งค่าแจ้งเตือน Subscription
    /// </summary>
    [HttpGet("{companyId:guid}/notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> GetNotificationSettings(Guid companyId)
    {
        var result = await _subscriptionService.GetNotificationSettingsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result));
    }

    /// <summary>
    /// ตั้งค่าแจ้งเตือน Subscription (กำหนดวันแจ้งเตือนก่อนหมดอายุ, หลังหมดอายุ, ก่อนตัดบัญชี)
    /// </summary>
    [HttpPut("{companyId:guid}/notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> UpdateNotificationSettings(
        Guid companyId, [FromBody] UpdateSubscriptionNotificationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UpdateNotificationSettingsAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result, "อัพเดทการตั้งค่าแจ้งเตือนสำเร็จ"));
    }

    // ===== Payment (โอนเงิน + อัพโหลดสลิป) =====

    /// <summary>
    /// ส่งข้อมูลการชำระเงิน (โอนเงิน)
    /// </summary>
    [HttpPost("{companyId:guid}/payments")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> SubmitPayment(
        Guid companyId, [FromBody] SubmitSubscriptionPaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.SubmitPaymentAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result, "ส่งข้อมูลการชำระเงินสำเร็จ รอการตรวจสอบ"));
    }

    /// <summary>
    /// อัพโหลดสลิปการโอนเงิน
    /// </summary>
    [HttpPost("payments/{paymentId:guid}/slip")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB max
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> UploadSlip(Guid paymentId, IFormFile file)
    {
        var deny = await DenyForeignPaymentAsync(paymentId); if (deny != null) return deny;
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null!, "กรุณาอัพโหลดไฟล์สลิป"));

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType.ToLower()))
            return BadRequest(new ApiResponse<SubscriptionPaymentResponse>(false, null!, "รองรับเฉพาะไฟล์ JPG, PNG, WebP, PDF"));

        // Compress + downsize slips. PDF passes through (we can't compress PDF here).
        var dir = Path.Combine(_env.WebRootPath, "uploads", "slips");
        await using var s = file.OpenReadStream();
        var processed = await _images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, dir, "/uploads/slips", ImageProfile.Slip);
        var fileName = Path.GetFileName(processed.AbsolutePath);
        var storagePath = Path.Combine("uploads", "slips", fileName);
        var finalSize = processed.FinalBytes > 0 ? processed.FinalBytes : file.Length;

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UploadPaymentSlipAsync(
            paymentId, fileName, file.FileName, file.ContentType, finalSize, storagePath, userId);

        // WP-C3: อ่านสลิป (local OCR) เทียบยอด → ช่วย admin review. best-effort ไม่ block.
        // ใช้ dir + fileName (path จริงบน filesystem ที่เพิ่งเซฟ) ตรง ๆ กันความกำกวม
        try
        {
            var savedPath = Path.Combine(dir, fileName);
            if (System.IO.File.Exists(savedPath))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(savedPath);
                await _slipOcr.ParseAndStoreAsync(paymentId, bytes, file.ContentType);
            }
        }
        catch { /* advisory only */ }

        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result, "อัพโหลดสลิปสำเร็จ"));
    }

    /// <summary>
    /// ดูรายการชำระเงินทั้งหมดของบริษัท
    /// </summary>
    [HttpGet("{companyId:guid}/payments")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentListResponse>>> GetPayments(Guid companyId)
    {
        var result = await _subscriptionService.GetPaymentsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionPaymentListResponse>(true, result));
    }

    /// <summary>
    /// ดูรายละเอียดการชำระเงิน
    /// </summary>
    [HttpGet("payments/{paymentId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> GetPayment(Guid paymentId)
    {
        var deny = await DenyForeignPaymentAsync(paymentId); if (deny != null) return deny;
        var result = await _subscriptionService.GetPaymentAsync(paymentId);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result));
    }

    /// <summary>WP-B3: ลูกค้าดาวน์โหลดใบเสร็จ/ใบกำกับค่าบริการของตัวเอง.
    /// ตรวจ tenant: payment ต้องอยู่ใต้ subscription ของ companyId ที่ระบุ.</summary>
    [HttpGet("{companyId:guid}/payments/{paymentId:guid}/receipt")]
    public async Task<IActionResult> DownloadOwnReceipt(Guid companyId, Guid paymentId)
    {
        var list = await _subscriptionService.GetPaymentsAsync(companyId);
        if (list.Payments.All(p => p.Id != paymentId))
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการชำระเงินของบริษัทนี้"));

        var pdf = await _billing.GetReceiptPdfAsync(paymentId);
        if (pdf == null)
            return NotFound(new ApiResponse<object>(false, null, "ยังไม่มีใบเสร็จสำหรับรายการนี้"));
        return File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
    }

    /// <summary>WP-B1: ลูกค้าดาวน์โหลดใบแจ้งหนี้ต่ออายุของบริษัทตัวเอง.</summary>
    [HttpGet("{companyId:guid}/renewal-invoice")]
    public async Task<IActionResult> DownloadRenewalInvoice(Guid companyId)
    {
        var sub = await _subscriptionService.GetSubscriptionAsync(companyId);
        var pdf = await _billing.GetRenewalInvoicePdfAsync(sub.Id);
        if (pdf == null)
            return NotFound(new ApiResponse<object>(false, null, "ยังไม่มีใบแจ้งหนี้ต่ออายุ"));
        return File(pdf.Value.Bytes, "application/pdf", pdf.Value.FileName);
    }

    // ===== Usage Monitor =====

    /// <summary>
    /// ดูรายละเอียดการใช้งาน (Usage Monitor)
    /// </summary>
    [HttpGet("{companyId:guid}/usage/detail")]
    public async Task<ActionResult<ApiResponse<UsageDetailResponse>>> GetUsageDetail(Guid companyId)
    {
        var result = await _subscriptionService.GetUsageDetailAsync(companyId);
        return Ok(new ApiResponse<UsageDetailResponse>(true, result));
    }

    // ===== Feature Check =====

    /// <summary>
    /// ตรวจสอบว่ามีสิทธิ์ใช้ feature หรือไม่
    /// </summary>
    [HttpGet("{companyId:guid}/features/{feature}")]
    public async Task<ActionResult<ApiResponse<bool>>> CheckFeature(Guid companyId, string feature)
    {
        if (!Enum.TryParse<Models.Enums.FeatureFlags>(feature, true, out var featureFlag))
            return BadRequest(new ApiResponse<bool>(false, false, "ไม่รู้จัก feature นี้"));

        var result = await _subscriptionService.CheckFeatureAccessAsync(companyId, featureFlag);
        return Ok(new ApiResponse<bool>(true, result));
    }

    // ===== Usage Tracking =====

    /// <summary>
    /// ตรวจสอบว่ายังใช้งานได้ตาม limit หรือไม่
    /// </summary>
    [HttpGet("{companyId:guid}/usage/{limitType}/check")]
    public async Task<ActionResult<ApiResponse<bool>>> CheckUsageLimit(Guid companyId, string limitType)
    {
        var result = await _subscriptionService.CheckUsageLimitAsync(companyId, limitType);
        return Ok(new ApiResponse<bool>(true, result));
    }

    /// <summary>
    /// เพิ่มจำนวนการใช้งาน
    /// </summary>
    [HttpPost("{companyId:guid}/usage/{usageType}/increment")]
    public async Task<ActionResult<ApiResponse<string>>> IncrementUsage(Guid companyId, string usageType)
    {
        await _subscriptionService.IncrementUsageAsync(companyId, usageType);
        return Ok(new ApiResponse<string>(true, null, "บันทึกการใช้งานสำเร็จ"));
    }
}
