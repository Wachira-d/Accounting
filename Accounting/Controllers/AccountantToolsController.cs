using Accounting.Models.DTOs;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// Endpoints that power the accountant-tools UI pages — sub-ledger
/// reconciliation, pre-close checklist, document completeness check,
/// global search. Read-only / aggregate operations, no state mutation.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/accountant")]
[Authorize]
public class AccountantToolsController : ControllerBase
{
    private readonly SubLedgerReconciliationService _subRecon;
    private readonly PreCloseChecklistService _preClose;
    private readonly DocumentCompletenessService _docComplete;
    private readonly GlobalSearchService _search;

    public AccountantToolsController(
        SubLedgerReconciliationService subRecon,
        PreCloseChecklistService preClose,
        DocumentCompletenessService docComplete,
        GlobalSearchService search)
    {
        _subRecon = subRecon;
        _preClose = preClose;
        _docComplete = docComplete;
        _search = search;
    }

    /// <summary>Sub-Ledger ↔ GL reconciliation across AR / AP / Inventory /
    /// Fixed Assets / Cash / Bank for a given as-of date.</summary>
    [HttpGet("sub-ledger-recon")]
    public async Task<ActionResult<ApiResponse<SubLedgerReconciliationService.ReconResult>>> SubLedgerRecon(
        Guid companyId, [FromQuery] DateTime? asOf = null, [FromQuery] decimal tolerance = 0.01m)
    {
        var result = await _subRecon.ReconcileAsync(companyId, asOf ?? DateTime.UtcNow.Date, tolerance);
        return Ok(new ApiResponse<SubLedgerReconciliationService.ReconResult>(true, result));
    }

    /// <summary>Pre-close checklist for a given month — aggregates 8
    /// red/green checks the accountant must verify before closing.</summary>
    [HttpGet("pre-close-checklist")]
    public async Task<ActionResult<ApiResponse<PreCloseChecklistService.ChecklistResult>>> PreCloseChecklist(
        Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _preClose.RunAsync(companyId, year, month);
        return Ok(new ApiResponse<PreCloseChecklistService.ChecklistResult>(true, result));
    }

    /// <summary>Sequential gap detection per document type + missing-JE
    /// audit for the given period.</summary>
    [HttpGet("document-completeness")]
    public async Task<ActionResult<ApiResponse<DocumentCompletenessService.CompletenessResult>>> DocCompleteness(
        Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var result = await _docComplete.AnalyzeAsync(companyId, year, month);
        return Ok(new ApiResponse<DocumentCompletenessService.CompletenessResult>(true, result));
    }

    /// <summary>Global search across documents / journal entries / contacts.
    /// One unified endpoint for the header search bar.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<ApiResponse<GlobalSearchService.SearchResult>>> Search(
        Guid companyId, [FromQuery] string q, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(new ApiResponse<GlobalSearchService.SearchResult>(true,
                new GlobalSearchService.SearchResult(new(), new(), new(), 0)));
        var result = await _search.SearchAsync(companyId, q.Trim(), Math.Min(Math.Max(limit, 5), 50));
        return Ok(new ApiResponse<GlobalSearchService.SearchResult>(true, result));
    }
}
