using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Audit;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

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

    /// <summary>Re-compute RowHash ของแต่ละ row เรียงตาม Timestamp + เทียบกับ
    /// stored hash. ถ้ามี mismatch = chain ถูก tamper (แก้/แทรก/ลบหลัง insert).
    /// algorithm ตรงกับ AccountingDbContext.HashChain insert path: SHA-256 ของ
    /// "Timestamp|UserId|UserEmail|Action|EntityType|EntityId|NewValues|PrevHash"</summary>
    public async Task<AuditChainVerifyResult> VerifyHashChainAsync(Guid companyId)
    {
        var rows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.RowHash != null)
            .OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
            .ToListAsync();
        string? prev = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.PrevHash != prev)
                return new AuditChainVerifyResult(rows.Count, i, r.Id.ToString(), r.Timestamp, false);
            // ใช้ฟังก์ชันกลางตัวเดียวกับฝั่งเขียน (Helpers/AuditHashChain) —
            // ห้ามเขียน format string ซ้ำที่นี่ เดิมทำแบบนั้นแล้ว drift จนตรวจ
            // ไม่มีวันผ่าน (ดู comment ใน AuditHashChain)
            var expected = Accounting.Helpers.AuditHashChain.ComputeRowHash(r);
            if (!string.Equals(expected, r.RowHash, StringComparison.OrdinalIgnoreCase))
                return new AuditChainVerifyResult(rows.Count, i, r.Id.ToString(), r.Timestamp, false);
            prev = r.RowHash;
        }
        return new AuditChainVerifyResult(rows.Count, -1, null, null, true);
    }

    // ===== Static helper for SaveChanges audit logging =====

    /// <summary>
    /// Captures audit log entries from EF Core ChangeTracker before SaveChanges.
    /// Call this from DbContext.SaveChangesAsync() override.
    /// </summary>
    public static List<AuditLog> CaptureAuditEntries(ChangeTracker changeTracker, Guid? userId, string? userEmail, string? ipAddress)
    {
        var auditEntries = new List<AuditLog>();

        foreach (var entry in changeTracker.Entries()
            .Where(e => e.Entity is TenantEntity or BaseEntity
                && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var entityType = entry.Entity.GetType().Name;
            var entityId = entry.Property("Id").CurrentValue?.ToString() ?? "";

            // Skip audit log entities to prevent infinite recursion
            if (entityType == "AuditLog" || entityType == "Notification") continue;

            Guid? companyId = null;
            if (entry.Entity is TenantEntity tenantEntity)
            {
                companyId = tenantEntity.CompanyId;
            }

            var auditLog = new AuditLog
            {
                CompanyId = companyId ?? Guid.Empty,
                UserId = userId,
                UserEmail = userEmail ?? "",
                Action = entry.State switch
                {
                    EntityState.Added => AuditAction.Create,
                    EntityState.Modified => AuditAction.Update,
                    EntityState.Deleted => AuditAction.Delete,
                    _ => AuditAction.View
                },
                EntityType = entityType,
                EntityId = entityId,
                IpAddress = ipAddress,
                Timestamp = DateTime.UtcNow
            };

            // Capture old and new values for updates
            if (entry.State == EntityState.Modified)
            {
                var oldValues = new Dictionary<string, object?>();
                var newValues = new Dictionary<string, object?>();

                foreach (var prop in entry.Properties.Where(p => p.IsModified))
                {
                    oldValues[prop.Metadata.Name] = prop.OriginalValue;
                    newValues[prop.Metadata.Name] = prop.CurrentValue;
                }

                auditLog.OldValues = JsonSerializer.Serialize(oldValues);
                auditLog.NewValues = JsonSerializer.Serialize(newValues);
            }
            else if (entry.State == EntityState.Added)
            {
                var newValues = new Dictionary<string, object?>();
                foreach (var prop in entry.Properties)
                {
                    newValues[prop.Metadata.Name] = prop.CurrentValue;
                }
                auditLog.NewValues = JsonSerializer.Serialize(newValues);
            }

            auditEntries.Add(auditLog);
        }

        return auditEntries;
    }

    private static AuditLogResponse MapToResponse(AuditLog a) => new(
        a.Id, a.CompanyId, a.UserId, a.UserEmail, a.Action, a.EntityType,
        a.EntityId, a.OldValues, a.NewValues, a.IpAddress, a.UserAgent, a.Timestamp);
}

/// <summary>F14 hash chain — กันการแก้ไข AuditLogs หลังจากบันทึก. ปกติ
/// ใน production ควรเพิ่ม PostgreSQL trigger BEFORE UPDATE/DELETE
/// บน AuditLogs → RAISE EXCEPTION เพื่อกัน DBA จาก raw SQL. แต่ application
/// guarantee ขั้นต่ำ: ChangeTracker entries ไม่ track AuditLog (AuditTrailService
/// line 118 skip "AuditLog" → no Update via DbContext). hash chain ตรวจ
/// ภายหลังเป็นชั้นที่ 2.</summary>

