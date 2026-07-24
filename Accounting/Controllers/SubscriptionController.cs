using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public SubscriptionController(ISubscriptionService subscriptionService, IImageProcessingService images,
        IWebHostEnvironment env, ISaasBillingDocumentService billing)
    {
        _subscriptionService = subscriptionService;
        _images = images;
        _env = env;
        _billing = billing;
    }

    // ===== Trial =====

    /// <summary>
    /// เริ่มทดลองใช้งาน
    /// </summary>
    [HttpPost("trial/start")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> StartTrial([FromBody] StartTrialRequest request)
    {
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
