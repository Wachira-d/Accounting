using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/ecommerce")]
[Authorize]
public class ECommerceController : ControllerBase
{
    private readonly IECommerceService _service;
    /// <summary>รอบ 232 (232-Notify): แจ้ง "ซิงก์ร้านค้าออนไลน์สำเร็จ" ผ่านเครื่องแจ้งเตือนกลาง (ค่าตั้งของบริษัท) — เดิม <c>NotifyECommerceSyncAsync</c> ประกาศกลุ่ม LINE
    /// โดยถอยไปกลุ่มกลางของแพลตฟอร์มเมื่อบริษัทไม่มี OA</summary>
    private readonly INotificationEngine? _notify;

    public ECommerceController(IECommerceService service, INotificationEngine? notify = null)
    {
        _service = service;
        _notify = notify;
    }

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections(Guid companyId)
    {
        var result = await _service.GetConnectionsAsync(companyId);
        return Ok(new ApiResponse<object>(true, result));
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(Guid companyId, [FromBody] ConnectECommerceRequest request)
    {
        var result = await _service.ConnectAsync(companyId, request);
        return Created($"api/companies/{companyId}/ecommerce/connections/{result.Id}", new ApiResponse<object>(true, result));
    }

    [HttpPost("connections/{connectionId}/sync")]
    public async Task<IActionResult> SyncOrders(Guid companyId, Guid connectionId, [FromQuery] DateTime? since = null)
    {
        var result = await _service.SyncOrdersAsync(companyId, connectionId, since);
        if (result.Status == "Success" && result.NewOrders > 0 && _notify != null)
            await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.ECommerceSyncCompleted, new NotificationContext
            {
                Title = $"🛒 Sync {result.Platform} สำเร็จ",
                Message = $"📦 ออเดอร์ใหม่ {result.NewOrders} รายการ\n💰 ยอดรวม {result.TotalAmount:N2} บาท",
                ActionUrl = "/pages/integrations.html",
                EntityType = "ECommerceConnection", EntityId = connectionId,
            });
        return Ok(new ApiResponse<object>(true, result));
    }

    [HttpPost("sync-all")]
    public async Task<IActionResult> SyncAll(Guid companyId)
    {
        var result = await _service.SyncAllAsync(companyId);
        return Ok(new ApiResponse<object>(true, result));
    }

    [HttpPost("connections/{connectionId}/test")]
    public async Task<IActionResult> TestConnection(Guid companyId, Guid connectionId)
    {
        var result = await _service.TestConnectionAsync(companyId, connectionId);
        return Ok(new ApiResponse<object>(true, result));
    }

    [HttpDelete("connections/{connectionId}")]
    public async Task<IActionResult> Disconnect(Guid companyId, Guid connectionId)
    {
        await _service.DisconnectAsync(companyId, connectionId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกการเชื่อมต่อสำเร็จ"));
    }
}
