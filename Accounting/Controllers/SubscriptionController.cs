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

    public SubscriptionController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
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
}
