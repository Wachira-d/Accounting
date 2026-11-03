using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/bank-feeds")]
[Authorize]
public class BankFeedController : ControllerBase
{
    private readonly IBankFeedService _service;
    /// <summary>รอบ 232 (232-Notify): แจ้ง "ซิงก์ธนาคารสำเร็จ" ผ่านเครื่องแจ้งเตือนกลาง (ค่าตั้งของบริษัท: กระดิ่ง/อีเมล/LINE/กลุ่ม LINE ของบริษัท) —
    /// เดิม <c>ILineNotifyService.NotifyBankSyncCompleteAsync</c> ประกาศเข้ากลุ่ม LINE โดยถอยไปกลุ่มกลางของแพลตฟอร์มเมื่อบริษัทไม่มี OA</summary>
    private readonly INotificationEngine? _notify;

    public BankFeedController(IBankFeedService service, INotificationEngine? notify = null)
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

    [HttpGet("connections/{connectionId}")]
    public async Task<IActionResult> GetConnection(Guid companyId, Guid connectionId)
    {
        var result = await _service.GetConnectionAsync(companyId, connectionId);
        return Ok(new ApiResponse<object>(true, result));
    }

    [HttpPost("connections")]
    public async Task<IActionResult> CreateConnection(Guid companyId, [FromBody] CreateBankFeedConnectionRequest request)
    {
        var result = await _service.CreateConnectionAsync(companyId, request);
        return Created($"api/companies/{companyId}/bank-feeds/connections/{result.Id}", new ApiResponse<object>(true, result));
    }

    [HttpPost("connections/{connectionId}/sync")]
    public async Task<IActionResult> Sync(Guid companyId, Guid connectionId)
    {
        var result = await _service.SyncAsync(companyId, connectionId);
        if (result.Status == "Success" && result.NewTransactions > 0 && _notify != null)
            await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.BankSyncCompleted, new NotificationContext
            {
                Title = "🏦 Sync ธนาคารสำเร็จ",
                Message = $"🔄 {result.BankName}\n📊 รายการใหม่ {result.NewTransactions} รายการ",
                ActionUrl = "/pages/bank.html",
                EntityType = "BankFeedConnection", EntityId = connectionId,
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
    public async Task<IActionResult> DeleteConnection(Guid companyId, Guid connectionId)
    {
        await _service.DeleteConnectionAsync(companyId, connectionId);
        return Ok(new ApiResponse<bool>(true, true, "ลบการเชื่อมต่อสำเร็จ"));
    }
}
