using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId}/ecommerce")]
[Authorize]
public class ECommerceController : ControllerBase
{
    private readonly IECommerceService _service;
    private readonly ILineNotifyService _lineNotify;

    public ECommerceController(IECommerceService service, ILineNotifyService lineNotify)
    {
        _service = service;
        _lineNotify = lineNotify;
    }

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections(Guid companyId)
    {
        var result = await _service.GetConnectionsAsync(companyId);
        return Ok(new { data = result });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(Guid companyId, [FromBody] ConnectECommerceRequest request)
    {
        var result = await _service.ConnectAsync(companyId, request);
        return Created($"api/companies/{companyId}/ecommerce/connections/{result.Id}", new { data = result });
    }

    [HttpPost("connections/{connectionId}/sync")]
    public async Task<IActionResult> SyncOrders(Guid companyId, Guid connectionId, [FromQuery] DateTime? since = null)
    {
        var result = await _service.SyncOrdersAsync(companyId, connectionId, since);
        if (result.Status == "Success" && result.NewOrders > 0)
            await _lineNotify.NotifyECommerceSyncAsync(companyId, result.Platform, result.NewOrders, result.TotalAmount);
        return Ok(new { data = result });
    }

    [HttpPost("sync-all")]
    public async Task<IActionResult> SyncAll(Guid companyId)
    {
        var result = await _service.SyncAllAsync(companyId);
        return Ok(new { data = result });
    }

    [HttpPost("connections/{connectionId}/test")]
    public async Task<IActionResult> TestConnection(Guid companyId, Guid connectionId)
    {
        var result = await _service.TestConnectionAsync(companyId, connectionId);
        return Ok(new { data = result });
    }

    [HttpDelete("connections/{connectionId}")]
    public async Task<IActionResult> Disconnect(Guid companyId, Guid connectionId)
    {
        await _service.DisconnectAsync(companyId, connectionId);
        return NoContent();
    }
}
