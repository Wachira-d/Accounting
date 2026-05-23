using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/pos")]
[Authorize]
public class PosController : ControllerBase
{
    private readonly IPosService _pos;
    public PosController(IPosService pos) => _pos = pos;

    // ===== Terminal =====
    [HttpGet("terminals")]
    public async Task<ActionResult<ApiResponse<List<TerminalResponse>>>> GetTerminals(Guid companyId)
        => Ok(new ApiResponse<List<TerminalResponse>>(true, await _pos.GetTerminalsAsync(companyId)));

    [HttpPost("terminals")]
    public async Task<ActionResult<ApiResponse<TerminalResponse>>> CreateTerminal(Guid companyId, [FromBody] CreateTerminalRequest request)
        => StatusCode(201, new ApiResponse<TerminalResponse>(true, await _pos.CreateTerminalAsync(companyId, request)));

    [HttpPut("terminals/{terminalId:guid}")]
    public async Task<ActionResult<ApiResponse<TerminalResponse>>> UpdateTerminal(Guid companyId, Guid terminalId, [FromBody] UpdateTerminalRequest request)
        => Ok(new ApiResponse<TerminalResponse>(true, await _pos.UpdateTerminalAsync(companyId, terminalId, request)));

    // ===== Session =====
    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<SessionResponse>>>> GetSessions(Guid companyId, [FromQuery] Guid? terminalId = null, [FromQuery] bool? isOpen = null)
        => Ok(new ApiResponse<List<SessionResponse>>(true, await _pos.GetSessionsAsync(companyId, terminalId, isOpen)));

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> GetSession(Guid companyId, Guid sessionId)
        => Ok(new ApiResponse<SessionResponse>(true, await _pos.GetSessionAsync(companyId, sessionId)));

    [HttpPost("sessions/open")]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> OpenSession(Guid companyId, [FromBody] OpenSessionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return StatusCode(201, new ApiResponse<SessionResponse>(true, await _pos.OpenSessionAsync(companyId, userId, request)));
    }

    [HttpPost("sessions/{sessionId:guid}/close")]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> CloseSession(Guid companyId, Guid sessionId, [FromBody] CloseSessionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return Ok(new ApiResponse<SessionResponse>(true, await _pos.CloseSessionAsync(companyId, userId, sessionId, request)));
    }

