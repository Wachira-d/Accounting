using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/ocr")]
[Authorize]
public class OcrController : ControllerBase
{
    private readonly IOcrService _service;
    public OcrController(IOcrService service) => _service = service;

    [HttpPost("scan/{fileAttachmentId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> Scan(Guid companyId, Guid fileAttachmentId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.ScanAsync(companyId, fileAttachmentId)));

    [HttpGet("{scanId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> GetResult(Guid companyId, Guid scanId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.GetResultAsync(companyId, scanId)));

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<OcrResultResponse>>>> GetResults(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(new ApiResponse<PagedResponse<OcrResultResponse>>(true, await _service.GetResultsAsync(companyId, status, new PagedRequest(page, pageSize))));

    [HttpPost("{scanId:guid}/create-document")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> CreateDocument(Guid companyId, Guid scanId)
        => StatusCode(201, new ApiResponse<OcrResultResponse>(true, await _service.CreateDocumentFromScanAsync(companyId, scanId, User.Identity?.Name ?? "")));

    [HttpPost("{scanId:guid}/match-contact/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<OcrResultResponse>>> MatchContact(Guid companyId, Guid scanId, Guid contactId)
        => Ok(new ApiResponse<OcrResultResponse>(true, await _service.MatchContactAsync(companyId, scanId, contactId)));
}
