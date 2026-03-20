using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/intercompany")]
[Authorize]
public class IntercompanyController : ControllerBase
{
    private readonly IIntercompanyService _service;
    public IntercompanyController(IIntercompanyService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<IntercompanyTxnResponse>>> Create(Guid companyId, [FromBody] CreateIntercompanyTxnRequest request)
        => Ok(new ApiResponse<IntercompanyTxnResponse>(true, await _service.CreateAsync(companyId, request, User.Identity?.Name ?? "")));

    [HttpGet("{transactionId:guid}")]
    public async Task<ActionResult<ApiResponse<IntercompanyTxnResponse>>> GetById(Guid companyId, Guid transactionId)
        => Ok(new ApiResponse<IntercompanyTxnResponse>(true, await _service.GetByIdAsync(companyId, transactionId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<IntercompanyTxnResponse>>>> GetAll(Guid companyId, [FromQuery] IntercompanyStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<IntercompanyTxnResponse>>(true, await _service.GetAllAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPost("{transactionId:guid}/confirm")]
    public async Task<ActionResult<ApiResponse<IntercompanyTxnResponse>>> Confirm(Guid companyId, Guid transactionId)
        => Ok(new ApiResponse<IntercompanyTxnResponse>(true, await _service.ConfirmAsync(companyId, transactionId, User.Identity?.Name ?? "")));

    [HttpPost("{transactionId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid transactionId)
    { await _service.VoidAsync(companyId, transactionId); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpGet("balances")]
    public async Task<ActionResult<ApiResponse<List<IntercompanyBalanceResponse>>>> GetBalances(Guid companyId)
        => Ok(new ApiResponse<List<IntercompanyBalanceResponse>>(true, await _service.GetIntercompanyBalancesAsync(companyId)));
}
