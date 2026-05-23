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
    private readonly ICompanyService _companyService;

    public AccountingController(IAccountingService accountingService, ICompanyService companyService)
    {
        _accountingService = accountingService;
        _companyService = companyService;
    }

    // ===== Chart of Accounts =====

    [HttpGet("accounts")]
    public async Task<ActionResult<ApiResponse<List<AccountResponse>>>> GetAccounts(
        Guid companyId, [FromQuery] AccountType? type = null)
    {
        var result = await _accountingService.GetAccountsAsync(companyId, type);
        return Ok(new ApiResponse<List<AccountResponse>>(true, result));
    }

    [HttpGet("accounts/payment-channels")]
    public async Task<ActionResult<ApiResponse<List<AccountResponse>>>> GetPaymentChannelAccounts(Guid companyId)
    {
        var result = await _accountingService.GetPaymentChannelAccountsAsync(companyId);
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
    public async Task<ActionResult<ApiResponse<object>>> PreviewAccountTemplate([FromQuery] BusinessType businessType = BusinessType.JuristicPerson, [FromQuery] IndustryType industryType = IndustryType.General)
    {
        // Reflects admin customisation of the master template — identical to
        // what SeedDefaultAccountsAsync will actually create.
        var templates = await _accountingService.GetSeedTemplatePreviewAsync(businessType, industryType);
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
        [FromQuery] string? journalType = null, [FromQuery] Guid? dimensionId = null, [FromQuery] Guid? branchId = null,
        [FromQuery] Guid? projectId = null, [FromQuery] string? tag = null,
        [FromQuery] Guid? sourceDocumentId = null, [FromQuery] string? sourceDocumentNumber = null)
    {
        var result = await _accountingService.GetJournalEntriesAsync(companyId, new PagedRequest(page, pageSize, search), status, fromDate, toDate, journalType, dimensionId, branchId, projectId, tag, sourceDocumentId, sourceDocumentNumber);
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

    [HttpPut("journals/{entryId:guid}")]
    public async Task<ActionResult<ApiResponse<JournalEntryResponse>>> UpdateJournalEntry(
        Guid companyId, Guid entryId, [FromBody] UpdateJournalEntryRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _accountingService.UpdateJournalEntryAsync(companyId, entryId, request, userId);
        return Ok(new ApiResponse<JournalEntryResponse>(true, result, "อัปเดตใบสำคัญสำเร็จ"));
    }

    [HttpPost("journals/{entryId:guid}/void")]
    public async Task<ActionResult<ApiResponse<string>>> VoidJournalEntry(Guid companyId, Guid entryId)
    {
        await _accountingService.VoidJournalEntryAsync(companyId, entryId);
        return Ok(new ApiResponse<string>(true, null, "Void ใบสำคัญสำเร็จ"));
    }

    [HttpDelete("journals/{entryId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteJournalEntry(Guid companyId, Guid entryId)
    {
        await _accountingService.DeleteJournalEntryAsync(companyId, entryId);
        return Ok(new ApiResponse<string>(true, null, "ลบใบสำคัญสำเร็จ"));
    }

    [HttpPost("journals/{entryId:guid}/reverse")]
    public async Task<ActionResult<ApiResponse<JournalEntryResponse>>> ReverseJournalEntry(
        Guid companyId, Guid entryId, [FromBody] ReverseJournalEntryRequest? request = null)
    {
        var result = await _accountingService.ReverseJournalEntryAsync(companyId, entryId, request?.ReversalDate, request?.Description);
        return Ok(new ApiResponse<JournalEntryResponse>(true, result, "กลับรายการสำเร็จ"));
    }

    [HttpPost("journals/{entryId:guid}/correct")]
    public async Task<ActionResult<ApiResponse<CorrectJournalEntryResponse>>> CorrectJournalEntry(
        Guid companyId, Guid entryId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var result = await _accountingService.CorrectJournalEntryAsync(companyId, entryId, userId.ToString());
        return Ok(new ApiResponse<CorrectJournalEntryResponse>(true, result, "กลับรายการและสร้าง Draft ใหม่สำเร็จ"));
    }

    [HttpPost("journals/batch-void")]
    public async Task<ActionResult<ApiResponse<string>>> BatchVoidJournalEntries(
        Guid companyId, [FromBody] BatchVoidRequest request)
    {
        var count = await _accountingService.BatchVoidJournalEntriesAsync(companyId, request.EntryIds);
        return Ok(new ApiResponse<string>(true, null, $"ยกเลิกสำเร็จ {count} รายการ"));
    }

    [HttpPost("journals/batch-delete")]
    public async Task<ActionResult<ApiResponse<string>>> BatchDeleteJournalEntries(
        Guid companyId, [FromBody] BatchVoidRequest request)
    {
        var count = await _accountingService.BatchDeleteJournalEntriesAsync(companyId, request.EntryIds);
        return Ok(new ApiResponse<string>(true, null, $"ลบสำเร็จ {count} รายการ"));
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
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate, [FromQuery] Guid? accountId = null,
        [FromQuery] Guid? dimensionId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? projectId = null)
    {
        var result = await _accountingService.GetGeneralLedgerAsync(companyId, fromDate, toDate, accountId, dimensionId, branchId, projectId);
        return Ok(new ApiResponse<GeneralLedgerResponse>(true, result));
    }

    [HttpGet("reports/gl-debug")]
    public async Task<ActionResult<ApiResponse<object>>> GetGlDebug(
        Guid companyId, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        var debug = await _accountingService.GetGlDebugAsync(companyId, fromDate, toDate);
        return Ok(new ApiResponse<object>(true, debug));
    }

    [HttpPost("reports/rebuild-lines")]
    public async Task<ActionResult<ApiResponse<string>>> RebuildMissingLines(Guid companyId)
    {
        var count = await _accountingService.RebuildMissingLinesAsync(companyId);
        return Ok(new ApiResponse<string>(true, null, $"สร้างรายการย่อยใหม่สำเร็จ {count} ใบสำคัญ"));
    }

    [HttpPost("reports/repair-dates")]
    public async Task<ActionResult<ApiResponse<string>>> RepairBuddhistDates(Guid companyId)
    {
        var count = await _accountingService.RepairBuddhistDatesAsync(companyId);
        return Ok(new ApiResponse<string>(true, null, $"แก้ไขวันที่สำเร็จ {count} รายการ"));
    }

    // ===== Reports =====

    [HttpGet("reports/trial-balance")]
    public async Task<ActionResult<ApiResponse<TrialBalanceResponse>>> GetTrialBalance(
        Guid companyId, [FromQuery] DateTime? asOfDate = null,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        var result = await _accountingService.GetTrialBalanceAsync(companyId, asOfDate ?? DateTime.UtcNow, projectId, branchId, dimensionId);
        return Ok(new ApiResponse<TrialBalanceResponse>(true, result));
    }

    [HttpGet("reports/balance-sheet")]
    public async Task<ActionResult<ApiResponse<BalanceSheetResponse>>> GetBalanceSheet(
        Guid companyId, [FromQuery] DateTime? asOfDate = null,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        var result = await _accountingService.GetBalanceSheetAsync(companyId, asOfDate ?? DateTime.UtcNow, projectId, branchId, dimensionId);
        return Ok(new ApiResponse<BalanceSheetResponse>(true, result));
    }

    [HttpGet("reports/profit-loss")]
    public async Task<ActionResult<ApiResponse<ProfitAndLossResponse>>> GetProfitAndLoss(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        var result = await _accountingService.GetProfitAndLossAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId);
        return Ok(new ApiResponse<ProfitAndLossResponse>(true, result));
    }

    [HttpGet("reports/cash-flow")]
    public async Task<ActionResult<ApiResponse<CashFlowStatementResponse>>> GetCashFlowStatement(
        Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        var result = await _accountingService.GetCashFlowStatementAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId);
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

    /// <summary>สร้างงวดบัญชีรายเดือนที่ขาดของทั้งปีในคราวเดียว</summary>
    [HttpPost("fiscal-periods/ensure-year")]
    public async Task<ActionResult<ApiResponse<object>>> EnsureFiscalYear(Guid companyId, [FromQuery] int year)
    {
        var created = await _accountingService.EnsureFiscalYearPeriodsAsync(companyId, year);
        return Ok(new ApiResponse<object>(true, new { year, created },
            created > 0 ? $"สร้างงวดบัญชีปี {year} เพิ่ม {created} งวด" : $"งวดบัญชีปี {year} ครบอยู่แล้ว"));
    }

    [HttpPost("fiscal-periods/{periodId:guid}/close")]
    public async Task<ActionResult<ApiResponse<string>>> CloseFiscalPeriod(Guid companyId, Guid periodId)
    {
        await _accountingService.CloseFiscalPeriodAsync(companyId, periodId);
        return Ok(new ApiResponse<string>(true, null, "ปิดงวดบัญชีสำเร็จ"));
    }

    /// <summary>Edit a fiscal period's year/month/start/end dates — for
    /// fixing accidentally-mis-typed values. Allowed only on Open periods
    /// with no posted data inside the old or new window.</summary>
    [HttpPut("fiscal-periods/{periodId:guid}")]
    public async Task<ActionResult<ApiResponse<FiscalPeriodResponse>>> UpdateFiscalPeriod(
        Guid companyId, Guid periodId, [FromBody] CreateFiscalPeriodRequest request)
    {
        var result = await _accountingService.UpdateFiscalPeriodAsync(companyId, periodId, request);
        return Ok(new ApiResponse<FiscalPeriodResponse>(true, result, "แก้ไขงวดบัญชีสำเร็จ"));
    }

    /// <summary>Delete an Open fiscal period that has no JEs/openings yet
    /// (e.g. one accidentally created with the wrong dates).</summary>
    [HttpDelete("fiscal-periods/{periodId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteFiscalPeriod(Guid companyId, Guid periodId)
    {
        await _accountingService.DeleteFiscalPeriodAsync(companyId, periodId);
        return Ok(new ApiResponse<string>(true, null, "ลบงวดบัญชีสำเร็จ"));
    }

    // ===== Year-End Close + Soft/Hard Close (Task 1 ERP upgrade) =====

    /// <summary>Soft close — flips period to Closed but admin can reopen.</summary>
    [HttpPost("fiscal-periods/{periodId:guid}/soft-close")]
    public async Task<ActionResult<ApiResponse<object>>> SoftClosePeriod(Guid companyId, Guid periodId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        var period = await _accountingService.SoftClosePeriodAsync(companyId, periodId, userId.ToString());
        return Ok(new ApiResponse<object>(true, new { period.Id, period.Status, period.ClosedAt }, "Soft-close งวดบัญชีสำเร็จ"));
    }

    /// <summary>Re-open a soft-closed period (cannot reopen Locked).</summary>
    [HttpPost("fiscal-periods/{periodId:guid}/reopen")]
    public async Task<ActionResult<ApiResponse<object>>> ReopenPeriod(Guid companyId, Guid periodId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        var period = await _accountingService.ReopenPeriodAsync(companyId, periodId, userId.ToString());
        return Ok(new ApiResponse<object>(true, new { period.Id, period.Status }, "เปิดงวดบัญชีอีกครั้งสำเร็จ"));
    }

    public record YearEndCloseRequest(int FiscalYear, Guid RetainedEarningsAccountId, DateTime? ClosingDate);

    /// <summary>
    /// Hard close: year-end — auto-generates the P&amp;L → Retained Earnings
    /// transfer journal entry, locks every month of the year, then rolls
    /// Asset/Liability/Equity ending balances to January of the next year
    /// as OpeningBalance rows.
    /// </summary>
    [HttpPost("year-end-close")]
    public async Task<ActionResult<ApiResponse<object>>> YearEndClose(Guid companyId, [FromBody] YearEndCloseRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        var result = await _accountingService.YearEndCloseAsync(companyId, request.FiscalYear, request.RetainedEarningsAccountId, request.ClosingDate, userId.ToString());
        return Ok(new ApiResponse<object>(true,
            new { result.Id, result.FiscalYear, result.TransferredAmount, result.ClosingJournalEntryId },
            $"ปิดงบประจำปี {request.FiscalYear} สำเร็จ — โอน {result.TransferredAmount:N2} ไป Retained Earnings"));
    }

    public record RollOpeningBalancesRequest(int Year);

    /// <summary>Re-run the opening-balance roll-over for a given target year
    /// (idempotent — overwrites existing OpeningBalance rows for January).</summary>
    [HttpPost("roll-opening-balances")]
    public async Task<ActionResult<ApiResponse<int>>> RollOpeningBalances(Guid companyId, [FromBody] RollOpeningBalancesRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _companyService.EnsureOwnerAccessAsync(companyId, userId);
        var count = await _accountingService.RollOpeningBalancesAsync(companyId, request.Year, userId.ToString());
        return Ok(new ApiResponse<int>(true, count, $"อัพเดต Opening Balance {count} บัญชี"));
    }
}
