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
        // Clamp pageSize เพื่อกัน DoS หรือ accidental large pull —
        // audit logs ปริมาณมหาศาล query ใหญ่ ๆ จะกิน DB CPU/memory.
        // 200 พอสำหรับ UI ที่ใช้จริง ส่วน export ใช้ stream endpoint แยก.
        if (pageSize < 1) pageSize = 50;
        else if (pageSize > 200) pageSize = 200;
        if (page < 1) page = 1;
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

    /// <summary>F14 — verify hash chain integrity. ตรวจตั้งแต่ row แรกของ
    /// บริษัทถึงปัจจุบัน, recompute hash ของแต่ละ row + compare กับที่เก็บไว้
    /// + check link กับ PrevHash ของ row ถัดไป. คืนรายการ tampered rows
    /// (ถ้ามี). Admin role only — endpoint forensic-grade.</summary>
    [HttpGet("verify-hash-chain")]
    public async Task<ActionResult<ApiResponse<object>>> VerifyHashChain(
        Guid companyId,
        [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int maxRows = 100000)
    {
        if (maxRows < 1 || maxRows > 1_000_000) maxRows = 100_000;
        var rows = await db.AuditLogs
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.Id)
            .Take(maxRows)
            .ToListAsync();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var tampered = new List<object>();
        string? expectedPrev = null;
        foreach (var e in rows)
        {
            if (e.PrevHash != expectedPrev)
                tampered.Add(new { e.Id, issue = "broken chain", e.PrevHash, expected = expectedPrev });
            var canonical = $"{e.Timestamp:O}|{e.UserId}|{e.UserEmail}|{(int)e.Action}|{e.EntityType}|{e.EntityId}|{e.NewValues}|{e.OldValues}|{e.PrevHash}";
            var recomputed = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical)));
            if (recomputed != e.RowHash)
                tampered.Add(new { e.Id, issue = "row mutated", stored = e.RowHash, recomputed });
            expectedPrev = e.RowHash;
        }
        return Ok(new ApiResponse<object>(true, new
        {
            scanned = rows.Count,
            tamperedCount = tampered.Count,
            valid = tampered.Count == 0,
            firstTen = tampered.Take(10),
        }));
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
