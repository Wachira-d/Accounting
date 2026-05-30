using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Cheque;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cheques")]
[Authorize]
public class ChequeController : ControllerBase
{
    private readonly IChequeService _svc;

    public ChequeController(IChequeService svc) { _svc = svc; }

    public sealed record OpenBookRequest(Guid BankAccountId, string BookNumber,
        long StartNumber, long EndNumber);

    [HttpPost("books")]
    public async Task<ActionResult<ApiResponse<ChequeBook>>> OpenBook(
        Guid companyId, [FromBody] OpenBookRequest req, CancellationToken ct)
    {
        try
        {
            var book = await _svc.OpenChequeBookAsync(companyId, req.BankAccountId,
                req.BookNumber, req.StartNumber, req.EndNumber, ct);
            return Ok(new ApiResponse<ChequeBook>(true, book,
                $"เปิดเล่มเช็ค {req.BookNumber} เลขที่ {req.StartNumber}-{req.EndNumber}"));
        }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record IssueOutboundRequest(Guid ChequeBookId, Guid? ContactId,
        DateTime ChequeDate, decimal Amount, Guid? PaymentId, string? Notes);

    [HttpPost("outbound")]
    public async Task<ActionResult<ApiResponse<Cheque>>> IssueOutbound(
        Guid companyId, [FromBody] IssueOutboundRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _svc.IssueOutboundAsync(companyId, req.ChequeBookId,
                req.ContactId, req.ChequeDate, req.Amount, req.PaymentId, req.Notes, ct);
            return Ok(new ApiResponse<Cheque>(true, c,
                $"ออกเช็คเลขที่ {c.ChequeNumber} จำนวน {c.Amount:N2} บาท"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record RecordInboundRequest(Guid? ContactId, long ChequeNumber,
        string IssuingBank, DateTime ChequeDate, decimal Amount, Guid? PaymentId, string? Notes);

    [HttpPost("inbound")]
    public async Task<ActionResult<ApiResponse<Cheque>>> RecordInbound(
        Guid companyId, [FromBody] RecordInboundRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _svc.RecordInboundAsync(companyId, req.ContactId, req.ChequeNumber,
                req.IssuingBank, req.ChequeDate, req.Amount, req.PaymentId, req.Notes, ct);
            return Ok(new ApiResponse<Cheque>(true, c, "บันทึกเช็ครับจากลูกค้า"));
        }
        catch (ArgumentException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record ClearRequest(DateTime ClearedAt);

    [HttpPost("{chequeId:guid}/clear")]
    public async Task<ActionResult<ApiResponse<Cheque>>> MarkCleared(
        Guid companyId, Guid chequeId, [FromBody] ClearRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _svc.MarkClearedAsync(companyId, chequeId, req.ClearedAt, ct);
            return Ok(new ApiResponse<Cheque>(true, c, "อัปเดตเช็คผ่านแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record BounceRequest(string Reason);

    [HttpPost("{chequeId:guid}/bounce")]
    public async Task<ActionResult<ApiResponse<Cheque>>> MarkBounced(
        Guid companyId, Guid chequeId, [FromBody] BounceRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _svc.MarkBouncedAsync(companyId, chequeId, req.Reason, ct);
            return Ok(new ApiResponse<Cheque>(true, c, "บันทึกเช็คคืนแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("outstanding")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<Cheque>>>> Outstanding(
        Guid companyId, [FromQuery] Guid? bankAccountId, CancellationToken ct)
    {
        var rows = await _svc.ListOutstandingAsync(companyId, bankAccountId, ct);
        return Ok(new ApiResponse<IReadOnlyList<Cheque>>(true, rows,
            $"เช็คคงค้าง {rows.Count} ฉบับ"));
    }
}
