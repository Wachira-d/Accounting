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
        return StatusCode(201, new ApiResponse<BankAccountResponse>(true, result, "สร้างบัญชีธนาคารสำเร็จ"));
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
        return StatusCode(201, new ApiResponse<BankTransactionResponse>(true, result));
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

    [HttpPost("import-statement")]
    public async Task<ActionResult<ApiResponse<int>>> ImportStatement(
        Guid companyId, [FromBody] ImportBankStatementRequest request)
    {
        var count = await _bankService.ImportBankStatementAsync(companyId, request);
        return Ok(new ApiResponse<int>(true, count, $"นำเข้า {count} รายการสำเร็จ"));
    }

    [HttpPost("accounts/{accountId:guid}/ai-match")]
    public async Task<ActionResult<ApiResponse<AiReconciliationResult>>> AiSmartMatch(
        Guid companyId, Guid accountId, [FromBody] AiReconciliationRequest? request)
    {
        var result = await _bankService.AiSmartMatchAsync(companyId, accountId, request ?? new());
        return Ok(new ApiResponse<AiReconciliationResult>(true, result,
            $"AI วิเคราะห์เสร็จ: พบ {result.SuggestionsFound} คู่ที่แนะนำ"));
    }

    [HttpGet("accounts/{accountId:guid}/reconciliation-summary")]
    public async Task<ActionResult<ApiResponse<ReconciliationSummaryDto>>> GetReconciliationSummary(
        Guid companyId, Guid accountId)
    {
        var result = await _bankService.GetReconciliationSummaryAsync(companyId, accountId);
        return Ok(new ApiResponse<ReconciliationSummaryDto>(true, result));
    }

    [HttpPost("batch-reconcile")]
    public async Task<ActionResult<ApiResponse<List<BankTransactionResponse>>>> BatchReconcile(
        Guid companyId, [FromBody] BatchReconcileRequest request)
    {
        var result = await _bankService.BatchReconcileAsync(companyId, request);
        return Ok(new ApiResponse<List<BankTransactionResponse>>(true, result,
            $"จับคู่สำเร็จ {result.Count} รายการ"));
    }

    [HttpPost("unmatch")]
    public async Task<ActionResult<ApiResponse<BankTransactionResponse>>> UnmatchTransaction(
        Guid companyId, [FromBody] UnmatchRequest request)
    {
        var result = await _bankService.UnmatchTransactionAsync(companyId, request);
        return Ok(new ApiResponse<BankTransactionResponse>(true, result, "ยกเลิกการจับคู่สำเร็จ"));
    }

    /// <summary>
    /// List ranked match candidates (Payments + JournalEntries) for a bank transaction.
    /// Used by the manual reconciliation picker so the user can choose from a list
    /// instead of typing UUIDs.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/match-candidates")]
    public async Task<ActionResult<ApiResponse<MatchCandidatesResponse>>> GetMatchCandidates(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.GetMatchCandidatesAsync(companyId, transactionId);
        return Ok(new ApiResponse<MatchCandidatesResponse>(true, result));
    }

    /// <summary>
    /// AI auto-suggest a single or many-to-one match. Picks the subset of payments or
    /// journal entries whose amounts sum exactly to the bank transaction's amount.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/ai-suggest-match")]
    public async Task<ActionResult<ApiResponse<AiMatchSuggestionResponse>>> AiSuggestMatch(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.SuggestMatchAsync(companyId, transactionId);
        return Ok(new ApiResponse<AiMatchSuggestionResponse>(true, result, result.Message));
    }

    [HttpDelete("transactions/{transactionId:guid}")]
    public async Task<ActionResult<ApiResponse<int>>> DeleteTransaction(Guid companyId, Guid transactionId)
    {
        var count = await _bankService.DeleteTransactionAsync(companyId, transactionId);
        return Ok(new ApiResponse<int>(true, count, "ลบรายการสำเร็จ"));
    }

    /// <summary>
    /// Bulk delete transactions — by ID list, or by bank account + date range.
    /// Reconciled transactions are skipped unless DeleteReconciled=true.
    /// </summary>
    [HttpPost("transactions/bulk-delete")]
    public async Task<ActionResult<ApiResponse<int>>> BulkDeleteTransactions(
        Guid companyId, [FromBody] DeleteTransactionsRequest request)
    {
        var count = await _bankService.DeleteTransactionsAsync(companyId, request);
        return Ok(new ApiResponse<int>(true, count, $"ลบ {count} รายการสำเร็จ"));
    }
}