    // ===== Order =====
    [HttpGet("orders")]
    public async Task<ActionResult<ApiResponse<PagedResponse<OrderResponse>>>> GetOrders(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] Guid? sessionId = null, [FromQuery] string? status = null)
        => Ok(new ApiResponse<PagedResponse<OrderResponse>>(true, await _pos.GetOrdersAsync(companyId, new PagedRequest(page, pageSize, search), sessionId, status)));

    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetOrder(Guid companyId, Guid orderId)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.GetOrderAsync(companyId, orderId)));

    [HttpPost("orders")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CreateOrder(Guid companyId, [FromBody] CreateOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return StatusCode(201, new ApiResponse<OrderResponse>(true, await _pos.CreateOrderAsync(companyId, request, userId)));
    }

    [HttpPut("orders/{orderId:guid}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrder(Guid companyId, Guid orderId, [FromBody] UpdateOrderRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateOrderAsync(companyId, orderId, request)));

    [HttpPost("orders/{orderId:guid}/status")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateOrderStatus(Guid companyId, Guid orderId, [FromBody] UpdateOrderStatusRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateOrderStatusAsync(companyId, orderId, request, userId)));
    }

    [HttpPost("orders/{orderId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidOrder(Guid companyId, Guid orderId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _pos.VoidOrderAsync(companyId, orderId, userId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกออเดอร์สำเร็จ"));
    }

    [HttpPost("orders/{orderId:guid}/refund")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> RefundOrder(Guid companyId, Guid orderId, [FromBody] RefundOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.RefundOrderAsync(companyId, orderId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "คืนเงินสำเร็จ"));
    }

    [HttpPost("orders/{orderId:guid}/issue-tax-invoice")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> IssueTaxInvoice(Guid companyId, Guid orderId, [FromBody] IssueTaxInvoiceRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.IssueTaxInvoiceAsync(companyId, orderId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "ออกใบกำกับภาษีเต็มรูปสำเร็จ"));
    }

    [HttpPost("orders/sync-offline")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> SyncOfflineOrder(Guid companyId, [FromBody] OfflineOrderRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _pos.SyncOfflineOrderAsync(companyId, request, userId);
        return Ok(new ApiResponse<OrderResponse>(true, result, "Sync ออเดอร์ออฟไลน์สำเร็จ"));
    }

    // ===== Order Items =====
    [HttpPost("orders/{orderId:guid}/items")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> AddOrderItem(Guid companyId, Guid orderId, [FromBody] CreateOrderItemRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.AddOrderItemAsync(companyId, orderId, request)));

    [HttpDelete("orders/{orderId:guid}/items/{itemId:guid}")]
    public async Task<ActionResult> RemoveOrderItem(Guid companyId, Guid orderId, Guid itemId)
    {
        await _pos.RemoveOrderItemAsync(companyId, orderId, itemId);
        return NoContent();
    }

    [HttpPost("orders/{orderId:guid}/items/{itemId:guid}/status")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> UpdateItemStatus(Guid companyId, Guid orderId, Guid itemId, [FromBody] UpdateItemStatusRequest request)
        => Ok(new ApiResponse<OrderResponse>(true, await _pos.UpdateItemStatusAsync(companyId, orderId, itemId, request)));

    // ===== Payment =====
    [HttpPost("payments")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> AddPayment(Guid companyId, [FromBody] CreatePaymentRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.AddPaymentAsync(companyId, request, userId)));
    }

    [HttpPost("orders/{orderId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> CompleteOrder(Guid companyId, Guid orderId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        return Ok(new ApiResponse<OrderResponse>(true, await _pos.CompleteOrderAsync(companyId, orderId, userId), "ปิดบิลสำเร็จ"));
    }

    // ===== Service Package =====
    [HttpGet("packages")]
    public async Task<ActionResult<ApiResponse<List<ServicePackageResponse>>>> GetPackages(Guid companyId, [FromQuery] string? category = null)
        => Ok(new ApiResponse<List<ServicePackageResponse>>(true, await _pos.GetServicePackagesAsync(companyId, category)));

    [HttpGet("packages/{packageId:guid}")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> GetPackage(Guid companyId, Guid packageId)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.GetServicePackageAsync(companyId, packageId)));

    [HttpPost("packages")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> CreatePackage(Guid companyId, [FromBody] CreateServicePackageRequest request)
        => StatusCode(201, new ApiResponse<ServicePackageResponse>(true, await _pos.CreateServicePackageAsync(companyId, request)));

    [HttpPut("packages/{packageId:guid}")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> UpdatePackage(Guid companyId, Guid packageId, [FromBody] UpdateServicePackageRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.UpdateServicePackageAsync(companyId, packageId, request)));

    [HttpDelete("packages/{packageId:guid}")]
    public async Task<ActionResult> DeletePackage(Guid companyId, Guid packageId)
    {
        await _pos.DeleteServicePackageAsync(companyId, packageId);
        return NoContent();
    }

    // ===== Service Component =====
    [HttpPost("packages/{packageId:guid}/components")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> AddComponent(Guid companyId, Guid packageId, [FromBody] CreateServiceComponentRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.AddComponentAsync(companyId, packageId, request)));

    [HttpPut("packages/{packageId:guid}/components/{componentId:guid}")]
    public async Task<ActionResult<ApiResponse<ServicePackageResponse>>> UpdateComponent(Guid companyId, Guid packageId, Guid componentId, [FromBody] UpdateServiceComponentRequest request)
        => Ok(new ApiResponse<ServicePackageResponse>(true, await _pos.UpdateComponentAsync(companyId, packageId, componentId, request)));

    [HttpDelete("packages/{packageId:guid}/components/{componentId:guid}")]
    public async Task<ActionResult> RemoveComponent(Guid companyId, Guid packageId, Guid componentId)
    {
        await _pos.RemoveComponentAsync(companyId, packageId, componentId);
        return NoContent();
    }

    // ===== Service Activity =====
    [HttpPut("activities/{activityId:guid}")]
    public async Task<ActionResult<ApiResponse<ServiceActivityResponse>>> UpdateActivity(Guid companyId, Guid activityId, [FromBody] UpdateServiceActivityRequest request)
        => Ok(new ApiResponse<ServiceActivityResponse>(true, await _pos.UpdateServiceActivityAsync(companyId, activityId, request)));

    // ===== Modifier Group =====
    [HttpGet("modifier-groups")]
    public async Task<ActionResult<ApiResponse<List<ModifierGroupResponse>>>> GetModifierGroups(Guid companyId, [FromQuery] Guid? productId = null)
        => Ok(new ApiResponse<List<ModifierGroupResponse>>(true, await _pos.GetModifierGroupsAsync(companyId, productId)));

    [HttpPost("modifier-groups")]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> CreateModifierGroup(Guid companyId, [FromBody] CreateModifierGroupRequest request)
        => StatusCode(201, new ApiResponse<ModifierGroupResponse>(true, await _pos.CreateModifierGroupAsync(companyId, request)));

    [HttpPut("modifier-groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> UpdateModifierGroup(Guid companyId, Guid groupId, [FromBody] UpdateModifierGroupRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.UpdateModifierGroupAsync(companyId, groupId, request)));

    [HttpDelete("modifier-groups/{groupId:guid}")]
    public async Task<ActionResult> DeleteModifierGroup(Guid companyId, Guid groupId)
    {
        await _pos.DeleteModifierGroupAsync(companyId, groupId);
        return NoContent();
    }

    // ===== Modifier Option =====
    [HttpPost("modifier-groups/{groupId:guid}/options")]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> AddOption(Guid companyId, Guid groupId, [FromBody] CreateModifierOptionRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.AddModifierOptionAsync(companyId, groupId, request)));

    [HttpPut("modifier-groups/{groupId:guid}/options/{optionId:guid}")]
    public async Task<ActionResult<ApiResponse<ModifierGroupResponse>>> UpdateOption(Guid companyId, Guid groupId, Guid optionId, [FromBody] UpdateModifierOptionRequest request)
        => Ok(new ApiResponse<ModifierGroupResponse>(true, await _pos.UpdateModifierOptionAsync(companyId, groupId, optionId, request)));

    [HttpDelete("modifier-groups/{groupId:guid}/options/{optionId:guid}")]
    public async Task<ActionResult> RemoveOption(Guid companyId, Guid groupId, Guid optionId)
    {
        await _pos.RemoveModifierOptionAsync(companyId, groupId, optionId);
        return NoContent();
    }

    // ===== Reports =====
    [HttpGet("daily-summary")]
    public async Task<ActionResult<ApiResponse<PosDailySummaryResponse>>> GetDailySummary(Guid companyId, [FromQuery] DateTime? date = null)
        => Ok(new ApiResponse<PosDailySummaryResponse>(true, await _pos.GetDailySummaryAsync(companyId, date ?? DateTime.UtcNow)));

    [HttpGet("commission-summary")]
    public async Task<ActionResult<ApiResponse<List<CommissionSummaryResponse>>>> GetCommissionSummary(
        Guid companyId, [FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd)
        => Ok(new ApiResponse<List<CommissionSummaryResponse>>(true, await _pos.GetCommissionSummariesAsync(companyId, periodStart, periodEnd)));
}
