using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Consignment;
using Accounting.Services.Implementations.Production;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Two related manufacturing-ish features grouped together:
///   • Production orders with BOM backflush.
///   • Consignment stock movement (receive / dispatch / consume).
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/mfg")]
[Authorize]
public class ProductionConsignmentController : ControllerBase
{
    public sealed record CreateOrderRequest(string OrderNumber, Guid BomId,
        decimal PlannedQty, DateTime PlannedStartAt, string? Notes);

    [HttpPost("production-orders")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> CreateOrder(
        Guid companyId, [FromBody] CreateOrderRequest req,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.CreateAsync(companyId, req.OrderNumber, req.BomId,
                req.PlannedQty, req.PlannedStartAt, req.Notes, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o,
                $"สร้าง production order {o.OrderNumber} แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("production-orders/{orderId:guid}/shortages")]
    public async Task<ActionResult<ApiResponse<ShortageReport>>> CheckShortages(
        Guid companyId, Guid orderId,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.CheckShortagesAsync(companyId, orderId, ct);
            return Ok(new ApiResponse<ShortageReport>(true, r,
                r.Shortages.Count == 0 ? "วัตถุดิบครบ พร้อมเริ่มผลิต"
                    : $"ขาด component {r.Shortages.Count} รายการ"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("production-orders/{orderId:guid}/release")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> Release(
        Guid companyId, Guid orderId,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.ReleaseAsync(companyId, orderId, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o, "ปล่อยงานผลิตแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record CompleteOrderRequest(decimal CompletedQty, DateTime CompletedAt);

    [HttpPost("production-orders/{orderId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<ProductionOrder>>> Complete(
        Guid companyId, Guid orderId, [FromBody] CompleteOrderRequest req,
        [FromServices] IProductionOrderService svc, CancellationToken ct)
    {
        try
        {
            var o = await svc.CompleteAsync(companyId, orderId, req.CompletedQty, req.CompletedAt, ct);
            return Ok(new ApiResponse<ProductionOrder>(true, o,
                $"ผลิตเสร็จ {req.CompletedQty} หน่วย ที่ราคา {o.CumulativeComponentCost / Math.Max(1, req.CompletedQty):N4} / หน่วย"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("production-orders")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ProductionOrder>>>> List(
        Guid companyId, [FromQuery] string? status,
        [FromServices] IProductionOrderService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<ProductionOrder>>(true,
            await svc.ListAsync(companyId, status, ct)));

    // ── Consignment ─────────────────────────────────────────────────
    public sealed record ReceiveInboundRequest(Guid ProductId, Guid VendorContactId,
        decimal Quantity, decimal? AgreedUnitPrice, DateTime ReceivedAt);

    [HttpPost("consignment/inbound")]
    public async Task<ActionResult<ApiResponse<ConsignmentRecord>>> ReceiveInbound(
        Guid companyId, [FromBody] ReceiveInboundRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.ReceiveInboundAsync(companyId, req.ProductId, req.VendorContactId,
                req.Quantity, req.AgreedUnitPrice, req.ReceivedAt, ct);
            return Ok(new ApiResponse<ConsignmentRecord>(true, r,
                "รับสินค้า consignment (Inbound) — ยังไม่ผูก AP จนกว่าจะใช้/ขาย"));
        }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record DispatchOutboundRequest(Guid ProductId, Guid CustomerContactId,
        decimal Quantity, decimal? AgreedUnitPrice, DateTime DispatchedAt);

    [HttpPost("consignment/outbound")]
    public async Task<ActionResult<ApiResponse<ConsignmentRecord>>> DispatchOutbound(
        Guid companyId, [FromBody] DispatchOutboundRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.DispatchOutboundAsync(companyId, req.ProductId, req.CustomerContactId,
                req.Quantity, req.AgreedUnitPrice, req.DispatchedAt, ct);
            return Ok(new ApiResponse<ConsignmentRecord>(true, r,
                "ส่งสินค้าฝากขาย (Outbound) — รับรู้รายได้ตอนลูกค้าใช้/ขาย"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record ConsumeRequest(Guid ConsignmentRecordId,
        decimal ConsumedQuantity, DateTime ConsumedAt);

    [HttpPost("consignment/consume")]
    public async Task<ActionResult<ApiResponse<ConsumptionResult>>> Consume(
        Guid companyId, [FromBody] ConsumeRequest req,
        [FromServices] IConsignmentService svc, CancellationToken ct)
    {
        try
        {
            var r = await svc.RecordConsumptionAsync(companyId, req.ConsignmentRecordId,
                req.ConsumedQuantity, req.ConsumedAt, ct);
            var direction = r.Direction == "Inbound" ? "AP" : "AR";
            return Ok(new ApiResponse<ConsumptionResult>(true, r,
                r.CreatedDocumentId.HasValue
                    ? $"บันทึก consumption + auto-create {direction} {r.CreatedDocumentNumber} ยอด {r.Amount:N2}"
                    : "บันทึก consumption แล้ว (ไม่มีราคาตกลง — ไม่ post เอกสาร)"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("consignment")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ConsignmentRecord>>>> ListConsignment(
        Guid companyId, [FromQuery] string? direction,
        [FromServices] IConsignmentService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<ConsignmentRecord>>(true,
            await svc.ListAsync(companyId, direction, ct)));
}
