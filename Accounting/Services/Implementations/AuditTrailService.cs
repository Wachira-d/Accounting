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

    /// <summary>Re-compute RowHash ของแต่ละ row เรียงตาม Id + เทียบกับ
    /// stored hash. ถ้ามี mismatch = chain ถูก tamper (แก้/แทรก/ลบหลัง insert).
    /// สูตรอยู่ที่ <c>Helpers/AuditHashChain</c> ตัวเดียว (v2 = round-trip ผ่าน PostgreSQL ได้ · v1 ตรวจแบบ legacy)</summary>
    public async Task<AuditChainVerifyResult> VerifyHashChainAsync(Guid companyId, long afterId = 0)
    {
        // เรียงตาม Id = ลำดับเดียวกับที่ฝั่งเขียนหา hash ก่อนหน้า (เดิมเรียง Timestamp ก่อน — เวลาข้ามเครื่องไม่ตรงกัน
        // ⇒ ลำดับไม่ตรงลำดับเขียน ⇒ ฟ้องว่าถูกแก้ทั้งที่ไม่มีใครแตะ)
        var rows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.RowHash != null && a.Id > afterId)
            .OrderBy(a => a.Id)
            .ToListAsync();
        // รอบ 201 ทีม PL (A-PL3): ตรวจเป็นช่วง ⇒ parent ที่อยู่ก่อนช่วงต้อง "มีอยู่จริงในฐานของบริษัทนี้" จึงนับเป็นจุดยึด —
        // ค้นจริงทีละชุด ไม่ส่งชุดที่แถวอ้างเป็นจุดยึดตรง ๆ (= ปิดการตรวจขาดตอน)
        List<string>? anchors = null;
        if (afterId > 0)
        {
            var outside = Accounting.Helpers.AuditHashChain.ExternalParents(rows);
            anchors = new List<string>();
            foreach (var chunk in outside.Chunk(500))
            {
                var hashes = chunk.ToList();
                anchors.AddRange(await _db.AuditLogs.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && a.Id <= afterId && a.RowHash != null && hashes.Contains(a.RowHash!))
                    .Select(a => a.RowHash!)
                    .ToListAsync());
            }
        }
        // ใช้ฟังก์ชันกลางตัวเดียวกับฝั่งเขียน (Helpers/AuditHashChain — v2 + ตรวจแถว v1 แบบ legacy) —
        // ห้ามเขียน format string ซ้ำที่นี่ เดิมทำแบบนั้นแล้ว drift จนตรวจไม่มีวันผ่าน ·
        // รอบสอง W2-C1: แยก "ถูกแก้" / "ขาดตอน" / "แตกกิ่งจากคำขอพร้อมกัน" และรายงานทุกแถว ไม่ใช่แค่แถวแรก
        var a = Accounting.Helpers.AuditHashChain.Analyze(rows, anchors);
        var first = a.Tampered.Concat(a.Dangling).OrderBy(r => r.Id).FirstOrDefault();
        // คำตัดสินรอบ 201 (DV Q3): แถวนอก chain รุ่นเก่า นับแยก (ไม่ใช่ "ถูกแก้" · ไม่เติม hash ย้อนหลัง) — ทั้งบริษัท ไม่ขึ้นกับ watermark
        var unchainedCount = await _db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.CompanyId == companyId && x.RowHash == null);
        DateTime? unchainedLatest = unchainedCount == 0 ? null : await _db.AuditLogs.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.RowHash == null)
            .MaxAsync(x => (DateTime?)x.Timestamp);
        return new AuditChainVerifyResult(
            rows.Count,
            first == null ? -1 : rows.IndexOf(first),
            first?.Id.ToString(),
            first?.Timestamp,
            !a.HasIntegrityFindings,
            a.Tampered.Count,
            a.Dangling.Count,
            a.ForkCount,
            a.Tampered.Select(r => r.Id.ToString()).ToList(),
            a.Dangling.Select(r => r.Id.ToString()).ToList(),
            Accounting.Helpers.AuditHashChain.AlertMessage(a),
            rows.Count == 0 ? 0 : rows[^1].Id,
            unchainedCount, unchainedLatest,
            Accounting.Helpers.AuditHashChain.UnchainedNote(unchainedCount, unchainedLatest));
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

                // RV2-1 (ฝ่ายค้าน GW รอบสอง · รอบ 201 ทีม PL): ช่องลับ (WebhookToken · *Protected · PasswordHash ฯลฯ) ห้ามเข้า audit เป็นข้อความเปล่า
                // (append-only — ลบไม่ได้ · ทุกบทบาทอ่าน /audit/logs ได้) ⇒ ตัวตัดสินเดียว Helpers/AuditRedaction
                foreach (var prop in entry.Properties.Where(p => p.IsModified))
                {
                    oldValues[prop.Metadata.Name] = Accounting.Helpers.AuditRedaction.Value(prop.Metadata.Name, prop.OriginalValue);
                    newValues[prop.Metadata.Name] = Accounting.Helpers.AuditRedaction.Value(prop.Metadata.Name, prop.CurrentValue);
                }

                auditLog.OldValues = JsonSerializer.Serialize(oldValues);
                auditLog.NewValues = JsonSerializer.Serialize(newValues);
            }
            else if (entry.State == EntityState.Added)
            {
                var newValues = new Dictionary<string, object?>();
                foreach (var prop in entry.Properties)
                {
                    newValues[prop.Metadata.Name] = Accounting.Helpers.AuditRedaction.Value(prop.Metadata.Name, prop.CurrentValue);
                }
                auditLog.NewValues = JsonSerializer.Serialize(newValues);
            }

            auditEntries.Add(auditLog);
        }

        return auditEntries;
    }

    /// <summary>RV2-1: แถวเก่าที่เก็บค่าลับไปแล้ว (ก่อนรอบ 201) แก้ไม่ได้ (append-only · hash chain) — ปิดค่าตอนแสดงแทน</summary>
    private static AuditLogResponse MapToResponse(AuditLog a)
    {
        return new AuditLogResponse(
            a.Id, a.CompanyId, a.UserId, a.UserEmail, a.Action, a.EntityType,
            a.EntityId, Accounting.Helpers.AuditRedaction.RedactJson(a.OldValues), Accounting.Helpers.AuditRedaction.RedactJson(a.NewValues),
            a.IpAddress, a.UserAgent, a.Timestamp);
    }
}

/// <summary>F14 hash chain — กันการแก้ไข AuditLogs หลังจากบันทึก. ปกติ
/// ใน production ควรเพิ่ม PostgreSQL trigger BEFORE UPDATE/DELETE
/// บน AuditLogs → RAISE EXCEPTION เพื่อกัน DBA จาก raw SQL. แต่ application
/// guarantee ขั้นต่ำ: ChangeTracker entries ไม่ track AuditLog (AuditTrailService
/// line 118 skip "AuditLog" → no Update via DbContext). hash chain ตรวจ
/// ภายหลังเป็นชั้นที่ 2.</summary>

