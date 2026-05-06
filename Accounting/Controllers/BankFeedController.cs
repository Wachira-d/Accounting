using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId}/bank-feeds")]
[Authorize]
public class BankFeedController : ControllerBase
{
    private readonly IBankFeedService _service;
    private readonly ILineNotifyService _lineNotify;

    public BankFeedController(IBankFeedService service, ILineNotifyService lineNotify)
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

    [HttpGet("connections/{connectionId}")]
    public async Task<IActionResult> GetConnection(Guid companyId, Guid connectionId)
    {
        var result = await _service.GetConnectionAsync(companyId, connectionId);
        return Ok(new { data = result });
    }

    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(Guid companyId, [FromBody] CreateBankConnectionRequest request)
    {
        var result = await _service.CreateConnectionAsync(companyId, request);
        return Created($"api/companies/{companyId}/bank-feeds/connections/{result.Id}", new { data = result });
    }

    [HttpPost("connections/{connectionId}/sync")]
    public async Task<IActionResult> Sync(Guid companyId, Guid connectionId)
    {
        var result = await _service.SyncAsync(companyId, connectionId);
        if (result.Status == "Success" && result.NewTransactions > 0)
            await _lineNotify.NotifyBankSyncCompleteAsync(companyId, result.BankName, result.NewTransactions);
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
    public async Task<IActionResult> DeleteConnection(Guid companyId, Guid connectionId)
    {
        await _service.DeleteConnectionAsync(companyId, connectionId);
        return NoContent();
    }
}
