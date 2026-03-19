using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Audit;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/audit")]
[Authorize]
public class AuditTrailController : ControllerBase
{
    private readonly IAuditTrailService _auditService;

    public AuditTrailController(IAuditTrailService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet("logs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<AuditLogResponse>>>> GetLogs(
        Guid companyId,
        [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        [FromQuery] Guid? userId, [FromQuery] AuditAction? action,
        [FromQuery] string? entityType, [FromQuery] string? entityId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var request = new AuditLogQueryRequest(fromDate, toDate, userId, action, entityType, entityId, page, pageSize);
        var result = await _auditService.GetLogsAsync(companyId, request);
        return Ok(new ApiResponse<PagedResponse<AuditLogResponse>>(true, result));
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<AuditSummaryResponse>>> GetSummary(
        Guid companyId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
    {
        var from = fromDate ?? DateTime.UtcNow.AddDays(-30);
        var to = toDate ?? DateTime.UtcNow;
        var result = await _auditService.GetSummaryAsync(companyId, from, to);
        return Ok(new ApiResponse<AuditSummaryResponse>(true, result));
    }

    [HttpGet("entity/{entityType}/{entityId}")]
    public async Task<ActionResult<ApiResponse<List<AuditLogResponse>>>> GetEntityHistory(
        Guid companyId, string entityType, string entityId)
    {
        var result = await _auditService.GetEntityHistoryAsync(companyId, entityType, entityId);
        return Ok(new ApiResponse<List<AuditLogResponse>>(true, result));
    }

    [HttpGet("users/{userId:guid}/activity")]
    public async Task<ActionResult<ApiResponse<List<AuditLogResponse>>>> GetUserActivity(
        Guid companyId, Guid userId, [FromQuery] int limit = 100)
    {
        var result = await _auditService.GetUserActivityAsync(companyId, userId, limit);
        return Ok(new ApiResponse<List<AuditLogResponse>>(true, result));
    }
}
