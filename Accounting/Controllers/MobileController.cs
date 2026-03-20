using Accounting.Models.DTOs;
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
        => Ok(new ApiResponse<DeviceRegistrationResponse>(true, await _service.RegisterDeviceAsync(Guid.Empty, request)));

    [HttpDelete("devices/{deviceToken}")]
    public async Task<ActionResult<ApiResponse<bool>>> UnregisterDevice(string deviceToken)
    { await _service.UnregisterDeviceAsync(Guid.Empty, deviceToken); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpGet("companies/{companyId:guid}/dashboard")]
    public async Task<ActionResult<ApiResponse<MobileDashboardResponse>>> GetDashboard(Guid companyId)
        => Ok(new ApiResponse<MobileDashboardResponse>(true, await _service.GetMobileDashboardAsync(companyId)));

    [HttpGet("companies/{companyId:guid}/quick-actions")]
    public async Task<ActionResult<ApiResponse<MobileQuickActionsResponse>>> GetQuickActions(Guid companyId)
        => Ok(new ApiResponse<MobileQuickActionsResponse>(true, await _service.GetQuickActionsAsync(companyId)));

    [HttpPost("companies/{companyId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<MobileApprovalResponse>>> QuickApprove(Guid companyId, [FromQuery] Guid entityId, [FromQuery] string entityType, [FromQuery] string action)
        => Ok(new ApiResponse<MobileApprovalResponse>(true, await _service.QuickApproveAsync(companyId, entityId, entityType, action, Guid.Empty)));

    [HttpPost("companies/{companyId:guid}/sync")]
    public async Task<ActionResult<ApiResponse<SyncResponse>>> Sync(Guid companyId, [FromBody] SyncRequest request)
        => Ok(new ApiResponse<SyncResponse>(true, await _service.SyncAsync(companyId, Guid.Empty, request)));
}
