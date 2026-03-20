using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/open-banking")]
[Authorize]
public class OpenBankingController : ControllerBase
{
    private readonly IOpenBankingService _service;
    public OpenBankingController(IOpenBankingService service) => _service = service;

    [HttpPost("connections")]
    public async Task<ActionResult<ApiResponse<BankConnectionResponse>>> CreateConnection(Guid companyId, [FromBody] CreateBankConnectionRequest request)
        => Ok(new ApiResponse<BankConnectionResponse>(true, await _service.CreateConnectionAsync(companyId, request)));

    [HttpGet("connections")]
    public async Task<ActionResult<ApiResponse<List<BankConnectionResponse>>>> GetConnections(Guid companyId)
        => Ok(new ApiResponse<List<BankConnectionResponse>>(true, await _service.GetConnectionsAsync(companyId)));

    [HttpPut("connections/{connectionId:guid}")]
    public async Task<ActionResult<ApiResponse<BankConnectionResponse>>> UpdateConnection(Guid companyId, Guid connectionId, [FromBody] UpdateBankConnectionRequest request)
        => Ok(new ApiResponse<BankConnectionResponse>(true, await _service.UpdateConnectionAsync(companyId, connectionId, request)));

    [HttpDelete("connections/{connectionId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteConnection(Guid companyId, Guid connectionId)
    { await _service.DeleteConnectionAsync(companyId, connectionId); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpPost("connections/{connectionId:guid}/sync")]
    public async Task<ActionResult<ApiResponse<BankFeedImportResponse>>> Sync(Guid companyId, Guid connectionId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
        => Ok(new ApiResponse<BankFeedImportResponse>(true, await _service.SyncTransactionsAsync(companyId, connectionId, fromDate, toDate)));

    [HttpGet("connections/{connectionId:guid}/imports")]
    public async Task<ActionResult<ApiResponse<List<BankFeedImportResponse>>>> GetImports(Guid companyId, Guid connectionId)
        => Ok(new ApiResponse<List<BankFeedImportResponse>>(true, await _service.GetImportHistoryAsync(companyId, connectionId)));

    [HttpPost("import-file")]
    public async Task<ActionResult<ApiResponse<BankFeedImportResponse>>> ImportFile(Guid companyId, [FromQuery] Guid bankAccountId, [FromQuery] string fileFormat, [FromBody] string base64Content)
        => Ok(new ApiResponse<BankFeedImportResponse>(true, await _service.ImportFileAsync(companyId, bankAccountId, fileFormat, base64Content)));
}
