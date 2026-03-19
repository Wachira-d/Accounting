using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Audit;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AuditTrailService : IAuditTrailService
{
    private readonly AccountingDbContext _db;

    public AuditTrailService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResponse<AuditLogResponse>> GetLogsAsync(Guid companyId, AuditLogQueryRequest request)
    {
        var query = _db.AuditLogs.Where(a => a.CompanyId == companyId);

        if (request.FromDate.HasValue)
            query = query.Where(a => a.Timestamp >= request.FromDate.Value);
        if (request.ToDate.HasValue)
            query = query.Where(a => a.Timestamp <= request.ToDate.Value);
        if (request.UserId.HasValue)
            query = query.Where(a => a.UserId == request.UserId.Value);
        if (request.Action.HasValue)
            query = query.Where(a => a.Action == request.Action.Value);
        if (!string.IsNullOrEmpty(request.EntityType))
            query = query.Where(a => a.EntityType == request.EntityType);
        if (!string.IsNullOrEmpty(request.EntityId))
            query = query.Where(a => a.EntityId == request.EntityId);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(a => a.Timestamp)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<AuditLogResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<AuditSummaryResponse> GetSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var logs = await _db.AuditLogs
            .Where(a => a.CompanyId == companyId && a.Timestamp >= fromDate && a.Timestamp <= toDate)
            .ToListAsync();

        var actionSummary = logs.GroupBy(a => a.Action)
            .Select(g => new AuditActionSummary(g.Key, g.Count()))
            .OrderByDescending(a => a.Count)
            .ToList();

        var userSummary = logs.Where(a => a.UserId.HasValue)
            .GroupBy(a => new { a.UserId, a.UserEmail })
            .Select(g => new AuditUserSummary(g.Key.UserId!.Value, g.Key.UserEmail, g.Count(), g.Max(a => a.Timestamp)))
            .OrderByDescending(u => u.ActionCount)
            .ToList();

        var entitySummary = logs.GroupBy(a => a.EntityType)
            .Select(g => new AuditEntitySummary(
                g.Key,
                g.Count(a => a.Action == AuditAction.Create),
                g.Count(a => a.Action == AuditAction.Update),
                g.Count(a => a.Action == AuditAction.Delete)))
            .OrderByDescending(e => e.CreateCount + e.UpdateCount + e.DeleteCount)
            .ToList();

        return new AuditSummaryResponse(fromDate, toDate, logs.Count, actionSummary, userSummary, entitySummary);
    }

    public async Task<List<AuditLogResponse>> GetEntityHistoryAsync(Guid companyId, string entityType, string entityId)
    {
        var logs = await _db.AuditLogs
            .Where(a => a.CompanyId == companyId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();

        return logs.Select(MapToResponse).ToList();
    }

    public async Task<List<AuditLogResponse>> GetUserActivityAsync(Guid companyId, Guid userId, int limit = 100)
    {
        var logs = await _db.AuditLogs
            .Where(a => a.CompanyId == companyId && a.UserId == userId)
            .OrderByDescending(a => a.Timestamp)
            .Take(limit)
            .ToListAsync();

        return logs.Select(MapToResponse).ToList();
    }

    private static AuditLogResponse MapToResponse(Models.Entities.AuditLog a) => new(
        a.Id, a.CompanyId, a.UserId, a.UserEmail, a.Action, a.EntityType,
        a.EntityId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.Timestamp);
}
