using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class BankController : ControllerBase
{
    private readonly IBankService _bankService;

    public BankController(IBankService bankService)
    {
        _bankService = bankService;
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<BankAccountResponse>>>> GetAccounts(Guid companyId)
    {
        var result = await _bankService.GetBankAccountsAsync(companyId);
        return Ok(new ApiResponse<List<BankAccountResponse>>(true, result));
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<BankAccountResponse>>> CreateAccount(Guid companyId, [FromBody] CreateBankAccountRequest request)
    {
        var result = await _bankService.CreateBankAccountAsync(companyId, request);
        return Ok(new ApiResponse<BankAccountResponse>(true, result, "สร้างบัญชีธนาคารสำเร็จ"));
    }

    [HttpPut("accounts/{accountId:guid}")]
    public async Task<ActionResult<ApiResponse<BankAccountResponse>>> UpdateAccount(Guid companyId, Guid accountId, [FromBody] UpdateBankAccountRequest request)
    {
        var result = await _bankService.UpdateBankAccountAsync(companyId, accountId, request);
        return Ok(new ApiResponse<BankAccountResponse>(true, result));
    }

    [HttpGet("accounts/{accountId:guid}/transactions")]
    public async Task<ActionResult<ApiResponse<PagedResponse<BankTransactionResponse>>>> GetTransactions(
        Guid companyId, Guid accountId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _bankService.GetTransactionsAsync(companyId, accountId, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<BankTransactionResponse>>(true, result));
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> CreateTransaction(Guid companyId, [FromBody] CreateBankTransactionRequest request)
    {
        var result = await _bankService.CreateTransactionAsync(companyId, request);
        return Ok(new ApiResponse<BankTransactionResponse>(true, result));
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> Reconcile(Guid companyId, [FromBody] ReconcileRequest request)
    {
        var result = await _bankService.ReconcileAsync(companyId, request);
        return Ok(new ApiResponse<BankTransactionResponse>(true, result, "กระทบยอดสำเร็จ"));
    }

    [HttpGet("accounts/{accountId:guid}/unreconciled")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> GetUnreconciled(Guid companyId, Guid accountId)
    {
        var result = await _bankService.GetUnreconciledAsync(companyId, accountId);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result));
    }

    [HttpPost("accounts/{accountId:guid}/auto-match")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> AutoMatch(Guid companyId, Guid accountId)
    {
        var result = await _bankService.AutoMatchAsync(companyId, accountId);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result, $"จับคู่อัตโนมัติได้ {result.Count} รายการ"));
    }
}
