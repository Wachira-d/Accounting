using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-7/G-8 · คำตัดสินข้อ 180) — ส่งใบแจ้งหนี้/ทวงหนี้/ระงับ/คืนสถานะค่าบริการ NextAcc ทุกชั่วโมง
/// <para>กฎ #4 D: ทีละเครื่อง (<see cref="JobLock"/>) · <b>watermark ต่อบริษัท</b> = <c>PlatformDunningState.LastEvaluatedAt</c> (บริษัทที่ประเมินภายใน
/// <see cref="EvaluateEveryHours"/> ชม. ข้าม · รอบละไม่เกิน <see cref="BatchSize"/> บริษัท เก่าสุดก่อน) · ส่งซ้ำไม่ได้ด้วยคีย์ข้อความ (notifier) ·
/// แต่ละบริษัทใช้ scope ของตัวเอง (บริษัทหนึ่งล้มไม่ลากบริษัทอื่น — ล้มดังใน log + สรุปรอบ)</para>
/// </summary>
public class PlatformDunningJob : BackgroundService
{
    public const int BatchSize = 200, EvaluateEveryHours = 6, CandidateScan = 5000;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlatformDunningJob> _logger;

    public PlatformDunningJob(IServiceScopeFactory scopes, ILogger<PlatformDunningJob> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _scopes.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(PlatformDunningJob),
                    () => RunOnceAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "PlatformDunningJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>หนึ่งรอบ — public เพื่อเทสต์/ปุ่มแอดมิน "ประเมินทั้งหมดตอนนี้" (ต้องถือ JobLock ก่อนเรียกเสมอ)</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        List<Guid> due;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            due = await DueCompaniesAsync(db, started, ct);
        }
        int done = 0, failed = 0;
        foreach (var companyId in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IPlatformDunningService>();
                await svc.EvaluateCompanyAsync(companyId, sendReminders: true, ct);
                done++;
            }
            catch (Exception ex) { failed++; _logger.LogError(ex, "ประเมินค้างชำระค่าบริการของบริษัท {Company} ไม่สำเร็จ", companyId); }
        }
        using (var scope = _scopes.CreateScope())
        {
            var rec = scope.ServiceProvider.GetService<IJobRunRecorder>();
            if (rec != null)
                await rec.RecordAsync("PlatformDunning", failed == 0, $"ประเมิน {done}/{due.Count} บริษัท · ล้ม {failed}", done, started);
        }
        return done;
    }

    /// <summary>บริษัทที่ถึงรอบประเมิน: มีใบค้าง หรือถูกระงับ/"จะระงับ" โดยระบบ · ข้ามที่ประเมินภายใน watermark · เก่าสุดก่อน</summary>
    public static async Task<List<Guid>> DueCompaniesAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(db, ct);
        if (platformId == null) return new List<Guid>();
        var withOpen = (await PlatformInvoiceQueries.OpenForAllBuyersAsync(db, platformId.Value, CandidateScan, ct))
            .Select(r => r.BuyerCompanyId).Distinct().ToList();
        var flagged = await db.PlatformDunningStates.AsNoTracking()
            .Where(s => s.SuspendedByDunningAt != null || s.WouldSuspendSince != null)
            .Select(s => s.CompanyId).ToListAsync(ct);
        var candidates = withOpen.Union(flagged).ToList();
        var cutoff = nowUtc.AddHours(-EvaluateEveryHours);
        var last = await db.PlatformDunningStates.AsNoTracking()
            .Where(s => candidates.Contains(s.CompanyId))
            .ToDictionaryAsync(s => s.CompanyId, s => s.LastEvaluatedAt, ct);
        return candidates
            .Where(c => !last.TryGetValue(c, out var at) || at == null || at < cutoff)
            .OrderBy(c => last.TryGetValue(c, out var at) ? at ?? DateTime.MinValue : DateTime.MinValue)
            .Take(BatchSize).ToList();
    }
}
