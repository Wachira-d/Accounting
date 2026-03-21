using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

// Admin endpoints
[ApiController]
[Route("api/companies/{companyId:guid}/portal")]
[Authorize]
public class PortalAdminController : ControllerBase
{
    private readonly IPortalService _service;
    public PortalAdminController(IPortalService service) => _service = service;

    [HttpPost("access")]
    public async Task<ActionResult<ApiResponse<PortalAccessResponse>>> CreateAccess(Guid companyId, [FromBody] CreatePortalAccessRequest request)
        => Ok(new ApiResponse<PortalAccessResponse>(true, await _service.CreateAccessAsync(companyId, request)));

    [HttpGet("access")]
    public async Task<ActionResult<ApiResponse<List<PortalAccessResponse>>>> GetAccesses(Guid companyId)
        => Ok(new ApiResponse<List<PortalAccessResponse>>(true, await _service.GetAccessesAsync(companyId)));

    [HttpPut("access/{accessId:guid}")]
    public async Task<ActionResult<ApiResponse<PortalAccessResponse>>> UpdateAccess(Guid companyId, Guid accessId, [FromBody] UpdatePortalAccessRequest request)
        => Ok(new ApiResponse<PortalAccessResponse>(true, await _service.UpdateAccessAsync(companyId, accessId, request)));

    [HttpPost("access/{accessId:guid}/deactivate")]
    public async Task<ActionResult<ApiResponse<bool>>> Deactivate(Guid companyId, Guid accessId)
    { await _service.DeactivateAccessAsync(companyId, accessId); return Ok(new ApiResponse<bool>(true, true)); }
}

// Public portal endpoints
[ApiController]
[Route("api/portal")]
public class PortalPublicController : ControllerBase
{
    private readonly IPortalService _service;
    public PortalPublicController(IPortalService service) => _service = service;

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<PortalLoginResponse>>> Login([FromBody] PortalLoginRequest request)
        => Ok(new ApiResponse<PortalLoginResponse>(true, await _service.LoginAsync(request)));

    [HttpGet("{companyId:guid}/documents")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<PortalDocumentResponse>>>> GetDocuments(Guid companyId, [FromQuery] Guid contactId, [FromQuery] string? type)
        => Ok(new ApiResponse<List<PortalDocumentResponse>>(true, await _service.GetMyDocumentsAsync(companyId, contactId, type)));

    [HttpGet("{companyId:guid}/documents/{documentId:guid}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PortalDocumentResponse>>> GetDocument(Guid companyId, Guid documentId, [FromQuery] Guid contactId)
        => Ok(new ApiResponse<PortalDocumentResponse>(true, await _service.GetDocumentAsync(companyId, contactId, documentId)));

    [HttpGet("{companyId:guid}/documents/{documentId:guid}/pdf")]
    [Authorize]
    public async Task<ActionResult> DownloadPdf(Guid companyId, Guid documentId, [FromQuery] Guid contactId)
    { var pdf = await _service.DownloadDocumentPdfAsync(companyId, contactId, documentId); return File(pdf, "application/pdf"); }

    [HttpGet("{companyId:guid}/statement")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PortalStatementResponse>>> GetStatement(Guid companyId, [FromQuery] Guid contactId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
        => Ok(new ApiResponse<PortalStatementResponse>(true, await _service.GetMyStatementAsync(companyId, contactId, fromDate, toDate)));
}
