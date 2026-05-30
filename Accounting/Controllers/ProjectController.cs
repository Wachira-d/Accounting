using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Project;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/projects")]
[Authorize]
public class ProjectController : ControllerBase
{
    private readonly IProjectAccountingService _service;
    public ProjectController(IProjectAccountingService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> Create(Guid companyId, [FromBody] CreateProjectRequest request)
        => StatusCode(201, new ApiResponse<ProjectResponse>(true, await _service.CreateAsync(companyId, request)));

    [HttpGet("{projectId:guid}")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> GetById(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.GetByIdAsync(companyId, projectId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<ProjectResponse>>>> GetAll(Guid companyId, [FromQuery] string? status, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ProjectResponse>>(true, await _service.GetAllAsync(companyId, status, new PagedRequest(page, pageSize, search))));

    [HttpGet("active")]
    public async Task<ActionResult<ApiResponse<List<ProjectResponse>>>> GetActive(Guid companyId)
        => Ok(new ApiResponse<List<ProjectResponse>>(true, await _service.GetActiveListAsync(companyId)));

    [HttpPut("{projectId:guid}")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> Update(Guid companyId, Guid projectId, [FromBody] UpdateProjectRequest request)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.UpdateAsync(companyId, projectId, request)));

    [HttpPost("{projectId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> Complete(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.CompleteAsync(companyId, projectId)));

    public sealed record ChangeStatusRequest(string Status, string? Reason);

    /// <summary>Generic status transition — Active | OnHold | Completed
    /// | Cancelled. Replaces the limited /complete endpoint with a
    /// uniform surface so partner systems can drive the full lifecycle
    /// from one call.</summary>
    [HttpPost("{projectId:guid}/status")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> ChangeStatus(
        Guid companyId, Guid projectId, [FromBody] ChangeStatusRequest req)
    {
        var allowed = new[] { "Active", "OnHold", "Completed", "Cancelled" };
        if (!allowed.Contains(req.Status))
            return BadRequest(new ApiResponse<object>(false, null,
                $"Status ต้องเป็น Active | OnHold | Completed | Cancelled (received: {req.Status})"));
        var result = await _service.ChangeStatusAsync(companyId, projectId, req.Status, req.Reason);
        return Ok(new ApiResponse<ProjectResponse>(true, result, $"สถานะโปรเจคเปลี่ยนเป็น {req.Status}"));
    }

    public sealed record SyncRequest(string ExternalSystem, string ExternalId,
        string? ExternalUrl, DateTime? LastSyncedAt);

    /// <summary>Attach / update the partner-system identity on a
    /// project. Idempotent — repeated calls with the same
    /// (ExternalSystem, ExternalId) on the SAME project succeed
    /// and bump LastSyncedAt. Used by partner ERPs to mark a
    /// project as "this is our internal job XYZ" so subsequent
    /// webhook callbacks + lookups can resolve back without our GUID.</summary>
    [HttpPost("{projectId:guid}/external-link")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> AttachExternal(
        Guid companyId, Guid projectId, [FromBody] SyncRequest req)
    {
        var result = await _service.AttachExternalAsync(companyId, projectId,
            req.ExternalSystem, req.ExternalId, req.ExternalUrl, req.LastSyncedAt);
        return Ok(new ApiResponse<ProjectResponse>(true, result, "ผูก external id แล้ว"));
    }

    /// <summary>Lookup by the partner's identifier. Lets the partner
    /// system fetch the project they previously created without
    /// having to store our GUID — they just remember their own ID.</summary>
    [HttpGet("by-external/{externalSystem}/{externalId}")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> GetByExternal(
        Guid companyId, string externalSystem, string externalId)
    {
        var result = await _service.GetByExternalAsync(companyId, externalSystem, externalId);
        if (result == null) return NotFound(new ApiResponse<object>(false, null,
            $"ไม่พบโปรเจคที่มี {externalSystem}/{externalId}"));
        return Ok(new ApiResponse<ProjectResponse>(true, result));
    }

    /// <summary>Cash flow statement scoped to ONE project — walks
    /// JournalEntryLine.ProjectId (and JE.ProjectId fallback) to
    /// surface Operating / Investing / Financing per the same
    /// taxonomy as the company-wide cash flow report. Lets PM
    /// answer "is project X bringing in cash or burning it" in
    /// one screen. From/To default to current calendar year.</summary>
    [HttpGet("{projectId:guid}/cash-flow")]
    public async Task<ActionResult<ApiResponse<Accounting.Models.DTOs.CashFlowStatementResponse>>> GetCashFlow(
        Guid companyId, Guid projectId,
        [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        [FromServices] IAccountingService accounting,
        CancellationToken ct)
    {
        var from = fromDate ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
        var to = toDate ?? DateTime.UtcNow.Date;
        var result = await accounting.GetCashFlowStatementAsync(companyId, from, to, projectId);
        return Ok(new ApiResponse<Accounting.Models.DTOs.CashFlowStatementResponse>(true, result,
            $"กระแสเงินสด {from:yyyy-MM-dd} ถึง {to:yyyy-MM-dd}"));
    }

    /// <summary>Aggregated project dashboard payload — P&amp;L + cash
    /// flow + outstanding AR + outstanding AP in ONE call so the
    /// project detail UI can render KPIs without N round trips.</summary>
    [HttpGet("{projectId:guid}/dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboard(
        Guid companyId, Guid projectId,
        [FromServices] IAccountingService accounting,
        [FromServices] Data.AccountingDbContext db,
        CancellationToken ct)
    {
        var project = await _service.GetByIdAsync(companyId, projectId);
        var from = new DateTime(DateTime.UtcNow.Year, 1, 1);
        var to = DateTime.UtcNow.Date;
        var cashFlow = await accounting.GetCashFlowStatementAsync(companyId, from, to, projectId);
        var profit = await _service.GetProfitabilityAsync(companyId, projectId);

        // Outstanding AR/AP — sum balanceDue on Documents with this project.
        var arDue = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ProjectId == projectId
                && (d.DocumentType == Models.Enums.DocumentType.Invoice
                    || d.DocumentType == Models.Enums.DocumentType.TaxInvoice
                    || d.DocumentType == Models.Enums.DocumentType.BillingNote)
                && d.BalanceDue > 0
                && d.Status != Models.Enums.DocumentStatus.Voided)
            .SumAsync(d => d.BalanceDue, ct);
        var apDue = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ProjectId == projectId
                && (d.DocumentType == Models.Enums.DocumentType.PurchaseInvoice
                    || d.DocumentType == Models.Enums.DocumentType.Expense)
                && d.BalanceDue > 0
                && d.Status != Models.Enums.DocumentStatus.Voided)
            .SumAsync(d => d.BalanceDue, ct);

        return Ok(new ApiResponse<object>(true, new
        {
            project,
            profit,
            cashFlow = new
            {
                operating = cashFlow.NetCashFromOperating,
                investing = cashFlow.NetCashFromInvesting,
                financing = cashFlow.NetCashFromFinancing,
                netChange = cashFlow.NetChangeInCash,
                period = new { from, to },
            },
            outstanding = new
            {
                receivable = arDue,
                payable = apDue,
                net = arDue - apDue,
            },
        }));
    }

    [HttpDelete("{projectId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid projectId)
    {
        await _service.DeleteAsync(companyId, projectId);
        return Ok(new ApiResponse<string>(true, null, "ลบโครงการสำเร็จ"));
    }

    // Tasks
    [HttpPost("{projectId:guid}/tasks")]
    public async Task<ActionResult<ApiResponse<ProjectTaskResponse>>> CreateTask(Guid companyId, Guid projectId, [FromBody] CreateProjectTaskRequest request)
        => StatusCode(201, new ApiResponse<ProjectTaskResponse>(true, await _service.CreateTaskAsync(companyId, projectId, request)));

    [HttpGet("{projectId:guid}/tasks")]
    public async Task<ActionResult<ApiResponse<List<ProjectTaskResponse>>>> GetTasks(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<List<ProjectTaskResponse>>(true, await _service.GetTasksAsync(companyId, projectId)));

    [HttpPut("tasks/{taskId:guid}")]
    public async Task<ActionResult<ApiResponse<ProjectTaskResponse>>> UpdateTask(Guid companyId, Guid taskId, [FromBody] UpdateProjectTaskRequest request)
        => Ok(new ApiResponse<ProjectTaskResponse>(true, await _service.UpdateTaskAsync(companyId, taskId, request)));

    [HttpDelete("tasks/{taskId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteTask(Guid companyId, Guid taskId)
    {
        await _service.DeleteTaskAsync(companyId, taskId);
        return Ok(new ApiResponse<string>(true, null, "ลบงานสำเร็จ"));
    }

    // Cost entries
    [HttpPost("{projectId:guid}/costs")]
    public async Task<ActionResult<ApiResponse<ProjectCostEntryResponse>>> AddCost(Guid companyId, Guid projectId, [FromBody] CreateProjectCostEntryRequest request)
        => StatusCode(201, new ApiResponse<ProjectCostEntryResponse>(true, await _service.AddCostEntryAsync(companyId, projectId, request)));

    [HttpGet("{projectId:guid}/costs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ProjectCostEntryResponse>>>> GetCosts(Guid companyId, Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ProjectCostEntryResponse>>(true, await _service.GetCostEntriesAsync(companyId, projectId, new PagedRequest(page, pageSize))));

    [HttpDelete("costs/{costEntryId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> DeleteCost(Guid companyId, Guid costEntryId)
    {
        await _service.DeleteCostEntryAsync(companyId, costEntryId);
        return Ok(new ApiResponse<string>(true, null, "ลบรายการต้นทุนสำเร็จ"));
    }

    // Reports
    [HttpGet("{projectId:guid}/profitability")]
    public async Task<ActionResult<ApiResponse<ProjectProfitabilityResponse>>> GetProfitability(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectProfitabilityResponse>(true, await _service.GetProfitabilityAsync(companyId, projectId)));

    [HttpGet("{projectId:guid}/gl-summary")]
    public async Task<ActionResult<ApiResponse<ProjectGlSummaryResponse>>> GetGlSummary(Guid companyId, Guid projectId, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
        => Ok(new ApiResponse<ProjectGlSummaryResponse>(true, await _service.GetGlSummaryAsync(companyId, projectId, fromDate, toDate)));

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<List<ProjectSummaryResponse>>>> GetSummary(Guid companyId)
        => Ok(new ApiResponse<List<ProjectSummaryResponse>>(true, await _service.GetProjectSummaryAsync(companyId)));
}
