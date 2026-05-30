using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Forex;
using Accounting.Services.Implementations.Inventory;
using Accounting.Services.Implementations.PettyCash;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// SME operations — petty cash, stock count, FX revaluation. Each
/// is a small surface so they share a controller; can be split if/when
/// any grows.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/sme")]
[Authorize]
public class SmeOperationsController : ControllerBase
{
    // ── Petty cash ───────────────────────────────────────────────────
    public sealed record CreateFundRequest(string Name, Guid? CustodianUserId,
        decimal ImprestAmount, Guid? LinkedAccountId);

    [HttpPost("petty-cash/funds")]
    public async Task<ActionResult<ApiResponse<PettyCashFund>>> CreateFund(
        Guid companyId, [FromBody] CreateFundRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var fund = await svc.CreateFundAsync(companyId, req.Name, req.CustodianUserId,
            req.ImprestAmount, req.LinkedAccountId, ct);
        return Ok(new ApiResponse<PettyCashFund>(true, fund, $"เปิดเงินสดย่อย {fund.Name}"));
    }

    [HttpGet("petty-cash/funds")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PettyCashFund>>>> ListFunds(
        Guid companyId, [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<PettyCashFund>>(true, await svc.ListAsync(companyId, ct)));

    public sealed record DisburseRequest(decimal Amount, string Description,
        Guid? ExpenseAccountId, string? ReceiptReference, DateTime TxnDate);

    [HttpPost("petty-cash/funds/{fundId:guid}/disburse")]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> Disburse(
        Guid companyId, Guid fundId, [FromBody] DisburseRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        try
        {
            var t = await svc.DisburseAsync(companyId, fundId, req.Amount, req.Description,
                req.ExpenseAccountId, req.ReceiptReference, req.TxnDate, ct);
            return Ok(new ApiResponse<PettyCashTransaction>(true, t, $"จ่ายเงินสดย่อย {req.Amount:N2}"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record ReplenishRequest(decimal Amount, string? Notes, DateTime TxnDate);

    [HttpPost("petty-cash/funds/{fundId:guid}/replenish")]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> Replenish(
        Guid companyId, Guid fundId, [FromBody] ReplenishRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var t = await svc.ReplenishAsync(companyId, fundId, req.Amount, req.Notes, req.TxnDate, ct);
        return Ok(new ApiResponse<PettyCashTransaction>(true, t, "เติมเงินสดย่อยแล้ว"));
    }

    [HttpGet("petty-cash/funds/{fundId:guid}/transactions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PettyCashTransaction>>>> ListTransactions(
        Guid companyId, Guid fundId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<PettyCashTransaction>>(true,
            await svc.ListTransactionsAsync(companyId, fundId, fromDate, toDate, ct)));

    // ── Stock count ─────────────────────────────────────────────────
    public sealed record StartCountRequest(string CountNumber, Guid? WarehouseId,
        string CountType, List<Guid> ProductIds);

    [HttpPost("stock-counts")]
    public async Task<ActionResult<ApiResponse<StockCount>>> StartStockCount(
        Guid companyId, [FromBody] StartCountRequest req,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        var sc = await svc.StartAsync(companyId, req.CountNumber, req.WarehouseId,
            req.CountType, req.ProductIds, ct);
        return Ok(new ApiResponse<StockCount>(true, sc,
            $"เปิดรอบนับสต็อก {sc.CountNumber} จำนวน {sc.Lines.Count} รายการ"));
    }

    public sealed record SetLineCountRequest(Guid LineId, decimal CountedQuantity, string? Notes);

    [HttpPut("stock-counts/{stockCountId:guid}/line")]
    public async Task<ActionResult<ApiResponse<StockCount>>> SetLine(
        Guid companyId, Guid stockCountId, [FromBody] SetLineCountRequest req,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        try
        {
            var sc = await svc.SetLineCountAsync(companyId, stockCountId,
                req.LineId, req.CountedQuantity, req.Notes, ct);
            return Ok(new ApiResponse<StockCount>(true, sc, "บันทึกจำนวนนับแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("stock-counts/{stockCountId:guid}/close")]
    public async Task<ActionResult<ApiResponse<StockCount>>> CloseCount(
        Guid companyId, Guid stockCountId,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        try
        {
            var sc = await svc.CloseAsync(companyId, stockCountId, ct);
            return Ok(new ApiResponse<StockCount>(true, sc, "ปิดรอบนับสต็อก + อัปเดต CurrentStock แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("stock-counts")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockCount>>>> ListStockCounts(
        Guid companyId, [FromQuery] string? status,
        [FromServices] IStockCountService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<StockCount>>(true, await svc.ListAsync(companyId, status, ct)));

    // ── FX revaluation ──────────────────────────────────────────────
    public sealed record FxRevalRequest(DateTime AsOf, Dictionary<string, decimal> ClosingRates);

    [HttpPost("fx-revaluation/propose")]
    public async Task<ActionResult<ApiResponse<FxRevaluationResult>>> ProposeFxRevaluation(
        Guid companyId, [FromBody] FxRevalRequest req,
        [FromServices] IFxRevaluationService svc, CancellationToken ct)
    {
        var result = await svc.ProposeAsync(companyId, req.AsOf, req.ClosingRates, ct);
        return Ok(new ApiResponse<FxRevaluationResult>(true, result,
            $"กำไรสุทธิจาก FX revaluation: {result.NetEffect:N2}"));
    }
}
