using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Subscription;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Admin API สำหรับจัดการ Plan Templates และ Trial Config
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;

    public AdminController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    // ===== Plan Template Management =====

    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<PlanTemplateResponse>>> CreatePlanTemplate([FromBody] CreatePlanTemplateRequest request)
    {
        var result = await _subscriptionService.CreatePlanTemplateAsync(request);
        return Ok(new ApiResponse<PlanTemplateResponse>(true, result, "สร้าง plan template สำเร็จ"));
    }

    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<List<PlanTemplateResponse>>>> GetPlanTemplates([FromQuery] bool includeInactive = false)
    {
        var result = await _subscriptionService.GetPlanTemplatesAsync(includeInactive);
        return Ok(new ApiResponse<List<PlanTemplateResponse>>(true, result));
    }

    [HttpPut("plans/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<PlanTemplateResponse>>> UpdatePlanTemplate(Guid templateId, [FromBody] UpdatePlanTemplateRequest request)
    {
        var result = await _subscriptionService.UpdatePlanTemplateAsync(templateId, request);
        return Ok(new ApiResponse<PlanTemplateResponse>(true, result, "อัพเดท plan template สำเร็จ"));
    }

    // ===== Trial Config Management =====

    /// <summary>
    /// ดูสถานะ Trial ของบริษัท (Admin view)
    /// </summary>
    [HttpGet("companies/{companyId:guid}/trial")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> GetTrialStatus(Guid companyId)
    {
        var result = await _subscriptionService.GetTrialStatusAsync(companyId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result));
    }

    /// <summary>
    /// แก้ไข Trial Config ของบริษัทโดยตรง
    /// </summary>
    [HttpPut("companies/{companyId:guid}/trial")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> UpdateTrialConfig(Guid companyId, [FromBody] UpdateTrialConfigRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UpdateTrialConfigAsync(companyId, request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "อัพเดท trial config สำเร็จ"));
    }

    /// <summary>
    /// ขยายเวลา Trial (Admin)
    /// </summary>
    [HttpPost("companies/{companyId:guid}/trial/extend")]
    public async Task<ActionResult<ApiResponse<TrialStatusResponse>>> ExtendTrial(Guid companyId, [FromBody] ExtendTrialRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ExtendTrialAsync(companyId, request, userId);
        return Ok(new ApiResponse<TrialStatusResponse>(true, result, "ขยายเวลา trial สำเร็จ"));
    }

    /// <summary>
    /// บังคับ Expire Trial
    /// </summary>
    [HttpPost("companies/{companyId:guid}/trial/expire")]
    public async Task<ActionResult<ApiResponse<string>>> ExpireTrial(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _subscriptionService.ExpireTrialAsync(companyId, userId);
        return Ok(new ApiResponse<string>(true, null, "Expire trial สำเร็จ"));
    }

    /// <summary>
    /// Process expired trials (can be called by scheduler)
    /// </summary>
    [HttpPost("trial/process-expired")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessExpiredTrials()
    {
        await _subscriptionService.ProcessExpiredTrialsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผล expired trials สำเร็จ"));
    }

    // ===== Subscription Payment Review =====

    /// <summary>
    /// ดูรายการชำระเงินรอตรวจสอบทั้งหมด (Admin)
    /// </summary>
    [HttpGet("subscription-payments/pending")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentListResponse>>> GetPendingPayments()
    {
        var result = await _subscriptionService.GetAllPendingPaymentsAsync();
        return Ok(new ApiResponse<SubscriptionPaymentListResponse>(true, result));
    }

    /// <summary>
    /// ดูรายละเอียดการชำระเงิน (Admin)
    /// </summary>
    [HttpGet("subscription-payments/{paymentId:guid}")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> GetPaymentDetail(Guid paymentId)
    {
        var result = await _subscriptionService.GetPaymentAsync(paymentId);
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result));
    }

    /// <summary>
    /// ตรวจสอบและอนุมัติ/ปฏิเสธการชำระเงิน (Admin)
    /// เมื่ออนุมัติจะต่ออายุ Subscription อัตโนมัติ
    /// </summary>
    [HttpPost("subscription-payments/{paymentId:guid}/review")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentResponse>>> ReviewPayment(
        Guid paymentId, [FromBody] ReviewSubscriptionPaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.ReviewPaymentAsync(paymentId, request, userId);
        var message = request.Approve ? "อนุมัติการชำระเงินสำเร็จ ต่ออายุ Subscription แล้ว" : "ปฏิเสธการชำระเงินแล้ว";
        return Ok(new ApiResponse<SubscriptionPaymentResponse>(true, result, message));
    }

    /// <summary>
    /// ดูรายการชำระเงินทั้งหมดของบริษัท (Admin)
    /// </summary>
    [HttpGet("companies/{companyId:guid}/subscription-payments")]
    public async Task<ActionResult<ApiResponse<SubscriptionPaymentListResponse>>> GetCompanyPayments(Guid companyId)
    {
        var result = await _subscriptionService.GetPaymentsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionPaymentListResponse>(true, result));
    }

    // ===== Subscription Notification Settings (Admin) =====

    /// <summary>
    /// ดูการตั้งค่าแจ้งเตือน Subscription ของบริษัท (Admin)
    /// </summary>
    [HttpGet("companies/{companyId:guid}/subscription-notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> GetNotificationSettings(Guid companyId)
    {
        var result = await _subscriptionService.GetNotificationSettingsAsync(companyId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result));
    }

    /// <summary>
    /// แก้ไขการตั้งค่าแจ้งเตือน Subscription ของบริษัท (Admin)
    /// </summary>
    [HttpPut("companies/{companyId:guid}/subscription-notifications")]
    public async Task<ActionResult<ApiResponse<SubscriptionNotificationSettingsResponse>>> UpdateNotificationSettings(
        Guid companyId, [FromBody] UpdateSubscriptionNotificationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _subscriptionService.UpdateNotificationSettingsAsync(companyId, request, userId);
        return Ok(new ApiResponse<SubscriptionNotificationSettingsResponse>(true, result, "อัพเดทการตั้งค่าแจ้งเตือนสำเร็จ"));
    }

    // ===== Background Processing (Scheduler) =====

    /// <summary>
    /// ประมวลผลแจ้งเตือน Subscription (เรียกจาก scheduler)
    /// </summary>
    [HttpPost("subscription/process-notifications")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessSubscriptionNotifications()
    {
        await _subscriptionService.ProcessSubscriptionNotificationsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผลแจ้งเตือน subscription สำเร็จ"));
    }

    /// <summary>
    /// ประมวลผล Subscription หมดอายุ + ตัดบัญชี (เรียกจาก scheduler)
    /// </summary>
    [HttpPost("subscription/process-expired")]
    public async Task<ActionResult<ApiResponse<string>>> ProcessExpiredSubscriptions()
    {
        await _subscriptionService.ProcessExpiredSubscriptionsAsync();
        return Ok(new ApiResponse<string>(true, null, "ประมวลผล expired subscriptions สำเร็จ"));
    }
}
