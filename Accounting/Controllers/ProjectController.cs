using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.CreateAsync(companyId, request)));

    [HttpGet("{projectId:guid}")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> GetById(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.GetByIdAsync(companyId, projectId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<ProjectResponse>>>> GetAll(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ProjectResponse>>(true, await _service.GetAllAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPut("{projectId:guid}")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> Update(Guid companyId, Guid projectId, [FromBody] UpdateProjectRequest request)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.UpdateAsync(companyId, projectId, request)));

    [HttpPost("{projectId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<ProjectResponse>>> Complete(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectResponse>(true, await _service.CompleteAsync(companyId, projectId)));

    // Tasks
    [HttpPost("{projectId:guid}/tasks")]
    public async Task<ActionResult<ApiResponse<ProjectTaskResponse>>> CreateTask(Guid companyId, Guid projectId, [FromBody] CreateProjectTaskRequest request)
        => Ok(new ApiResponse<ProjectTaskResponse>(true, await _service.CreateTaskAsync(companyId, projectId, request)));

    [HttpGet("{projectId:guid}/tasks")]
    public async Task<ActionResult<ApiResponse<List<ProjectTaskResponse>>>> GetTasks(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<List<ProjectTaskResponse>>(true, await _service.GetTasksAsync(companyId, projectId)));

    // Cost entries
    [HttpPost("{projectId:guid}/costs")]
    public async Task<ActionResult<ApiResponse<ProjectCostEntryResponse>>> AddCost(Guid companyId, Guid projectId, [FromBody] CreateProjectCostEntryRequest request)
        => Ok(new ApiResponse<ProjectCostEntryResponse>(true, await _service.AddCostEntryAsync(companyId, projectId, request)));

    [HttpGet("{projectId:guid}/costs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<ProjectCostEntryResponse>>>> GetCosts(Guid companyId, Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<ProjectCostEntryResponse>>(true, await _service.GetCostEntriesAsync(companyId, projectId, new PagedRequest(page, pageSize))));

    // Reports
    [HttpGet("{projectId:guid}/profitability")]
    public async Task<ActionResult<ApiResponse<ProjectProfitabilityResponse>>> GetProfitability(Guid companyId, Guid projectId)
        => Ok(new ApiResponse<ProjectProfitabilityResponse>(true, await _service.GetProfitabilityAsync(companyId, projectId)));

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<List<ProjectSummaryResponse>>>> GetSummary(Guid companyId)
        => Ok(new ApiResponse<List<ProjectSummaryResponse>>(true, await _service.GetProjectSummaryAsync(companyId)));
}
