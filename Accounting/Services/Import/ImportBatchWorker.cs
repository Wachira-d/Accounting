using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// งานเบื้องหลังของศูนย์นำเข้าข้อมูล (รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.4) — ลงทะเบียน <c>AddHostedService</c> แล้ว (AddImportHub)
///
/// <para>ทุก ~<see cref="PollInterval"/>: claim batch ที่ <c>Status = Queued</c> (ไฟล์ใหญ่รอวิเคราะห์/รอลงข้อมูล) หรือ <c>Committing</c> ที่ heartbeat เก่ากว่า
/// <see cref="StaleAfter"/> (เครื่องที่ทำอยู่ตาย) ด้วย <c>FOR UPDATE SKIP LOCKED</c> (แบบ <c>EmailScheduleService</c>) แล้วทำใต้
/// <c>JobLock.RunExclusiveAsync(db, AdvisoryLockKey.ImportBatch, batchId, …, companyId)</c> (CLAUDE.md F4 ข้อ 6) · หนึ่งรอบ = หนึ่ง chunk 500 แถว
/// (<see cref="IImportPipelineService.ProcessQueuedAsync"/>) + heartbeat · scope ใหม่ต่อรอบ · <b>ไม่มี static state</b> (หลายเครื่องรันพร้อมกันได้)</para>
/// <para>วันละครั้ง: ล้างแถวพัก (<c>ImportStagedRows</c>) ของชุดที่ปิดแล้วเกิน <see cref="RetainDays"/> วัน — คงสรุปของชุด (ReviewSummaryJson) + ไฟล์ต้นทาง
/// (หลักฐานประกอบรายการ · พ.ร.บ.การบัญชี ม.10) · แถวพักมีค่าดิบที่เป็น PII (เข้ารหัส) ⇒ ไม่เก็บเกินจำเป็น (PDPA)</para>
/// <para>ล้มดัง: งานของ batch ล้ม ⇒ pipeline ประทับสถานะ + หมายเหตุที่ผู้ใช้เห็น · worker log ระดับ Error</para>
/// </summary>
public class ImportBatchWorker : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    /// <summary>heartbeat เก่ากว่านี้ = เครื่องที่ถืออยู่ตาย/ค้าง ⇒ เครื่องอื่นหยิบต่อได้ (งาน idempotent — แถวที่ลงแล้วไม่ถูกส่งซ้ำ)</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    public const int RetainDays = 90;
    /// <summary>ชุดที่ค้างไม่ได้ลง (รอตรวจ/ล้ม/วิเคราะห์ค้าง) ไม่มีการใช้งานเกินนี้ ⇒ ยกเลิกอัตโนมัติ + ล้างแถวพัก (ฝ่ายค้านรอบ ETL — เดิมค้างตลอดไปพร้อม PII)</summary>
    public const int AbandonedDays = 30;

    private readonly IServiceProvider _services;
    private readonly ILogger<ImportBatchWorker> _logger;

    public ImportBatchWorker(IServiceProvider services, ILogger<ImportBatchWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ชื่อ worker ต่อ instance (ไม่ใช่ static) — ใช้ประทับ ClaimedBy ให้ claim ซ้ำของตัวเองผ่าน
        var workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        if (workerId.Length > 100) workerId = workerId[^100..];
        var lastPurge = DateTime.MinValue;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunOnceAsync(workerId, stoppingToken);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(24))
                {
                    await PurgeAsync(stoppingToken);
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImportBatchWorker: รอบนี้ล้ม — จะลองใหม่รอบหน้า");
            }
            if (worked) continue;   // มีงานต่อ (chunk ถัดไป) — ไม่ต้องรอ
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>claim หนึ่ง batch แล้วทำหนึ่ง chunk — คืน true เมื่อได้ทำงาน (รอบถัดไปไม่ต้องรอ)</summary>
    private async Task<bool> RunOnceAsync(string workerId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var now = DateTime.UtcNow;
        var stale = now - StaleAfter;
        var queued = (int)ImportSessionStatus.Queued;
        var committing = (int)ImportSessionStatus.Committing;
        // Atomic claim (SKIP LOCKED): สองเครื่องไม่ได้ batch เดียวกัน · batch ที่เครื่องอื่นเพิ่งเต้น (heartbeat สด) ไม่ถูกแย่ง ·
        // ของตัวเอง (ClaimedBy = worker นี้) หยิบต่อได้ทันที · งานข้ามบริษัทโดยธรรมชาติ (worker ระบบ) — งานจริงด้านล่างกรอง CompanyId ทุกคำสั่ง
        var claimed = await db.Database.SqlQuery<Guid>(
            $@"WITH claimed AS (
                SELECT ""Id""
                FROM ""SmartImportSessions""
                WHERE NOT ""IsDeleted""
                  AND (""Status"" = {queued} OR ""Status"" = {committing})
                  AND (""HeartbeatAt"" IS NULL OR ""HeartbeatAt"" < {stale} OR ""ClaimedBy"" = {workerId})
                ORDER BY ""CreatedAt""
                LIMIT 1
                FOR UPDATE SKIP LOCKED
              )
              UPDATE ""SmartImportSessions"" s
              SET ""ClaimedBy"" = {workerId}, ""ClaimedAt"" = {now}, ""HeartbeatAt"" = {now}
              FROM claimed
              WHERE s.""Id"" = claimed.""Id""
              RETURNING s.""Id""").ToListAsync(ct);
        if (claimed.Count == 0) return false;
        var batchId = claimed[0];
        var companyId = await db.SmartImportSessions.AsNoTracking().Where(s => s.Id == batchId).Select(s => s.CompanyId).FirstAsync(ct);

        var pipeline = scope.ServiceProvider.GetRequiredService<IImportPipelineService>();
        var ran = await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
        {
            await pipeline.ProcessQueuedAsync(companyId, batchId, workerId, ct);
        }, _logger, companyId, ct);
        return ran;
    }

    /// <summary>ล้างแถวพักของชุดที่ปิดแล้วเกิน 90 วัน (ทีละไม่เกิน 200 ชุดต่อรอบ · ต่อบริษัท) — คงสรุป + ไฟล์ต้นทาง · ล็อกข้ามเครื่อง</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.ImportBatch, "purge", async () =>
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetainDays);
            var closed = new[]
            {
                ImportSessionStatus.Completed, ImportSessionStatus.PartiallyCompleted, ImportSessionStatus.Undone, ImportSessionStatus.Cancelled,
            };
            var sessions = await db.SmartImportSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => closed.Contains(s.Status) && (s.CompletedAt ?? s.UndoneAt ?? s.UpdatedAt ?? s.CreatedAt) < cutoff
                    && db.ImportStagedRows.IgnoreQueryFilters().Any(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId))
                .OrderBy(s => s.CompanyId).ThenBy(s => s.CreatedAt)
                .Select(s => new { s.Id, s.CompanyId, s.ReviewSummaryJson })
                .Take(200)
                .ToListAsync(ct);
            var purged = 0;
            foreach (var s in sessions)
            {
                // สรุปต้องมีก่อนล้าง (หน้ารายการ/รายละเอียดอ่านจากสรุปเมื่อไม่มีแถว) — ไม่มี = ข้าม (ล้างไม่ได้โดยไม่เสียตัวเลข)
                if (string.IsNullOrWhiteSpace(s.ReviewSummaryJson)) continue;
                purged += await db.ImportStagedRows.IgnoreQueryFilters()
                    .Where(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId)
                    .ExecuteDeleteAsync(ct);
            }
            if (purged > 0)
                _logger.LogInformation("ImportBatchWorker: ล้างแถวพักที่เกิน {Days} วันแล้ว {Rows} แถว ({Batches} ชุด)", RetainDays, purged, sessions.Count);
            await PurgeAbandonedAsync(db, ct);
        }, _logger, Guid.Empty, ct);
    }

    /// <summary>
    /// ชุดที่ถูกทิ้งไว้ (รอตรวจ · ลงไม่สำเร็จ · วิเคราะห์ค้าง) และไม่มีการใช้งานเกิน <see cref="AbandonedDays"/> วัน ⇒ ยกเลิกอัตโนมัติพร้อมหมายเหตุที่ผู้ใช้เห็น ·
    /// เก็บสรุปของชุด (ReviewSummaryJson — สร้างจากแถวถ้ายังไม่มี) แล้วล้างแถวพัก (ค่าดิบที่เป็น PII · PDPA ม.37 เก็บไม่เกินจำเป็น) ·
    /// ชุดที่ลงไปแล้วบางแถวไม่แตะ (ต้องย้อนได้) · ทีละไม่เกิน 200 ชุดต่อรอบ · ไฟล์ต้นทางคงไว้ (ดาวน์โหลด/นำเข้าใหม่ได้)
    /// </summary>
    internal static async Task<int> PurgeAbandonedAsync(AccountingDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-AbandonedDays);
        var stale = new[]
        {
            ImportSessionStatus.Staged, ImportSessionStatus.Failed, ImportSessionStatus.Analyzing, ImportSessionStatus.MappingRequired,
            ImportSessionStatus.Uploading,
        };
        var sessions = await db.SmartImportSessions.IgnoreQueryFilters()
            // ชุดของศูนย์นำเข้าเท่านั้น (OptionsJson + ไฟล์ต้นทาง — เกณฑ์เดียวกับรายการประวัติ) · Smart Import เดิมมีวงจรของตัวเอง
            .Where(s => !s.IsDeleted && stale.Contains(s.Status) && (s.UpdatedAt ?? s.CreatedAt) < cutoff
                && s.OptionsJson != null && s.SourceFileAttachmentId != null
                && !db.ImportStagedRows.IgnoreQueryFilters().Any(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId
                    && (r.Status == ImportRowStatus.Committed || r.Status == ImportRowStatus.Undone)))
            .OrderBy(s => s.CompanyId).ThenBy(s => s.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        var purged = 0;
        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.ReviewSummaryJson))
            {
                var counts = await db.ImportStagedRows.IgnoreQueryFilters().AsNoTracking()
                    .Where(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId)
                    .GroupBy(r => r.Status).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
                var snap = new ImportReviewSnapshot();
                foreach (var c in counts) snap.StatusCounts[c.Key.ToString()] = c.C;
                s.ReviewSummaryJson = ImportJson.Serialize(snap);
            }
            var state = ImportJson.Deserialize<ImportBatchState>(s.OptionsJson) ?? new ImportBatchState();
            state.Notes.Add($"ยกเลิกอัตโนมัติ: ไม่มีการใช้งานเกิน {AbandonedDays} วัน — ล้างแถวที่พักไว้แล้ว (ข้อมูลส่วนบุคคลไม่เก็บเกินจำเป็น) · "
                + "ไฟล์ต้นทางยังดาวน์โหลดได้ ถ้าต้องการนำเข้าให้อัปโหลดเป็นชุดใหม่");
            state.PendingCommit = null;
            state.PendingActor = null;
            s.OptionsJson = ImportJson.Serialize(state);
            s.Status = ImportSessionStatus.Cancelled;
            s.Phase = ImportBatchPhase.Review;
            s.CompletedAt = DateTime.UtcNow;
            s.ClaimedBy = null;
            s.HeartbeatAt = null;
            await db.SaveChangesAsync(ct);
            purged += await db.ImportStagedRows.IgnoreQueryFilters()
                .Where(r => r.SessionId == s.Id && r.CompanyId == s.CompanyId)
                .ExecuteDeleteAsync(ct);
        }
        return purged;
    }
}
