using Accounting.Models.DTOs;
using Accounting.Models.DTOs.ReportBuilder;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/reports")]
[Authorize]
public class ReportBuilderController : ControllerBase
{
    private readonly IReportBuilderService _service;
    public ReportBuilderController(IReportBuilderService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CustomReportResponse>>> Create(Guid companyId, [FromBody] CreateCustomReportRequest request)
        => Ok(new ApiResponse<CustomReportResponse>(true, await _service.CreateAsync(companyId, request, User.Identity?.Name ?? "")));

    [HttpGet("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<CustomReportResponse>>> GetById(Guid companyId, Guid reportId)
        => Ok(new ApiResponse<CustomReportResponse>(true, await _service.GetByIdAsync(companyId, reportId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<CustomReportListResponse>>>> GetAll(Guid companyId, [FromQuery] string? category)
        => Ok(new ApiResponse<List<CustomReportListResponse>>(true, await _service.GetAllAsync(companyId, category)));

    [HttpPut("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<CustomReportResponse>>> Update(Guid companyId, Guid reportId, [FromBody] UpdateCustomReportRequest request)
        => Ok(new ApiResponse<CustomReportResponse>(true, await _service.UpdateAsync(companyId, reportId, request)));

    [HttpDelete("{reportId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid reportId)
    { await _service.DeleteAsync(companyId, reportId); return Ok(new ApiResponse<bool>(true, true)); }

    [HttpPost("{reportId:guid}/duplicate")]
    public async Task<ActionResult<ApiResponse<CustomReportResponse>>> Duplicate(Guid companyId, Guid reportId, [FromQuery] string newName)
        => Ok(new ApiResponse<CustomReportResponse>(true, await _service.DuplicateAsync(companyId, reportId, newName)));

    [HttpPost("{reportId:guid}/execute")]
    public async Task<ActionResult<ApiResponse<ReportExecutionResponse>>> Execute(Guid companyId, Guid reportId, [FromBody] Dictionary<string, string>? parameters = null)
        => Ok(new ApiResponse<ReportExecutionResponse>(true, await _service.ExecuteAsync(companyId, reportId, parameters)));

    [HttpPost("{reportId:guid}/export/{format}")]
    public async Task<ActionResult> Export(Guid companyId, Guid reportId, string format, [FromBody] Dictionary<string, string>? parameters = null)
    {
        var bytes = await _service.ExportAsync(companyId, reportId, format, parameters);
        var contentType = format.ToLower() switch { "pdf" => "application/pdf", "csv" => "text/csv", _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
        return File(bytes, contentType, $"report.{format.ToLower()}");
    }

    [HttpGet("data-sources")]
    public async Task<ActionResult<ApiResponse<List<ReportDataSourceResponse>>>> GetDataSources()
        => Ok(new ApiResponse<List<ReportDataSourceResponse>>(true, await _service.GetDataSourcesAsync()));

    [HttpGet("data-sources/{dataSourceType}/columns")]
    public async Task<ActionResult<ApiResponse<List<ReportColumnDefinition>>>> GetColumns(string dataSourceType)
        => Ok(new ApiResponse<List<ReportColumnDefinition>>(true, await _service.GetColumnsForDataSourceAsync(dataSourceType)));
}
