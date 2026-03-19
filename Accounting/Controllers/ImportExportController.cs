using System.Security.Claims;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Import;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/import-export")]
[Authorize]
public class ImportExportController : ControllerBase
{
    private readonly IImportExportService _importExportService;

    public ImportExportController(IImportExportService importExportService)
    {
        _importExportService = importExportService;
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Import(
        Guid companyId, [FromBody] ImportRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
        var result = await _importExportService.ImportAsync(companyId, request, userId);
        return Ok(new ApiResponse<ImportResult>(true, result,
            $"นำเข้าสำเร็จ {result.SuccessCount}/{result.TotalRows} รายการ"));
    }

    [HttpPost("validate")]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Validate(
        Guid companyId, [FromBody] ImportRequest request)
    {
        var result = await _importExportService.ValidateImportAsync(companyId, request);
        return Ok(new ApiResponse<ImportResult>(true, result));
    }

    [HttpGet("templates/{entityType}")]
    public async Task<ActionResult<ApiResponse<ImportTemplateResponse>>> GetTemplate(Guid companyId, string entityType)
    {
        var result = await _importExportService.GetImportTemplateAsync(entityType);
        return Ok(new ApiResponse<ImportTemplateResponse>(true, result));
    }

    [HttpPost("export")]
    public async Task<ActionResult> Export(Guid companyId, [FromBody] ExportRequest request)
    {
        var result = await _importExportService.ExportAsync(companyId, request);
        return File(result.Data, result.ContentType, result.FileName);
    }

    [HttpGet("exportable-entities")]
    public async Task<ActionResult<ApiResponse<List<string>>>> GetExportableEntities(Guid companyId)
    {
        var result = await _importExportService.GetExportableEntitiesAsync();
        return Ok(new ApiResponse<List<string>>(true, result));
    }
}
