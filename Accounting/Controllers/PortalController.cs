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
        => StatusCode(201, new ApiResponse<PortalAccessResponse>(true, await _service.CreateAccessAsync(companyId, request)));

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

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<PortalLoginResponse>>> Refresh([FromBody] PortalRefreshRequest request)
        => Ok(new ApiResponse<PortalLoginResponse>(true, await _service.RefreshTokenAsync(request.RefreshToken)));

    [HttpGet("{companyId:guid}/payments")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<PortalPaymentResponse>>>> GetPayments(Guid companyId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<List<PortalPaymentResponse>>(false, null!, "Invalid portal token"));
        return Ok(new ApiResponse<List<PortalPaymentResponse>>(true, await _service.GetMyPaymentsAsync(companyId, contactId.Value)));
    }

    [HttpGet("{companyId:guid}/documents")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<PortalDocumentResponse>>>> GetDocuments(Guid companyId, [FromQuery] string? type)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<List<PortalDocumentResponse>>(false, null!, "Invalid portal token"));
        return Ok(new ApiResponse<List<PortalDocumentResponse>>(true, await _service.GetMyDocumentsAsync(companyId, contactId.Value, type)));
    }

    [HttpGet("{companyId:guid}/documents/{documentId:guid}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PortalDocumentResponse>>> GetDocument(Guid companyId, Guid documentId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<PortalDocumentResponse>(false, null!, "Invalid portal token"));
        return Ok(new ApiResponse<PortalDocumentResponse>(true, await _service.GetDocumentAsync(companyId, contactId.Value, documentId)));
    }

    [HttpGet("{companyId:guid}/documents/{documentId:guid}/pdf")]
    [Authorize]
    public async Task<ActionResult> DownloadPdf(Guid companyId, Guid documentId)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized();
        var pdf = await _service.DownloadDocumentPdfAsync(companyId, contactId.Value, documentId);
        return File(pdf, "application/pdf");
    }

    [HttpGet("{companyId:guid}/statement")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<PortalStatementResponse>>> GetStatement(Guid companyId, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate)
    {
        var contactId = GetContactIdFromToken();
        if (contactId == null) return Unauthorized(new ApiResponse<PortalStatementResponse>(false, null!, "Invalid portal token"));
        return Ok(new ApiResponse<PortalStatementResponse>(true, await _service.GetMyStatementAsync(companyId, contactId.Value, fromDate, toDate)));
    }

    private Guid? GetContactIdFromToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return null;
        return _service.ExtractContactIdFromToken(authHeader["Bearer ".Length..]);
    }
}
