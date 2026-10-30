using System.Data.Common;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175 ง) — เดินคิว <c>DeferredNotifications</c> (แจ้งเตือน "สร้างแล้ว" ของการจอง/ออเดอร์หน้าเว็บ) ·
/// หนึ่งรอบ: หยิบแถวที่ถึงกำหนด (lease <see cref="DeferredNotifyPolicy.Lease"/> · <c>FOR UPDATE SKIP LOCKED</c> ⇒ หลายเครื่องไม่หยิบแถวเดียวกัน) →
/// ส่งผ่านเส้นเดิมของ service (<c>DispatchCreatedNotifyAsync</c> → Notify*CreatedAsync: ข้อความถึงลูกค้ามีคีย์กันซ้ำใน CustomerNotifyLogs) → ปิดแถว ·
/// ข้อยกเว้น ⇒ ถอยแล้วลองใหม่ (<see cref="DeferredNotifyPolicy.AfterFailure"/>) · ทุกคำสั่งกรอง <c>CompanyId</c> ของแถว (กฎ M)
/// </summary>
public class DeferredNotifyDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeferredNotifyDispatcher> _logger;

    public DeferredNotifyDispatcher(IServiceScopeFactory scopeFactory, ILogger<DeferredNotifyDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public sealed record Claimed(Guid Id, Guid CompanyId, Guid SiteId, string Kind, Guid EntityId, int Attempts);
    /// <summary>Done = ส่งแล้ว · Skipped = ปิดโดยไม่ส่ง (ฝ่ายค้าน S4 — ยกเลิกก่อนถึงคิว · เหตุอยู่ที่ Note)</summary>
    public sealed record RunResult(int Claimed, int Done, int Retried, int GaveUp, int Skipped = 0);

    /// <summary>หยิบแถวที่ถึงกำหนดพร้อมประทับ lease ในคำสั่งเดียว (หลายเครื่อง/สองรอบซ้อนไม่ได้แถวเดียวกัน) ·
    /// <paramref name="onlyCompanyId"/> = เดินเฉพาะบริษัทเดียว (Db test บนฐานร่วม · เครื่องมือซ่อม) — job ส่ง null (ทุกบริษัท)</summary>
    public static async Task<List<Claimed>> ClaimAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct, Guid? onlyCompanyId = null)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "DeferredNotifications" d
                   SET "ClaimedUntil" = @lease, "Attempts" = d."Attempts" + 1, "UpdatedAt" = @now
                 WHERE d."Id" IN (
                       SELECT "Id" FROM "DeferredNotifications"
                        WHERE "DoneAt" IS NULL AND "IsDeleted" = false AND "DueAt" <= @now
                          AND ("ClaimedUntil" IS NULL OR "ClaimedUntil" < @now)
                          AND (@company IS NULL OR "CompanyId" = @company)
                        ORDER BY "DueAt"
                        LIMIT @batch
                        FOR UPDATE SKIP LOCKED)
                RETURNING d."Id", d."CompanyId", d."SiteId", d."Kind", d."EntityId", d."Attempts"
                """;
            Add(cmd, "lease", nowUtc + DeferredNotifyPolicy.Lease);
            Add(cmd, "now", nowUtc);
            Add(cmd, "batch", DeferredNotifyPolicy.BatchSize);
            // ชนิดต้องระบุ — Npgsql เดาชนิดของ NULL ใน "@company IS NULL" ไม่ได้
            var company = cmd.CreateParameter();
            company.ParameterName = "company";
            company.DbType = System.Data.DbType.Guid;
            company.Value = onlyCompanyId.HasValue ? onlyCompanyId.Value : DBNull.Value;
            cmd.Parameters.Add(company);
            var list = new List<Claimed>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new Claimed(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetInt32(5)));
            return list;
        }
        finally
        {
            if (openedHere) await conn.CloseAsync();
        }
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>หนึ่งรอบ — คืนจำนวนที่หยิบ/ปิด/เลื่อน/ยอมแพ้ (ผู้เรียกบันทึกลง JobRunRecorder)</summary>
    public async Task<RunResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct, Guid? onlyCompanyId = null)
    {
        List<Claimed> batch;
        using (var claimScope = _scopeFactory.CreateScope())
            batch = await ClaimAsync(claimScope.ServiceProvider.GetRequiredService<AccountingDbContext>(), nowUtc, ct, onlyCompanyId);

        int done = 0, retried = 0, gaveUp = 0, skipped = 0;
        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();
            string? error = null, skipNote = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                try
                {
                    var sp = scope.ServiceProvider;
                    switch (row.Kind)
                    {
                        case DeferredNotifyPolicy.BookingCreated:
                            skipNote = await sp.GetRequiredService<ICmsBookingService>().DispatchCreatedNotifyAsync(row.CompanyId, row.SiteId, row.EntityId);
                            break;
                        case DeferredNotifyPolicy.OrderCreated:
                            skipNote = await sp.GetRequiredService<ICmsCommerceService>().DispatchCreatedNotifyAsync(row.CompanyId, row.SiteId, row.EntityId);
                            break;
                        default:
                            error = $"ไม่รู้จักชนิดคิว '{row.Kind}'";
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                    _logger.LogError(ex, "คิวแจ้งเตือน {Kind} {EntityId} ล้ม (ครั้งที่ {Attempt} · company {CompanyId})",
                        row.Kind, row.EntityId, row.Attempts, row.CompanyId);
                }
            }

            using var markScope = _scopeFactory.CreateScope();
            var db = markScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            if (error == null)
            {
                await db.DeferredNotifications.Where(d => d.Id == row.Id && d.CompanyId == row.CompanyId)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.DoneAt, (DateTime?)DateTime.UtcNow).SetProperty(d => d.ClaimedUntil, (DateTime?)null)
                        .SetProperty(d => d.LastError, (string?)null).SetProperty(d => d.Note, skipNote), ct);
                if (skipNote == null) done++; else skipped++;
                continue;
            }
            var (giveUp, next) = DeferredNotifyPolicy.AfterFailure(row.Attempts, DateTime.UtcNow);
            var msg = DeferredNotifyPolicy.Truncate(error);
            await db.DeferredNotifications.Where(d => d.Id == row.Id && d.CompanyId == row.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.LastError, msg).SetProperty(d => d.ClaimedUntil, (DateTime?)null)
                    .SetProperty(d => d.DueAt, next).SetProperty(d => d.DoneAt, giveUp ? (DateTime?)DateTime.UtcNow : null), ct);
            if (giveUp)
            {
                gaveUp++;
                _logger.LogError("คิวแจ้งเตือน {Kind} {EntityId} ยอมแพ้หลัง {Attempts} ครั้ง: {Error}", row.Kind, row.EntityId, row.Attempts, msg);
            }
            else retried++;
        }
        return new RunResult(batch.Count, done, retried, gaveUp, skipped);
    }
}

/// <summary>รอบ 217 (ข้อ 175 ง) — วนเดินคิวทุก <see cref="DeferredNotifyPolicy.PollInterval"/> · ทีละเครื่องผ่าน <see cref="JobLock"/> (F4 #6)</summary>
public class DeferredNotifyDispatchJob : BackgroundService
{
    public const string JobName = "DeferredNotifyDispatch";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeferredNotifyDispatchJob> _logger;

    public DeferredNotifyDispatchJob(IServiceScopeFactory scopeFactory, ILogger<DeferredNotifyDispatchJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "DeferredNotifyDispatchJob รอบนี้ล้ม"); }
            try { await Task.Delay(DeferredNotifyPolicy.PollInterval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(DeferredNotifyDispatchJob), async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<DeferredNotifyDispatcher>();
            var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
            var started = DateTime.UtcNow;
            var r = await dispatcher.RunOnceAsync(DateTime.UtcNow, ct);
            if (recorder != null && (r.Retried > 0 || r.GaveUp > 0))
                await recorder.RecordAsync(JobName, r.GaveUp == 0,
                    $"แจ้งเตือนตามหลัง: หยิบ {r.Claimed} · ส่งแล้ว {r.Done} · เลื่อนลองใหม่ {r.Retried} · ยอมแพ้ {r.GaveUp}", r.Done, started);
        }, _logger, ct: ct);
    }
}
