using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<NotificationResponse>>>> GetNotifications([FromQuery] int limit = 50)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _notificationService.GetUserNotificationsAsync(userId, limit);
        return Ok(new ApiResponse<List<NotificationResponse>>(true, result));
    }

    [HttpGet("count")]
    public async Task<ActionResult<ApiResponse<NotificationCountResponse>>> GetCount()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _notificationService.GetCountAsync(userId);
        return Ok(new ApiResponse<NotificationCountResponse>(true, result));
    }

    [HttpPost("mark-read")]
    public async Task<ActionResult<ApiResponse<string>>> MarkAsRead([FromBody] MarkReadRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _notificationService.MarkAsReadAsync(userId, request.NotificationIds);
        return Ok(new ApiResponse<string>(true, null, "อ่านแล้ว"));
    }

    [HttpPost("mark-all-read")]
    public async Task<ActionResult<ApiResponse<string>>> MarkAllAsRead()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _notificationService.MarkAllAsReadAsync(userId);
        return Ok(new ApiResponse<string>(true, null, "อ่านทั้งหมดแล้ว"));
    }

    [HttpPost("send")]
    [Authorize(Roles = "SystemAdmin,Admin")]
    public async Task<ActionResult<ApiResponse<string>>> Send([FromBody] SendNotificationRequest request)
    {
        await _notificationService.SendAsync(
            request.UserId, request.CompanyId, request.Type,
            request.Title, request.Message, request.ActionUrl,
            request.EntityType, request.EntityId);
        return Ok(new ApiResponse<string>(true, null, "ส่งการแจ้งเตือนสำเร็จ"));
    }
}
