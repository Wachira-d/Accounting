using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class AccountingController : ControllerBase
{
    private readonly IAccountingService _accountingService;

    public AccountingController(IAccountingService accountingService)
    {
        _accountingService = accountingService;
    }

    // ===== Chart of Accounts =====

    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<AccountResponse>>>> GetAccounts(Guid companyId)
    {
        var result = await _accountingService.GetAccountsAsync(companyId);
        return Ok(new ApiResponse<List<AccountResponse>>(true, result));
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<AccountResponse>>> CreateAccount(Guid companyId, [FromBody] CreateAccountRequest request)
    {
        var result = await _accountingService.CreateAccountAsync(companyId, request);
        return StatusCode(201, new ApiResponse<AccountResponse>(true, result, "สร้างบัญชีสำเร็จ"));
    }

    [HttpPut("accounts/{accountId:guid}")]
    public async Task<ActionResult<ApiResponse<AccountResponse>>> UpdateAccount(Guid companyId, Guid accountId, [FromBody] UpdateAccountRequest request)
    {
        var result = await _accountingService.UpdateAccountAsync(companyId, accountId, request);
        return Ok(new ApiResponse<AccountResponse>(true, result));
    }

    [HttpPost("accounts/seed")]
    public async Task<ActionResult<ApiResponse<string>>> SeedAccounts(Guid companyId, [FromQuery] BusinessType? businessType = null, [FromQuery] IndustryType? industryType = null)
    {
        if (businessType.HasValue)
            await _accountingService.SeedDefaultAccountsAsync(companyId, businessType.Value, industryType ?? IndustryType.General);
        else
            await _accountingService.SeedDefaultAccountsAsync(companyId);

        return Ok(new ApiResponse<string>(true, null, "สร้างผังบัญชีเริ่มต้นสำเร็จ"));
    }

    [HttpGet("accounts/template-preview")]
    public ActionResult<ApiResponse<object>> PreviewAccountTemplate([FromQuery] BusinessType businessType = BusinessType.JuristicPerson, [FromQuery] IndustryType industryType = IndustryType.General)
    {
        var templates = Services.ChartOfAccountTemplates.GetTemplateByBusinessType(businessType, industryType);
        var preview = templates.Select(t => new { t.Code, t.NameTh, t.NameEn, AccountType = t.Type.ToString(), t.Level }).ToList();
        return Ok(new ApiResponse<object>(true, new { totalAccounts = preview.Count, accounts = preview }));
    }

    [HttpGet("business-types")]
    public ActionResult<ApiResponse<object>> GetBusinessTypes()
    {
        var businessTypes = Services.ChartOfAccountTemplates.GetAllBusinessTypes()
            .Select(b => new { Type = b.Type.ToString(), b.NameTh, b.NameEn, b.Description, b.Icon, b.EquityLabel });
        var industryTypes = Services.ChartOfAccountTemplates.GetAllIndustryTypes()
            .Select(i => new { Type = i.Type.ToString(), i.NameTh, i.NameEn, i.Description, i.Icon });
        return Ok(new ApiResponse<object>(true, new { businessTypes, industryTypes }));
    }

    // ===== Journal Entries =====

    [HttpGet("journals")]
    public async Task<ActionResult<ApiResponse<PagedResponse<JournalEntryResponse>>>> GetJournalEntries(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] string? journalType = null)
    {
        var result = await _accountingService.GetJournalEntriesAsync(companyId, new PagedRequest(page, pageSize, search), status, fromDate, toDate, journalType);
        return Ok(new ApiResponse<PagedResponse<JournalEntryResponse>>(true, result));
    }

    [HttpGet("journals/{entryId:guid}")]
    public async Task<ActionResult<ApiResponse<JournalEntryResponse>>> GetJournalEntry(Guid companyId, Guid entryId)
    {
        var result = await _accountingService.GetJournalEntryAsync(companyId, entryId);
        return Ok(new ApiResponse<JournalEntryResponse>(true, result));
    }

    [HttpPost("journals")]
    public async Task<ActionResult<ApiResponse<JournalEntryResponse>>> CreateJournalEntry(Guid companyId, [FromBody] CreateJournalEntryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _accountingService.CreateJournalEntryAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<JournalEntryResponse>(true, result, "สร้างใบสำคัญสำเร็จ"));
    }

    [HttpPost("journals/{entryId:guid}/post")]
    public async Task<ActionResult<ApiResponse<JournalEntryResponse>>> PostJournalEntry(Guid companyId, Guid entryId)
    {
        var result = await _accountingService.PostJournalEntryAsync(companyId, entryId);
        return Ok(new ApiResponse<JournalEntryResponse>(true, result, "Post ใบสำคัญสำเร็จ"));
    }

    [HttpPost("journals/{entryId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidJournalEntry(Guid companyId, Guid entryId)
    {
        await _accountingService.VoidJournalEntryAsync(companyId, entryId);
        return Ok(new ApiResponse<string>(true, null, "Void ใบสำคัญสำเร็จ"));
    }

    [HttpPost("journals/batch-void")]
    public async Task<ActionResult<ApiResponse<string>>> BatchVoidJournalEntries(
        Guid companyId, [FromBody] BatchVoidRequest request)
    {
        var count = await _accountingService.BatchVoidJournalEntriesAsync(companyId, request.EntryIds);
        return Ok(new ApiResponse<string>(true, null, $"ยกเลิกสำเร็จ {count} รายการ"));
    }

    [HttpPost("journals/batch-post")]
    public async Task<ActionResult<ApiResponse<string>>> BatchPostJournalEntries(Guid companyId)
    {
        var count = await _accountingService.BatchPostJournalEntriesAsync(companyId);
        return Ok(new ApiResponse<string>(true, null, $"ผ่านรายการสำเร็จ {count} รายการ"));
    }

    // ===== General Ledger =====

    [HttpGet("reports/general-ledger")]
    public async Task<ActionResult<ApiResponse<GeneralLedgerResponse>>> GetGeneralLedger(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate, [FromQuery] Guid? accountId = null)
    {
        var result = await _accountingService.GetGeneralLedgerAsync(companyId, fromDate, toDate, accountId);
        return Ok(new ApiResponse<GeneralLedgerResponse>(true, result));
    }

    // ===== Reports =====

    [HttpGet("reports/trial-balance")]
    public async Task<ActionResult<ApiResponse<TrialBalanceResponse>>> GetTrialBalance(Guid companyId, [FromQuery] DateTime? asOfDate = null)
    {
        var result = await _accountingService.GetTrialBalanceAsync(companyId, asOfDate ?? DateTime.UtcNow);
        return Ok(new ApiResponse<TrialBalanceResponse>(true, result));
    }

    [HttpGet("reports/balance-sheet")]
    public async Task<ActionResult<ApiResponse<BalanceSheetResponse>>> GetBalanceSheet(Guid companyId, [FromQuery] DateTime? asOfDate = null)
    {
        var result = await _accountingService.GetBalanceSheetAsync(companyId, asOfDate ?? DateTime.UtcNow);
        return Ok(new ApiResponse<BalanceSheetResponse>(true, result));
    }

    [HttpGet("reports/profit-loss")]
    public async Task<ActionResult<ApiResponse<ProfitAndLossResponse>>> GetProfitAndLoss(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
    {
        var result = await _accountingService.GetProfitAndLossAsync(companyId, fromDate, toDate);
        return Ok(new ApiResponse<ProfitAndLossResponse>(true, result));
    }

    [HttpGet("reports/cash-flow")]
    public async Task<ActionResult<ApiResponse<CashFlowStatementResponse>>> GetCashFlowStatement(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
    {
        var result = await _accountingService.GetCashFlowStatementAsync(companyId, fromDate, toDate);
        return Ok(new ApiResponse<CashFlowStatementResponse>(true, result));
    }

    // ===== Fiscal Periods =====

    [HttpGet("fiscal-periods")]
    public async Task<ActionResult<ApiResponse<List<FiscalPeriodResponse>>>> GetFiscalPeriods(Guid companyId)
    {
        var result = await _accountingService.GetFiscalPeriodsAsync(companyId);
        return Ok(new ApiResponse<List<FiscalPeriodResponse>>(true, result));
    }

    [HttpPost("fiscal-periods")]
    public async Task<ActionResult<ApiResponse<FiscalPeriodResponse>>> CreateFiscalPeriod(Guid companyId, [FromBody] CreateFiscalPeriodRequest request)
    {
        var result = await _accountingService.CreateFiscalPeriodAsync(companyId, request);
        return StatusCode(201, new ApiResponse<FiscalPeriodResponse>(true, result, "สร้างงวดบัญชีสำเร็จ"));
    }

    [HttpPost("fiscal-periods/{periodId:guid}/close")]
    public async Task<ActionResult<ApiResponse<string>>> CloseFiscalPeriod(Guid companyId, Guid periodId)
    {
        await _accountingService.CloseFiscalPeriodAsync(companyId, periodId);
        return Ok(new ApiResponse<string>(true, null, "ปิดงวดบัญชีสำเร็จ"));
    }
}
