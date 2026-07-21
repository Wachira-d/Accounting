using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Mobile;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/mobile")]
[Authorize]
public class MobileController : ControllerBase
{
    private readonly IMobileApiService _service;
    public MobileController(IMobileApiService service) => _service = service;

    [HttpPost("devices")]
    public async Task<ActionResult<ApiResponse<DeviceRegistrationResponse>>> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return StatusCode(201, new ApiResponse<DeviceRegistrationResponse>(true, await _service.RegisterDeviceAsync(userId, request)));
    }

    [HttpDelete("devices/{deviceToken}")]
    public async Task<ActionResult<ApiResponse<bool>>> UnregisterDevice(string deviceToken)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _service.UnregisterDeviceAsync(userId, deviceToken);
        return NoContent();
    }

    [HttpGet("companies/{companyId:guid}/dashboard")]
    public async Task<ActionResult<ApiResponse<MobileDashboardResponse>>> GetDashboard(Guid companyId)
        => Ok(new ApiResponse<MobileDashboardResponse>(true, await _service.GetMobileDashboardAsync(companyId)));

    [HttpGet("companies/{companyId:guid}/quick-actions")]
    public async Task<ActionResult<ApiResponse<MobileQuickActionsResponse>>> GetQuickActions(Guid companyId)
        => Ok(new ApiResponse<MobileQuickActionsResponse>(true, await _service.GetQuickActionsAsync(companyId)));

    [HttpPost("companies/{companyId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<MobileApprovalResponse>>> QuickApprove(Guid companyId, [FromQuery] Guid entityId, [FromQuery] string entityType, [FromQuery] string action)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<MobileApprovalResponse>(true, await _service.QuickApproveAsync(companyId, entityId, entityType, action, userId)));
    }

    [HttpPost("companies/{companyId:guid}/sync")]
    public async Task<ActionResult<ApiResponse<SyncResponse>>> Sync(Guid companyId, [FromBody] SyncRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<SyncResponse>(true, await _service.SyncAsync(companyId, userId, request)));
    }
}
