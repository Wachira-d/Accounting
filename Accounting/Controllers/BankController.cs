using Accounting.Helpers;
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
    // Default Kestrel body limit is 30MB. A multi-month .xlsx that's base64-
    // encoded inside the JSON body inflates ~33% so the practical CSV/Excel
    // file ceiling is ~22MB without this override. Bumping to 100MB covers
    // even year-long exports comfortably.
    [RequestSizeLimit(100_000_000)]
    public async Task<ActionResult<ApiResponse<ImportBankStatementResponse>>> ImportStatement(
        Guid companyId, [FromBody] ImportBankStatementRequest request)
    {
        var result = await _bankService.ImportBankStatementAsync(companyId, request);
        if (result.Conflicts > 0)
            return Ok(new ApiResponse<ImportBankStatementResponse>(true, result,
                $"พบรายการซ้ำ {result.Conflicts} รายการ กรุณาเลือกใช้ข้อมูลเก่าหรือใหม่"));
        var msg = $"นำเข้า {result.Imported} รายการสำเร็จ";
        if (result.Skipped > 0) msg += $" (ข้าม {result.Skipped} รายการซ้ำ)";
        return Ok(new ApiResponse<ImportBankStatementResponse>(true, result, msg));
    }

    [HttpPost("accounts/{accountId:guid}/ai-match")]
    public async Task<ActionResult<ApiResponse<AiReconciliationResult>>> AiSmartMatch(
        Guid companyId, Guid accountId, [FromBody] AiReconciliationRequest? request)
    {
        var result = await _bankService.AiSmartMatchAsync(companyId, accountId, request ?? new());
        return Ok(new ApiResponse<AiReconciliationResult>(true, result,
            $"AI วิเคราะห์เสร็จ: พบ {result.SuggestionsFound} คู่ที่แนะนำ"));
    }

    /// <summary>One-shot bulk reconciliation — bundles a whole month
    /// of unmatched bank txns + every open AR/AP/JE/Payment + company
    /// context into a single AI call. Returns a complete match plan
    /// (1:1, M:1, 1:M), unmatched list with reasons, and "missing
    /// data" hints for memos that cite non-existent doc numbers.
    ///
    /// One button → one decision per line — user accepts, rejects, or
    /// edits each proposed match in the UI; bulk-confirmed matches are
    /// then applied via /batch-reconcile so the existing atomic write
    /// path stays the single source of truth.</summary>
    [HttpPost("accounts/{accountId:guid}/bulk-ai-match")]
    public async Task<ActionResult<ApiResponse<Services.Implementations.Bank.BulkAiMatchPlan>>> BulkAiMatch(
        Guid companyId, Guid accountId,
        [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromServices] Services.Implementations.Bank.IBulkBankAiMatchService bulk,
        CancellationToken ct)
    {
        if (toDate < fromDate)
            return BadRequest(new ApiResponse<object>(false, null, "toDate ต้องอยู่หลัง fromDate"));
        if ((toDate - fromDate).TotalDays > 92)
            return BadRequest(new ApiResponse<object>(false, null, "ช่วงเวลาเกิน 92 วัน — กรุณาแบ่งเป็นช่วงสั้นกว่า"));

        var plan = await bulk.ProposeAsync(companyId, accountId, fromDate, toDate, ct);
        var msg = plan.Status switch
        {
            "Success" => $"AI เสนอ match {plan.Matches.Count} รายการ, ไม่ตรง {plan.Unmatched.Count}, ขาดข้อมูล {plan.MissingData.Count}",
            "Truncated" => $"ข้อมูลเยอะ — ส่งให้ AI แค่บางส่วน. AI เสนอ match {plan.Matches.Count} รายการ",
            "AiUnavailable" => "AI ใช้งานไม่ได้ — กรุณา match ด้วยมือ",
            _ => plan.Warnings.FirstOrDefault() ?? "ไม่มีข้อมูลให้ match",
        };
        return Ok(new ApiResponse<Services.Implementations.Bank.BulkAiMatchPlan>(true, plan, msg));
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

    // ===== M:N reconciliation + net-off (Receipt − PaymentVoucher = bank line) =====

    /// <summary>
    /// Create a reconciliation group that binds N bank transactions to M match items
    /// (Payments / Journal Entries / Documents). Each side carries a signed
    /// AllocatedAmount so net-off cases (เช่น Receipt + Payment Voucher = ยอดธนาคาร
    /// รายการเดียว) work without splitting into two reconciliations.
    /// </summary>
    [HttpPost("reconciliation-groups")]
    public async Task<ActionResult<ApiResponse<ReconciliationGroupResponse>>> CreateReconciliationGroup(
        Guid companyId, [FromBody] CreateReconciliationGroupRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _bankService.CreateReconciliationGroupAsync(companyId, request, userId);
        return Ok(new ApiResponse<ReconciliationGroupResponse>(true, result,
            result.IsBalanced ? "กระทบยอดสำเร็จ" : "บันทึกกลุ่มแล้ว — ยังไม่สมดุล กรุณาตรวจสอบ"));
    }

    [HttpGet("reconciliation-groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<ReconciliationGroupResponse>>> GetReconciliationGroup(
        Guid companyId, Guid groupId)
    {
        var result = await _bankService.GetReconciliationGroupAsync(companyId, groupId);
        return Ok(new ApiResponse<ReconciliationGroupResponse>(true, result));
    }

    [HttpGet("accounts/{accountId:guid}/reconciliation-groups")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ReconciliationGroupListItem>>>> ListReconciliationGroups(
        Guid companyId, Guid accountId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _bankService.GetReconciliationGroupsAsync(companyId, accountId, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<ReconciliationGroupListItem>>(true, result));
    }

    [HttpDelete("reconciliation-groups/{groupId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> UnreconcileGroup(Guid companyId, Guid groupId)
    {
        await _bankService.UnreconcileGroupAsync(companyId, groupId);
        return Ok(new ApiResponse<string>(true, null, "ยกเลิกกลุ่มกระทบยอดสำเร็จ"));
    }

    /// <summary>
    /// Full pool of unmatched items (Payment / JournalEntry / Document) for an
    /// account — feeds the M:N workbench so the operator can see everything,
    /// not just candidates near a single bank transaction. Optional date range
    /// (defaults to trailing 6 months) and free-text filter.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/unmatched-items")]
    public async Task<ActionResult<ApiResponse<UnmatchedItemsResponse>>> GetUnmatchedItems(
        Guid companyId, Guid accountId,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var result = await _bankService.GetUnmatchedItemsAsync(companyId, accountId, search, fromDate, toDate);
        return Ok(new ApiResponse<UnmatchedItemsResponse>(true, result));
    }

    /// <summary>
    /// AI suggestions backed by learned patterns — for an unmatched bank
    /// transaction, returns items ranked by the company's historical
    /// reconciliation history. Confidence 0..1 with a human reason string.
    /// Complements the heuristic AI suggest endpoint (amount/date/payee
    /// scoring) by leaning on what the operator has actually confirmed in
    /// the past.
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}/learned-suggestions")]
    public async Task<ActionResult<ApiResponse<LearnedSuggestionsResponse>>> GetLearnedSuggestions(
        Guid companyId, Guid transactionId)
    {
        var result = await _bankService.GetLearnedSuggestionsAsync(companyId, transactionId);
        return Ok(new ApiResponse<LearnedSuggestionsResponse>(true, result));
    }

    /// <summary>
    /// Excel export of reconciliation state — Transactions / Groups / Group Items / Summary.
    /// Date range defaults to last 3 months when not specified.
    /// </summary>
    [HttpGet("accounts/{accountId:guid}/reconciliation-report.xlsx")]
    public async Task<IActionResult> ExportReconciliationReport(
        Guid companyId, Guid accountId,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        var bytes = await _bankService.ExportReconciliationReportAsync(companyId, accountId, fromDate, toDate);
        var fileName = $"reconciliation-{accountId:N}-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
