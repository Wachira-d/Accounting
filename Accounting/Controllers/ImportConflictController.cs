using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Import conflict resolution — central endpoint สำหรับทุก import flow.
/// User เห็น list ของแถวที่ตรวจพบของซ้ำ + เลือก resolution per row
/// (UseExisting / UseNew / Skip / Merge / CreateAnyway). ระบบ apply ตอน
/// import service finalize.
///
/// Flow:
///   1. Import service (SmartImport / BankImport / etc.) call
///      IDuplicateDetector.DetectBatchAsync → list of ImportConflicts
///   2. ถ้า count > 0 → insert ทั้งหมด + set session status to
///      AwaitingDuplicateResolution + return session id
///   3. UI redirect ไป /import-conflicts.html?session=X
///   4. User resolve ทีละแถว / bulk
///   5. UI call POST /import-conflicts/session/{id}/apply
///   6. Import service iterate ImportConflicts ตาม Resolution + perform
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/import-conflicts")]
[Authorize]
public class ImportConflictController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDuplicateDetector _detector;
    public ImportConflictController(AccountingDbContext db, IDuplicateDetector detector)
    {
        _db = db;
        _detector = detector;
    }

    /// <summary>List conflicts pending สำหรับ session (SessionId หรือ SessionRef).</summary>
    [HttpGet("session/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> GetBySession(Guid companyId, Guid sessionId)
    {
        var rows = await _db.ImportConflicts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.SessionId == sessionId && !c.IsDeleted)
            .OrderBy(c => c.RowNumber)
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            sessionId,
            total = rows.Count,
            pending = rows.Count(r => r.Resolution == ImportConflictResolution.Pending),
            byEntityType = rows.GroupBy(r => r.EntityType).Select(g => new { type = g.Key, count = g.Count() }),
            conflicts = rows.Select(r => new {
                r.Id, r.RowNumber, r.EntityType,
                staged = System.Text.Json.JsonSerializer.Deserialize<object>(r.StagedDataJson),
                existing = System.Text.Json.JsonSerializer.Deserialize<object>(r.ExistingDataJson),
                r.ExistingEntityId, r.MatchScore, r.MatchReason,
                r.Resolution, r.ResolvedAt, r.ResolvedBy, r.UserNote
            })
        }));
    }

    [HttpGet("session-ref/{sessionRef}")]
    public async Task<ActionResult<ApiResponse<object>>> GetBySessionRef(Guid companyId, string sessionRef)
    {
        var rows = await _db.ImportConflicts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.SessionRef == sessionRef && !c.IsDeleted)
            .OrderBy(c => c.RowNumber)
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            sessionRef,
            total = rows.Count,
            pending = rows.Count(r => r.Resolution == ImportConflictResolution.Pending),
            conflicts = rows.Select(r => new {
                r.Id, r.RowNumber, r.EntityType,
                staged = System.Text.Json.JsonSerializer.Deserialize<object>(r.StagedDataJson),
                existing = System.Text.Json.JsonSerializer.Deserialize<object>(r.ExistingDataJson),
                r.ExistingEntityId, r.MatchScore, r.MatchReason, r.Resolution
            })
        }));
    }

    public sealed record ResolveRequest(
        ImportConflictResolution Resolution,
        Dictionary<string, string>? MergeChoices,
        string? UserNote);

    /// <summary>Resolve 1 row. UseExisting/UseNew/Skip/CreateAnyway need no
    /// extra data. Merge requires MergeChoices map field→"existing"|"staged".</summary>
    [HttpPost("{conflictId:guid}/resolve")]
    public async Task<ActionResult<ApiResponse<object>>> Resolve(
        Guid companyId, Guid conflictId, [FromBody] ResolveRequest req)
    {
        var c = await _db.ImportConflicts
            .FirstOrDefaultAsync(x => x.Id == conflictId && x.CompanyId == companyId);
        if (c == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ conflict"));
        c.Resolution = req.Resolution;
        c.MergeChoicesJson = req.MergeChoices != null ? System.Text.Json.JsonSerializer.Serialize(req.MergeChoices) : null;
        c.UserNote = req.UserNote;
        c.ResolvedAt = DateTime.UtcNow;
        c.ResolvedBy = User.Identity?.Name ?? "user";
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { c.Id, c.Resolution },
            $"บันทึก resolution: {c.Resolution}"));
    }

    public sealed record BulkResolveRequest(ImportConflictResolution Resolution);

    /// <summary>Bulk apply same resolution to all pending conflicts ใน session.
    /// ใช้กับ "ใช้ของเดิมทั้งหมด" / "ใช้ใหม่ทั้งหมด" / "ข้ามทั้งหมด" buttons.</summary>
    [HttpPost("session/{sessionId:guid}/resolve-all")]
    public async Task<ActionResult<ApiResponse<object>>> ResolveAll(
        Guid companyId, Guid sessionId, [FromBody] BulkResolveRequest req)
    {
        var pending = await _db.ImportConflicts
            .Where(c => c.CompanyId == companyId && c.SessionId == sessionId
                && c.Resolution == ImportConflictResolution.Pending && !c.IsDeleted)
            .ToListAsync();
        var who = User.Identity?.Name ?? "user";
        foreach (var c in pending)
        {
            c.Resolution = req.Resolution;
            c.ResolvedAt = DateTime.UtcNow;
            c.ResolvedBy = who;
            c.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { resolved = pending.Count },
            $"แก้ {pending.Count} แถวเป็น {req.Resolution}"));
    }

    /// <summary>Detect duplicates สำหรับ staged data ที่ส่งมา. ใช้ใน import
    /// flow ที่ยังไม่ persist session — preview duplicate ก่อน confirm.</summary>
    public sealed record DetectRequest(
        string EntityType,
        List<Dictionary<string, object?>> Rows,
        string? SessionRef);

    [HttpPost("detect")]
    public async Task<ActionResult<ApiResponse<object>>> Detect(
        Guid companyId, [FromBody] DetectRequest req, CancellationToken ct)
    {
        if (req.Rows == null || req.Rows.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไม่มี row"));

        var conflicts = await _detector.DetectBatchAsync(companyId, req.EntityType,
            req.Rows, sessionId: null, sessionRef: req.SessionRef, ct);

        // Persist เพื่อให้ user resolve ผ่าน UI
        if (conflicts.Count > 0)
        {
            _db.ImportConflicts.AddRange(conflicts);
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new ApiResponse<object>(true, new
        {
            totalRows = req.Rows.Count,
            conflictCount = conflicts.Count,
            sessionRef = req.SessionRef,
            conflictIds = conflicts.Select(c => c.Id)
        }, conflicts.Count > 0
            ? $"พบของซ้ำ {conflicts.Count} แถว — กรุณาเลือกว่าจะใช้ของเดิมหรือใหม่"
            : "ไม่พบของซ้ำ — import ปกติได้เลย"));
    }
}
